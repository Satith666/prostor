package main

import (
	"bytes"
	"embed"
	"os"
	"path/filepath"
	"strings"
	"sync"

	"prostor/internal/rules"
	"prostor/internal/winapi"
)

// Cleanup rules. They are data (rules/builtin.json — Prostor's own,
// rules/winapp2.json — converted from Winapp2, see cmd/rulegen). Paths use
// %ENV% variables, * wildcards and placeholders resolved at runtime: {steam},
// {locallow}. Only caches, temporary files, logs and dumps are listed:
// nothing here holds passwords, cookies, history, saved games or documents.

type Category struct {
	ID   string `json:"id"`
	Name string `json:"name"`
	Desc string `json:"desc"`
}

var categories = []Category{
	{"system", "Система Windows", "Временные файлы, кэш обновлений, отчёты об ошибках, журналы"},
	{"browsers", "Браузеры", "Только кэш. Пароли, вкладки, история и куки не затрагиваются"},
	{"apps", "Программы", "Кэш мессенджеров, плееров, редакторов и лаунчеров"},
	{"games", "Игры и видеокарта", "Кэш шейдеров, игровых лаунчеров и остатки драйверов"},
	{"dev", "Разработка", "Кэши пакетных менеджеров и сборщиков"},
	{"privacy", "Конфиденциальность", "Следы недавней активности"},
}

type Target struct {
	ID      string   `json:"id"`
	Cat     string   `json:"cat"`
	Name    string   `json:"name"`
	Desc    string   `json:"desc"`
	Admin   bool     `json:"admin"`
	Default bool     `json:"default"`
	Caution bool     `json:"caution"`
	Risk    string   `json:"risk"` // safe | caution | expert (set at load)
	Procs   []string `json:"procs"`
	Source  string   `json:"source,omitempty"` // "" = Prostor, "winapp2"
	Where   []string `json:"where"`            // folders, for the expert view

	Locs       []rules.Loc     `json:"-"`
	Excludes   []rules.Exclude `json:"-"`
	MinAge     int             `json:"-"` // only files older than N days
	Recent     bool            `json:"-"` // apply the "skip recent files" guard
	Special    string          `json:"-"` // recycle | takeown | services:a,b
	Detect     []string        `json:"-"` // registry keys of the program
	DetectFile []string        `json:"-"` // files or folders of the program
}

//go:embed rules/builtin.json rules/winapp2.json
var rulesFS embed.FS

// rulesInfo describes the loaded rule files (shown in settings and the self-test).
var rulesInfo struct {
	Builtin, Imported int
	W2Version         string
	W2Notice          string
}

var (
	targets   []Target
	targetIdx map[string]int
)

func loadRules() {
	targets = nil
	for _, name := range []string{"rules/builtin.json", "rules/winapp2.json"} {
		b, err := rulesFS.ReadFile(name)
		if err != nil {
			panic(err)
		}
		f, err := rules.Decode(bytes.NewReader(b))
		if err != nil {
			panic(name + ": " + err.Error())
		}
		if f.Source == "winapp2" {
			rulesInfo.Imported, rulesInfo.W2Version, rulesInfo.W2Notice = len(f.Rules), f.Version, f.Notice
		} else {
			rulesInfo.Builtin = len(f.Rules)
		}
		for _, r := range f.Rules {
			if r.Source == "prostor" {
				r.Source = ""
			}
			targets = append(targets, Target{ID: r.ID, Cat: r.Cat, Name: r.Name, Desc: r.Desc, Admin: r.Admin, Default: r.Default,
				Caution: r.Caution, Procs: r.Procs, Source: r.Source, Locs: r.Locs, Excludes: r.Excludes, MinAge: r.MinAge,
				Recent: r.Recent, Special: r.Special, Detect: r.Detect, DetectFile: r.DetectFile})
		}
	}
	indexTargets()
}

// indexTargets recomputes derived fields; tests call it after editing targets.
func indexTargets() {
	targetIdx = make(map[string]int, len(targets))
	for i := range targets {
		t := &targets[i]
		targetIdx[t.ID] = i
		t.Risk = riskOf(t)
		t.Where = whereOf(t)
	}
}

