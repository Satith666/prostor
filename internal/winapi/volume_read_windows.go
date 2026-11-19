package winapi

import (
	"io"
	"os"
	"strings"
	"syscall"
	"unsafe"
)

// RawVolume is a read-only handle to a whole volume (\\.\C:).
type RawVolume struct{ f *os.File }

func (v *RawVolume) ReadAt(p []byte, off int64) (int, error) { return v.f.ReadAt(p, off) }
func (v *RawVolume) Close() error                            { return v.f.Close() }

// OpenVolume opens the volume of a drive root such as `C:\` for raw reading.
// Needs administrator rights.
func OpenVolume(root string) (io.ReaderAt, func() error, error) {
	letter := strings.TrimRight(root, `\/`)
	if len(letter) != 2 || letter[1] != ':' {
		return nil, nil, syscall.Errno(123) // ERROR_INVALID_NAME
	}
	p, _ := syscall.UTF16PtrFromString(`\\.\` + letter)
	h, err := syscall.CreateFile(p, syscall.GENERIC_READ,
		syscall.FILE_SHARE_READ|syscall.FILE_SHARE_WRITE|syscall.FILE_SHARE_DELETE, nil, syscall.OPEN_EXISTING,
		0x08000000, 0) // FILE_FLAG_SEQUENTIAL_SCAN
	if err != nil {
		return nil, nil, err
	}
	v := &RawVolume{os.NewFile(uintptr(h), `\\.\`+letter)}
	return v, v.Close, nil
}

var procGetCompressedFileSizeW = kernel32.NewProc("GetCompressedFileSizeW")

// FileAlloc asks a file itself for its size on disk: the allocation the file
// system reports for an open handle, and the compressed size Explorer shows.
func FileAlloc(path string) (alloc, compressed int64, err error) {
	p, _ := syscall.UTF16PtrFromString(LongPath(path))
	h, err := syscall.CreateFile(p, 0x80, // FILE_READ_ATTRIBUTES
		syscall.FILE_SHARE_READ|syscall.FILE_SHARE_WRITE|syscall.FILE_SHARE_DELETE, nil, syscall.OPEN_EXISTING, syscall.FILE_FLAG_BACKUP_SEMANTICS, 0)
	if err != nil {
		return 0, 0, err
	}
	defer syscall.CloseHandle(h)
	var info struct {
		Alloc, EOF int64
		Links      uint32
		Del, Dir   byte
		_          [2]byte
	}
	r, _, e := procGetFileInformationByHandleEx.Call(uintptr(h), 1, // FileStandardInfo
		uintptr(unsafe.Pointer(&info)), unsafe.Sizeof(info))
	if r == 0 {
		return 0, 0, e
	}
	var hi uint32
	lo, _, e := procGetCompressedFileSizeW.Call(uintptr(unsafe.Pointer(p)), uintptr(unsafe.Pointer(&hi)))
	if uint32(lo) == 0xFFFFFFFF && e != nil && e.(syscall.Errno) != 0 {
		return info.Alloc, -1, nil
	}
	return info.Alloc, int64(hi)<<32 | int64(uint32(lo)), nil
}
