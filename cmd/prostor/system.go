package main

import (
	"encoding/binary"
	"errors"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"prostor/internal/winapi"
)

// ---------------------------------------------------------------- installed programs

type Program struct {
	Key         string `json:"key"`
	Name        string `json:"name"`
	Publisher   string `json:"publisher"`
	Version     string `json:"version"`
	InstallDate string `json:"installDate"`
	Size        int64  `json:"size"`
	SizeSource  string `json:"sizeSource"` // registry | folder | ""
	Location    string `json:"location"`
	Uninstall   string `json:"-"`
	Scope       string `json:"scope"` // machine | user
}

var (
	programsMu    sync.Mutex
	programsCache []Program
	folderSizes   = map[string]int64{}
)

var uninstallRoots = []struct {
	root  winapi.Root
	path  string
	scope string
}{
	{winapi.HKLM, `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`, "machine"},
	{winapi.HKLM, `SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall`, "machine"},
	{winapi.HKCU, `Software\Microsoft\Windows\CurrentVersion\Uninstall`, "user"},
}

func listPrograms() []Program {
	seen := map[string]bool{}
	var out []Program
	for _, ur := range uninstallRoots {
		for _, sub := range winapi.RegSubKeys(ur.root, ur.path) {
			k := ur.path + `\` + sub
			name, _ := winapi.RegGetString(ur.root, k, "DisplayName")
			name = strings.TrimSpace(name)
			if name == "" {
				continue
			}
			if v, _ := winapi.RegGetDword(ur.root, k, "SystemComponent"); v == 1 {
				continue
			}
			if p, _ := winapi.RegGetString(ur.root, k, "ParentKeyName"); p != "" {
				continue
			}
			if rt, _ := winapi.RegGetString(ur.root, k, "ReleaseType"); rt == "Update" || rt == "Hotfix" || rt == "Security Update" {
				continue
			}
			un, _ := winapi.RegGetString(ur.root, k, "UninstallString")
			if un == "" {
				continue
			}
			ver, _ := winapi.RegGetString(ur.root, k, "DisplayVersion")
			dedupe := lower(name + "|" + ver)
			if seen[dedupe] {
				continue
			}
			seen[dedupe] = true
			p := Program{Name: name, Version: ver, Uninstall: un, Scope: ur.scope}
			p.Key = map[winapi.Root]string{winapi.HKLM: "HKLM", winapi.HKCU: "HKCU"}[ur.root] + `\` + k
			p.Publisher, _ = winapi.RegGetString(ur.root, k, "Publisher")
			if d, _ := winapi.RegGetString(ur.root, k, "InstallDate"); len(d) == 8 {
				p.InstallDate = d[:4] + "-" + d[4:6] + "-" + d[6:]
			}
			if kb, ok := winapi.RegGetDword(ur.root, k, "EstimatedSize"); ok && kb > 0 {
				p.Size = int64(kb) * 1024
				p.SizeSource = "registry"
			}
			loc, _ := winapi.RegGetString(ur.root, k, "InstallLocation")
			p.Location = strings.Trim(strings.TrimSpace(loc), `"`)
			out = append(out, p)
		}
	}
	programsMu.Lock()
	for i := range out {
		if out[i].Size == 0 && out[i].Location != "" {
			if s, ok := folderSizes[lower(out[i].Location)]; ok && s > 0 {
				out[i].Size, out[i].SizeSource = s, "folder"
			}
		}
	}
	programsCache = out
	programsMu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].Size > out[j].Size })
	return out
}

// measurePrograms sizes the install folders of programs that do not report a size.
func measurePrograms(j *Job) (any, error) {
	progs := listPrograms()
	var todo []Program
	for _, p := range progs {
		if p.SizeSource == "" && p.Location != "" && safeRoot(p.Location) {
			if st, err := os.Stat(p.Location); err == nil && st.IsDir() {
				todo = append(todo, p)
			}
		}
	}
	j.Total.Store(int64(len(todo)))
	for _, p := range todo {
		if j.Cancelled() {
			break
		}
		j.SetCurrent(p.Name)
		s, _ := dirSize(p.Location, j.Cancelled)
		programsMu.Lock()
		folderSizes[lower(p.Location)] = s
		programsMu.Unlock()
		j.Done.Add(1)
	}
	return listPrograms(), nil
}

// splitCommand separates an executable path from its arguments.
func splitCommand(s string) (string, string) {
	s = strings.TrimSpace(s)
	if strings.HasPrefix(s, `"`) {
		if i := strings.Index(s[1:], `"`); i >= 0 {
			return s[1 : i+1], strings.TrimSpace(s[i+2:])
		}
	}
	ls := lower(s)
	if i := strings.Index(ls, ".exe"); i >= 0 {
		return s[:i+4], strings.TrimSpace(s[i+4:])
	}
	if i := strings.IndexByte(s, ' '); i >= 0 {
		return s[:i], strings.TrimSpace(s[i+1:])
	}
	return s, ""
}

