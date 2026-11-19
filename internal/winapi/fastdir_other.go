//go:build !windows

package winapi

import (
	"os"
	"syscall"
)

type DirEnt struct {
	Name  string
	Size  int64
	Alloc int64
	MTime int64
	Attr  uint32
	ID    uint64
	IsDir bool
	Link  bool
}

func LongPath(p string) string { return p }

// ReadDir mirrors the Windows version. Size on disk equals the logical size
// here (dev sandboxes use sparse files), the inode stands in for the file id.
func ReadDir(dir string) ([]DirEnt, error) {
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
		d := DirEnt{Name: e.Name(), IsDir: fi.IsDir(), MTime: fi.ModTime().Unix(), Link: fi.Mode()&os.ModeSymlink != 0}
		if !d.IsDir {
			d.Size, d.Alloc = fi.Size(), fi.Size()
		}
		if st, ok := fi.Sys().(*syscall.Stat_t); ok {
			d.ID = uint64(st.Ino)
		}
		out = append(out, d)
	}
	return out, nil
}

func BackgroundThread() func()    { return func() {} }
func EnableBackupPrivilege() bool { return false }

func Console(alloc bool, title string) (ok, created bool) { return true, false }
func WaitEnter()                                          {}
