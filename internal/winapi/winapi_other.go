//go:build !windows

// Non-Windows stubs so the app logic and UI can be exercised on a dev machine.
package winapi

import (
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"
)

const IsWindows = false

type Drive struct {
	Root  string `json:"root"`
	Label string `json:"label"`
	FS    string `json:"fs"`
	Type  string `json:"type"`
	Total uint64 `json:"total"`
	Free  uint64 `json:"free"`
}

func DiskSpace(path string) (total, free uint64, err error) {
	var st syscall.Statfs_t
	if err := syscall.Statfs(path, &st); err != nil {
		return 0, 0, err
	}
	return st.Blocks * uint64(st.Bsize), st.Bavail * uint64(st.Bsize), nil
}

// Drives on a dev machine: PROSTOR_FAKE_DISK="<dir>:<total GB>" pretends that
// <dir> is drive C: of the given size (used by the UI tests and screenshots).
func Drives() []Drive {
	if v := os.Getenv("PROSTOR_FAKE_DISK"); v != "" {
		if i := strings.LastIndex(v, ":"); i > 0 {
			dir := v[:i]
			total := uint64(atoiSafe(v[i+1:])) << 30
			used := fakeUsed(dir)
			if used > total {
				total = used + used/10
			}
			return []Drive{{Root: dir, Label: "", FS: "NTFS", Type: "fixed", Total: total, Free: total - used}}
		}
	}
	t, f, _ := DiskSpace("/")
	return []Drive{{Root: "/", Label: "System", FS: "ext4", Type: "fixed", Total: t, Free: f}}
}

func atoiSafe(s string) int {
	n := 0
	for _, c := range s {
		if c < '0' || c > '9' {
			break
		}
		n = n*10 + int(c-'0')
	}
	return n
}

// fakeUsed sums apparent file sizes under dir.
func fakeUsed(dir string) uint64 {
	var n uint64
	filepath.WalkDir(dir, func(p string, d os.DirEntry, err error) error {
		if err == nil && !d.IsDir() {
			if fi, e := d.Info(); e == nil {
				n += uint64(fi.Size())
			}
		}
		return nil
	})
	return n + 9<<30 // pretend the OS itself takes 9 GB that a scan cannot see
}

func Memory() (uint64, uint64)          { return 16 << 30, 7 << 30 }
func Uptime() time.Duration             { return 5 * time.Hour }
func OSVersion() string                 { return "Linux (режим разработки)" }
func IsAdmin() bool                     { return os.Geteuid() == 0 }
func ScreenSize() (int, int)            { return 1920, 1080 }
func FocusWindow(string) bool           { return false }
func RecycleBin() (int64, int64)        { return 0, 0 }
func EmptyRecycleBin() error            { return nil }
func DeleteOnReboot(string) error       { return errors.New("unsupported") }
func FileAttributes(os.FileInfo) uint32 { return 0 }
func PickFolder(string) string          { return os.Getenv("HOME") }
func DecodeConsole(b []byte) string     { return string(b) }
func HideWindow(*exec.Cmd)              {}

var ErrCancelled = errors.New("cancelled")

func RunElevated(exe, args string) error { return ErrCancelled }
func ShellOpen(string) error             { return nil }
func ShellRun(string, string) error      { return nil }
func Reveal(string) error                { return nil }

const (
	MB_OK              = 0x0
	MB_OKCANCEL        = 0x1
	MB_YESNO           = 0x4
	MB_ICONERROR       = 0x10
	MB_ICONQUESTION    = 0x20
	MB_ICONWARNING     = 0x30
	MB_ICONINFORMATION = 0x40
	IDOK               = 1
	IDYES              = 6
)

func MessageBox(title, text string, flags uint32) int { println(title + ": " + text); return IDYES }

func MoveToRecycleBin(paths []string) error {
	for _, p := range paths {
		if err := os.RemoveAll(p); err != nil {
			return err
		}
	}
	return nil
}

type Process struct {
	PID    uint32
	Parent uint32
	Name   string
}

func Processes() []Process {
	var out []Process
	ents, _ := os.ReadDir("/proc")
	for _, e := range ents {
		b, err := os.ReadFile(filepath.Join("/proc", e.Name(), "comm"))
		if err != nil {
			continue
		}
		out = append(out, Process{Name: strings.TrimSpace(string(b))})
	}
	return out
}

func KnownFolder(name string) string {
	home, _ := os.UserHomeDir()
	switch name {
	case "downloads":
		return filepath.Join(home, "Downloads")
	case "desktop":
		return filepath.Join(home, "Desktop")
	case "documents":
		return filepath.Join(home, "Documents")
	case "videos":
		return filepath.Join(home, "Videos")
	case "pictures":
		return filepath.Join(home, "Pictures")
	case "music":
		return filepath.Join(home, "Music")
	}
	return ""
}

type Root int

const (
	HKCU Root = 1
	HKLM Root = 2
)

type RegValue struct {
	Name  string
	Value string
}

func RegGetString(Root, string, string) (string, bool) { return "", false }
func RegGetDword(Root, string, string) (uint32, bool)  { return 0, false }
func RegGetBinary(Root, string, string) ([]byte, bool) { return nil, false }
func RegSubKeys(Root, string) []string                 { return nil }
func RegStringValues(Root, string) []RegValue          { return nil }
func RegSetString(Root, string, string, string) error  { return errors.New("unsupported") }
func RegSetDword(Root, string, string, uint32) error   { return errors.New("unsupported") }
func RegSetBinary(Root, string, string, []byte) error  { return errors.New("unsupported") }
func RegDeleteTree(Root, string) error                 { return errors.New("unsupported") }
func RegDeleteValue(Root, string, string) error        { return errors.New("unsupported") }

func ExpandEnv(s string) string {
	if !strings.Contains(s, "%") {
		return s
	}
	var b strings.Builder
	for {
		i := strings.IndexByte(s, '%')
		if i < 0 {
			b.WriteString(s)
			break
		}
		j := strings.IndexByte(s[i+1:], '%')
		if j < 0 {
			b.WriteString(s)
			break
		}
		name := s[i+1 : i+1+j]
		b.WriteString(s[:i])
		if v, ok := os.LookupEnv(name); ok && name != "" {
			b.WriteString(v)
		} else {
			b.WriteString(s[i : i+j+2])
		}
		s = s[i+j+2:]
	}
	return b.String()
}

func SetCmdLine(c *exec.Cmd, line string) {}

func CreateShortcut(lnk, target, args, workDir, icon, desc string) error {
	return errors.New("unsupported")
}

func KillByName(string) int { return 0 }

// FullNames: no 8.3 names outside Windows.
func FullNames(p string) string { return p }

// ShortPath: no 8.3 names outside Windows.
func ShortPath(p string) string { return "" }

func RegKeyExists(Root, string) bool { return false }
