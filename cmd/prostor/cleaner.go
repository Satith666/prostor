package main

import (
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"prostor/internal/winapi"
)

const (
	attrReadonly   = 0x1
	attrReparse    = 0x400
	attrOffline    = 0x1000
	attrRecallOpen = 0x40000
	attrRecallData = 0x400000
)

// ---------------------------------------------------------------- path resolution

func placeholder(name string) string {
	switch name {
	case "steam":
		if sandboxMode {
			return filepath.Join(os.Getenv("ProgramFiles(x86)"), "Steam")
		}
		if p, ok := winapi.RegGetString(winapi.HKCU, `Software\Valve\Steam`, "SteamPath"); ok && p != "" {
			return filepath.Clean(filepath.FromSlash(p))
		}
		if p, ok := winapi.RegGetString(winapi.HKLM, `SOFTWARE\WOW6432Node\Valve\Steam`, "InstallPath"); ok && p != "" {
			return filepath.Clean(p)
		}
		return ""
	case "locallow":
		if up := os.Getenv("USERPROFILE"); up != "" {
			return filepath.Join(up, "AppData", "LocalLow")
		}
	}
	return ""
}

func expandPath(p string) string {
	for {
		i := strings.IndexByte(p, '{')
		if i < 0 {
			break
		}
		j := strings.IndexByte(p[i:], '}')
		if j < 0 {
			break
		}
		v := placeholder(p[i+1 : i+j])
		if v == "" {
			return ""
		}
		p = p[:i] + v + p[i+j+1:]
	}
	p = winapi.ExpandEnv(p)
	if strings.Contains(p, "%") {
		return "" // unresolved variable
	}
	if !winapi.IsWindows {
		p = strings.ReplaceAll(p, `\`, "/")
	}
	return filepath.Clean(p)
}

var forbiddenRoots []string

func initForbidden() {
	env := []string{"SystemRoot", "SystemDrive", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData",
		"USERPROFILE", "APPDATA", "LOCALAPPDATA", "PUBLIC", "HOMEDRIVE", "HOME"}
	for _, e := range env {
		if v := os.Getenv(e); v != "" {
			forbiddenRoots = append(forbiddenRoots, lower(filepath.Clean(v)))
		}
	}
	if sr := os.Getenv("SystemRoot"); sr != "" {
		forbiddenRoots = append(forbiddenRoots, lower(filepath.Join(sr, "System32")), lower(filepath.Join(sr, "SysWOW64")))
	}
	if up := os.Getenv("USERPROFILE"); up != "" {
		forbiddenRoots = append(forbiddenRoots, lower(filepath.Dir(up)))
		for _, k := range []string{"desktop", "documents", "downloads", "pictures", "videos", "music"} {
			if p := winapi.KnownFolder(k); p != "" {
				forbiddenRoots = append(forbiddenRoots, lower(filepath.Clean(p)))
			}
		}
	}
}

// safeRoot refuses to treat drive roots, system folders or profile roots as
// cleanup roots, no matter how the environment is configured.
func safeRoot(p string) bool {
	c := filepath.Clean(p)
	vol := filepath.VolumeName(c)
	rest := strings.Trim(strings.TrimPrefix(c, vol), `\/`)
	if rest == "" || !strings.ContainsAny(rest, `\/`) && len(rest) < 3 {
		return false
	}
	lc := lower(c)
	for _, f := range forbiddenRoots {
		if lc == f {
			return false
		}
	}
	return true
}

type root struct {
	path   string
	isFile bool
	info   fs.FileInfo
}

func resolveRoots(t *Target) []root {
	seen := map[string]bool{}
	var out []root
	var all []string
	for _, pat := range t.Paths {
		p := expandPath(pat)
		if p == "" {
			continue
		}
		if strings.ContainsAny(p, "*?") {
			m, _ := filepath.Glob(p)
			all = append(all, m...)
		} else {
			all = append(all, p)
		}
	}
	// shortest first, so a parent folder always wins over its own subfolders
	// (e.g. %LOCALAPPDATA%\Temp vs a %TEMP% that points to Temp\2)
	sort.SliceStable(all, func(a, b int) bool { return len(all[a]) < len(all[b]) })
	{
		matches := all
		for _, m := range matches {
			key := lower(m)
			if seen[key] || !safeRoot(m) {
				continue
			}
			fi, err := os.Lstat(m)
			if err != nil || fi.Mode()&os.ModeSymlink != 0 || winapi.FileAttributes(fi)&attrReparse != 0 {
				continue
			}
			// skip roots nested inside an already selected root
			nested := false
			for _, r := range out {
				if !r.isFile && strings.HasPrefix(key, lower(r.path)+string(filepath.Separator)) {
					nested = true
					break
				}
			}
			if nested {
				continue
			}
			seen[key] = true
			out = append(out, root{path: m, isFile: !fi.IsDir(), info: fi})
		}
	}
	return out
}

// ---------------------------------------------------------------- walking

type walkOpts struct {
	masks       []string
	olderThan   time.Time // zero = no limit
	flat        bool
	excludes    []string
	removeLinks bool
	cancel      func() bool
	touched     map[string]bool // folders we emptied ourselves (their mtime is now fresh)
}

func newWalkOpts(t *Target, s Settings, cancel func() bool) *walkOpts {
	o := &walkOpts{masks: t.Masks, flat: t.Flat, cancel: cancel, removeLinks: t.Special == "takeown"}
	now := time.Now()
	if t.MinAge > 0 {
		o.olderThan = now.Add(-time.Duration(t.MinAge) * 24 * time.Hour)
	}
	if t.Recent && s.SkipRecentHours > 0 {
		g := now.Add(-time.Duration(s.SkipRecentHours) * time.Hour)
		if o.olderThan.IsZero() || g.Before(o.olderThan) {
			o.olderThan = g
		}
	}
	for _, e := range s.Excludes {
		e = strings.TrimSpace(e)
		if e != "" {
			o.excludes = append(o.excludes, lower(filepath.Clean(winapi.ExpandEnv(e))))
		}
	}
	return o
}

func (o *walkOpts) excluded(path string) bool {
	if len(o.excludes) == 0 {
		return false
	}
	lp := lower(path)
	for _, e := range o.excludes {
		if lp == e || strings.HasPrefix(lp, e+string(filepath.Separator)) {
			return true
		}
	}
	return false
}

func (o *walkOpts) matches(name string, fi fs.FileInfo) bool {
	if !o.olderThan.IsZero() && fi.ModTime().After(o.olderThan) {
		return false
	}
	if len(o.masks) == 0 {
		return true
	}
	ln := lower(name)
	for _, m := range o.masks {
		if ok, _ := filepath.Match(m, ln); ok {
			return true
		}
	}
	return false
}

func isLink(fi fs.FileInfo) bool {
	return fi.Mode()&(os.ModeSymlink|os.ModeIrregular) != 0 || winapi.FileAttributes(fi)&attrReparse != 0
}

// walk visits matching files under dir. Reparse points (junctions, symlinks)
// are never followed.
func (o *walkOpts) walk(dir string, fn func(path string, fi fs.FileInfo), link func(path string)) {
	if o.cancel != nil && o.cancel() {
		return
	}
	ents, err := os.ReadDir(dir)
	if err != nil {
		return
	}
	for _, e := range ents {
		p := filepath.Join(dir, e.Name())
		if o.excluded(p) {
			continue
		}
		fi, err := e.Info()
		if err != nil {
			continue
		}
		if isLink(fi) {
			if link != nil {
				link(p)
			}
			continue
		}
		if fi.IsDir() {
			if !o.flat {
				o.walk(p, fn, link)
			}
			continue
		}
		if o.matches(e.Name(), fi) {
			fn(p, fi)
		}
	}
}

func dirSize(dir string, cancel func() bool) (size, files int64) {
	o := &walkOpts{cancel: cancel}
	o.walk(dir, func(_ string, fi fs.FileInfo) {
		size += fi.Size()
		files++
	}, nil)
	return
}

// ---------------------------------------------------------------- scanning

type ScanItem struct {
	ID      string   `json:"id"`
	Size    int64    `json:"size"`
	Files   int64    `json:"files"`
	Found   bool     `json:"found"`
	Running []string `json:"running"`
}

func runningProcs(own map[uint32]bool) map[string]bool {
	out := map[string]bool{}
	for _, p := range winapi.Processes() {
		if own[p.PID] {
			continue
		}
		out[lower(p.Name)] = true
	}
	return out
}

func scanTarget(t *Target, s Settings, cancel func() bool) ScanItem {
	it := ScanItem{ID: t.ID}
	if t.Special == "recycle" {
		b, n := winapi.RecycleBin()
		it.Size, it.Files, it.Found = b, n, true
		return it
	}
	o := newWalkOpts(t, s, cancel)
	for _, r := range resolveRoots(t) {
		it.Found = true
		if r.isFile {
			if o.matches(r.info.Name(), r.info) {
				it.Size += r.info.Size()
				it.Files++
			}
			continue
		}
		o.walk(r.path, func(_ string, fi fs.FileInfo) {
			it.Size += fi.Size()
			it.Files++
		}, nil)
	}
	return it
}

func scanTargets(j *Job, ids []string, s Settings, own map[uint32]bool) []ScanItem {
	var list []*Target
	for _, id := range ids {
		if t := targetByID(id); t != nil {
			list = append(list, t)
		}
	}
	j.Total.Store(int64(len(list)))
	running := runningProcs(own)
	res := make([]ScanItem, len(list))
	var wg sync.WaitGroup
	sem := make(chan struct{}, 4)
	for i, t := range list {
		wg.Add(1)
		sem <- struct{}{}
		go func(i int, t *Target) {
			defer func() { <-sem; wg.Done() }()
			defer winapi.BackgroundThread()()
			if j.Cancelled() {
				res[i] = ScanItem{ID: t.ID}
				return
			}
			j.SetCurrent(t.Name)
			it := scanTarget(t, s, j.Cancelled)
			for _, p := range t.Procs {
				if running[p] {
					it.Running = append(it.Running, p)
				}
			}
			res[i] = it
			j.Bytes.Add(it.Size)
			j.Files.Add(it.Files)
			j.Done.Add(1)
		}(i, t)
	}
	wg.Wait()
	if !j.Cancelled() {
		rememberScan(res)
	}
	return res
}

// The last scan results are reused by the advisor, so starting the program
// does not walk the same folders twice.
var lastScan struct {
	sync.Mutex
	at    time.Time
	items map[string]ScanItem
}

func rememberScan(items []ScanItem) {
	lastScan.Lock()
	defer lastScan.Unlock()
	if lastScan.items == nil || time.Since(lastScan.at) > 5*time.Minute {
		lastScan.items = map[string]ScanItem{}
	}
	for _, it := range items {
		lastScan.items[it.ID] = it
	}
	lastScan.at = time.Now()
}

func forgetScan() {
	lastScan.Lock()
	lastScan.items = nil
	lastScan.Unlock()
}

// cachedScan returns fresh results for all ids, or nil.
func cachedScan(ids []string, maxAge time.Duration) []ScanItem {
	lastScan.Lock()
	defer lastScan.Unlock()
	if lastScan.items == nil || time.Since(lastScan.at) > maxAge {
		return nil
	}
	out := make([]ScanItem, 0, len(ids))
	for _, id := range ids {
		it, ok := lastScan.items[id]
		if !ok {
			return nil
		}
		out = append(out, it)
	}
	return out
}

// ---------------------------------------------------------------- cleaning

type CleanItem struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	Freed     int64  `json:"freed"`
	Files     int64  `json:"files"`
	Failed    int64  `json:"failed"`
	Scheduled int64  `json:"scheduled"`
	Note      string `json:"note"`
}

type CleanResult struct {
	Items     []CleanItem `json:"items"`
	Freed     int64       `json:"freed"`
	Files     int64       `json:"files"`
	Failed    int64       `json:"failed"`
	Scheduled int64       `json:"scheduled"`
	Seconds   float64     `json:"seconds"`
	Journal   string      `json:"journal"`
}

func removeFile(p string) error {
	err := os.Remove(p)
	if err == nil || os.IsNotExist(err) {
		return nil
	}
	// clear the read-only attribute and retry
	if os.Chmod(p, 0o666) == nil {
		if err2 := os.Remove(p); err2 == nil {
			return nil
		}
	}
	return err
}

// removeEmptyDirs deletes empty subfolders bottom-up, never the root itself.
func removeEmptyDirs(dir string, isRoot bool, o *walkOpts) bool {
	ents, err := os.ReadDir(dir)
	if err != nil {
		return false
	}
	empty := true
	for _, e := range ents {
		p := filepath.Join(dir, e.Name())
		fi, err := e.Info()
		if err != nil || isLink(fi) || !fi.IsDir() || o.excluded(p) {
			empty = false
			continue
		}
		if !removeEmptyDirs(p, false, o) {
			empty = false
		} else if o.touched != nil {
			o.touched[lower(dir)] = true
		}
	}
	if isRoot || !empty {
		return false
	}
	// A folder that became empty only because we deleted its files has a fresh
	// mtime; skip the age guard for it, but keep the guard for folders found empty.
	if fi, err := os.Stat(dir); err == nil && !o.olderThan.IsZero() && fi.ModTime().After(o.olderThan) && !o.touched[lower(dir)] {
		return false
	}
	if err := os.Remove(dir); err != nil {
		os.Chmod(dir, 0o777)
		return os.Remove(dir) == nil
	}
	return true
}

func netService(action, name string) {
	runQuiet("net", action, name, "/y")
}

func takeOwnership(path string) {
	// SIDs instead of group names: works on any Windows language.
	runQuiet("icacls", path, "/setowner", "*S-1-5-32-544", "/T", "/C", "/Q")
	runQuiet("icacls", path, "/grant", "*S-1-5-32-544:F", "/T", "/C", "/Q")
	runQuiet("attrib", "-r", "-s", "-h", path+`\*`, "/S", "/D")
}

func cleanTarget(j *Job, t *Target, s Settings, admin bool, jr *journal) CleanItem {
	ci := CleanItem{ID: t.ID, Name: t.Name}
	if t.Special == "recycle" {
		if sandboxMode {
			ci.Note = "Пропущено в песочнице"
			return ci
		}
		b, n := winapi.RecycleBin()
		if err := winapi.EmptyRecycleBin(); err != nil {
			ci.Note = "Не удалось очистить корзину"
			ci.Failed = n
			return ci
		}
		ci.Freed, ci.Files = b, n
		jr.add("d", fmt.Sprintf("Корзина: %d объектов", n), b, t.ID)
		return ci
	}
	if t.Admin && !admin {
		ci.Note = "Нужны права администратора"
		return ci
	}
	var services []string
	if strings.HasPrefix(t.Special, "services:") && admin && !sandboxMode {
		services = strings.Split(strings.TrimPrefix(t.Special, "services:"), ",")
		for _, svc := range services {
			j.SetCurrent("Остановка службы " + svc)
			netService("stop", svc)
		}
		defer func() {
			for _, svc := range services {
				netService("start", svc)
			}
		}()
	}
	o := newWalkOpts(t, s, j.Cancelled)
	o.touched = map[string]bool{}
	onFail := func(p string, size int64) {
		ci.Failed++
		if s.DeleteOnReboot && admin && winapi.IsWindows && !sandboxMode {
			if winapi.DeleteOnReboot(p) == nil {
				ci.Scheduled++
				jr.add("s", p, size, t.ID)
				return
			}
		}
		jr.add("f", p, size, t.ID)
	}
	for _, r := range resolveRoots(t) {
		if j.Cancelled() {
			break
		}
		j.SetCurrent(r.path)
		if r.isFile {
			if o.matches(r.info.Name(), r.info) {
				if removeFile(r.path) == nil {
					ci.Freed += r.info.Size()
					ci.Files++
					jr.add("d", r.path, r.info.Size(), t.ID)
				} else {
					onFail(r.path, r.info.Size())
				}
			}
			continue
		}
		if t.Special == "takeown" && admin && winapi.IsWindows && !sandboxMode {
			j.SetCurrent("Получение доступа: " + r.path)
			takeOwnership(r.path)
		}
		var linkFn func(string)
		if o.removeLinks {
			linkFn = func(p string) { os.Remove(p) } // removes the link itself, never its target
		}
		o.walk(r.path, func(p string, fi fs.FileInfo) {
			if removeFile(p) == nil {
				o.touched[lower(filepath.Dir(p))] = true
				ci.Freed += fi.Size()
				ci.Files++
				j.Bytes.Add(fi.Size())
				j.Files.Add(1)
				jr.add("d", p, fi.Size(), t.ID)
			} else {
				onFail(p, fi.Size())
			}
		}, linkFn)
		if !t.KeepDirs && !t.Flat {
			removeEmptyDirs(r.path, true, o)
		}
		if t.Special == "takeown" {
			os.Remove(r.path) // the leftover folder itself
		}
	}
	return ci
}

func cleanTargets(j *Job, ids []string, s Settings, admin bool, mode string) CleanResult {
	defer forgetScan()
	start := time.Now()
	var res CleanResult
	var list []*Target
	for _, id := range ids {
		if t := targetByID(id); t != nil {
			list = append(list, t)
		}
	}
	j.Total.Store(int64(len(list)))
	jr := openJournal("clean")
	for _, t := range list {
		if j.Cancelled() {
			break
		}
		j.SetPhase(t.Name)
		ci := cleanTarget(j, t, s, admin, jr)
		res.Items = append(res.Items, ci)
		res.Freed += ci.Freed
		res.Files += ci.Files
		res.Failed += ci.Failed
		res.Scheduled += ci.Scheduled
		j.Done.Add(1)
	}
	res.Seconds = time.Since(start).Seconds()
	res.Journal = jr.close()
	sort.SliceStable(res.Items, func(a, b int) bool { return res.Items[a].Freed > res.Items[b].Freed })
	if res.Files > 0 || res.Freed > 0 {
		e := HistoryEntry{Time: time.Now(), Mode: mode, Freed: res.Freed, Files: res.Files, Failed: res.Failed, Journal: res.Journal}
		for _, it := range res.Items {
			if it.Freed > 0 || it.Files > 0 {
				e.Items = append(e.Items, HistoryItem{Name: it.Name, Freed: it.Freed, Files: it.Files})
			}
		}
		addHistory(e)
	}
	return res
}

// selectedIDs returns the targets the user has ticked (or the defaults).
func selectedIDs(s Settings, admin bool, safeOnly bool) []string {
	var ids []string
	for _, t := range targets {
		on := t.Default
		if v, ok := s.Selected[t.ID]; ok {
			on = v
		}
		if !on || (t.Admin && !admin) {
			continue
		}
		if safeOnly && (t.Caution || t.Special == "takeown") {
			continue
		}
		ids = append(ids, t.ID)
	}
	return ids
}

// closeProcesses asks the named apps to exit, then force-closes leftovers.
func closeProcesses(names []string, own map[uint32]bool) {
	want := map[string]bool{}
	for _, n := range names {
		want[lower(n)] = true
	}
	pids := func() []uint32 {
		var out []uint32
		for _, p := range winapi.Processes() {
			if want[lower(p.Name)] && !own[p.PID] {
				out = append(out, p.PID)
			}
		}
		return out
	}
	kill := func(force bool) {
		for _, pid := range pids() {
			args := []string{"/PID", strconv.Itoa(int(pid))}
			if force {
				args = append(args, "/F", "/T")
			}
			c := exec.Command("taskkill", args...)
			winapi.HideWindow(c)
			c.Run()
		}
	}
	kill(false)
	for i := 0; i < 12 && len(pids()) > 0; i++ {
		time.Sleep(250 * time.Millisecond)
	}
	if len(pids()) > 0 {
		kill(true)
		time.Sleep(500 * time.Millisecond)
	}
}

// sandboxMode is set by the bait test: everything that reaches outside the
// redirected folders (Recycle Bin, services, ownership changes, registry
// lookups) is skipped.
var sandboxMode bool
