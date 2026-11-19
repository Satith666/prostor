// Package webview2 shows the interface in a native window with the Microsoft
// Edge WebView2 control, without WebView2Loader.dll and without cgo.
//
// The runtime is found through its EdgeUpdate registration and created with
// CreateWebViewEnvironmentWithOptionsInternal, the documented export of
// EmbeddedBrowserWebView.dll that WebView2Loader itself calls. If a
// WebView2Loader.dll lies next to the program, its public
// CreateCoreWebView2EnvironmentWithOptions is used instead.
//
// Interface method numbers follow WebView2.idl (vtable order, IUnknown first).
package webview2

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"
)

var (
	user32   = syscall.NewLazyDLL("user32.dll")
	kernel32 = syscall.NewLazyDLL("kernel32.dll")
	ole32    = syscall.NewLazyDLL("ole32.dll")
	gdi32    = syscall.NewLazyDLL("gdi32.dll")
	dwmapi   = syscall.NewLazyDLL("dwmapi.dll")
	advapi32 = syscall.NewLazyDLL("advapi32.dll")

	pRegisterClassExW      = user32.NewProc("RegisterClassExW")
	pCreateWindowExW       = user32.NewProc("CreateWindowExW")
	pDefWindowProcW        = user32.NewProc("DefWindowProcW")
	pDestroyWindow         = user32.NewProc("DestroyWindow")
	pShowWindow            = user32.NewProc("ShowWindow")
	pSetForegroundWindow   = user32.NewProc("SetForegroundWindow")
	pGetMessageW           = user32.NewProc("GetMessageW")
	pTranslateMessage      = user32.NewProc("TranslateMessage")
	pDispatchMessageW      = user32.NewProc("DispatchMessageW")
	pPostQuitMessage       = user32.NewProc("PostQuitMessage")
	pPostMessageW          = user32.NewProc("PostMessageW")
	pGetClientRect         = user32.NewProc("GetClientRect")
	pSetWindowPos          = user32.NewProc("SetWindowPos")
	pLoadImageW            = user32.NewProc("LoadImageW")
	pLoadCursorW           = user32.NewProc("LoadCursorW")
	pSetTimer              = user32.NewProc("SetTimer")
	pKillTimer             = user32.NewProc("KillTimer")
	pGetDpiForWindow       = user32.NewProc("GetDpiForWindow")
	pSystemParametersInfoW = user32.NewProc("SystemParametersInfoW")
	pGetModuleHandleW      = kernel32.NewProc("GetModuleHandleW")
	pLoadLibraryExW        = kernel32.NewProc("LoadLibraryExW")
	pGetProcAddress        = kernel32.NewProc("GetProcAddress")
	pCoInitializeEx        = ole32.NewProc("CoInitializeEx")
	pCoTaskMemFree         = ole32.NewProc("CoTaskMemFree")
	pCreateSolidBrush      = gdi32.NewProc("CreateSolidBrush")
	pDwmSetWindowAttribute = dwmapi.NewProc("DwmSetWindowAttribute")
	pRegOpenKeyExW         = advapi32.NewProc("RegOpenKeyExW")
	pRegQueryValueExW      = advapi32.NewProc("RegQueryValueExW")
	pRegCloseKey           = advapi32.NewProc("RegCloseKey")
)

const (
	wmDestroy       = 0x0002
	wmSize          = 0x0005
	wmMove          = 0x0003
	wmSetFocus      = 0x0007
	wmClose         = 0x0010
	wmGetMinMaxInfo = 0x0024
	wmTimer         = 0x0113
	wmDpiChanged    = 0x02E0
	wmApp           = 0x8000

	wsOverlappedWindow = 0x00CF0000
	swShowNormal       = 1
	swHide             = 0

	timerFail = 1
)

// Options describe the window.
type Options struct {
	Title     string
	URL       string
	Width     int // at 96 dpi
	Height    int
	MinWidth  int
	MinHeight int
	DataDir   string // WebView2 user data folder (must be writable)
	Dark      bool   // dark title bar and background before the page paints
	Hidden    bool   // never show the window (probe)
	Timeout   time.Duration
}

