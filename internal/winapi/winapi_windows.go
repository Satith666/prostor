//go:build windows

// Package winapi wraps the handful of Win32 calls Prostor needs, using only
// the standard library (syscall + lazy DLL loading).
package winapi

import (
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"time"
	"unicode/utf8"
	"unsafe"
)

var (
	kernel32 = syscall.NewLazyDLL("kernel32.dll")
	shell32  = syscall.NewLazyDLL("shell32.dll")
	user32   = syscall.NewLazyDLL("user32.dll")
	advapi32 = syscall.NewLazyDLL("advapi32.dll")
	ole32    = syscall.NewLazyDLL("ole32.dll")

	procGetLogicalDrives      = kernel32.NewProc("GetLogicalDrives")
	procGetDriveTypeW         = kernel32.NewProc("GetDriveTypeW")
	procGetDiskFreeSpaceExW   = kernel32.NewProc("GetDiskFreeSpaceExW")
	procGetVolumeInformationW = kernel32.NewProc("GetVolumeInformationW")
	procGlobalMemoryStatusEx  = kernel32.NewProc("GlobalMemoryStatusEx")
	procGetTickCount64        = kernel32.NewProc("GetTickCount64")
	procMoveFileExW           = kernel32.NewProc("MoveFileExW")
	procGetOEMCP              = kernel32.NewProc("GetOEMCP")
	procMultiByteToWideChar   = kernel32.NewProc("MultiByteToWideChar")

	procSHQueryRecycleBinW   = shell32.NewProc("SHQueryRecycleBinW")
	procSHEmptyRecycleBinW   = shell32.NewProc("SHEmptyRecycleBinW")
	procSHFileOperationW     = shell32.NewProc("SHFileOperationW")
	procSHBrowseForFolderW   = shell32.NewProc("SHBrowseForFolderW")
	procSHGetPathFromIDListW = shell32.NewProc("SHGetPathFromIDListW")
	procSHGetKnownFolderPath = shell32.NewProc("SHGetKnownFolderPath")
	procShellExecuteW        = shell32.NewProc("ShellExecuteW")

	procMessageBoxW         = user32.NewProc("MessageBoxW")
	procGetSystemMetrics    = user32.NewProc("GetSystemMetrics")
	procGetForegroundWindow = user32.NewProc("GetForegroundWindow")
	procFindWindowW         = user32.NewProc("FindWindowW")
	procShowWindow          = user32.NewProc("ShowWindow")
	procSetForegroundWindow = user32.NewProc("SetForegroundWindow")
	procIsIconic            = user32.NewProc("IsIconic")
	procGetDpiForSystem     = user32.NewProc("GetDpiForSystem")

	procRegCreateKeyExW = advapi32.NewProc("RegCreateKeyExW")
	procRegSetValueExW  = advapi32.NewProc("RegSetValueExW")
	procRegDeleteValueW = advapi32.NewProc("RegDeleteValueW")
	procRegDeleteTreeW  = advapi32.NewProc("RegDeleteTreeW")
	procRegDeleteKeyW   = advapi32.NewProc("RegDeleteKeyW")
	procRegEnumValueW   = advapi32.NewProc("RegEnumValueW")

	procCoInitializeEx = ole32.NewProc("CoInitializeEx")
	procCoUninitialize = ole32.NewProc("CoUninitialize")
	procCoTaskMemFree  = ole32.NewProc("CoTaskMemFree")
)

const IsWindows = true

func u16(s string) *uint16 {
	p, _ := syscall.UTF16PtrFromString(s)
	return p
}

// ---------------------------------------------------------------- drives

type Drive struct {
	Root  string `json:"root"`
	Label string `json:"label"`
	FS    string `json:"fs"`
	Type  string `json:"type"`
	Total uint64 `json:"total"`
	Free  uint64 `json:"free"`
}

func DiskSpace(path string) (total, free uint64, err error) {
	var freeAvail, tot, totFree uint64
	r, _, e := procGetDiskFreeSpaceExW.Call(uintptr(unsafe.Pointer(u16(path))),
		uintptr(unsafe.Pointer(&freeAvail)), uintptr(unsafe.Pointer(&tot)), uintptr(unsafe.Pointer(&totFree)))
	if r == 0 {
		return 0, 0, e
	}
	return tot, freeAvail, nil
}

