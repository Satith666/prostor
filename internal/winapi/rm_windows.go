package winapi

import (
	"errors"
	"syscall"
	"unsafe"
)

// Restart Manager: which processes hold files open.

var (
	rstrtmgr                = syscall.NewLazyDLL("rstrtmgr.dll")
	procRmStartSession      = rstrtmgr.NewProc("RmStartSession")
	procRmRegisterResources = rstrtmgr.NewProc("RmRegisterResources")
	procRmGetList           = rstrtmgr.NewProc("RmGetList")
	procRmEndSession        = rstrtmgr.NewProc("RmEndSession")
)

type rmUniqueProcess struct {
	PID       uint32
	StartTime syscall.Filetime
}

type rmProcessInfo struct {
	Process     rmUniqueProcess
	AppName     [256]uint16 // CCH_RM_MAX_APP_NAME + 1
	ServiceName [64]uint16  // CCH_RM_MAX_SVC_NAME + 1
	AppType     uint32
	AppStatus   uint32
	TSSessionID uint32
	Restartable int32
}

// WhoLocks lists the processes that keep any of the files open.
func WhoLocks(paths []string) ([]LockHolder, error) {
	if len(paths) == 0 {
		return nil, nil
	}
	if procRmStartSession.Find() != nil {
		return nil, errors.New("Restart Manager недоступен")
	}
	var session uint32
	key := make([]uint16, 33) // CCH_RM_SESSION_KEY + 1
	if r, _, _ := procRmStartSession.Call(uintptr(unsafe.Pointer(&session)), 0, uintptr(unsafe.Pointer(&key[0]))); r != 0 {
		return nil, syscall.Errno(r)
	}
	defer procRmEndSession.Call(uintptr(session))
	names := make([]*uint16, 0, len(paths))
	for _, p := range paths {
		if u, err := syscall.UTF16PtrFromString(LongPath(p)); err == nil {
			names = append(names, u)
		}
	}
	if len(names) == 0 {
		return nil, nil
	}
	if r, _, _ := procRmRegisterResources.Call(uintptr(session), uintptr(len(names)), uintptr(unsafe.Pointer(&names[0])), 0, 0, 0, 0); r != 0 {
		return nil, syscall.Errno(r)
	}
	n := uint32(16)
	for tries := 0; tries < 4; tries++ {
		buf := make([]rmProcessInfo, n)
		var needed, got, reasons uint32
		got = n
		r, _, _ := procRmGetList.Call(uintptr(session), uintptr(unsafe.Pointer(&needed)), uintptr(unsafe.Pointer(&got)),
			uintptr(unsafe.Pointer(&buf[0])), uintptr(unsafe.Pointer(&reasons)))
		if r == 234 { // ERROR_MORE_DATA
			n = needed + 4
			continue
		}
		if r != 0 {
			return nil, syscall.Errno(r)
		}
		out := make([]LockHolder, 0, got)
		for _, p := range buf[:got] {
			out = append(out, LockHolder{PID: p.Process.PID, App: syscall.UTF16ToString(p.AppName[:]),
				Service: syscall.UTF16ToString(p.ServiceName[:]), Kind: p.AppType})
		}
		return out, nil
	}
	return nil, errors.New("список процессов меняется слишком быстро")
}

// HoldFile opens a file without sharing (tests: a file "in use").
func HoldFile(path string) (func(), error) {
	p, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return nil, err
	}
	h, err := syscall.CreateFile(p, syscall.GENERIC_READ, 0, nil, syscall.OPEN_EXISTING, 0, 0)
	if err != nil {
		return nil, err
	}
	return func() { syscall.CloseHandle(h) }, nil
}
