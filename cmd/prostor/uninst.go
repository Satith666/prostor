package main

// Removing a program completely. First its own uninstaller runs (silently
// when it supports that: MSI, Inno Setup, NSIS or a QuietUninstallString),
// then Prostor looks for what it left behind: folders, registry keys,
// services, scheduled tasks and shortcuts. Each find has a confidence:
// "high" — the program's own folder or something pointing into it, ticked by
// default; "medium" — only the name matches, the user decides. Files go to
// the quarantine, registry keys are exported to a .reg file before deletion.
//
// What belongs to the program is recorded before its uninstaller runs
// (services, tasks and shortcuts that point into its folders), because the
// uninstaller removes the folders those links point to.

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
	"unicode"

	"prostor/internal/winapi"
)

type Leftover struct {
	ID   int    `json:"id"`
	Kind string `json:"kind"` // folder | registry | service | task | shortcut
	Path string `json:"path"`
	Size int64  `json:"size"`
	Sure string `json:"sure"` // high | medium
	Why  string `json:"why"`
}

type UninstallReport struct {
	Name      string     `json:"name"`
	Key       string     `json:"key"`
	Quiet     bool       `json:"quiet"`
	Ran       bool       `json:"ran"`
	Removed   bool       `json:"removed"` // the program is gone from the installed list
	Seconds   float64    `json:"seconds"`
	Note      string     `json:"note,omitempty"`
	Leftovers []Leftover `json:"leftovers"`
}

// ---------------------------------------------------------------- names

var (
	archRe  = regexp.MustCompile(`(?i)\((x64|x86|64[- ]?bit|32[- ]?bit|amd64|arm64)\)|\b(x64|x86|64[- ]?bit|32[- ]?bit|amd64|arm64)\b`)
	verRe   = regexp.MustCompile(`(?i)\bv?\d+(\.\d+)+[\w.-]*|\(\s*\)|\bversion\b`)
	spaceRe = regexp.MustCompile(`\s+`)
)

// genericNames are folder and key names shared by many programs.
var genericNames = map[string]bool{}

func init() {
	for _, w := range strings.Fields(`microsoft windows google mozilla adobe intel nvidia amd apple oracle java visual update updater tools tool
		driver drivers runtime runtimes media player free pro the app apps setup system common commonfiles data cache temp tmp program programs
		files software service services new open my user users documents packages package packagecache classes policies wow6432node clients
		registeredapplications installer installshield uninstall logs log config settings plugins extensions local roaming locallow desktop
		start menu startmenu shared lib bin`) {
		genericNames[w] = true
	}
}

// nameKey keeps letters and digits only: "Notepad++" and "notepad" match.
func nameKey(s string) string {
	var b strings.Builder
	for _, r := range strings.ToLower(s) {
		if unicode.IsLetter(r) || unicode.IsDigit(r) {
			b.WriteRune(r)
		}
	}
	return b.String()
}

// baseName drops versions and architecture: "7-Zip 24.08 (x64)" → "7-Zip".
var parenRe = regexp.MustCompile(`\([^()]*\)|\[[^\]]*\]`)

func baseName(name string) string {
	s := parenRe.ReplaceAllString(name, " ") // "(x64 ru)", "(64-bit)", "[beta]"
	s = archRe.ReplaceAllString(s, " ")
	s = verRe.ReplaceAllString(s, " ")
	s = strings.Trim(spaceRe.ReplaceAllString(s, " "), " -–—_,.")
	if s == "" {
		return name
	}
	return s
}

// nameForms are the keys a program's folders and registry keys may carry.
func nameForms(name, publisher string) map[string]bool {
	out := map[string]bool{}
	add := func(s string) {
		if k := nameKey(s); len(k) >= 3 && !genericNames[k] {
			out[k] = true
		}
	}
	b := baseName(name)
	add(b)
	add(name)
	words := strings.Fields(b)
	if len(words) > 1 {
		add(words[0]) // "VLC media player" → vlc; "Mozilla Firefox" → skipped as generic
		if pk := nameKey(publisher); pk != "" && nameKey(words[0]) == pk {
			add(strings.Join(words[1:], " ")) // "Mozilla Firefox" → firefox
		}
	}
	return out
}

