//go:build windows

package winapi

import (
	"encoding/binary"
	"os"
	"runtime"
	"strings"
	"syscall"
	"unicode/utf16"
	"unsafe"
)

// DirEnt is one directory entry with everything the analyzer needs, read in
// bulk from the directory itself (no per-file system calls).
type DirEnt struct {
	Name  string
	Size  int64  // logical size
	Alloc int64  // size on disk (0 for cloud-only and tiny MFT-resident files)
	MTime int64  // unix seconds
	Attr  uint32 // FILE_ATTRIBUTE_*
	ID    uint64 // NTFS file id; equal for hard links of one file (0 = unknown)
	IsDir bool
	Link  bool // junction, symlink or mount point: never followed
}

const (
	classIDBothDir        = 10 // FileIdBothDirectoryInfo
	classIDBothDirRestart = 11
	classFullDir          = 14 // FileFullDirectoryInfo
	classFullDirRestart   = 15

	attrDirectory = 0x10
	attrReparse   = 0x400
)

var (
	procGetFileInformationByHandleEx = kernel32.NewProc("GetFileInformationByHandleEx")
	procSetThreadPriority            = kernel32.NewProc("SetThreadPriority")
	procGetCurrentThread             = kernel32.NewProc("GetCurrentThread")
	procSetErrorMode                 = kernel32.NewProc("SetErrorMode")
	procOpenProcessToken             = advapi32.NewProc("OpenProcessToken")
	procLookupPrivilegeValueW        = advapi32.NewProc("LookupPrivilegeValueW")
	procAdjustTokenPrivileges        = advapi32.NewProc("AdjustTokenPrivileges")
)

func init() {
	// never show "insert a disk" boxes for empty card readers
	procSetErrorMode.Call(0x0001 | 0x8000) // SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX
}