// Window is a running native window.
type Window struct {
	hwnd   uintptr
	done   chan struct{}
	pid    uint32
	closed sync.Once
}

func (w *Window) Done() <-chan struct{} { return w.done }

// Close asks the window to close; safe from any goroutine.
func (w *Window) Close() {
	w.closed.Do(func() { pPostMessageW.Call(w.hwnd, wmClose, 0, 0) })
}

// BrowserPID is the WebView2 browser process id.
func (w *Window) BrowserPID() uint32 { return w.pid }

// ---------------------------------------------------------------- COM helpers

func comCall(obj unsafe.Pointer, index int, args ...uintptr) uintptr {
	vtbl := *(*unsafe.Pointer)(obj)
	fn := *(*uintptr)(unsafe.Add(vtbl, uintptr(index)*unsafe.Sizeof(uintptr(0))))
	r, _, _ := syscall.SyscallN(fn, append([]uintptr{uintptr(obj)}, args...)...)
	return r
}

func failed(hr uintptr) bool { return int32(uint32(hr)) < 0 }

// handler is a COM object we implement: IUnknown + Invoke.
type handler struct {
	vtbl *[4]uintptr
}

var (
	cbQI      = syscall.NewCallback(func(this unsafe.Pointer, riid unsafe.Pointer, ppv *unsafe.Pointer) uintptr { *ppv = this; return 0 })
	cbAddRef  = syscall.NewCallback(func(this unsafe.Pointer) uintptr { return 1 })
	cbRelease = syscall.NewCallback(func(this unsafe.Pointer) uintptr { return 1 })
	keepAlive []*handler
)

func newHandler(invoke uintptr) *handler {
	h := &handler{vtbl: &[4]uintptr{cbQI, cbAddRef, cbRelease, invoke}}
	keepAlive = append(keepAlive, h)
	return h
}

// ---------------------------------------------------------------- runtime lookup

const runtimeClient = `{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`

func regString(root uintptr, path, name string) string {
	var key uintptr
	p, _ := syscall.UTF16PtrFromString(path)
	if r, _, _ := pRegOpenKeyExW.Call(root, uintptr(unsafe.Pointer(p)), 0, 0x20019, uintptr(unsafe.Pointer(&key))); r != 0 {
		return ""
	}
	defer pRegCloseKey.Call(key)
	n, _ := syscall.UTF16PtrFromString(name)
	buf := make([]uint16, 1024)
	size := uint32(len(buf) * 2)
	var typ uint32
	if r, _, _ := pRegQueryValueExW.Call(key, uintptr(unsafe.Pointer(n)), 0, uintptr(unsafe.Pointer(&typ)), uintptr(unsafe.Pointer(&buf[0])), uintptr(unsafe.Pointer(&size))); r != 0 {
		return ""
	}
	return syscall.UTF16ToString(buf)
}

func archDir() string {
	switch runtime.GOARCH {
	case "arm64":
		return "arm64"
	case "386":
		return "x86"
	}
	return "x64"
}

func versionLess(a, b string) bool {
	pa, pb := strings.Split(a, "."), strings.Split(b, ".")
	for i := 0; i < len(pa) && i < len(pb); i++ {
		x, _ := strconv.Atoi(pa[i])
		y, _ := strconv.Atoi(pb[i])
		if x != y {
			return x < y
		}
	}
	return len(pa) < len(pb)
}