// ---------------------------------------------------------------- what a program owns

type ownedService struct{ name, image string }
type ownedTask struct{ name, file string }

type progSnap struct {
	Program
	root     winapi.Root
	path     string // uninstall key, without the root
	dirs     []string
	services []ownedService
	tasks    []ownedTask
	links    []string
	forms    map[string]bool
	pubKey   string
}

func findProgram(key string) (*progSnap, error) {
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
		for _, p := range listPrograms() {
			if p.Key == key {
				q := p
				prog = &q
			}
		}
	}
	if prog == nil {
		return nil, errors.New("программа не найдена — обновите список")
	}
	s := &progSnap{Program: *prog, forms: nameForms(prog.Name, prog.Publisher), pubKey: nameKey(prog.Publisher)}
	rootName, path, _ := strings.Cut(prog.Key, `\`)
	s.root, s.path = winapi.HKLM, path
	if rootName == "HKCU" {
		s.root = winapi.HKCU
	}
	// folders the program certainly owns
	addDir := func(d string) {
		d = filepath.Clean(strings.Trim(strings.TrimSpace(winapi.ExpandEnv(d)), `"`))
		if d == "." || d == "" || !filepath.IsAbs(d) || !safeRoot(d) || isSharedDir(d) {
			return
		}
		for _, x := range s.dirs {
			if strings.EqualFold(x, d) {
				return
			}
		}
		s.dirs = append(s.dirs, d)
	}
	addDir(prog.Location)
	if exe, _ := splitCommand(winapi.ExpandEnv(prog.Uninstall)); exe != "" && !strings.Contains(lower(exe), "msiexec") {
		addDir(filepath.Dir(exe))
	}
	if icon, _ := winapi.RegGetString(s.root, s.path, "DisplayIcon"); icon != "" {
		icon, _, _ = strings.Cut(icon, ",")
		icon = strings.Trim(icon, `"`)
		if !strings.Contains(lower(icon), `\windows\`) && !strings.Contains(lower(icon), `\installer\`) {
			addDir(filepath.Dir(icon))
		}
	}
	s.services = servicesIn(s.dirs)
	s.tasks = tasksIn(s.dirs, nil)
	s.links = shortcutsInto(s.dirs)
	return s, nil
}

// isSharedDir: folders that hold many programs, never a program's own.
func isSharedDir(d string) bool {
	ld := lower(filepath.Clean(d))
	for _, e := range []string{"ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "APPDATA", "LOCALAPPDATA",
		"CommonProgramFiles", "CommonProgramFiles(x86)", "SystemRoot", "USERPROFILE", "PUBLIC"} {
		if v := os.Getenv(e); v != "" && ld == lower(filepath.Clean(v)) {
			return true
		}
	}
	base := nameKey(filepath.Base(d))
	return genericNames[base] || base == ""
}

func under(p string, dirs []string) bool {
	lp := lower(filepath.Clean(p))
	for _, d := range dirs {
		ld := lower(filepath.Clean(d))
		if lp == ld || strings.HasPrefix(lp, ld+`\`) || strings.HasPrefix(lp, ld+"/") {
			return true
		}
	}
	return false
}

var imagePathRe = regexp.MustCompile(`^\s*"([^"]+)"|^\s*(\S+\.(?i:exe|sys|dll))`)

