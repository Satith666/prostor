//go:build !windows

package winapi

import (
	"errors"
	"io"
)

// OpenVolume: raw volumes are read only on Windows.
func OpenVolume(root string) (io.ReaderAt, func() error, error) {
	return nil, nil, errors.New("только в Windows")
}

// FileAlloc: no allocation details outside Windows.
func FileAlloc(path string) (alloc, compressed int64, err error) {
	return 0, 0, errors.New("только в Windows")
}
