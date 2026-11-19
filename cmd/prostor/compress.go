package main

// NTFS compression of rarely used folders (expert mode): transparent to
// programs, reversible, helps for documents, logs and old projects; video,
// photos and archives barely shrink. Windows' own and program folders are
// refused.

import (
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"prostor/internal/winapi"
)

func compressAllowed(path string) error {
	if !winapi.IsWindows {
		return errors.New("сжатие NTFS есть только в Windows")
	}
	path = filepath.Clean(path)
	fi, err := os.Stat(path)
	if err != nil || !fi.IsDir() {
		return errors.New("папка недоступна")
	}
	if !safeRoot(path) {
		return errors.New("эту папку сжимать нельзя: она системная или слишком общая")
	}
	lp := lower(path)
	for _, e := range []string{"SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData"} {
		if v := os.Getenv(e); v != "" {
			lv := lower(filepath.Clean(v))
			if lp == lv || strings.HasPrefix(lp, lv+string(filepath.Separator)) {
				return errors.New("папки Windows и установленных программ не сжимаются: это может замедлить их работу")
			}
		}
	}
	for _, d := range winapi.Drives() {
		if strings.HasPrefix(lp, lower(strings.TrimRight(d.Root, `\`))) && !strings.EqualFold(d.FS, "NTFS") {
			return errors.New("сжатие работает только на дисках NTFS")
		}
	}
	return nil
}

// allocSize sums the space files take on disk.
func allocSize(dir string) (size int64) {
	stack := []string{dir}
	for len(stack) > 0 {
		d := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		ents, err := winapi.ReadDir(d)
		if err != nil {
			continue
		}
		for _, e := range ents {
			switch {
			case e.Link:
			case e.IsDir:
				stack = append(stack, filepath.Join(d, e.Name))
			default:
				size += e.Alloc
			}
		}
	}
	return
}

func compressFolder(j *Job, path string) (map[string]any, error) {
	if err := compressAllowed(path); err != nil {
		return nil, err
	}
	j.SetPhase("Подсчёт места")
	before := allocSize(path)
	j.SetPhase("Сжатие файлов")
	j.SetCurrent(path)
	cmd := exec.Command("compact", "/c", "/s", "/i", "/q")
	cmd.Dir = path
	winapi.HideWindow(cmd)
	out, err := cmd.CombinedOutput()
	if j.Cancelled() {
		return nil, errors.New("остановлено")
	}
	after := allocSize(path)
	if err != nil && after >= before {
		return nil, errors.New("compact: " + strings.TrimSpace(winapi.DecodeConsole(out)))
	}
	if a := currentAnalysis(); a != nil && after < before {
		a.resize(path, after-before)
	}
	return map[string]any{"path": path, "before": before, "after": after, "saved": before - after}, nil
}