func whereOf(t *Target) []string {
	var out []string
	for _, l := range t.Locs {
		w := l.Path
		if len(l.Masks) > 0 {
			w += " (" + strings.Join(l.Masks, ", ") + ")"
		}
		if l.Flat {
			w += " — без подпапок"
		}
		out = append(out, w)
	}
	return out
}

// riskOf classifies a target for the interface: "safe" — the files are
// recreated by the programs themselves; "caution" — something noticeable
// changes (first runs get slower, the Recycle Bin is gone); "expert" — the
// action cannot be undone (Windows.old, upgrade files).
func riskOf(t *Target) string {
	switch {
	case t.Special == "takeown":
		return "expert"
	case t.Caution || t.Special == "recycle":
		return "caution"
	}
	return "safe"
}

func init() { loadRules() }

func targetByID(id string) *Target {
	if i, ok := targetIdx[id]; ok && i < len(targets) && targets[i].ID == id {
		return &targets[i]
	}
	return nil
}

// ---------------------------------------------------------------- detection

// detected: the program a rule belongs to is installed (or the rule does
// not say how to tell). The bait test plants every rule, so in the sandbox
// everything counts as installed.
func (t *Target) detected(dc *dirCache) bool {
	if sandboxMode || (len(t.Detect) == 0 && len(t.DetectFile) == 0) {
		return true
	}
	for _, k := range t.Detect {
		root, path, _ := strings.Cut(k, `\`)
		r := winapi.HKCU
		if root == "HKLM" {
			r = winapi.HKLM
		}
		if winapi.RegKeyExists(r, path) {
			return true
		}
	}
	for _, f := range t.DetectFile {
		if p := expandPath(f); p != "" && len(dc.glob(p)) > 0 {
			return true
		}
	}
	return false
}

// offered lists the rules shown to the user: every Prostor rule and the
// imported ones whose program is installed.
func offered(dc *dirCache) []*Target {
	out := make([]*Target, 0, 128)
	for i := range targets {
		t := &targets[i]
		if t.Source == "" || t.detected(dc) {
			out = append(out, t)
		}
	}
	return out
}

// ---------------------------------------------------------------- folder listings

// dirCache remembers folder listings for the duration of one scan, so the
// thousands of patterns of imported rules do not list the same folders
// (AppData, Program Files) over and over. Matching is case-insensitive, as on
// Windows.
type dirCache struct {
	mu   sync.Mutex
	dirs map[string][]string
}

func newDirCache() *dirCache { return &dirCache{dirs: map[string][]string{}} }

func (dc *dirCache) list(dir string) []string {
	key := lower(dir)
	dc.mu.Lock()
	names, ok := dc.dirs[key]
	dc.mu.Unlock()
	if ok {
		return names
	}
	ents, _ := os.ReadDir(dir)
	names = make([]string, 0, len(ents))
	for _, e := range ents {
		names = append(names, e.Name())
	}
	dc.mu.Lock()
	dc.dirs[key] = names
	dc.mu.Unlock()
	return names
}

// glob expands * and ? in any part of an absolute path.
func (dc *dirCache) glob(p string) []string {
	if !strings.ContainsAny(p, "*?") {
		if _, err := os.Lstat(p); err == nil {
			return []string{p}
		}
		return nil
	}
	sep := string(filepath.Separator)
	vol := filepath.VolumeName(p)
	parts := strings.Split(strings.TrimPrefix(p[len(vol):], sep), sep)
	cur := []string{vol + sep}
	for _, seg := range parts {
		if seg == "" {
			continue
		}
		var next []string
		if !strings.ContainsAny(seg, "*?") {
			for _, c := range cur {
				next = append(next, filepath.Join(c, seg))
			}
		} else {
			ls := lower(seg)
			for _, c := range cur {
				for _, n := range dc.list(c) {
					if ok, _ := filepath.Match(ls, lower(n)); ok {
						next = append(next, filepath.Join(c, n))
					}
				}
			}
		}
		if len(next) == 0 {
			return nil
		}
		cur = next
	}
	var out []string
	for _, c := range cur {
		if _, err := os.Lstat(c); err == nil {
			out = append(out, c)
		}
	}
	return out
}
