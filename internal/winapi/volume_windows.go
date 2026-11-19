package winapi

import (
	"path/filepath"
	"strings"
	"syscall"
	"unsafe"
)

var (
	procGetVolumePathNameW = kernel32.NewProc("GetVolumePathNameW")
	procSetFileAttributesW = kernel32.NewProc("SetFileAttributesW")
	procGetFileAttributesW = kernel32.NewProc("GetFileAttributesW")
)

// VolumeRoot returns the mount point of the volume that holds path
// ("C:\", or a folder for volumes mounted into folders).
func VolumeRoot(path string) string {
	p, err := syscall.UTF16PtrFromString(filepath.Clean(path))
	if err != nil {
		return ""
	}
	buf := make([]uint16, 1024)
	r, _, _ := procGetVolumePathNameW.Call(uintptr(unsafe.Pointer(p)), uintptr(unsafe.Pointer(&buf[0])), uintptr(len(buf)))
	if r == 0 {
		return filepath.VolumeName(path) + `\`
	}
	return syscall.UTF16ToString(buf)
}

// SameVolume reports whether a and b live on the same volume, so a rename
// between them is instant and never copies data.
func SameVolume(a, b string) bool {
	va, vb := VolumeRoot(a), VolumeRoot(b)
	return va != "" && strings.EqualFold(va, vb)
}

// SetHidden marks a file or folder hidden (and system, so Explorer keeps it
// out of sight even with "show hidden files" on).
func SetHidden(path string) error {
	p, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return err
	}
	a, _, _ := procGetFileAttributesW.Call(uintptr(unsafe.Pointer(p)))
	if a == 0xFFFFFFFF {
		a = 0
	}
	r, _, e := procSetFileAttributesW.Call(uintptr(unsafe.Pointer(p)), a|0x2|0x4)
	if r == 0 {
		return e
	}
	return nil
}
