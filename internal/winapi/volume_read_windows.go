package winapi

import (
	"io"
	"os"
	"strings"
	"syscall"
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
