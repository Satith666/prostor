//go:build windows

// Prostor setup: a small native Win32 wizard that installs Prostor.exe into
// Program Files, creates shortcuts and registers the uninstaller.
// Usage: Prostor-Setup.exe [/S] [/D=C:\path]   (/S = silent install)
package main

import (
	"bytes"
	"compress/gzip"
	_ "embed"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"

	"prostor/internal/winapi"
)

//go:embed payload/Prostor.exe.gz
var payload []byte

//go:embed check.cmd
var checkScript []byte

const checkName = "Проверка Prostor"

var version = "1.5.0"

const (
	appName      = "Prostor"
	exeName      = "Prostor.exe"
	uninstallKey = `Software\Microsoft\Windows\CurrentVersion\Uninstall\Prostor`
)

func init() { runtime.LockOSThread() } // the window lives on the main OS thread

// ============================================================ install logic

type options struct {
	Dir     string
	Desktop bool
}

type installInfo struct {
	Version   string   `json:"version"`
	Dir       string   `json:"dir"`
	Scope     string   `json:"scope"`
	Shortcuts []string `json:"shortcuts"`
}

func defaultDir() string {
	if p, ok := winapi.RegGetString(winapi.HKLM, uninstallKey, "InstallLocation"); ok && p != "" {
		return strings.TrimRight(p, `\`)
	}
	pf := os.Getenv("ProgramW6432")
	if pf == "" {
		pf = os.Getenv("ProgramFiles")
	}
	if pf == "" {
		pf = `C:\Program Files`
	}
	return filepath.Join(pf, appName)
}

func installedVersion() string {
	v, _ := winapi.RegGetString(winapi.HKLM, uninstallKey, "DisplayVersion")
	return v
}

func hidden(c *exec.Cmd) *exec.Cmd { winapi.HideWindow(c); return c }

// psShortcut is the fallback when COM shortcut creation fails.
func psShortcut(lnk, target, args, workDir, desc string) error {
	q := func(s string) string { return "'" + strings.ReplaceAll(s, "'", "''") + "'" }
	script := fmt.Sprintf(`$s=(New-Object -ComObject WScript.Shell).CreateShortcut(%s);$s.TargetPath=%s;$s.Arguments=%s;$s.WorkingDirectory=%s;$s.Description=%s;$s.IconLocation=%s;$s.Save()`,
		q(lnk), q(target), q(args), q(workDir), q(desc), q(target+",0"))
	u := make([]byte, 0, len(script)*2)
	for _, r := range script {
		u = append(u, byte(r), byte(r>>8))
	}
	return hidden(exec.Command("powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", base64.StdEncoding.EncodeToString(u))).Run()
}

func shortcut(lnk, target, args, dir, desc string) error {
	os.MkdirAll(filepath.Dir(lnk), 0o755)
	if err := winapi.CreateShortcut(lnk, target, args, dir, target+",0", desc); err == nil {
		return nil
	}
	return psShortcut(lnk, target, args, dir, desc)
}

const appDesc = "Очистка Windows и анализ занятого места"

// install performs the installation; progress(pct, text) reports each step.
func install(o options, progress func(int, string)) (string, error) {
	dir := filepath.Clean(strings.TrimSpace(o.Dir))
	if !filepath.IsAbs(dir) || len(dir) < 4 {
		return "", errors.New("укажите полный путь к папке, например C:\\Program Files\\Prostor")
	}
	exe := filepath.Join(dir, exeName)

	progress(5, "Закрываем запущенную копию Prostor…")
	if winapi.KillByName(exeName) > 0 {
		time.Sleep(800 * time.Millisecond)
	}

	progress(15, "Создаём папку…")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", fmt.Errorf("не удалось создать папку %s: %v", dir, err)
	}

	progress(25, "Распаковка программы…")
	zr, err := gzip.NewReader(bytes.NewReader(payload))
	if err != nil {
		return "", fmt.Errorf("установщик повреждён: %v", err)
	}
	tmp := exe + ".new"
	f, err := os.Create(tmp)
	if err != nil {
		return "", fmt.Errorf("нет доступа к папке %s: %v", dir, err)
	}
	n, err := io.Copy(f, zr)
	f.Close()
	if err != nil {
		os.Remove(tmp)
		return "", fmt.Errorf("не удалось записать файл: %v", err)
	}
	if err := os.Rename(tmp, exe); err != nil {
		// the old exe is locked: move it aside (allowed for running files) and retry
		old := exe + ".old"
		os.Remove(old)
		if err2 := os.Rename(exe, old); err2 == nil {
			winapi.DeleteOnReboot(old)
			err = os.Rename(tmp, exe)
		}
		if err != nil {
			os.Remove(tmp)
			return "", fmt.Errorf("файл %s занят. Закройте Prostor и попробуйте снова", exe)
		}
	}
	os.Remove(exe + ".old")

	progress(55, "Создаём ярлыки…")
	var shortcuts []string
	if pd := os.Getenv("ProgramData"); pd != "" {
		menu := filepath.Join(pd, `Microsoft\Windows\Start Menu\Programs`)
		lnk := filepath.Join(menu, appName+".lnk")
		if shortcut(lnk, exe, "", dir, appDesc) == nil {
			shortcuts = append(shortcuts, lnk)
		}
		lnk = filepath.Join(menu, checkName+".lnk")
		if shortcut(lnk, exe, "--selftest", dir, "Полная самопроверка Prostor с отчётом") == nil {
			shortcuts = append(shortcuts, lnk)
		}
	}
	os.WriteFile(filepath.Join(dir, checkName+".cmd"), checkScript, 0o644)
	if o.Desktop {
		desk := filepath.Join(os.Getenv("PUBLIC"), "Desktop")
		if os.Getenv("PUBLIC") == "" {
			desk = winapi.KnownFolder("desktop")
		}
		if desk != "" {
			lnk := filepath.Join(desk, appName+".lnk")
			if shortcut(lnk, exe, "", dir, appDesc) == nil {
				shortcuts = append(shortcuts, lnk)
			}
		}
	}

	progress(75, "Регистрируем программу в Windows…")
	info := installInfo{Version: version, Dir: dir, Scope: "machine", Shortcuts: shortcuts}
	if b, err := json.MarshalIndent(info, "", "  "); err == nil {
		os.WriteFile(filepath.Join(dir, "install.json"), b, 0o644)
	}
	k := winapi.HKLM
	q := `"` + exe + `"`
	vals := map[string]string{
		"DisplayName":          "Prostor — очистка и анализ диска",
		"DisplayVersion":       version,
		"Publisher":            appName,
		"DisplayIcon":          exe + ",0",
		"InstallLocation":      dir,
		"UninstallString":      q + " --uninstall",
		"QuietUninstallString": q + " --uninstall --quiet",
		"InstallDate":          time.Now().Format("20060102"),
		"Comments":             "Очистка Windows от мусора, анализ диска, дубликаты, автозагрузка",
	}
	for name, v := range vals {
		if err := winapi.RegSetString(k, uninstallKey, name, v); err != nil {
			return "", fmt.Errorf("не удалось записать в реестр: %v", err)
		}
	}
	winapi.RegSetDword(k, uninstallKey, "EstimatedSize", uint32(n/1024))
	winapi.RegSetDword(k, uninstallKey, "NoModify", 1)
	winapi.RegSetDword(k, uninstallKey, "NoRepair", 1)

	progress(100, "Готово")
	return exe, nil
}

// launchUnelevated starts the app as the logged-on user (not as admin):
// explorer.exe hands the request to the already running shell.
func launchUnelevated(exe string) {
	if err := exec.Command("explorer.exe", exe).Start(); err != nil {
		winapi.ShellRun(exe, "")
	}
}

// ============================================================ Win32 plumbing

var (
	user32   = syscall.NewLazyDLL("user32.dll")
	gdi32    = syscall.NewLazyDLL("gdi32.dll")
	kernel32 = syscall.NewLazyDLL("kernel32.dll")
	comctl32 = syscall.NewLazyDLL("comctl32.dll")
	shell32  = syscall.NewLazyDLL("shell32.dll")
	ole32    = syscall.NewLazyDLL("ole32.dll")

	pRegisterClassExW     = user32.NewProc("RegisterClassExW")
	pCreateWindowExW      = user32.NewProc("CreateWindowExW")
	pDefWindowProcW       = user32.NewProc("DefWindowProcW")
	pGetMessageW          = user32.NewProc("GetMessageW")
	pTranslateMessage     = user32.NewProc("TranslateMessage")
	pDispatchMessageW     = user32.NewProc("DispatchMessageW")
	pIsDialogMessageW     = user32.NewProc("IsDialogMessageW")
	pPostQuitMessage      = user32.NewProc("PostQuitMessage")
	pPostMessageW         = user32.NewProc("PostMessageW")
	pSendMessageW         = user32.NewProc("SendMessageW")
	pShowWindow           = user32.NewProc("ShowWindow")
	pUpdateWindow         = user32.NewProc("UpdateWindow")
	pDestroyWindow        = user32.NewProc("DestroyWindow")
	pSetWindowTextW       = user32.NewProc("SetWindowTextW")
	pGetWindowTextW       = user32.NewProc("GetWindowTextW")
	pGetWindowTextLengthW = user32.NewProc("GetWindowTextLengthW")
	pEnableWindow         = user32.NewProc("EnableWindow")
	pSetFocus             = user32.NewProc("SetFocus")
	pLoadCursorW          = user32.NewProc("LoadCursorW")
	pLoadImageW           = user32.NewProc("LoadImageW")
	pBeginPaint           = user32.NewProc("BeginPaint")
	pEndPaint             = user32.NewProc("EndPaint")
	pFillRect             = user32.NewProc("FillRect")
	pDrawTextW            = user32.NewProc("DrawTextW")
	pDrawIconEx           = user32.NewProc("DrawIconEx")
	pInvalidateRect       = user32.NewProc("InvalidateRect")
	pGetDC                = user32.NewProc("GetDC")
	pReleaseDC            = user32.NewProc("ReleaseDC")
	pAdjustWindowRectEx   = user32.NewProc("AdjustWindowRectEx")
	pGetSystemMetrics     = user32.NewProc("GetSystemMetrics")
	pMessageBoxW          = user32.NewProc("MessageBoxW")
	pSetForegroundWindow  = user32.NewProc("SetForegroundWindow")

	pCreateFontW      = gdi32.NewProc("CreateFontW")
	pCreateSolidBrush = gdi32.NewProc("CreateSolidBrush")
	pSelectObject     = gdi32.NewProc("SelectObject")
	pSetTextColor     = gdi32.NewProc("SetTextColor")
	pSetBkMode        = gdi32.NewProc("SetBkMode")
	pSetBkColor       = gdi32.NewProc("SetBkColor")
	pGetDeviceCaps    = gdi32.NewProc("GetDeviceCaps")

	pGetModuleHandleW     = kernel32.NewProc("GetModuleHandleW")
	pInitCommonControlsEx = comctl32.NewProc("InitCommonControlsEx")
	pSHBrowseForFolderW   = shell32.NewProc("SHBrowseForFolderW")
	pSHGetPathFromIDListW = shell32.NewProc("SHGetPathFromIDListW")
	pCoInitializeEx       = ole32.NewProc("CoInitializeEx")
	pCoTaskMemFree        = ole32.NewProc("CoTaskMemFree")
)

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

type point struct{ X, Y int32 }

type msgT struct {
	Hwnd    uintptr
	Message uint32
	WParam  uintptr
	LParam  uintptr
	Time    uint32
	Pt      point
	Private uint32
}

type rect struct{ Left, Top, Right, Bottom int32 }

type paintStruct struct {
	Hdc       uintptr
	Erase     int32
	RcPaint   rect
	Restore   int32
	IncUpdate int32
	Reserved  [32]byte
}

type browseInfo struct {
	Owner       uintptr
	Root        uintptr
	DisplayName *uint16
	Title       *uint16
	Flags       uint32
	Callback    uintptr
	LParam      uintptr
	Image       int32
}

const (
	wmDestroy        = 0x0002
	wmPaint          = 0x000F
	wmClose          = 0x0010
	wmEraseBkgnd     = 0x0014
	wmSetFont        = 0x0030
	wmCommand        = 0x0111
	wmCtlColorStatic = 0x0138
	wmCtlColorBtn    = 0x0135
	wmApp            = 0x8000
	wmProgress       = wmApp + 1
	wmDone           = wmApp + 2

	wsChild        = 0x40000000
	wsVisible      = 0x10000000
	wsTabStop      = 0x00010000
	wsCaption      = 0x00C00000
	wsSysMenu      = 0x00080000
	wsMinimizeBox  = 0x00020000
	wsClipChildren = 0x02000000
	wsExClientEdge = 0x00000200

	bsDefPushButton = 1
	bsAutoCheckbox  = 3
	esAutoHScroll   = 0x80
	ssNoPrefix      = 0x80

	bmGetCheck = 0x00F0
	bmSetCheck = 0x00F1

	pbmSetPos     = 0x0402
	pbmSetRange32 = 0x0406

	idOK      = 1
	idCancel  = 2
	idEdit    = 101
	idBrowse  = 102
	idDesktop = 103
	idLaunch  = 104
	idProg    = 105
	idHead    = 106
	idText    = 107
	idLabel   = 108
	idNote    = 109
	idStatus  = 110
)

func u16(s string) *uint16 { p, _ := syscall.UTF16PtrFromString(s); return p }
func up(s string) uintptr  { return uintptr(unsafe.Pointer(u16(s))) }
func rgb(r, g, b uint8) uintptr {
	return uintptr(r) | uintptr(g)<<8 | uintptr(b)<<16
}

// ============================================================ wizard window

type ui struct {
	hwnd, inst                      uintptr
	scale                           float64
	fBody, fHead, fTitle, fSub      uintptr
	brWhite, brHead, brFoot, brLine uintptr
	icon                            uintptr
	ctl                             map[int]uintptr
	page                            string // welcome | progress | done | error
	busy                            bool
	exe                             string
	mu                              sync.Mutex
	status                          string
	errText                         string
	upgradeFrom                     string
}

var w = &ui{ctl: map[int]uintptr{}}

const clientW, clientH = 600, 430

func (u *ui) s(v int) int32 { return int32(float64(v)*u.scale + 0.5) }

func (u *ui) font(pt float64, weight int) uintptr { return u.fontFace(pt, weight, "Segoe UI") }

func (u *ui) fontFace(pt float64, weight int, face string) uintptr {
	h := -int32(pt*u.scale*96/72 + 0.5)
	f, _, _ := pCreateFontW.Call(uintptr(h), 0, 0, 0, uintptr(weight), 0, 0, 0, 1, 0, 0, 5, 0, up(face))
	return f
}

func (u *ui) child(id int, class, text string, style uint32, ex uint32, x, y, cw, ch int, font uintptr) uintptr {
	h, _, _ := pCreateWindowExW.Call(uintptr(ex), up(class), up(text), uintptr(wsChild|wsVisible|style),
		uintptr(u.s(x)), uintptr(u.s(y)), uintptr(u.s(cw)), uintptr(u.s(ch)), u.hwnd, uintptr(id), u.inst, 0)
	pSendMessageW.Call(h, wmSetFont, font, 1)
	u.ctl[id] = h
	return h
}

func setText(h uintptr, s string) { pSetWindowTextW.Call(h, up(s)) }
func getText(h uintptr) string {
	n, _, _ := pGetWindowTextLengthW.Call(h)
	buf := make([]uint16, n+1)
	pGetWindowTextW.Call(h, uintptr(unsafe.Pointer(&buf[0])), n+1)
	return syscall.UTF16ToString(buf)
}
func show(h uintptr, on bool) {
	v := uintptr(0)
	if on {
		v = 5
	}
	pShowWindow.Call(h, v)
}
func checked(h uintptr) bool { r, _, _ := pSendMessageW.Call(h, bmGetCheck, 0, 0); return r == 1 }

func (u *ui) create() {
	u.inst, _, _ = pGetModuleHandleW.Call(0)
	hdc, _, _ := pGetDC.Call(0)
	dpi, _, _ := pGetDeviceCaps.Call(hdc, 90) // LOGPIXELSY
	pReleaseDC.Call(0, hdc)
	if dpi == 0 {
		dpi = 96
	}
	u.scale = float64(dpi) / 96

	icc := [2]uint32{8, 0x20 | 0x4000} // ICC_PROGRESS_CLASS | ICC_STANDARD_CLASSES
	pInitCommonControlsEx.Call(uintptr(unsafe.Pointer(&icc[0])))

	u.fBody = u.font(9.5, 400)
	u.fHead = u.font(13, 600)
	u.fTitle = u.fontFace(22, 400, "Bahnschrift")
	u.fSub = u.font(9.5, 400)
	u.brWhite, _, _ = pCreateSolidBrush.Call(rgb(255, 255, 255))
	u.brHead, _, _ = pCreateSolidBrush.Call(rgb(0x13, 0x14, 0x17))
	u.brFoot, _, _ = pCreateSolidBrush.Call(rgb(0xF5, 0xF6, 0xF9))
	u.brLine, _, _ = pCreateSolidBrush.Call(rgb(0xE3, 0xE6, 0xEC))
	u.icon, _, _ = pLoadImageW.Call(u.inst, 1, 1, uintptr(u.s(48)), uintptr(u.s(48)), 0)
	big, _, _ := pLoadImageW.Call(u.inst, 1, 1, uintptr(u.s(32)), uintptr(u.s(32)), 0)
	small, _, _ := pLoadImageW.Call(u.inst, 1, 1, uintptr(u.s(16)), uintptr(u.s(16)), 0)
	cursor, _, _ := pLoadCursorW.Call(0, 32512) // IDC_ARROW

	cls := u16("ProstorSetupWnd")
	wc := wndClassEx{
		Size: uint32(unsafe.Sizeof(wndClassEx{})), WndProc: syscall.NewCallback(wndProc), Instance: u.inst,
		Icon: big, IconSm: small, Cursor: cursor, Background: u.brWhite, ClassName: cls,
	}
	pRegisterClassExW.Call(uintptr(unsafe.Pointer(&wc)))

	style := uint32(wsCaption | wsSysMenu | wsMinimizeBox | wsClipChildren)
	r := rect{0, 0, u.s(clientW), u.s(clientH)}
	pAdjustWindowRectEx.Call(uintptr(unsafe.Pointer(&r)), uintptr(style), 0, 0)
	ww, wh := r.Right-r.Left, r.Bottom-r.Top
	sw, _, _ := pGetSystemMetrics.Call(0)
	sh, _, _ := pGetSystemMetrics.Call(1)
	x, y := (int32(sw)-ww)/2, (int32(sh)-wh)/2
	title := "Установка Prostor " + version
	u.hwnd, _, _ = pCreateWindowExW.Call(0, uintptr(unsafe.Pointer(cls)), up(title), uintptr(style),
		uintptr(x), uintptr(y), uintptr(ww), uintptr(wh), 0, 0, u.inst, 0)

	// --- controls
	const L = 32
	u.child(idHead, "STATIC", "", ssNoPrefix, 0, L, 116, 536, 30, u.fHead)
	u.child(idText, "STATIC", "", ssNoPrefix, 0, L, 150, 536, 54, u.fBody)
	u.child(idLabel, "STATIC", "Папка установки:", ssNoPrefix, 0, L, 210, 536, 20, u.fBody)
	u.child(idEdit, "EDIT", defaultDir(), wsTabStop|esAutoHScroll, wsExClientEdge, L, 232, 428, 28, u.fBody)
	u.child(idBrowse, "BUTTON", "Обзор…", wsTabStop, 0, 468, 231, 100, 30, u.fBody)
	h := u.child(idDesktop, "BUTTON", "Создать ярлык на рабочем столе", wsTabStop|bsAutoCheckbox, 0, L, 276, 400, 24, u.fBody)
	pSendMessageW.Call(h, bmSetCheck, 1, 0)
	u.child(idNote, "STATIC", "Ярлык также появится в меню «Пуск». Программе потребуется около 10 МБ.", ssNoPrefix, 0, L, 308, 536, 40, u.fBody)
	u.child(idStatus, "STATIC", "", ssNoPrefix, 0, L, 160, 536, 22, u.fBody)
	u.child(idProg, "msctls_progress32", "", 0, 0, L, 192, 536, 10, 0)
	pSendMessageW.Call(u.ctl[idProg], pbmSetRange32, 0, 100)
	h = u.child(idLaunch, "BUTTON", "Запустить Prostor", wsTabStop|bsAutoCheckbox, 0, L, 222, 400, 24, u.fBody)
	pSendMessageW.Call(h, bmSetCheck, 1, 0)
	u.child(idOK, "BUTTON", "Установить", wsTabStop|bsDefPushButton, 0, 330, clientH-46, 130, 32, u.fBody)
	u.child(idCancel, "BUTTON", "Отмена", wsTabStop, 0, 468, clientH-46, 100, 32, u.fBody)

	u.upgradeFrom = installedVersion()
	u.setPage("welcome")
	pShowWindow.Call(u.hwnd, 1)
	pUpdateWindow.Call(u.hwnd)
	pSetForegroundWindow.Call(u.hwnd)
	pSetFocus.Call(u.ctl[idOK])
}

func (u *ui) setPage(p string) {
	u.page = p
	welcome := p == "welcome"
	for _, id := range []int{idLabel, idEdit, idBrowse, idDesktop, idNote} {
		show(u.ctl[id], welcome)
	}
	show(u.ctl[idText], p != "progress")
	show(u.ctl[idStatus], p == "progress")
	show(u.ctl[idProg], p == "progress")
	show(u.ctl[idLaunch], p == "done")
	show(u.ctl[idCancel], welcome)
	show(u.ctl[idOK], p != "progress")
	switch p {
	case "welcome":
		if u.upgradeFrom != "" {
			setText(u.ctl[idHead], "Обновление Prostor")
			setText(u.ctl[idText], fmt.Sprintf("Установлена версия %s. Мастер обновит её до версии %s — настройки и история сохранятся.", u.upgradeFrom, version))
			setText(u.ctl[idOK], "Обновить")
		} else {
			setText(u.ctl[idHead], "Установка Prostor")
			setText(u.ctl[idText], "Программа для очистки Windows от мусора, анализа занятого места, поиска дубликатов и управления автозагрузкой.")
			setText(u.ctl[idOK], "Установить")
		}
	case "progress":
		setText(u.ctl[idHead], "Установка…")
		setText(u.ctl[idText], "")
	case "done":
		setText(u.ctl[idHead], "Prostor установлен")
		setText(u.ctl[idText], "Программу можно найти в меню «Пуск». При запуске Windows попросит разрешение администратора — оно нужно для очистки системных папок.")
		setText(u.ctl[idOK], "Готово")
	case "error":
		setText(u.ctl[idHead], "Не удалось установить")
		u.mu.Lock()
		setText(u.ctl[idText], u.errText)
		u.mu.Unlock()
		setText(u.ctl[idOK], "Закрыть")
	}
	pInvalidateRect.Call(u.hwnd, 0, 1)
	pSetFocus.Call(u.ctl[idOK])
}

func (u *ui) browse() {
	name := make([]uint16, 260)
	bi := browseInfo{Owner: u.hwnd, DisplayName: &name[0], Title: u16("Выберите папку для установки Prostor"), Flags: 0x1 | 0x40} // RETURNONLYFSDIRS | NEWDIALOGSTYLE
	pidl, _, _ := pSHBrowseForFolderW.Call(uintptr(unsafe.Pointer(&bi)))
	if pidl == 0 {
		return
	}
	defer pCoTaskMemFree.Call(pidl)
	buf := make([]uint16, 1024)
	if r, _, _ := pSHGetPathFromIDListW.Call(pidl, uintptr(unsafe.Pointer(&buf[0]))); r == 0 {
		return
	}
	p := syscall.UTF16ToString(buf)
	if !strings.EqualFold(filepath.Base(p), appName) {
		p = filepath.Join(p, appName)
	}
	setText(u.ctl[idEdit], p)
}

func (u *ui) startInstall() {
	o := options{Dir: getText(u.ctl[idEdit]), Desktop: checked(u.ctl[idDesktop])}
	u.busy = true
	u.setPage("progress")
	go func() {
		exe, err := install(o, func(pct int, text string) {
			u.mu.Lock()
			u.status = text
			u.mu.Unlock()
			pPostMessageW.Call(u.hwnd, wmProgress, uintptr(pct), 0)
			time.Sleep(120 * time.Millisecond) // let the user see each step
		})
		u.mu.Lock()
		u.exe = exe
		if err != nil {
			u.errText = err.Error()
		}
		u.mu.Unlock()
		code := uintptr(0)
		if err != nil {
			code = 1
		}
		pPostMessageW.Call(u.hwnd, wmDone, code, 0)
	}()
}

func (u *ui) paint() {
	var ps paintStruct
	hdc, _, _ := pBeginPaint.Call(u.hwnd, uintptr(unsafe.Pointer(&ps)))
	W, H := u.s(clientW), u.s(clientH)
	fill := func(l, t, r, b int32, br uintptr) {
		rc := rect{l, t, r, b}
		pFillRect.Call(hdc, uintptr(unsafe.Pointer(&rc)), br)
	}
	head := u.s(92)
	fill(0, 0, W, head, u.brHead)
	fill(0, head, W, H-u.s(62), u.brWhite)
	fill(0, H-u.s(62), W, H, u.brFoot)
	fill(0, H-u.s(62), W, H-u.s(62)+1, u.brLine)
	if u.icon != 0 {
		pDrawIconEx.Call(hdc, uintptr(u.s(30)), uintptr(u.s(22)), u.icon, uintptr(u.s(48)), uintptr(u.s(48)), 0, 0, 3)
	}
	pSetBkMode.Call(hdc, 1) // TRANSPARENT
	text := func(s string, font uintptr, color uintptr, l, t, r, b int32) {
		pSelectObject.Call(hdc, font)
		pSetTextColor.Call(hdc, color)
		rc := rect{l, t, r, b}
		pDrawTextW.Call(hdc, up(s), ^uintptr(0), uintptr(unsafe.Pointer(&rc)), 0x20|0x800) // SINGLELINE | NOPREFIX
	}
	text("prostor", u.fTitle, rgb(0xEC, 0xEE, 0xF1), u.s(92), u.s(18), W-u.s(20), u.s(56))
	sub := "Очистка и анализ диска, версия " + version
	text(sub, u.fSub, rgb(0xA6, 0xAA, 0xB3), u.s(93), u.s(54), W-u.s(20), u.s(80))
	pEndPaint.Call(u.hwnd, uintptr(unsafe.Pointer(&ps)))
}

func wndProc(hwnd, msg, wp, lp uintptr) uintptr {
	u := w
	switch uint32(msg) {
	case wmPaint:
		u.paint()
		return 0
	case wmEraseBkgnd:
		return 1
	case wmCtlColorStatic, wmCtlColorBtn:
		pSetBkMode.Call(wp, 1)
		pSetBkColor.Call(wp, rgb(255, 255, 255))
		color := rgb(0x18, 0x20, 0x2F)
		if lp == u.ctl[idNote] || lp == u.ctl[idStatus] {
			color = rgb(0x5A, 0x64, 0x78)
		}
		if lp == u.ctl[idText] && u.page == "error" {
			color = rgb(0xC0, 0x2B, 0x31)
		}
		pSetTextColor.Call(wp, color)
		if lp == u.ctl[idOK] || lp == u.ctl[idCancel] {
			return u.brFoot
		}
		return u.brWhite
	case wmCommand:
		id := int(wp & 0xFFFF)
		switch id {
		case idBrowse:
			u.browse()
		case idOK:
			switch u.page {
			case "welcome":
				u.startInstall()
			case "done":
				if checked(u.ctl[idLaunch]) && u.exe != "" {
					launchUnelevated(u.exe)
				}
				pDestroyWindow.Call(hwnd)
			case "error":
				pDestroyWindow.Call(hwnd)
			}
		case idCancel:
			if !u.busy && (u.page == "welcome" || u.page == "error") {
				pDestroyWindow.Call(hwnd)
			}
		}
		return 0
	case wmProgress:
		u.mu.Lock()
		st := u.status
		u.mu.Unlock()
		setText(u.ctl[idStatus], st)
		pSendMessageW.Call(u.ctl[idProg], pbmSetPos, wp, 0)
		return 0
	case wmDone:
		u.busy = false
		if wp == 0 {
			u.setPage("done")
		} else {
			u.setPage("error")
		}
		return 0
	case wmClose:
		if u.busy {
			return 0
		}
		pDestroyWindow.Call(hwnd)
		return 0
	case wmDestroy:
		pPostQuitMessage.Call(0)
		return 0
	}
	r, _, _ := pDefWindowProcW.Call(hwnd, msg, wp, lp)
	return r
}

func msgBox(text string, flags uintptr) {
	pMessageBoxW.Call(0, up(text), up("Установка Prostor"), flags|0x10000)
}

func main() {
	defer func() {
		if r := recover(); r != nil {
			msgBox(fmt.Sprintf("Внутренняя ошибка установщика: %v", r), 0x10)
			os.Exit(1)
		}
	}()
	silent := false
	dir := ""
	for _, a := range os.Args[1:] {
		switch {
		case strings.EqualFold(a, "/S"), strings.EqualFold(a, "--silent"):
			silent = true
		case len(a) > 3 && strings.EqualFold(a[:3], "/D="):
			dir = a[3:]
		}
	}
	if !winapi.IsAdmin() {
		// the manifest asks for elevation; this only happens if it was bypassed
		exe, _ := os.Executable()
		if err := winapi.RunElevated(exe, strings.Join(os.Args[1:], " ")); err != nil && !silent {
			msgBox("Для установки нужны права администратора.", 0x30)
		}
		return
	}
	if dir == "" {
		dir = defaultDir()
	}
	if silent {
		if _, err := install(options{Dir: dir, Desktop: true}, func(int, string) {}); err != nil {
			os.Exit(1)
		}
		return
	}
	pCoInitializeEx.Call(0, 2) // STA for the folder picker
	w.create()
	if dir != defaultDir() {
		setText(w.ctl[idEdit], dir)
	}
	var m msgT
	for {
		r, _, _ := pGetMessageW.Call(uintptr(unsafe.Pointer(&m)), 0, 0, 0)
		if int32(r) <= 0 {
			break
		}
		if d, _, _ := pIsDialogMessageW.Call(w.hwnd, uintptr(unsafe.Pointer(&m))); d != 0 {
			continue
		}
		pTranslateMessage.Call(uintptr(unsafe.Pointer(&m)))
		pDispatchMessageW.Call(uintptr(unsafe.Pointer(&m)))
	}
}
