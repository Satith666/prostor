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