// Runtime returns the installed WebView2 runtime version and the full path
// of its EmbeddedBrowserWebView.dll.
func Runtime() (version, dll string, err error) {
	const hklm, hkcu = 0x80000002, 0x80000001
	type cand struct{ ver, dir string }
	var cands []cand
	for _, k := range []struct {
		root uintptr
		path string
	}{
		{hklm, `SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\` + runtimeClient},
		{hklm, `SOFTWARE\Microsoft\EdgeUpdate\Clients\` + runtimeClient},
		{hkcu, `Software\Microsoft\EdgeUpdate\Clients\` + runtimeClient},
	} {
		v := regString(k.root, k.path, "pv")
		if v == "" || v == "0.0.0.0" {
			continue
		}
		if loc := regString(k.root, k.path, "location"); loc != "" {
			cands = append(cands, cand{v, filepath.Join(loc, v)})
		}
		for _, base := range []string{os.Getenv("ProgramFiles(x86)"), os.Getenv("ProgramFiles"), os.Getenv("LOCALAPPDATA")} {
			if base != "" {
				cands = append(cands, cand{v, filepath.Join(base, "Microsoft", "EdgeWebView", "Application", v)})
			}
		}
	}
	// any version folder on disk, newest first (registry can lag behind)
	for _, base := range []string{os.Getenv("ProgramFiles(x86)"), os.Getenv("ProgramFiles"), os.Getenv("LOCALAPPDATA")} {
		if base == "" {
			continue
		}
		app := filepath.Join(base, "Microsoft", "EdgeWebView", "Application")
		ents, _ := os.ReadDir(app)
		var vers []string
		for _, e := range ents {
			if e.IsDir() && e.Name() != "" && e.Name()[0] >= '0' && e.Name()[0] <= '9' {
				vers = append(vers, e.Name())
			}
		}
		sort.Slice(vers, func(i, j int) bool { return versionLess(vers[j], vers[i]) })
		for _, v := range vers {
			cands = append(cands, cand{v, filepath.Join(app, v)})
		}
	}
	for _, c := range cands {
		p := filepath.Join(c.dir, "EBWebView", archDir(), "EmbeddedBrowserWebView.dll")
		if _, e := os.Stat(p); e == nil {
			return c.ver, p, nil
		}
	}
	return "", "", errors.New("среда выполнения WebView2 не установлена")
}

func loadLibrary(path string) (uintptr, error) {
	p, _ := syscall.UTF16PtrFromString(path)
	h, _, e := pLoadLibraryExW.Call(uintptr(unsafe.Pointer(p)), 0, 0x8) // LOAD_WITH_ALTERED_SEARCH_PATH
	if h == 0 {
		return 0, fmt.Errorf("не удалось загрузить %s: %v", filepath.Base(path), e)
	}
	return h, nil
}

func procAddr(mod uintptr, name string) uintptr {
	n, _ := syscall.BytePtrFromString(name)
	a, _, _ := pGetProcAddress.Call(mod, uintptr(unsafe.Pointer(n)))
	return a
}

// createEnvironment starts the asynchronous environment creation.
func createEnvironment(dataDir string, done *handler) error {
	dd, _ := syscall.UTF16PtrFromString(dataDir)
	exe, _ := os.Executable()
	if loader := filepath.Join(filepath.Dir(exe), "WebView2Loader.dll"); exe != "" {
		if _, err := os.Stat(loader); err == nil {
			if mod, err := loadLibrary(loader); err == nil {
				if fn := procAddr(mod, "CreateCoreWebView2EnvironmentWithOptions"); fn != 0 {
					hr, _, _ := syscall.SyscallN(fn, 0, uintptr(unsafe.Pointer(dd)), 0, uintptr(unsafe.Pointer(done)))
					if !failed(hr) {
						return nil
					}
				}
			}
		}
	}
	_, dll, err := Runtime()
	if err != nil {
		return err
	}
	mod, err := loadLibrary(dll)
	if err != nil {
		return err
	}
	fn := procAddr(mod, "CreateWebViewEnvironmentWithOptionsInternal")
	if fn == 0 {
		return errors.New("в среде WebView2 нет функции создания окружения")
	}
	// checkRunningInstance = true, runtimeType = 0 (evergreen)
	hr, _, _ := syscall.SyscallN(fn, 1, 0, uintptr(unsafe.Pointer(dd)), 0, uintptr(unsafe.Pointer(done)))
	if failed(hr) {
		return fmt.Errorf("WebView2: ошибка 0x%08X", uint32(hr))
	}
	return nil
}

// ---------------------------------------------------------------- window

type rect struct{ Left, Top, Right, Bottom int32 }

type msg struct {
	Hwnd    uintptr
	Message uint32
	WParam  uintptr
	LParam  uintptr
	Time    uint32
	Pt      [2]int32
	Private uint32
}

type wndClassEx struct {
	Size       uint32
	Style      uint32
	WndProc    uintptr
	ClsExtra   int32
	WndExtra   int32
	Instance   uintptr
	Icon       uintptr
	Cursor     uintptr
	Background uintptr
	MenuName   *uint16
	ClassName  *uint16
	IconSm     uintptr
}

// the single window of the process
type state struct {
	o          Options
	hwnd       uintptr
	controller unsafe.Pointer
	webview    unsafe.Pointer
	ready      chan error
	readyOnce  sync.Once
	shown      bool
	pid        uint32
}

var cur *state

func (s *state) finish(err error) {
	s.readyOnce.Do(func() { s.ready <- err })
}

func (s *state) resize() {
	if s.controller == nil {
		return
	}
	var r rect
	pGetClientRect.Call(s.hwnd, uintptr(unsafe.Pointer(&r)))
	// put_Bounds takes RECT by value: x64 passes a 16-byte structure by
	// reference, ARM64 passes it in two registers.
	if runtime.GOARCH == "arm64" {
		lo := uint64(uint32(r.Left)) | uint64(uint32(r.Top))<<32
		hi := uint64(uint32(r.Right)) | uint64(uint32(r.Bottom))<<32
		comCall(s.controller, 6, uintptr(lo), uintptr(hi))
		return
	}
	comCall(s.controller, 6, uintptr(unsafe.Pointer(&r)))
}

var wndProc = syscall.NewCallback(func(hwnd, m, wp uintptr, lp unsafe.Pointer) uintptr {
	s := cur
	switch m {
	case wmSize:
		if s != nil {
			s.resize()
		}
		return 0
	case wmMove:
		if s != nil && s.controller != nil {
			comCall(s.controller, 23) // NotifyParentWindowPositionChanged
		}
	case wmSetFocus:
		if s != nil && s.controller != nil {
			comCall(s.controller, 12, 0) // MoveFocus(PROGRAMMATIC)
		}
	case wmGetMinMaxInfo:
		if s != nil && s.o.MinWidth > 0 {
			dpi := dpiOf(hwnd)
			mm := (*[10]int32)(lp) // MINMAXINFO: ptMinTrackSize at [6],[7]
			mm[6] = int32(s.o.MinWidth * dpi / 96)
			mm[7] = int32(s.o.MinHeight * dpi / 96)
		}
		return 0
	case wmDpiChanged:
		r := (*rect)(lp)
		pSetWindowPos.Call(hwnd, 0, uintptr(r.Left), uintptr(r.Top), uintptr(r.Right-r.Left), uintptr(r.Bottom-r.Top), 0x0014) // NOZORDER|NOACTIVATE
		return 0
	case wmTimer:
		if wp == timerFail && s != nil {
			pKillTimer.Call(hwnd, timerFail)
			if !s.shown {
				s.finish(errors.New("WebView2 не ответил вовремя"))
				pDestroyWindow.Call(hwnd)
			}
		}
		return 0
	case wmClose:
		pDestroyWindow.Call(hwnd)
		return 0
	case wmDestroy:
		if s != nil && s.controller != nil {
			comCall(s.controller, 24) // Close
			s.controller = nil
		}
		pPostQuitMessage.Call(0)
		return 0
	}
	r, _, _ := pDefWindowProcW.Call(hwnd, m, wp, uintptr(lp))
	return r
})

func dpiOf(hwnd uintptr) int {
	if pGetDpiForWindow.Find() == nil {
		if d, _, _ := pGetDpiForWindow.Call(hwnd); d > 0 {
			return int(d)
		}
	}
	return 96
}

var (
	envDone = newHandler(syscall.NewCallback(func(this unsafe.Pointer, hr uintptr, env unsafe.Pointer) uintptr {
		s := cur
		if s == nil {
			return 0
		}
		if failed(hr) || env == nil {
			s.finish(fmt.Errorf("WebView2: окружение не создано (0x%08X)", uint32(hr)))
			pDestroyWindow.Call(s.hwnd)
			return 0
		}
		comCall(env, 1) // AddRef: we keep it while the controller is created
		if r := comCall(env, 3, s.hwnd, uintptr(unsafe.Pointer(ctrlDone))); failed(r) {
			s.finish(fmt.Errorf("WebView2: контроллер не создан (0x%08X)", uint32(r)))
			pDestroyWindow.Call(s.hwnd)
		}
		return 0
	}))

	ctrlDone = newHandler(syscall.NewCallback(func(this unsafe.Pointer, hr uintptr, ctrl unsafe.Pointer) uintptr {
		s := cur
		if s == nil {
			return 0
		}
		if failed(hr) || ctrl == nil {
			s.finish(fmt.Errorf("WebView2: контроллер не создан (0x%08X)", uint32(hr)))
			pDestroyWindow.Call(s.hwnd)
			return 0
		}
		comCall(ctrl, 1) // AddRef
		s.controller = ctrl
		var wv unsafe.Pointer
		if r := comCall(ctrl, 25, uintptr(unsafe.Pointer(&wv))); failed(r) || wv == nil { // get_CoreWebView2
			s.finish(errors.New("WebView2: нет объекта страницы"))
			pDestroyWindow.Call(s.hwnd)
			return 0
		}
		s.webview = wv
		var settings unsafe.Pointer
		if r := comCall(wv, 3, uintptr(unsafe.Pointer(&settings))); !failed(r) && settings != nil { // get_Settings
			// slot = 3 (IUnknown) + position in ICoreWebView2Settings
			comCall(settings, 10, 0) // put_IsStatusBarEnabled(false)
			comCall(settings, 18, 0) // put_IsZoomControlEnabled(false)
			if os.Getenv("PROSTOR_DEVTOOLS") == "" {
				comCall(settings, 12, 0) // put_AreDevToolsEnabled(false)
			}
			comCall(settings, 2) // Release
		}
		var pid uint32
		comCall(wv, 37, uintptr(unsafe.Pointer(&pid))) // get_BrowserProcessId
		s.pid = pid
		var token int64
		comCall(wv, 15, uintptr(unsafe.Pointer(navDone)), uintptr(unsafe.Pointer(&token))) // add_NavigationCompleted
		comCall(ctrl, 4, 1)                                                                // put_IsVisible(true)
		s.resize()
		u, _ := syscall.UTF16PtrFromString(s.o.URL)
		if r := comCall(wv, 5, uintptr(unsafe.Pointer(u))); failed(r) { // Navigate
			s.finish(fmt.Errorf("WebView2: страница не открылась (0x%08X)", uint32(r)))
			pDestroyWindow.Call(s.hwnd)
		}
		return 0
	}))

	navDone = newHandler(syscall.NewCallback(func(this, sender, args unsafe.Pointer) uintptr {
		s := cur
		if s == nil || s.shown {
			return 0
		}
		s.shown = true
		pKillTimer.Call(s.hwnd, timerFail)
		if !s.o.Hidden {
			pShowWindow.Call(s.hwnd, swShowNormal)
			pSetForegroundWindow.Call(s.hwnd)
			if s.controller != nil {
				comCall(s.controller, 12, 0)
			}
		}
		s.finish(nil)
		return 0
	}))
)

// Start opens the window on its own thread and returns once the page has
// loaded, or an error if WebView2 cannot be used (nothing is shown then).
func Start(o Options) (*Window, error) {
	if o.Timeout == 0 {
		o.Timeout = 20 * time.Second
	}
	if cur != nil {
		return nil, errors.New("окно уже открыто")
	}
	s := &state{o: o, ready: make(chan error, 1)}
	w := &Window{done: make(chan struct{})}
	go func() {
		runtime.LockOSThread()
		defer close(w.done)
		hr, _, _ := pCoInitializeEx.Call(0, 2) // COINIT_APARTMENTTHREADED
		if failed(hr) {
			s.finish(fmt.Errorf("COM: 0x%08X", uint32(hr)))
			return
		}
		cur = s
		if err := s.create(); err != nil {
			s.finish(err)
			return
		}
		w.hwnd = s.hwnd
		if err := os.MkdirAll(o.DataDir, 0o755); err != nil {
			s.finish(err)
			pDestroyWindow.Call(s.hwnd)
		} else {
			if err := createEnvironment(o.DataDir, envDone); err != nil {
				s.finish(err)
				pDestroyWindow.Call(s.hwnd)
			} else {
				pSetTimer.Call(s.hwnd, timerFail, uintptr(o.Timeout/time.Millisecond), 0)
			}
		}
		var m msg
		for {
			r, _, _ := pGetMessageW.Call(uintptr(unsafe.Pointer(&m)), 0, 0, 0)
			if int32(r) <= 0 {
				break
			}
			pTranslateMessage.Call(uintptr(unsafe.Pointer(&m)))
			pDispatchMessageW.Call(uintptr(unsafe.Pointer(&m)))
		}
		s.finish(errors.New("окно закрыто"))
	}()
	if err := <-s.ready; err != nil {
		<-w.done
		cur = nil
		return nil, err
	}
	w.pid = s.pid
	return w, nil
}

func (s *state) create() error {
	inst, _, _ := pGetModuleHandleW.Call(0)
	cls, _ := syscall.UTF16PtrFromString("ProstorWindow")
	title, _ := syscall.UTF16PtrFromString(s.o.Title)
	bg := uintptr(0x00FFFFFF)
	if s.o.Dark {
		bg = 0x001B1716 // RGB(22,23,27) as 0x00BBGGRR
	}
	brush, _, _ := pCreateSolidBrush.Call(bg)
	icon, _, _ := pLoadImageW.Call(inst, 1, 1, 0, 0, 0x40) // IDI 1, IMAGE_ICON, LR_DEFAULTSIZE
	iconSm, _, _ := pLoadImageW.Call(inst, 1, 1, 16, 16, 0)
	cursor, _, _ := pLoadCursorW.Call(0, 32512) // IDC_ARROW
	wc := wndClassEx{Style: 0x3, WndProc: wndProc, Instance: inst, Icon: icon, Cursor: cursor, Background: brush, ClassName: cls, IconSm: iconSm}
	wc.Size = uint32(unsafe.Sizeof(wc))
	pRegisterClassExW.Call(uintptr(unsafe.Pointer(&wc))) // fails harmlessly if already registered

	// size for the primary monitor's dpi, centred in the work area
	var work rect
	pSystemParametersInfoW.Call(0x30, 0, uintptr(unsafe.Pointer(&work)), 0) // SPI_GETWORKAREA
	x, y, w, h := int32(-32000), int32(-32000), int32(s.o.Width), int32(s.o.Height)
	hwnd, _, e := pCreateWindowExW.Call(0, uintptr(unsafe.Pointer(cls)), uintptr(unsafe.Pointer(title)), wsOverlappedWindow,
		uintptr(x), uintptr(y), uintptr(w), uintptr(h), 0, 0, inst, 0)
	if hwnd == 0 {
		return fmt.Errorf("окно не создано: %v", e)
	}
	s.hwnd = hwnd
	dpi := int32(dpiOf(hwnd))
	w, h = int32(s.o.Width)*dpi/96, int32(s.o.Height)*dpi/96
	if ww, wh := work.Right-work.Left, work.Bottom-work.Top; ww > 0 && wh > 0 {
		if w > ww {
			w = ww
		}
		if h > wh {
			h = wh
		}
		x, y = work.Left+(ww-w)/2, work.Top+(wh-h)/2
	} else {
		x, y = 80, 60
	}
	pSetWindowPos.Call(hwnd, 0, uintptr(x), uintptr(y), uintptr(w), uintptr(h), 0x0014)
	if s.o.Dark {
		on := int32(1)
		if r, _, _ := pDwmSetWindowAttribute.Call(hwnd, 20, uintptr(unsafe.Pointer(&on)), 4); r != 0 {
			pDwmSetWindowAttribute.Call(hwnd, 19, uintptr(unsafe.Pointer(&on)), 4) // older Windows 10
		}
	}
	return nil
}

// Version returns the installed runtime version ("" when there is none).
func Version() string {
	v, _, err := Runtime()
	if err != nil {
		return ""
	}
	return v
}

var _ = pCoTaskMemFree
var _ = swHide
var _ = wmApp