func exeOf(cmd string) string {
	cmd = strings.TrimPrefix(strings.TrimSpace(winapi.ExpandEnv(cmd)), `\??\`)
	if m := imagePathRe.FindStringSubmatch(cmd); m != nil {
		if m[1] != "" {
			return m[1]
		}
		return m[2]
	}
	exe, _ := splitCommand(cmd)
	return exe
}

const servicesKey = `SYSTEM\CurrentControlSet\Services`

func servicesIn(dirs []string) []ownedService {
	if len(dirs) == 0 {
		return nil
	}
	var out []ownedService
	for _, name := range winapi.RegSubKeys(winapi.HKLM, servicesKey) {
		img, _ := winapi.RegGetString(winapi.HKLM, servicesKey+`\`+name, "ImagePath")
		if img == "" {
			continue
		}
		if exe := exeOf(img); exe != "" && under(exe, dirs) {
			out = append(out, ownedService{name, exe})
		}
	}
	return out
}

var taskCmdRe = regexp.MustCompile(`(?s)<Command>([^<]+)</Command>`)

// tasksIn lists scheduled tasks whose program lies in dirs, or (forms) whose
// name matches the program.
func tasksIn(dirs []string, forms map[string]bool) []ownedTask {
	root := filepath.Join(os.Getenv("SystemRoot"), "System32", "Tasks")
	var out []ownedTask
	filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return nil
		}
		rel, _ := filepath.Rel(root, p)
		if strings.HasPrefix(lower(rel), `microsoft\`) {
			return nil
		}
		b, err := os.ReadFile(p)
		if err != nil {
			return nil
		}
		txt := decodeTaskXML(b)
		hit := false
		for _, m := range taskCmdRe.FindAllStringSubmatch(txt, -1) {
			if exe := exeOf(strings.TrimSpace(m[1])); exe != "" && under(exe, dirs) {
				hit = true
			}
		}
		if !hit && forms != nil {
			k := nameKey(filepath.Base(p))
			for f := range forms {
				if strings.Contains(k, f) {
					hit = true
				}
			}
		}
		if hit {
			out = append(out, ownedTask{name: `\` + rel, file: p})
		}
		return nil
	})
	return out
}

func decodeTaskXML(b []byte) string {
	if len(b) >= 2 && b[0] == 0xFF && b[1] == 0xFE { // UTF-16 LE
		u := make([]uint16, (len(b)-2)/2)
		for i := range u {
			u[i] = binary.LittleEndian.Uint16(b[2+2*i:])
		}
		var sb strings.Builder
		for _, r := range u {
			sb.WriteRune(rune(r))
		}
		return sb.String()
	}
	return string(b)
}

func shortcutDirs() []string {
	var out []string
	if pd := os.Getenv("ProgramData"); pd != "" {
		out = append(out, filepath.Join(pd, `Microsoft\Windows\Start Menu\Programs`))
	}
	if ad := os.Getenv("APPDATA"); ad != "" {
		out = append(out, filepath.Join(ad, `Microsoft\Windows\Start Menu\Programs`),
			filepath.Join(ad, `Microsoft\Internet Explorer\Quick Launch`))
	}
	if pub := os.Getenv("PUBLIC"); pub != "" {
		out = append(out, filepath.Join(pub, "Desktop"))
	}
	if d := winapi.KnownFolder("desktop"); d != "" {
		out = append(out, d)
	}
	return out
}

// shortcutsInto lists .lnk files whose target lies in dirs.
func shortcutsInto(dirs []string) []string {
	if len(dirs) == 0 {
		return nil
	}
	var out []string
	for _, root := range shortcutDirs() {
		filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
			if err != nil || d.IsDir() || !strings.EqualFold(filepath.Ext(p), ".lnk") {
				return nil
			}
			if t := lnkTarget(p); t != "" && under(t, dirs) {
				out = append(out, p)
			}
			return nil
		})
	}
	return out
}

// lnkTarget reads the local target path of a shell link (.lnk).
func lnkTarget(path string) string {
	b, err := os.ReadFile(path)
	if err != nil || len(b) < 0x4C || binary.LittleEndian.Uint32(b) != 0x4C {
		return ""
	}
	flags := binary.LittleEndian.Uint32(b[0x14:])
	off := 0x4C
	if flags&1 != 0 { // HasLinkTargetIDList
		if off+2 > len(b) {
			return ""
		}
		off += 2 + int(binary.LittleEndian.Uint16(b[off:]))
	}
	if flags&2 == 0 || off+0x1C > len(b) { // HasLinkInfo
		return ""
	}
	li := b[off:]
	size := int(binary.LittleEndian.Uint32(li))
	hdr := int(binary.LittleEndian.Uint32(li[4:]))
	if size > len(li) {
		return ""
	}
	li = li[:size]
	if hdr >= 0x24 && len(li) >= 0x20 { // Unicode path
		uo := int(binary.LittleEndian.Uint32(li[0x1C:]))
		if uo > 0 && uo < len(li) {
			var sb strings.Builder
			for i := uo; i+1 < len(li); i += 2 {
				c := binary.LittleEndian.Uint16(li[i:])
				if c == 0 {
					break
				}
				sb.WriteRune(rune(c))
			}
			if sb.Len() > 0 {
				return sb.String()
			}
		}
	}
	lo := int(binary.LittleEndian.Uint32(li[0x10:]))
	if lo <= 0 || lo >= len(li) {
		return ""
	}
	end := bytes.IndexByte(li[lo:], 0)
	if end < 0 {
		return ""
	}
	return string(li[lo : lo+end])
}

// ---------------------------------------------------------------- running the uninstaller

var guidRe = regexp.MustCompile(`\{[0-9A-Fa-f-]{36}\}`)

// quietCommand builds a silent uninstall command, or "" when the program
// has none Prostor knows.
func (s *progSnap) quietCommand() (exe, args string) {
	if q, _ := winapi.RegGetString(s.root, s.path, "QuietUninstallString"); q != "" {
		return splitCommand(winapi.ExpandEnv(q))
	}
	exe, args = splitCommand(winapi.ExpandEnv(s.Uninstall))
	if strings.Contains(lower(exe), "msiexec") {
		code := guidRe.FindString(args)
		if code == "" {
			code = guidRe.FindString(filepath.Base(s.path))
		}
		if code == "" {
			return "", ""
		}
		return "msiexec.exe", "/x " + code + " /qn /norestart"
	}
	base := lower(filepath.Base(exe))
	if ok, _ := filepath.Match("unins[0-9][0-9][0-9].exe", base); ok {
		return exe, strings.TrimSpace(args + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
	}
	if isNSIS(exe) {
		// _?= runs the uninstaller in place, so it can be waited for
		return exe, strings.TrimSpace(args+" /S") + " _?=" + filepath.Dir(exe)
	}
	return "", ""
}

func isNSIS(exe string) bool {
	f, err := os.Open(exe)
	if err != nil {
		return false
	}
	defer f.Close()
	buf := make([]byte, 4<<20)
	n, _ := f.Read(buf)
	return bytes.Contains(buf[:n], []byte("Nullsoft")) || bytes.Contains(buf[:n], []byte("NSIS"))
}

// stillInstalled: the program's uninstall entry exists and still points at
// an existing uninstaller.
func (s *progSnap) stillInstalled() bool {
	if !winapi.RegKeyExists(s.root, s.path) {
		return false
	}
	un, _ := winapi.RegGetString(s.root, s.path, "UninstallString")
	exe, _ := splitCommand(winapi.ExpandEnv(un))
	if exe == "" {
		return false
	}
	if strings.Contains(lower(exe), "msiexec") {
		return true
	}
	_, err := os.Stat(exe)
	return err == nil
}

// uninstallWait lets the user tell an interactive uninstall is finished.
var uninstallWait struct {
	sync.Mutex
	done chan struct{}
}

func runUninstaller(j *Job, s *progSnap, quiet bool) (ran, usedQuiet bool, note string, err error) {
	exe, args := "", ""
	if quiet {
		exe, args = s.quietCommand()
	}
	usedQuiet = exe != ""
	var cmd *exec.Cmd
	if usedQuiet {
		cmd = exec.Command(exe)
		winapi.SetCmdLine(cmd, `"`+exe+`" `+args)
		winapi.HideWindow(cmd)
		j.SetPhase("Тихое удаление «" + s.Name + "»")
	} else {
		if quiet {
			note = "программа не поддерживает тихое удаление, открыт её деинсталлятор"
		}
		ue, ua := splitCommand(winapi.ExpandEnv(s.Uninstall))
		if ue == "" {
			return false, false, note, errors.New("у программы нет команды удаления")
		}
		cmd = exec.Command(ue)
		winapi.SetCmdLine(cmd, `"`+ue+`" `+ua)
		j.SetPhase("Удаление «" + s.Name + "»: следуйте указаниям деинсталлятора")
		j.SetCurrent("interactive") // the UI offers "the uninstaller is closed"
	}
	if err := cmd.Start(); err != nil {
		return false, usedQuiet, note, err
	}
	ran = true
	uninstallWait.Lock()
	uninstallWait.done = make(chan struct{})
	done := uninstallWait.done
	uninstallWait.Unlock()
	exited := make(chan struct{})
	go func() { cmd.Wait(); close(exited) }()
	limit := 10 * time.Minute
	if !usedQuiet {
		limit = 60 * time.Minute
	}
	deadline := time.Now().Add(limit)
	exitedAt := time.Time{}
	for time.Now().Before(deadline) && !j.Cancelled() {
		select {
		case <-done:
			return ran, usedQuiet, note, nil
		case <-exited:
			if exitedAt.IsZero() {
				exitedAt = time.Now()
			}
			exited = nil
		case <-time.After(time.Second):
		}
		gone := !s.stillInstalled()
		// many uninstallers start a copy of themselves and exit at once:
		// wait for the program to leave the list, a minute at most after that
		if gone || (!exitedAt.IsZero() && time.Since(exitedAt) > time.Minute) {
			if gone {
				time.Sleep(2 * time.Second) // let it finish deleting files
			}
			return ran, usedQuiet, note, nil
		}
	}
	return ran, usedQuiet, note, nil
}

// ---------------------------------------------------------------- leftovers

func leftoverRoots() []string {
	var out []string
	for _, e := range []string{"ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "APPDATA", "LOCALAPPDATA",
		"CommonProgramFiles", "CommonProgramFiles(x86)"} {
		if v := os.Getenv(e); v != "" {
			out = append(out, v)
		}
	}
	if la := os.Getenv("LOCALAPPDATA"); la != "" {
		out = append(out, filepath.Join(la, "Programs"))
	}
	if up := os.Getenv("USERPROFILE"); up != "" {
		out = append(out, filepath.Join(up, "AppData", "LocalLow"))
	}
	seen := map[string]bool{}
	var uniq []string
	for _, r := range out {
		if k := lower(filepath.Clean(r)); !seen[k] {
			seen[k] = true
			uniq = append(uniq, r)
		}
	}
	return uniq
}

// otherOwned: folders of every other installed program.
func otherOwned(exceptKey string) []string {
	var out []string
	for _, p := range listPrograms() {
		if p.Key == exceptKey {
			continue
		}
		if p.Location != "" {
			out = append(out, filepath.Clean(p.Location))
		}
		if exe, _ := splitCommand(winapi.ExpandEnv(p.Uninstall)); exe != "" && !strings.Contains(lower(exe), "msiexec") && filepath.IsAbs(exe) {
			out = append(out, filepath.Dir(exe))
		}
	}
	return out
}

// touchesOther: the folder is, holds or lies inside another program's folder.
func touchesOther(d string, others []string) bool {
	ld := lower(filepath.Clean(d))
	for _, o := range others {
		lo := lower(o)
		if isSharedDir(o) {
			continue
		}
		if ld == lo || strings.HasPrefix(lo, ld+`\`) || strings.HasPrefix(ld, lo+`\`) {
			return true
		}
	}
	return false
}

func findLeftovers(s *progSnap) []Leftover {
	var out []Leftover
	seen := map[string]bool{}
	add := func(kind, path, sure, why string) {
		k := kind + "|" + lower(path)
		if seen[k] {
			return
		}
		seen[k] = true
		out = append(out, Leftover{Kind: kind, Path: path, Sure: sure, Why: why})
	}
	others := otherOwned(s.Key)
	okDir := func(d string) bool {
		return safeRoot(d) && !isSharedDir(d) && !touchesOther(d, others)
	}
	exists := func(p string) bool { _, err := os.Lstat(p); return err == nil }

	// the program's own folders
	for _, d := range s.dirs {
		if exists(d) && okDir(d) {
			add("folder", d, "high", "папка установки программы")
		}
	}
	// folders named after the program (or publisher\program)
	for _, root := range leftoverRoots() {
		ents, _ := os.ReadDir(root)
		for _, e := range ents {
			if !e.IsDir() {
				continue
			}
			p := filepath.Join(root, e.Name())
			k := nameKey(e.Name())
			if s.forms[k] && okDir(p) {
				add("folder", p, "high", "папка с именем программы")
				continue
			}
			if k != "" && k == s.pubKey && !genericNames[k] {
				sub, _ := os.ReadDir(p)
				matched := 0
				for _, c := range sub {
					cp := filepath.Join(p, c.Name())
					if c.IsDir() && s.forms[nameKey(c.Name())] && okDir(cp) {
						add("folder", cp, "high", "папка программы в папке производителя")
						matched++
					}
				}
				if matched > 0 && matched == len(sub) && okDir(p) {
					add("folder", p, "high", "папка производителя опустеет")
				}
			}
		}
	}
	// Start menu folders named after the program
	for _, root := range shortcutDirs() {
		ents, _ := os.ReadDir(root)
		for _, e := range ents {
			if e.IsDir() && s.forms[nameKey(e.Name())] {
				add("shortcut", filepath.Join(root, e.Name()), "high", "папка в меню «Пуск»")
			}
		}
	}
	for _, l := range s.links {
		if exists(l) {
			add("shortcut", l, "high", "ярлык программы")
		}
	}
	// services and tasks recorded before the uninstaller ran
	for _, sv := range s.services {
		if winapi.RegKeyExists(winapi.HKLM, servicesKey+`\`+sv.name) {
			add("service", sv.name, "high", "служба программы: "+sv.image)
		}
	}
	for _, t := range s.tasks {
		if exists(t.file) {
			add("task", t.name, "high", "задача планировщика запускает программу")
		}
	}
	for _, t := range tasksIn(nil, s.forms) {
		add("task", t.name, "medium", "имя задачи похоже на программу")
	}
	// registry
	if winapi.RegKeyExists(s.root, s.path) && !s.stillInstalled() {
		add("registry", rootName(s.root)+`\`+s.path, "high", "запись в списке установленных программ")
	}
	for _, rk := range []struct {
		root winapi.Root
		path string
	}{{winapi.HKCU, "Software"}, {winapi.HKLM, "SOFTWARE"}, {winapi.HKLM, `SOFTWARE\WOW6432Node`}} {
		for _, sub := range winapi.RegSubKeys(rk.root, rk.path) {
			k := nameKey(sub)
			full := rk.path + `\` + sub
			if s.forms[k] {
				add("registry", rootName(rk.root)+`\`+full, "high", "раздел реестра с именем программы")
				continue
			}
			if k != "" && k == s.pubKey && !genericNames[k] {
				kids := winapi.RegSubKeys(rk.root, full)
				matched := 0
				for _, c := range kids {
					if s.forms[nameKey(c)] {
						add("registry", rootName(rk.root)+`\`+full+`\`+c, "high", "раздел программы у производителя")
						matched++
					}
				}
				if matched > 0 && matched == len(kids) && len(winapi.RegStringValues(rk.root, full)) == 0 {
					add("registry", rootName(rk.root)+`\`+full, "high", "раздел производителя опустеет")
				}
			}
		}
	}
	// sizes, stable ids
	for i := range out {
		out[i].ID = i
		if out[i].Kind == "folder" || out[i].Kind == "shortcut" {
			if fi, err := os.Stat(out[i].Path); err == nil && fi.IsDir() {
				out[i].Size, _ = dirSize(out[i].Path, nil)
			} else if err == nil {
				out[i].Size = fi.Size()
			}
		}
	}
	sort.SliceStable(out, func(a, b int) bool {
		if out[a].Sure != out[b].Sure {
			return out[a].Sure == "high"
		}
		return out[a].Size > out[b].Size
	})
	for i := range out {
		out[i].ID = i
	}
	return out
}

func rootName(r winapi.Root) string {
	if r == winapi.HKCU {
		return "HKCU"
	}
	return "HKLM"
}

// ---------------------------------------------------------------- the job

// the finds of the latest uninstalls, by program key (a batch removes several)
var lastUninstall struct {
	sync.Mutex
	byKey map[string]*uninstallState
}

type uninstallState struct {
	snap  *progSnap
	items []Leftover
}

func uninstallJob(j *Job, key string, quiet bool) (*UninstallReport, error) {
	start := time.Now()
	s, err := findProgram(key)
	if err != nil {
		return nil, err
	}
	rep := &UninstallReport{Name: s.Name, Key: key, Leftovers: []Leftover{}}
	j.SetPhase("Подготовка: что принадлежит программе")
	ran, usedQuiet, note, err := runUninstaller(j, s, quiet)
	rep.Ran, rep.Quiet, rep.Note = ran, usedQuiet, note
	if err != nil {
		return nil, err
	}
	rep.Removed = !s.stillInstalled()
	if rep.Removed {
		j.SetPhase("Поиск остатков")
		rep.Leftovers = findLeftovers(s)
	} else if rep.Note == "" {
		rep.Note = "программа осталась в списке установленных: удаление отменено или не закончилось"
	}
	rep.Seconds = time.Since(start).Seconds()
	lastUninstall.Lock()
	if lastUninstall.byKey == nil {
		lastUninstall.byKey = map[string]*uninstallState{}
	}
	lastUninstall.byKey[key] = &uninstallState{snap: s, items: rep.Leftovers}
	lastUninstall.Unlock()
	programsMu.Lock()
	programsCache = nil
	programsMu.Unlock()
	log.Printf("uninstall %q: quiet=%v removed=%v leftovers=%d", s.Name, usedQuiet, rep.Removed, len(rep.Leftovers))
	return rep, nil
}

type LeftoverResult struct {
	Removed int      `json:"removed"`
	Freed   int64    `json:"freed"`
	Failed  []string `json:"failed"`
	Backup  string   `json:"backup,omitempty"` // .reg file with the deleted keys
}

// removeLeftovers deletes the chosen finds of a program's uninstall.
func removeLeftovers(key string, ids []int) (*LeftoverResult, error) {
	lastUninstall.Lock()
	st := lastUninstall.byKey[key]
	lastUninstall.Unlock()
	if st == nil {
		return nil, errors.New("нет результатов удаления этой программы")
	}
	items, s := st.items, st.snap
	pick := map[int]bool{}
	for _, id := range ids {
		pick[id] = true
	}
	res := &LeftoverResult{Failed: []string{}}
	var paths []string
	var regs []Leftover
	for _, it := range items {
		if !pick[it.ID] {
			continue
		}
		switch it.Kind {
		case "folder", "shortcut":
			paths = append(paths, it.Path)
		case "registry":
			regs = append(regs, it)
		case "service":
			runQuiet("sc", "stop", it.Path)
			if out, err := runQuiet("sc", "delete", it.Path); err != nil {
				res.Failed = append(res.Failed, it.Path+": "+strings.TrimSpace(out))
			} else {
				res.Removed++
			}
		case "task":
			if out, err := runQuiet("schtasks", "/delete", "/tn", it.Path, "/f"); err != nil {
				res.Failed = append(res.Failed, it.Path+": "+strings.TrimSpace(out))
			} else {
				res.Removed++
			}
		}
	}
	if len(paths) > 0 {
		days := getSettings().QuarantineDays
		if days <= 0 {
			days = 7 // what a program left behind is kept for a week anyway
		}
		jr := openJournal("uninstall")
		q := quarantinePaths(paths, "Удаление программы «"+s.Name+"»", days, jr)
		jr.close()
		res.Removed += len(q.Moved)
		res.Freed += q.Size
		for p, why := range q.Failed {
			// not on a volume with quarantine (or busy): delete directly
			if why == "volume" {
				if err := os.RemoveAll(p); err == nil {
					res.Removed++
					continue
				}
			}
			res.Failed = append(res.Failed, p)
		}
	}
	if len(regs) > 0 {
		// longest first: a publisher key after its program key
		sort.Slice(regs, func(a, b int) bool { return len(regs[a].Path) > len(regs[b].Path) })
		dir := filepath.Join(appDataDir(), "registry-backup")
		os.MkdirAll(dir, 0o755)
		res.Backup = filepath.Join(dir, fmt.Sprintf("%s %s.reg", time.Now().Format("2006-01-02 15-04-05"), safeFileName(s.Name)))
		var all bytes.Buffer
		for _, r := range regs {
			tmp := res.Backup + ".part"
			if _, err := runQuiet("reg", "export", r.Path, tmp, "/y"); err == nil {
				if b, err := os.ReadFile(tmp); err == nil {
					if all.Len() > 0 && len(b) > 2 && b[0] == 0xFF && b[1] == 0xFE {
						b = b[2:] // one UTF-16 BOM for the whole file
						b = bytes.TrimPrefix(b, utf16Header)
					}
					all.Write(b)
				}
				os.Remove(tmp)
			}
			root, path, _ := strings.Cut(r.Path, `\`)
			rr := winapi.HKLM
			if root == "HKCU" {
				rr = winapi.HKCU
			}
			if err := winapi.RegDeleteTree(rr, path); err != nil && winapi.RegKeyExists(rr, path) {
				res.Failed = append(res.Failed, r.Path)
			} else {
				res.Removed++
			}
		}
		if all.Len() > 0 {
			os.WriteFile(res.Backup, all.Bytes(), 0o644)
		} else {
			res.Backup = ""
		}
	}
	return res, nil
}

// "Windows Registry Editor Version 5.00\r\n" in UTF-16 LE, repeated by every export
var utf16Header = func() []byte {
	var b []byte
	for _, r := range "Windows Registry Editor Version 5.00\r\n" {
		b = append(b, byte(r), 0)
	}
	return b
}()

func safeFileName(s string) string {
	return strings.Map(func(r rune) rune {
		if strings.ContainsRune(`\/:*?"<>|`, r) || r < 32 {
			return '_'
		}
		return r
	}, s)
}