// LongPath converts an absolute path to the \\?\ form so paths longer than
// MAX_PATH work with raw Win32 calls.
func LongPath(p string) string {
	switch {
	case strings.HasPrefix(p, `\\?\`), strings.HasPrefix(p, `\\.\`):
		return p
	case strings.HasPrefix(p, `\\`):
		return `\\?\UNC\` + p[2:]
	case len(p) == 2 && p[1] == ':':
		return `\\?\` + p + `\`
	case len(p) >= 3 && p[1] == ':' && p[2] == '\\':
		return `\\?\` + p
	}
	return p
}

const filetimeEpoch = 116444736000000000 // 1601-01-01 → 1970-01-01 in 100 ns

// ReadDir lists a directory with GetFileInformationByHandleEx, which returns
// size on disk, file id and reparse tag for every entry in one pass.
func ReadDir(dir string) ([]DirEnt, error) {
	p, err := syscall.UTF16PtrFromString(LongPath(dir))
	if err != nil {
		return nil, err
	}
	h, err := syscall.CreateFile(p, 0x0001, // FILE_LIST_DIRECTORY
		syscall.FILE_SHARE_READ|syscall.FILE_SHARE_WRITE|syscall.FILE_SHARE_DELETE,
		nil, syscall.OPEN_EXISTING, syscall.FILE_FLAG_BACKUP_SEMANTICS, 0)
	if err != nil {
		return nil, err
	}
	defer syscall.CloseHandle(h)

	buf := make([]uint64, 64<<10/8) // 8-byte aligned
	b := unsafe.Slice((*byte)(unsafe.Pointer(&buf[0])), len(buf)*8)
	class, full := classIDBothDirRestart, false
	var out []DirEnt
	for {
		r, _, e := procGetFileInformationByHandleEx.Call(uintptr(h), uintptr(class),
			uintptr(unsafe.Pointer(&buf[0])), uintptr(len(b)))
		if r == 0 {
			errno, _ := e.(syscall.Errno)
			if errno == syscall.ERROR_NO_MORE_FILES {
				break
			}
			if !full && class == classIDBothDirRestart {
				// FAT/exFAT and some network shares have no file ids
				full, class = true, classFullDirRestart
				continue
			}
			if len(out) == 0 {
				return readDirSlow(dir)
			}
			break
		}
		nameOff := 104
		if full {
			nameOff = 68
		}
		for off := 0; off+nameOff <= len(b); {
			next := int(binary.LittleEndian.Uint32(b[off:]))
			attr := binary.LittleEndian.Uint32(b[off+56:])
			nlen := int(binary.LittleEndian.Uint32(b[off+60:]))
			if off+nameOff+nlen > len(b) {
				break
			}
			u := unsafe.Slice((*uint16)(unsafe.Pointer(&b[off+nameOff])), nlen/2)
			name := string(utf16.Decode(u))
			if name != "." && name != ".." {
				d := DirEnt{
					Name:  name,
					Size:  int64(binary.LittleEndian.Uint64(b[off+40:])),
					Alloc: int64(binary.LittleEndian.Uint64(b[off+48:])),
					Attr:  attr,
					IsDir: attr&attrDirectory != 0,
				}
				if ft := int64(binary.LittleEndian.Uint64(b[off+24:])); ft > filetimeEpoch {
					d.MTime = (ft - filetimeEpoch) / 10_000_000
				}
				if !full {
					d.ID = binary.LittleEndian.Uint64(b[off+96:])
				}
				if attr&attrReparse != 0 {
					tag := binary.LittleEndian.Uint32(b[off+64:]) // EaSize holds the reparse tag
					d.Link = tag&0x20000000 != 0                  // name surrogate: junction, symlink, mount point
				}
				if d.IsDir {
					d.Size, d.Alloc = 0, 0
				}
				out = append(out, d)
			}
			if next == 0 {
				break
			}
			off += next
		}
		if full {
			class = classFullDir
		} else {
			class = classIDBothDir
		}
	}
	return out, nil
}

func readDirSlow(dir string) ([]DirEnt, error) {
	ents, err := os.ReadDir(dir)
	if err != nil {
		return nil, err
	}
	out := make([]DirEnt, 0, len(ents))
	for _, e := range ents {
		fi, err := e.Info()
		if err != nil {
			continue
		}
		attr := FileAttributes(fi)
		d := DirEnt{Name: e.Name(), Attr: attr, IsDir: fi.IsDir(), MTime: fi.ModTime().Unix(),
			Link: fi.Mode()&os.ModeSymlink != 0 || (fi.IsDir() && attr&attrReparse != 0 && fi.Mode()&os.ModeIrregular != 0)}
		if !d.IsDir {
			d.Size, d.Alloc = fi.Size(), fi.Size()
		}
		out = append(out, d)
	}
	return out, nil
}

// BackgroundThread pins the calling goroutine to its OS thread and switches
// that thread to background mode (low CPU, I/O and memory priority), so long
// scans do not slow down the rest of the computer. Call the returned function
// from the same goroutine when the work is done.
func BackgroundThread() func() {
	runtime.LockOSThread()
	h, _, _ := procGetCurrentThread.Call()
	r, _, _ := procSetThreadPriority.Call(h, 0x00010000) // THREAD_MODE_BACKGROUND_BEGIN
	return func() {
		if r != 0 {
			procSetThreadPriority.Call(h, 0x00020000) // THREAD_MODE_BACKGROUND_END
		}
		runtime.UnlockOSThread()
	}
}

type luidAndAttrs struct {
	Low, High uint32
	Attrs     uint32
}

type tokenPrivileges struct {
	Count uint32
	Priv  luidAndAttrs
}

// EnableBackupPrivilege lets an elevated process list folders whose ACLs deny
// reading (System Volume Information, other profiles), so the disk analysis
// can account for that space. Only listing is affected.
func EnableBackupPrivilege() bool {
	proc, _ := syscall.GetCurrentProcess()
	var tok syscall.Token
	r, _, _ := procOpenProcessToken.Call(uintptr(proc), 0x0020|0x0008, uintptr(unsafe.Pointer(&tok))) // ADJUST_PRIVILEGES | QUERY
	if r == 0 {
		return false
	}
	defer tok.Close()
	var tp tokenPrivileges
	r, _, _ = procLookupPrivilegeValueW.Call(0, uintptr(unsafe.Pointer(u16("SeBackupPrivilege"))), uintptr(unsafe.Pointer(&tp.Priv)))
	if r == 0 {
		return false
	}
	tp.Count = 1
	tp.Priv.Attrs = 2 // SE_PRIVILEGE_ENABLED
	r, _, e := procAdjustTokenPrivileges.Call(uintptr(tok), 0, uintptr(unsafe.Pointer(&tp)), 0, 0, 0)
	return r != 0 && e != syscall.Errno(1300) // ERROR_NOT_ALL_ASSIGNED
}

var (
	procAttachConsole      = kernel32.NewProc("AttachConsole")
	procAllocConsole       = kernel32.NewProc("AllocConsole")
	procSetConsoleOutputCP = kernel32.NewProc("SetConsoleOutputCP")
	procSetConsoleTitleW   = kernel32.NewProc("SetConsoleTitleW")
)

// Console connects this GUI program to a text console: the one of the parent
// (when started from cmd) or, if alloc is set, a new window. Stdout and
// stderr are redirected there. Returns false if no console is available, and
// whether a new window was created.
func Console(alloc bool, title string) (ok, created bool) {
	r, _, _ := procAttachConsole.Call(uintptr(0xFFFFFFFF)) // ATTACH_PARENT_PROCESS
	if r == 0 {
		if !alloc {
			return false, false
		}
		if r, _, _ = procAllocConsole.Call(); r == 0 {
			return false, false
		}
		created = true
	}
	procSetConsoleOutputCP.Call(65001)
	if title != "" {
		procSetConsoleTitleW.Call(uintptr(unsafe.Pointer(u16(title))))
	}
	h, err := syscall.CreateFile(u16("CONOUT$"), syscall.GENERIC_READ|syscall.GENERIC_WRITE,
		syscall.FILE_SHARE_READ|syscall.FILE_SHARE_WRITE, nil, syscall.OPEN_EXISTING, 0, 0)
	if err != nil {
		return false, created
	}
	f := os.NewFile(uintptr(h), "CONOUT$")
	os.Stdout, os.Stderr = f, f
	return true, created
}

// WaitEnter waits until the user presses Enter in the console.
func WaitEnter() {
	h, err := syscall.CreateFile(u16("CONIN$"), syscall.GENERIC_READ|syscall.GENERIC_WRITE,
		syscall.FILE_SHARE_READ|syscall.FILE_SHARE_WRITE, nil, syscall.OPEN_EXISTING, 0, 0)
	if err != nil {
		return
	}
	f := os.NewFile(uintptr(h), "CONIN$")
	defer f.Close()
	buf := make([]byte, 64)
	f.Read(buf)
}