func uninstallProgram(key string) error {
	programsMu.Lock()
	var prog *Program
	for i := range programsCache {
		if programsCache[i].Key == key {
			p := programsCache[i]
			prog = &p
		}
	}
	programsMu.Unlock()
	if prog == nil {
		return errors.New("программа не найдена — обновите список")
	}
	exe, args := splitCommand(winapi.ExpandEnv(prog.Uninstall))
	if exe == "" {
		return errors.New("у программы нет команды удаления")
	}
	return winapi.ShellRun(exe, args)
}

// ---------------------------------------------------------------- startup items

type StartupItem struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	Command  string `json:"command"`
	Path     string `json:"path"`
	Source   string `json:"source"`
	Scope    string `json:"scope"`
	Enabled  bool   `json:"enabled"`
	Exists   bool   `json:"exists"`
	NeedsAdm bool   `json:"needsAdmin"`

	approvedRoot winapi.Root
	approvedKey  string
	approvedName string
}

const approvedBase = `Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\`

func approvalState(root winapi.Root, key, name string) bool {
	b, ok := winapi.RegGetBinary(root, approvedBase+key, name)
	if !ok || len(b) == 0 {
		return true
	}
	return b[0]&1 == 0 // even = enabled (02/06), odd = disabled (03/07)
}

func listStartup() []StartupItem {
	var out []StartupItem
	runKeys := []struct {
		root     winapi.Root
		path     string
		approved string
		source   string
		scope    string
	}{
		{winapi.HKCU, `Software\Microsoft\Windows\CurrentVersion\Run`, "Run", "Реестр (пользователь)", "user"},
		{winapi.HKLM, `SOFTWARE\Microsoft\Windows\CurrentVersion\Run`, "Run", "Реестр (все пользователи)", "machine"},
		{winapi.HKLM, `SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run`, "Run32", "Реестр (все пользователи, 32-бит)", "machine"},
	}
	for _, rk := range runKeys {
		for _, v := range winapi.RegStringValues(rk.root, rk.path) {
			if strings.TrimSpace(v.Value) == "" {
				continue
			}
			cmd := winapi.ExpandEnv(v.Value)
			exe, _ := splitCommand(cmd)
			_, err := os.Stat(exe)
			out = append(out, StartupItem{
				ID: rk.source + "|" + v.Name, Name: v.Name, Command: cmd, Path: exe, Source: rk.source, Scope: rk.scope,
				Enabled: approvalState(rk.root, rk.approved, v.Name), Exists: err == nil, NeedsAdm: rk.scope == "machine",
				approvedRoot: rk.root, approvedKey: rk.approved, approvedName: v.Name,
			})
		}
	}
	folders := []struct {
		dir    string
		root   winapi.Root
		source string
		scope  string
	}{
		{filepath.Join(os.Getenv("APPDATA"), `Microsoft\Windows\Start Menu\Programs\Startup`), winapi.HKCU, "Папка «Автозагрузка» (пользователь)", "user"},
		{filepath.Join(os.Getenv("ProgramData"), `Microsoft\Windows\Start Menu\Programs\StartUp`), winapi.HKLM, "Папка «Автозагрузка» (все пользователи)", "machine"},
	}
	for _, f := range folders {
		ents, _ := os.ReadDir(f.dir)
		for _, e := range ents {
			if e.IsDir() || lower(e.Name()) == "desktop.ini" {
				continue
			}
			p := filepath.Join(f.dir, e.Name())
			out = append(out, StartupItem{
				ID: f.source + "|" + e.Name(), Name: strings.TrimSuffix(e.Name(), filepath.Ext(e.Name())), Command: p, Path: p,
				Source: f.source, Scope: f.scope, Enabled: approvalState(f.root, "StartupFolder", e.Name()), Exists: true,
				NeedsAdm: f.scope == "machine", approvedRoot: f.root, approvedKey: "StartupFolder", approvedName: e.Name(),
			})
		}
	}
	sort.SliceStable(out, func(i, j int) bool { return lower(out[i].Name) < lower(out[j].Name) })
	return out
}

func setStartup(id string, enabled bool) error {
	for _, it := range listStartup() {
		if it.ID != id {
			continue
		}
		data := make([]byte, 12)
		if enabled {
			data[0] = 0x02
		} else {
			data[0] = 0x03
			ft := time.Now().UnixNano()/100 + 116444736000000000 // FILETIME
			binary.LittleEndian.PutUint64(data[4:], uint64(ft))
		}
		return winapi.RegSetBinary(it.approvedRoot, approvedBase+it.approvedKey, it.approvedName, data)
	}
	return errors.New("элемент автозагрузки не найден")
}
