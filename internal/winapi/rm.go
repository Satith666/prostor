package winapi

import (
	"errors"
	"syscall"
)

// LockHolder is a process that keeps a file open.
type LockHolder struct {
	PID     uint32 `json:"pid"`
	App     string `json:"app"`               // name shown by Windows
	Service string `json:"service,omitempty"` // for services
	Kind    uint32 `json:"kind"`              // RM_APP_TYPE: 1 window, 2 other window, 3 service, 4 Explorer, 5 console, 1000 critical
	Exe     string `json:"exe,omitempty"`     // process file name, filled in by the caller
}

// Closable: an ordinary program the user can close.
func (h LockHolder) Closable() bool { return h.Kind == 1 || h.Kind == 2 || h.Kind == 5 }

// InUse: a deletion failed because the file is open in some program
// (sharing or lock violation; access denied also covers running programs).
func InUse(err error) bool {
	if err == nil || !IsWindows {
		return false
	}
	var e syscall.Errno
	if !errors.As(err, &e) {
		return false
	}
	return e == 32 || e == 33 || e == 5
}