// Drives lists ready drives. Each drive is queried with a time limit, so a
// sleeping network share or a slow card reader cannot freeze the program.
func Drives() []Drive {
	mask, _, _ := procGetLogicalDrives.Call()
	type res struct {
		d  Drive
		ok bool
	}
	var chans []chan res
	for i := 0; i < 26; i++ {
		if mask&(1<<uint(i)) == 0 {
			continue
		}
		ch := make(chan res, 1)
		chans = append(chans, ch)
		go func(root string) {
			d, ok := queryDrive(root)
			ch <- res{d, ok}
		}(string(rune('A'+i)) + `:\`)
	}
	var out []Drive
	deadline := time.After(1500 * time.Millisecond)
	for _, ch := range chans {
		select {
		case r := <-ch:
			if r.ok {
				out = append(out, r.d)
			}
		case <-deadline:
			return out
		}
	}
	return out
}

func queryDrive(root string) (Drive, bool) {
	{
		t, _, _ := procGetDriveTypeW.Call(uintptr(unsafe.Pointer(u16(root))))
		var typ string
		switch t {
		case 2:
			typ = "removable"
		case 3:
			typ = "fixed"
		case 4:
			typ = "network"
		case 5:
			typ = "cdrom"
		case 6:
			typ = "ram"
		default:
			return Drive{}, false
		}
		if typ == "cdrom" {
			return Drive{}, false
		}
		d := Drive{Root: root, Type: typ}
		label := make([]uint16, 261)
		fs := make([]uint16, 261)
		r, _, _ := procGetVolumeInformationW.Call(uintptr(unsafe.Pointer(u16(root))),
			uintptr(unsafe.Pointer(&label[0])), 261, 0, 0, 0, uintptr(unsafe.Pointer(&fs[0])), 261)
		if r != 0 {
			d.Label = syscall.UTF16ToString(label)
			d.FS = syscall.UTF16ToString(fs)
		}
		if tot, free, err := DiskSpace(root); err == nil {
			d.Total, d.Free = tot, free
		} else {
			return Drive{}, false // not ready (empty card reader etc.)
		}
		return d, true
	}
}

// ---------------------------------------------------------------- system

type memoryStatusEx struct {
	Length               uint32
	MemoryLoad           uint32
	TotalPhys            uint64
	AvailPhys            uint64
	TotalPageFile        uint64
	AvailPageFile        uint64
	TotalVirtual         uint64
	AvailVirtual         uint64
	AvailExtendedVirtual uint64
}

func Memory() (total, avail uint64) {
	var m memoryStatusEx
	m.Length = uint32(unsafe.Sizeof(m))
	r, _, _ := procGlobalMemoryStatusEx.Call(uintptr(unsafe.Pointer(&m)))
	if r == 0 {
		return 0, 0
	}
	return m.TotalPhys, m.AvailPhys
}

func Uptime() time.Duration {
	r, _, _ := procGetTickCount64.Call()
	return time.Duration(r) * time.Millisecond
}

func OSVersion() string {
	const k = `SOFTWARE\Microsoft\Windows NT\CurrentVersion`
	name, _ := RegGetString(HKLM, k, "ProductName")
	disp, _ := RegGetString(HKLM, k, "DisplayVersion")
	build, _ := RegGetString(HKLM, k, "CurrentBuildNumber")
	ubr, _ := RegGetDword(HKLM, k, "UBR")
	if name == "" {
		name = "Windows"
	}
	if b := atoi(build); b >= 22000 {
		name = strings.Replace(name, "Windows 10", "Windows 11", 1)
	}
	s := name
	if disp != "" {
		s += " " + disp
	}
	if build != "" {
		s += " (сборка " + build
		if ubr > 0 {
			s += "." + itoa(int(ubr))
		}
		s += ")"
	}
	return s
}

// IsAdmin reports whether the current process token is elevated.
func IsAdmin() bool {
	var tok syscall.Token
	p, err := syscall.GetCurrentProcess()
	if err != nil {
		return false
	}
	if err := syscall.OpenProcessToken(p, syscall.TOKEN_QUERY, &tok); err != nil {
		return false
	}
	defer tok.Close()
	var elevated uint32
	var n uint32
	const tokenElevation = 20
	if err := syscall.GetTokenInformation(tok, tokenElevation, (*byte)(unsafe.Pointer(&elevated)), 4, &n); err != nil {
		return false
	}
	return elevated != 0
}

func shellExecute(verb, file, params string, show int) error {
	var v, p *uint16
	if verb != "" {
		v = u16(verb)
	}
	if params != "" {
		p = u16(params)
	}
	r, _, e := procShellExecuteW.Call(0, uintptr(unsafe.Pointer(v)), uintptr(unsafe.Pointer(u16(file))),
		uintptr(unsafe.Pointer(p)), 0, uintptr(show))
	if r > 32 {
		return nil
	}
	if errno, ok := e.(syscall.Errno); ok && errno == 1223 {
		return ErrCancelled
	}
	if errno, ok := e.(syscall.Errno); ok && errno != 0 {
		return errno
	}
	return syscall.Errno(r)
}

// ErrCancelled is returned when the user declines the UAC prompt.
var ErrCancelled = errors.New("cancelled")

// RunElevated starts exe with args through the "runas" verb (UAC prompt).
func RunElevated(exe, args string) error {
	return shellExecute("runas", exe, args, 1)
}

// ShellOpen opens a file, folder, URL or ms-settings: URI with its default handler.
func ShellOpen(target string) error {
	return shellExecute("open", target, "", 1)
}

// ShellRun runs an executable with parameters via ShellExecute (honours manifests / UAC).
func ShellRun(file, params string) error {
	return shellExecute("open", file, params, 1)
}

func Reveal(path string) error {
	cmd := exec.Command("explorer.exe")
	cmd.SysProcAttr = &syscall.SysProcAttr{CmdLine: `explorer.exe /select,"` + path + `"`}
	return cmd.Start()
}

func HideWindow(cmd *exec.Cmd) {
	if cmd.SysProcAttr == nil {
		cmd.SysProcAttr = &syscall.SysProcAttr{}
	}
	cmd.SysProcAttr.HideWindow = true
	cmd.SysProcAttr.CreationFlags |= 0x08000000 // CREATE_NO_WINDOW
}

const (
	MB_OK              = 0x0
	MB_OKCANCEL        = 0x1
	MB_YESNO           = 0x4
	MB_ICONERROR       = 0x10
	MB_ICONQUESTION    = 0x20
	MB_ICONWARNING     = 0x30
	MB_ICONINFORMATION = 0x40
	MB_SETFOREGROUND   = 0x10000
	MB_TOPMOST         = 0x40000
	IDOK               = 1
	IDYES              = 6
)

func MessageBox(title, text string, flags uint32) int {
	r, _, _ := procMessageBoxW.Call(0, uintptr(unsafe.Pointer(u16(text))), uintptr(unsafe.Pointer(u16(title))),
		uintptr(flags|MB_SETFOREGROUND|MB_TOPMOST))
	return int(r)
}

// ScreenSize returns the primary screen size in device-independent pixels.
func ScreenSize() (int, int) {
	w, _, _ := procGetSystemMetrics.Call(0)
	h, _, _ := procGetSystemMetrics.Call(1)
	dpi := uintptr(96)
	if procGetDpiForSystem.Find() == nil {
		if d, _, _ := procGetDpiForSystem.Call(); d > 0 {
			dpi = d
		}
	}
	return int(w * 96 / dpi), int(h * 96 / dpi)
}

// FocusWindow brings a top-level window with the exact title to the front.
func FocusWindow(title string) bool {
	hwnd, _, _ := procFindWindowW.Call(0, uintptr(unsafe.Pointer(u16(title))))
	if hwnd == 0 {
		return false
	}
	if ic, _, _ := procIsIconic.Call(hwnd); ic != 0 {
		procShowWindow.Call(hwnd, 9) // SW_RESTORE
	}
	procSetForegroundWindow.Call(hwnd)
	return true
}

// ---------------------------------------------------------------- recycle bin & files

type shQueryRBInfo struct {
	Size     uint32
	Bytes    int64
	NumItems int64
}

func RecycleBin() (bytes, items int64) {
	var info shQueryRBInfo
	info.Size = uint32(unsafe.Sizeof(info))
	r, _, _ := procSHQueryRecycleBinW.Call(0, uintptr(unsafe.Pointer(&info)))
	if uint32(r) != 0 {
		return 0, 0
	}
	return info.Bytes, info.NumItems
}

func EmptyRecycleBin() error {
	const flags = 0x1 | 0x2 | 0x4 // NOCONFIRMATION | NOPROGRESSUI | NOSOUND
	r, _, _ := procSHEmptyRecycleBinW.Call(0, 0, flags)
	hr := uint32(r)                  // HRESULT lives in the low 32 bits
	if hr != 0 && hr != 0x8000FFFF { // E_UNEXPECTED is returned when the bin is already empty
		return syscall.Errno(hr)
	}
	return nil
}

type shFileOpStruct struct {
	Hwnd          uintptr
	Func          uint32
	From          *uint16
	To            *uint16
	Flags         uint16
	AnyAborted    int32
	NameMappings  uintptr
	ProgressTitle *uint16
}

// MoveToRecycleBin sends the given files/folders to the Recycle Bin silently.
func MoveToRecycleBin(paths []string) error {
	if len(paths) == 0 {
		return nil
	}
	var buf []uint16
	for _, p := range paths {
		s, err := syscall.UTF16FromString(p)
		if err != nil {
			return err
		}
		buf = append(buf, s...) // includes its own NUL
	}
	buf = append(buf, 0)
	op := shFileOpStruct{
		Func:  3,                         // FO_DELETE
		From:  &buf[0],                   //
		Flags: 0x40 | 0x10 | 0x4 | 0x400, // ALLOWUNDO|NOCONFIRMATION|SILENT|NOERRORUI
	}
	r, _, _ := procSHFileOperationW.Call(uintptr(unsafe.Pointer(&op)))
	if r != 0 {
		return syscall.Errno(r)
	}
	if op.AnyAborted != 0 {
		return ErrCancelled
	}
	return nil
}

func DeleteOnReboot(path string) error {
	r, _, e := procMoveFileExW.Call(uintptr(unsafe.Pointer(u16(path))), 0, 4) // MOVEFILE_DELAY_UNTIL_REBOOT
	if r == 0 {
		return e
	}
	return nil
}

// FileAttributes returns the raw Win32 attributes of a FileInfo (0 if unknown).
func FileAttributes(fi os.FileInfo) uint32 {
	if fi == nil {
		return 0
	}
	if d, ok := fi.Sys().(*syscall.Win32FileAttributeData); ok {
		return d.FileAttributes
	}
	return 0
}

// ---------------------------------------------------------------- processes

type Process struct {
	PID    uint32
	Parent uint32
	Name   string
}

func Processes() []Process {
	snap, err := syscall.CreateToolhelp32Snapshot(syscall.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return nil
	}
	defer syscall.CloseHandle(snap)
	var e syscall.ProcessEntry32
	e.Size = uint32(unsafe.Sizeof(e))
	var out []Process
	if err := syscall.Process32First(snap, &e); err != nil {
		return nil
	}
	for {
		out = append(out, Process{PID: e.ProcessID, Parent: e.ParentProcessID, Name: syscall.UTF16ToString(e.ExeFile[:])})
		if err := syscall.Process32Next(snap, &e); err != nil {
			break
		}
	}
	return out
}

// ---------------------------------------------------------------- folder picker

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

// PickFolder shows the classic folder browser. Returns "" if cancelled.
func PickFolder(title string) string {
	res := make(chan string, 1)
	go func() {
		runtime.LockOSThread()
		defer runtime.UnlockOSThread()
		procCoInitializeEx.Call(0, 2) // COINIT_APARTMENTTHREADED
		defer procCoUninitialize.Call()
		owner, _, _ := procGetForegroundWindow.Call()
		name := make([]uint16, 260)
		bi := browseInfo{
			Owner:       owner,
			DisplayName: &name[0],
			Title:       u16(title),
			Flags:       0x1 | 0x10 | 0x40, // RETURNONLYFSDIRS | EDITBOX | NEWDIALOGSTYLE
		}
		pidl, _, _ := procSHBrowseForFolderW.Call(uintptr(unsafe.Pointer(&bi)))
		if pidl == 0 {
			res <- ""
			return
		}
		defer procCoTaskMemFree.Call(pidl)
		buf := make([]uint16, 1024)
		r, _, _ := procSHGetPathFromIDListW.Call(pidl, uintptr(unsafe.Pointer(&buf[0])))
		if r == 0 {
			res <- ""
			return
		}
		res <- syscall.UTF16ToString(buf)
	}()
	return <-res
}

// ---------------------------------------------------------------- known folders

type guid struct {
	Data1 uint32
	Data2 uint16
	Data3 uint16
	Data4 [8]byte
}

var knownFolders = map[string]guid{
	"downloads": {0x374DE290, 0x123F, 0x4565, [8]byte{0x91, 0x64, 0x39, 0xC4, 0x92, 0x5E, 0x46, 0x7B}},
	"desktop":   {0xB4BFCC3A, 0xDB2C, 0x424C, [8]byte{0xB0, 0x29, 0x7F, 0xE9, 0x9A, 0x87, 0xC6, 0x41}},
	"documents": {0xFDD39AD0, 0x238F, 0x46AF, [8]byte{0xAD, 0xB4, 0x6C, 0x85, 0x48, 0x03, 0x69, 0xC7}},
	"videos":    {0x18989B1D, 0x99B5, 0x455B, [8]byte{0x84, 0x1C, 0xAB, 0x7C, 0x74, 0xE4, 0xDD, 0xFC}},
	"pictures":  {0x33E28130, 0x4E1E, 0x4676, [8]byte{0x83, 0x5A, 0x98, 0x39, 0x5C, 0x3B, 0xC3, 0xBB}},
	"music":     {0x4BD8D571, 0x6D19, 0x48D3, [8]byte{0xBE, 0x97, 0x42, 0x22, 0x20, 0x08, 0x0E, 0x43}},
}

// KnownFolder resolves a shell folder (downloads, desktop, documents, videos, pictures, music).
func KnownFolder(name string) string {
	g, ok := knownFolders[name]
	if !ok {
		return ""
	}
	var p *uint16
	r, _, _ := procSHGetKnownFolderPath.Call(uintptr(unsafe.Pointer(&g)), 0, 0, uintptr(unsafe.Pointer(&p)))
	if r != 0 || p == nil {
		return ""
	}
	defer procCoTaskMemFree.Call(uintptr(unsafe.Pointer(p)))
	return syscall.UTF16ToString((*[1 << 15]uint16)(unsafe.Pointer(p))[:])
}

// ---------------------------------------------------------------- text decoding

// DecodeConsole converts console-tool output (OEM code page, e.g. CP866) to UTF-8.
func DecodeConsole(b []byte) string {
	if utf8.Valid(b) {
		return string(b)
	}
	cp, _, _ := procGetOEMCP.Call()
	n, _, _ := procMultiByteToWideChar.Call(cp, 0, uintptr(unsafe.Pointer(&b[0])), uintptr(len(b)), 0, 0)
	if n == 0 {
		return string(b)
	}
	w := make([]uint16, n)
	procMultiByteToWideChar.Call(cp, 0, uintptr(unsafe.Pointer(&b[0])), uintptr(len(b)), uintptr(unsafe.Pointer(&w[0])), n)
	return syscall.UTF16ToString(w)
}

// ---------------------------------------------------------------- registry

type Root syscall.Handle

const (
	HKCU = Root(syscall.HKEY_CURRENT_USER)
	HKLM = Root(syscall.HKEY_LOCAL_MACHINE)
)

const (
	keyRead  = syscall.KEY_READ
	keyWrite = 0x20006
	wow64_64 = 0x0100
)

func openKey(root Root, path string, access uint32) (syscall.Handle, error) {
	var h syscall.Handle
	err := syscall.RegOpenKeyEx(syscall.Handle(root), u16(path), 0, access|wow64_64, &h)
	return h, err
}

func queryRaw(h syscall.Handle, name string) (uint32, []byte, error) {
	var typ, n uint32
	nm := u16(name)
	if err := syscall.RegQueryValueEx(h, nm, nil, &typ, nil, &n); err != nil {
		return 0, nil, err
	}
	if n == 0 {
		return typ, nil, nil
	}
	buf := make([]byte, n+2)
	if err := syscall.RegQueryValueEx(h, nm, nil, &typ, &buf[0], &n); err != nil {
		return 0, nil, err
	}
	return typ, buf[:n], nil
}

func bytesToString(b []byte) string {
	if len(b) < 2 {
		return ""
	}
	u := unsafe.Slice((*uint16)(unsafe.Pointer(&b[0])), len(b)/2)
	return syscall.UTF16ToString(u)
}

func RegGetString(root Root, path, name string) (string, bool) {
	h, err := openKey(root, path, keyRead)
	if err != nil {
		return "", false
	}
	defer syscall.RegCloseKey(h)
	typ, b, err := queryRaw(h, name)
	if err != nil {
		return "", false
	}
	switch typ {
	case syscall.REG_SZ, syscall.REG_EXPAND_SZ:
		s := bytesToString(b)
		if typ == syscall.REG_EXPAND_SZ {
			s = ExpandEnv(s)
		}
		return s, true
	case syscall.REG_DWORD:
		if len(b) >= 4 {
			return itoa(int(*(*uint32)(unsafe.Pointer(&b[0])))), true
		}
	}
	return "", false
}

func RegGetDword(root Root, path, name string) (uint32, bool) {
	h, err := openKey(root, path, keyRead)
	if err != nil {
		return 0, false
	}
	defer syscall.RegCloseKey(h)
	typ, b, err := queryRaw(h, name)
	if err != nil || typ != syscall.REG_DWORD || len(b) < 4 {
		return 0, false
	}
	return *(*uint32)(unsafe.Pointer(&b[0])), true
}

func RegGetBinary(root Root, path, name string) ([]byte, bool) {
	h, err := openKey(root, path, keyRead)
	if err != nil {
		return nil, false
	}
	defer syscall.RegCloseKey(h)
	_, b, err := queryRaw(h, name)
	if err != nil {
		return nil, false
	}
	return b, true
}

func RegSubKeys(root Root, path string) []string {
	h, err := openKey(root, path, keyRead)
	if err != nil {
		return nil
	}
	defer syscall.RegCloseKey(h)
	var out []string
	for i := uint32(0); ; i++ {
		buf := make([]uint16, 512)
		n := uint32(len(buf))
		if err := syscall.RegEnumKeyEx(h, i, &buf[0], &n, nil, nil, nil, nil); err != nil {
			break
		}
		out = append(out, syscall.UTF16ToString(buf[:n]))
	}
	return out
}

// RegValue is a string-ish registry value (REG_SZ / REG_EXPAND_SZ).
type RegValue struct {
	Name  string
	Value string
}

func RegStringValues(root Root, path string) []RegValue {
	h, err := openKey(root, path, keyRead)
	if err != nil {
		return nil
	}
	defer syscall.RegCloseKey(h)
	var out []RegValue
	for i := uint32(0); ; i++ {
		name := make([]uint16, 16384)
		nameLen := uint32(len(name))
		var typ uint32
		data := make([]byte, 65536)
		dataLen := uint32(len(data))
		r, _, _ := procRegEnumValueW.Call(uintptr(h), uintptr(i), uintptr(unsafe.Pointer(&name[0])),
			uintptr(unsafe.Pointer(&nameLen)), 0, uintptr(unsafe.Pointer(&typ)),
			uintptr(unsafe.Pointer(&data[0])), uintptr(unsafe.Pointer(&dataLen)))
		if r == 259 { // ERROR_NO_MORE_ITEMS
			break
		}
		if r != 0 {
			continue
		}
		if typ != syscall.REG_SZ && typ != syscall.REG_EXPAND_SZ {
			continue
		}
		out = append(out, RegValue{Name: syscall.UTF16ToString(name[:nameLen]), Value: bytesToString(data[:dataLen])})
	}
	return out
}

func createKey(root Root, path string) (syscall.Handle, error) {
	var h syscall.Handle
	var disp uint32
	r, _, _ := procRegCreateKeyExW.Call(uintptr(root), uintptr(unsafe.Pointer(u16(path))), 0, 0, 0,
		uintptr(keyWrite|wow64_64), 0, uintptr(unsafe.Pointer(&h)), uintptr(unsafe.Pointer(&disp)))
	if r != 0 {
		return 0, syscall.Errno(r)
	}
	return h, nil
}

func setValue(root Root, path, name string, typ uint32, data []byte) error {
	h, err := createKey(root, path)
	if err != nil {
		return err
	}
	defer syscall.RegCloseKey(h)
	var p uintptr
	if len(data) > 0 {
		p = uintptr(unsafe.Pointer(&data[0]))
	}
	r, _, _ := procRegSetValueExW.Call(uintptr(h), uintptr(unsafe.Pointer(u16(name))), 0, uintptr(typ), p, uintptr(len(data)))
	if r != 0 {
		return syscall.Errno(r)
	}
	return nil
}

func RegSetString(root Root, path, name, value string) error {
	u, err := syscall.UTF16FromString(value)
	if err != nil {
		return err
	}
	b := unsafe.Slice((*byte)(unsafe.Pointer(&u[0])), len(u)*2)
	return setValue(root, path, name, syscall.REG_SZ, b)
}

func RegSetDword(root Root, path, name string, v uint32) error {
	b := []byte{byte(v), byte(v >> 8), byte(v >> 16), byte(v >> 24)}
	return setValue(root, path, name, syscall.REG_DWORD, b)
}

func RegSetBinary(root Root, path, name string, data []byte) error {
	return setValue(root, path, name, syscall.REG_BINARY, data)
}

func RegDeleteTree(root Root, path string) error {
	r, _, _ := procRegDeleteTreeW.Call(uintptr(root), uintptr(unsafe.Pointer(u16(path))))
	if r != 0 && r != 2 {
		return syscall.Errno(r)
	}
	// RegDeleteTree removes subkeys and values; remove the key itself too.
	procRegDeleteKeyW.Call(uintptr(root), uintptr(unsafe.Pointer(u16(path))))
	return nil
}

// ---------------------------------------------------------------- helpers

// ExpandEnv expands %VAR% references.
func ExpandEnv(s string) string {
	if !strings.Contains(s, "%") {
		return s
	}
	var b strings.Builder
	for {
		i := strings.IndexByte(s, '%')
		if i < 0 {
			b.WriteString(s)
			break
		}
		j := strings.IndexByte(s[i+1:], '%')
		if j < 0 {
			b.WriteString(s)
			break
		}
		name := s[i+1 : i+1+j]
		b.WriteString(s[:i])
		if v, ok := os.LookupEnv(name); ok && name != "" {
			b.WriteString(v)
		} else {
			b.WriteString(s[i : i+j+2])
		}
		s = s[i+j+2:]
	}
	return b.String()
}

func atoi(s string) int {
	n := 0
	for _, c := range s {
		if c < '0' || c > '9' {
			break
		}
		n = n*10 + int(c-'0')
	}
	return n
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var b [20]byte
	i := len(b)
	for n > 0 {
		i--
		b[i] = byte('0' + n%10)
		n /= 10
	}
	if neg {
		i--
		b[i] = '-'
	}
	return string(b[i:])
}

// ---------------------------------------------------------------- shortcuts (COM IShellLinkW)

var (
	procCoCreateInstance = ole32.NewProc("CoCreateInstance")

	clsidShellLink  = guid{0x00021401, 0, 0, [8]byte{0xC0, 0, 0, 0, 0, 0, 0, 0x46}}
	iidIShellLinkW  = guid{0x000214F9, 0, 0, [8]byte{0xC0, 0, 0, 0, 0, 0, 0, 0x46}}
	iidIPersistFile = guid{0x0000010B, 0, 0, [8]byte{0xC0, 0, 0, 0, 0, 0, 0, 0x46}}
)

// comCall invokes method #index of a COM object's vtable.
func comCall(obj unsafe.Pointer, index int, args ...uintptr) uint32 {
	vtbl := *(*unsafe.Pointer)(obj)
	fn := *(*uintptr)(unsafe.Add(vtbl, uintptr(index)*unsafe.Sizeof(uintptr(0))))
	r, _, _ := syscall.SyscallN(fn, append([]uintptr{uintptr(obj)}, args...)...)
	return uint32(r)
}

// CreateShortcut writes a .lnk file pointing to target.
func CreateShortcut(lnk, target, args, workDir, icon, desc string) error {
	errc := make(chan error, 1)
	go func() {
		runtime.LockOSThread()
		defer runtime.UnlockOSThread()
		procCoInitializeEx.Call(0, 2) // COINIT_APARTMENTTHREADED
		defer procCoUninitialize.Call()
		var psl unsafe.Pointer
		hr, _, _ := procCoCreateInstance.Call(uintptr(unsafe.Pointer(&clsidShellLink)), 0, 1, // CLSCTX_INPROC_SERVER
			uintptr(unsafe.Pointer(&iidIShellLinkW)), uintptr(unsafe.Pointer(&psl)))
		if uint32(hr) != 0 || psl == nil {
			errc <- errors.New("CoCreateInstance(ShellLink) failed")
			return
		}
		defer comCall(psl, 2) // Release
		set := func(index int, s string, extra ...uintptr) {
			p := u16(s)
			comCall(psl, index, append([]uintptr{uintptr(unsafe.Pointer(p))}, extra...)...)
			runtime.KeepAlive(p)
		}
		set(20, target) // SetPath
		if args != "" {
			set(11, args) // SetArguments
		}
		if workDir != "" {
			set(9, workDir) // SetWorkingDirectory
		}
		if desc != "" {
			set(7, desc) // SetDescription
		}
		if icon != "" {
			set(17, icon, 0) // SetIconLocation
		}
		var ppf unsafe.Pointer
		if comCall(psl, 0, uintptr(unsafe.Pointer(&iidIPersistFile)), uintptr(unsafe.Pointer(&ppf))) != 0 || ppf == nil {
			errc <- errors.New("QueryInterface(IPersistFile) failed")
			return
		}
		defer comCall(ppf, 2)
		p := u16(lnk)
		hr2 := comCall(ppf, 6, uintptr(unsafe.Pointer(p)), 1) // Save(path, TRUE)
		runtime.KeepAlive(p)
		if hr2 != 0 {
			errc <- errors.New("IPersistFile.Save failed")
			return
		}
		errc <- nil
	}()
	return <-errc
}

// KillByName terminates every process with the given image name (case-insensitive), except ourselves.
func KillByName(name string) int {
	me := uint32(os.Getpid())
	n := 0
	for _, p := range Processes() {
		if p.PID == me || !strings.EqualFold(p.Name, name) {
			continue
		}
		h, err := syscall.OpenProcess(syscall.PROCESS_TERMINATE|0x00100000 /*SYNCHRONIZE*/, false, p.PID)
		if err != nil {
			continue
		}
		if syscall.TerminateProcess(h, 1) == nil {
			syscall.WaitForSingleObject(h, 5000)
			n++
		}
		syscall.CloseHandle(h)
	}
	return n
}

// SetCmdLine sets the raw command line passed to CreateProcess.
func SetCmdLine(c *exec.Cmd, line string) {
	if c.SysProcAttr == nil {
		c.SysProcAttr = &syscall.SysProcAttr{}
	}
	c.SysProcAttr.CmdLine = line
}

var (
	procGetLongPathNameW  = kernel32.NewProc("GetLongPathNameW")
	procGetShortPathNameW = kernel32.NewProc("GetShortPathNameW")
)

func pathCall(proc *syscall.LazyProc, p string) string {
	buf := make([]uint16, 300)
	for {
		n, _, _ := proc.Call(uintptr(unsafe.Pointer(u16(p))), uintptr(unsafe.Pointer(&buf[0])), uintptr(len(buf)))
		if n == 0 {
			return ""
		}
		if int(n) < len(buf) {
			return syscall.UTF16ToString(buf[:n])
		}
		buf = make([]uint16, n+1)
	}
}

// FullNames turns 8.3 short names (C:\Users\IVAN~1) into long ones, so paths
// from the environment and from folder listings compare equal. Parts of the
// path that do not exist (or wildcards) are kept as they are.
func FullNames(p string) string {
	if !strings.Contains(p, "~") {
		return p
	}
	cur, rest := filepath.Clean(p), ""
	for {
		if l := pathCall(procGetLongPathNameW, cur); l != "" {
			return filepath.Join(l, rest)
		}
		parent := filepath.Dir(cur)
		if parent == cur {
			return p
		}
		rest = filepath.Join(filepath.Base(cur), rest)
		cur = parent
	}
}

// ShortPath returns the 8.3 form of an existing path ("" if unavailable).
func ShortPath(p string) string { return pathCall(procGetShortPathNameW, p) }

// RegKeyExists checks a key in the 64-bit and the 32-bit view of the registry.
func RegKeyExists(root Root, path string) bool {
	for _, view := range []uint32{0x0100, 0x0200} { // KEY_WOW64_64KEY, KEY_WOW64_32KEY
		var h syscall.Handle
		if syscall.RegOpenKeyEx(syscall.Handle(root), u16(path), 0, syscall.KEY_QUERY_VALUE|view, &h) == nil {
			syscall.RegCloseKey(h)
			return true
		}
	}
	return false
}