// ---------------------------------------------------------------- command line (automated tests)

// runRemoveProgram implements --remove-program "<name>": silent uninstall,
// then the leftovers Prostor ticks by default are removed. The report goes to
// --json. Used by CI to check that popular programs leave nothing behind.
func runRemoveProgram() int {
	want := lower(argValue("--remove-program"))
	out := argValue("--json")
	type result struct {
		Program   string           `json:"program"`
		Found     bool             `json:"found"`
		Report    *UninstallReport `json:"report,omitempty"`
		Cleaned   *LeftoverResult  `json:"cleaned,omitempty"`
		Remaining []Leftover       `json:"remaining"`
		Error     string           `json:"error,omitempty"`
	}
	res := result{Program: want, Remaining: []Leftover{}}
	defer func() {
		if out != "" {
			b, _ := json.MarshalIndent(res, "", "  ")
			os.WriteFile(out, b, 0o644)
		}
	}()
	var best *Program
	for _, p := range listPrograms() {
		n := lower(p.Name)
		if n == want || lower(baseName(p.Name)) == want {
			q := p
			best = &q
			break
		}
		if best == nil && strings.Contains(n, want) {
			q := p
			best = &q
		}
	}
	if best == nil {
		res.Error = "программа не найдена"
		return 2
	}
	res.Found, res.Program = true, best.Name
	j := newTestJob()
	rep, err := uninstallJob(j, best.Key, true)
	if err != nil {
		res.Error = err.Error()
		return 1
	}
	res.Report = rep
	if !rep.Removed {
		res.Error = "программа не удалилась: " + rep.Note
		return 1
	}
	var ids []int
	for _, it := range rep.Leftovers {
		if it.Sure == "high" {
			ids = append(ids, it.ID)
		}
	}
	if len(ids) > 0 {
		c, err := removeLeftovers(best.Key, ids)
		if err != nil {
			res.Error = err.Error()
			return 1
		}
		res.Cleaned = c
	}
	// what is still there after cleaning
	st := lastUninstall.byKey[best.Key]
	res.Remaining = findLeftovers(st.snap)
	return 0
}
