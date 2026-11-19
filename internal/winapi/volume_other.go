//go:build !windows

package winapi

import (
	"os"
	"path/filepath"
	"syscall"
)

func devOf(p string) (uint64, bool) {
	var st syscall.Stat_t
	if syscall.Lstat(p, &st) != nil {
		return 0, false
	}
	return uint64(st.Dev), true
}

// VolumeRoot returns the mount point of the file system that holds path.
func VolumeRoot(path string) string {
	p := filepath.Clean(path)
	for {
		if _, err := os.Lstat(p); err == nil {
			break
		}
		parent := filepath.Dir(p)
		if parent == p {
			return "/"
		}
		p = parent
	}
	dev, ok := devOf(p)
	if !ok {
		return "/"
	}
	for {
		parent := filepath.Dir(p)
		if parent == p {
			return p
		}
		if d, ok := devOf(parent); !ok || d != dev {
			return p
		}
		p = parent
	}
}

func SameVolume(a, b string) bool { return VolumeRoot(a) == VolumeRoot(b) }

func SetHidden(string) error { return nil }
