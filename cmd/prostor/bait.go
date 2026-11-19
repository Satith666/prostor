package main

// Bait test (Prostor.exe --bait-test): proves that cleaning never touches
// what it must not touch.
//
// Every cleanup rule gets junk planted where it should clean, and "baits" are
// planted around it: documents, photos, browser passwords and bookmarks,
// messenger keys, game saves, system files, files hidden behind links, files
// the rule's mask or age filter must skip, excluded folders. Then cleaning
// runs with every rule switched on and the baits are checked byte for byte.
// Junk that survives means a broken rule; a bait that is gone means a
// dangerous one.
//
// By default the whole Windows tree is redirected into a temporary folder
// (sandbox): nothing outside it is read or written, and the Recycle Bin,
// services and ownership changes are skipped. With --live (allowed only on CI
// machines, CI=true and PROSTOR_LIVE_BAIT=1) the real folders of the machine
// are used, which is what a throwaway Windows VM is for.

import (
	"encoding/json"
	"fmt"
	"hash/crc32"
	"io"
	"math/rand"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"prostor/internal/winapi"
)

type baitFile struct {
	Path string `json:"path"`
	Why  string `json:"why"`
	Size int64  `json:"size"`
	CRC  uint32 `json:"crc"`
	Real bool   `json:"real,omitempty"` // an existing system file we only watch
	MT   int64  `json:"-"`
}

type BaitReport struct {
	Mode      string         `json:"mode"` // sandbox | live
	Root      string         `json:"root,omitempty"`
	Rules     int            `json:"rules"`
	Baits     int            `json:"baits"`
	Damaged   []baitFile     `json:"damaged"`
	Junk      int            `json:"junk"`
	JunkLeft  map[string]int `json:"junkLeft"` // rule id -> junk files that survived
	Skipped   []string       `json:"skipped"`  // rules not exercised and why
	Freed     int64          `json:"freed"`
	Seconds   float64        `json:"seconds"`
	Journal   string         `json:"journal"`
	JournalOK bool           `json:"journalOK"`
	Error     string         `json:"error,omitempty"`
}

func (r *BaitReport) ok() bool {
	return r.Error == "" && len(r.Damaged) == 0 && len(r.JunkLeft) == 0 && r.JournalOK
}

func argValue(name string) string {
	for i, a := range os.Args[1:] {
		if strings.EqualFold(a, name) && i+2 < len(os.Args) {
			return os.Args[i+2]
		}
	}
	return ""
}

// ---------------------------------------------------------------- environment

// sandboxEnv redirects every folder a rule can point at into root.
func sandboxEnv(root string) map[string]string {
	c := filepath.Join(root, "C")
	user := filepath.Join(c, "Users", "bait")
	env := map[string]string{
		"SystemDrive":       c,
		"SystemRoot":        filepath.Join(c, "Windows"),
		"windir":            filepath.Join(c, "Windows"),
		"ProgramData":       filepath.Join(c, "ProgramData"),
		"ALLUSERSPROFILE":   filepath.Join(c, "ProgramData"),
		"ProgramFiles":      filepath.Join(c, "Program Files"),
		"ProgramFiles(x86)": filepath.Join(c, "Program Files (x86)"),
		"ProgramW6432":      filepath.Join(c, "Program Files"),
		"PUBLIC":            filepath.Join(c, "Users", "Public"),
		"USERPROFILE":       user,
		"HOMEDRIVE":         c,
		"APPDATA":           filepath.Join(user, "AppData", "Roaming"),
		"LOCALAPPDATA":      filepath.Join(user, "AppData", "Local"),
		"TEMP":              filepath.Join(user, "AppData", "Local", "Temp"),
		"TMP":               filepath.Join(user, "AppData", "Local", "Temp"),
	}
	if !winapi.IsWindows {
		env["HOME"] = user
		env["TMPDIR"] = env["TEMP"]
	}
	return env
}

// concrete turns a rule pattern into real paths to plant into: a whole "*"
// segment becomes a couple of typical names (browser profiles), a "*" inside
// a name becomes "1".
func concrete(pattern string) []string {
	p := expandPath(pattern)
	if p == "" {
		return nil
	}
	if !strings.Contains(p, "*") {
		return []string{p}
	}
	sep := string(filepath.Separator)
	parts := strings.Split(p, sep)
	outs := []string{""}
	for i, seg := range parts {
		var vars []string
		switch {
		case seg == "*":
			vars = []string{"Default", "Profile 1"}
		case strings.Contains(seg, "*"):
			vars = []string{strings.ReplaceAll(seg, "*", "1")}
		default:
			vars = []string{seg}
		}
		var next []string
		for _, o := range outs {
			for _, v := range vars {
				if i == 0 {
					next = append(next, v)
				} else {
					next = append(next, o+sep+v)
				}
			}
		}
		outs = next
	}
	return outs
}

func isFileRoot(p string) bool {
	return strings.EqualFold(filepath.Ext(p), ".dmp")
}

// maskName makes a file name that matches the mask ("*.log" -> "junk.log").
func maskName(mask string, n int) string {
	return strings.NewReplacer("*", fmt.Sprintf("junk%d", n), "?", "x").Replace(mask)
}

// ---------------------------------------------------------------- planting

type planter struct {
	rnd   *rand.Rand
	baits []baitFile
	junk  map[string][]string // rule -> junk files
	roots []string            // every concrete root of every rule (lowercase)
	old   time.Time
	err   error
}

func (pl *planter) write(path string, size int, mtime time.Time) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	b := make([]byte, size)
	pl.rnd.Read(b)
	if err := os.WriteFile(path, b, 0o644); err != nil {
		return err
	}
	return os.Chtimes(path, mtime, mtime)
}

// underAnyRoot: a bait placed inside some rule's folder would be junk, not bait.
func (pl *planter) underAnyRoot(p string) bool {
	lp := lower(filepath.Clean(p))
	for _, r := range pl.roots {
		if lp == r || strings.HasPrefix(lp, r+string(filepath.Separator)) {
			return true
		}
	}
	return false
}

func (pl *planter) bait(path, why string, age time.Duration) {
	if pl.underAnyRoot(path) {
		return
	}
	if _, err := os.Lstat(path); err == nil {
		return // already planted (shared parents)
	}
	size := 512 + pl.rnd.Intn(4096)
	if err := pl.write(path, size, time.Now().Add(-age)); err != nil {
		if pl.err == nil {
			pl.err = fmt.Errorf("приманка %s: %v", path, err)
		}
		return
	}
	b, _ := os.ReadFile(path)
	pl.baits = append(pl.baits, baitFile{Path: path, Why: why, Size: int64(len(b)), CRC: crc32.ChecksumIEEE(b)})
}

// force plants a precious file wherever it is: if some rule's folder covers
// it, that rule is exactly what the test must catch.
func (pl *planter) force(path, why string, age time.Duration) { pl.inner(path, why, age) }

// bait inside a rule's own folder: the rule's filters must protect it.
func (pl *planter) inner(path, why string, age time.Duration) {
	if _, err := os.Lstat(path); err == nil {
		return // two patterns of a rule can name the same folder
	}
	size := 300 + pl.rnd.Intn(2000)
	if err := pl.write(path, size, time.Now().Add(-age)); err != nil {
		return
	}
	b, _ := os.ReadFile(path)
	pl.baits = append(pl.baits, baitFile{Path: path, Why: why, Size: int64(len(b)), CRC: crc32.ChecksumIEEE(b)})
}

func (pl *planter) addJunk(id, path string) {
	if _, err := os.Lstat(path); err == nil {
		return
	}
	if err := pl.write(path, 2048+pl.rnd.Intn(16384), pl.old); err == nil {
		pl.junk[id] = append(pl.junk[id], path)
	}
}

func (pl *planter) plantRule(t *Target, docs string) {
	for _, pat := range t.Paths {
		for _, r := range concrete(pat) {
			if isFileRoot(r) {
				pl.addJunk(t.ID, r)
				continue
			}
			// junk the rule must remove
			if len(t.Masks) > 0 {
				for i, m := range t.Masks {
					pl.addJunk(t.ID, filepath.Join(r, maskName(m, i)))
				}
			} else {
				pl.addJunk(t.ID, filepath.Join(r, "junk.tmp"))
				pl.addJunk(t.ID, filepath.Join(r, "data_1"))
			}
			if !t.Flat {
				name := "deep.bin"
				if len(t.Masks) > 0 {
					name = maskName(t.Masks[0], 9)
				}
				pl.addJunk(t.ID, filepath.Join(r, "sub", "inner", name))
			}
			// next to the folder: what lives beside a cache (bookmarks, settings)
			pl.bait(filepath.Join(filepath.Dir(r), "prostor-bait-beside-"+t.ID+".dat"), "рядом с папкой правила «"+t.Name+"»", 40*24*time.Hour)
			// inside the folder, protected by the rule's own filters
			if len(t.Masks) > 0 {
				pl.inner(filepath.Join(r, "prostor-bait-keep.dll"), "не подходит под маску правила «"+t.Name+"»", 40*24*time.Hour)
			}
			if t.MinAge > 0 || t.Recent {
				name := "prostor-bait-fresh.tmp"
				if len(t.Masks) > 0 {
					name = maskName(t.Masks[0], 7)
				}
				pl.inner(filepath.Join(r, name), "свежий файл, правило «"+t.Name+"» должно его пропустить", time.Hour)
			}
			if t.Flat {
				name := "prostor-bait-nested.dat"
				if len(t.Masks) > 0 {
					name = maskName(t.Masks[0], 8)
				}
				pl.inner(filepath.Join(r, "Nested", name), "во вложенной папке, правило «"+t.Name+"» в неё не заходит", 40*24*time.Hour)
			}
			// a link from the cache to documents: the documents must survive
			link := filepath.Join(r, "prostor-bait-link")
			if _, err := os.Lstat(link); os.IsNotExist(err) && docs != "" {
				if makeLink(link, docs) != nil && pl.err == nil && winapi.IsWindows {
					pl.err = fmt.Errorf("не удалось создать ссылку %s", link)
				}
			}
		}
	}
}

// precious files that must never be touched, wherever they live.
func (pl *planter) plantPrecious() {
	up, la, ra := os.Getenv("USERPROFILE"), os.Getenv("LOCALAPPDATA"), os.Getenv("APPDATA")
	sr, pd, pf := os.Getenv("SystemRoot"), os.Getenv("ProgramData"), os.Getenv("ProgramFiles")
	x86 := os.Getenv("ProgramFiles(x86)")
	age := 200 * 24 * time.Hour
	j := filepath.Join
	type b struct{ p, why string }
	list := []b{
		{j(up, "Documents", "prostor-bait Отчёт.docx"), "документ"},
		{j(up, "Desktop", "prostor-bait фото.jpg"), "файл на рабочем столе"},
		{j(up, "Pictures", "prostor-bait IMG_0001.jpg"), "фотография"},
		{j(up, "Downloads", "prostor-bait setup.exe"), "старая загрузка"},
		{j(up, ".gradle", "prostor-bait gradle.properties"), "настройки Gradle"},
		{j(up, ".cargo", "prostor-bait config.toml"), "настройки Cargo"},
		{j(ra, "pip", "prostor-bait pip.ini"), "настройки pip"},
		{j(ra, "Code", "User", "prostor-bait settings.json"), "настройки VS Code"},
		{j(ra, "discord", "Local Storage", "leveldb", "prostor-bait 000003.log"), "вход в Discord"},
		{j(ra, "Telegram Desktop", "tdata", "prostor-bait key_datas"), "ключи Telegram"},
		{j(ra, "Telegram Desktop", "tdata", "D877F783D5D3EF8C", "prostor-bait maps"), "данные чатов Telegram"},
		{j(ra, ".minecraft", "saves", "World", "prostor-bait level.dat"), "сохранение Minecraft"},
		{j(ra, "Microsoft", "Windows", "Recent", "AutomaticDestinations", "prostor-bait.automaticDestinations-ms"), "закреплённые списки"},
		{j(la, "Mozilla", "Firefox", "Profiles", "Default", "prostor-bait cookies.sqlite"), "куки Firefox (локальная часть)"},
		{j(ra, "Mozilla", "Firefox", "Profiles", "Default", "prostor-bait logins.json"), "пароли Firefox"},
		{j(ra, "Mozilla", "Firefox", "Profiles", "Default", "prostor-bait places.sqlite"), "закладки Firefox"},
		{j(la, "Spotify", "Users", "bait-user", "prostor-bait prefs"), "настройки Spotify"},
		{j(la, "Roblox", "Plugins", "prostor-bait plugin.rbxm"), "плагин Roblox"},
		{j(x86, "Steam", "steamapps", "common", "Game", "prostor-bait game.exe"), "установленная игра"},
		{j(x86, "Steam", "userdata", "1", "remote", "prostor-bait save.dat"), "облачное сохранение Steam"},
		{j(pf, "Prostor Bait App", "prostor-bait app.exe"), "установленная программа"},
		{j(pd, "Prostor Bait App", "prostor-bait config.ini"), "настройки программы"},
		{j(sr, "System32", "drivers", "etc", "prostor-bait hosts"), "системный файл"},
		{j(sr, "System32", "prostor-bait kernel.dll"), "системная библиотека"},
	}
	for _, browser := range []string{`Google\Chrome`, `Microsoft\Edge`, `Yandex\YandexBrowser`, `BraveSoftware\Brave-Browser`, `Vivaldi`} {
		ud := j(la, filepath.FromSlash(strings.ReplaceAll(browser, `\`, "/")), "User Data")
		list = append(list, b{j(ud, "prostor-bait Local State"), "ключ шифрования браузера"})
		for _, f := range []string{"Bookmarks", "Login Data", "Cookies", "History", "Preferences", "Web Data"} {
			list = append(list, b{j(ud, "Default", "prostor-bait "+f), "профиль браузера: " + f})
		}
		list = append(list, b{j(ud, "Default", "Network", "prostor-bait Cookies"), "куки браузера"})
		list = append(list, b{j(ud, "Default", "Local Storage", "leveldb", "prostor-bait 000005.ldb"), "данные сайтов"})
		list = append(list, b{j(ud, "Default", "Extensions", "abc", "prostor-bait manifest.json"), "расширение браузера"})
	}
	for _, x := range list {
		if x.p == "" || !filepath.IsAbs(x.p) {
			continue
		}
		pl.force(x.p, x.why, age)
	}
	// Inside cleanup folders on purpose: only the right filters protect them.
	pl.force(j(os.Getenv("TEMP"), "prostor-bait-excluded", "important.txt"), "папка из исключений", age)
	pl.force(j(os.Getenv("TEMP"), "prostor-bait-excluded", "deep", "notes.txt"), "папка из исключений", age)
	pl.force(j(os.Getenv("TEMP"), "prostor-bait-installing", "setup.msi"), "свежий файл идущей установки во временной папке", time.Hour)
	pl.force(j(sr, "Temp", "prostor-bait-installing", "update.cab"), "свежий файл идущего обновления в Windows\\Temp", time.Hour)
	pl.force(j(ra, "Microsoft", "Windows", "Recent", "CustomDestinations", "prostor-bait.customDestinations-ms"), "закреплённые списки переходов", age)
	pl.force(j(sr, "Prefetch", "prostor-bait Layout.ini"), "служебный файл оптимизации загрузки", age)
	pl.force(j(sr, "Logs", "CBS", "prostor-bait CbsPersist.log"), "свежий журнал обслуживания Windows", time.Hour)
}

// watch existing system files (live mode): they must not change.
func (pl *planter) watchReal(paths ...string) {
	for _, p := range paths {
		fi, err := os.Stat(p)
		if err != nil || fi.IsDir() {
			continue
		}
		pl.baits = append(pl.baits, baitFile{Path: p, Why: "системный файл Windows", Size: fi.Size(), Real: true, MT: fi.ModTime().UnixNano()})
	}
}

func baitIntact(b baitFile) bool {
	fi, err := os.Stat(b.Path)
	if err != nil || fi.Size() != b.Size {
		return false
	}
	if b.Real {
		return fi.ModTime().UnixNano() == b.MT
	}
	f, err := os.Open(b.Path)
	if err != nil {
		return false
	}
	defer f.Close()
	h := crc32.NewIEEE()
	io.Copy(h, f)
	return h.Sum32() == b.CRC
}

// ---------------------------------------------------------------- the test

func runBaitTest() int {
	live := hasArg("--live")
	out := argValue("--json")
	if !hasArg("--no-console") {
		winapi.Console(true, "Prostor: тест приманок")
	}
	rep := baitTest(live)
	b, _ := json.MarshalIndent(rep, "", "  ")
	if out != "" {
		os.WriteFile(out, b, 0o644)
	}
	printBaitReport(rep)
	if rep.ok() {
		return 0
	}
	return 1
}

func baitTest(live bool) *BaitReport {
	start := time.Now()
	rep := &BaitReport{Mode: "sandbox", JunkLeft: map[string]int{}, Damaged: []baitFile{}, Skipped: []string{}}
	if live {
		rep.Mode = "live"
		if os.Getenv("CI") != "true" || os.Getenv("PROSTOR_LIVE_BAIT") != "1" {
			rep.Error = "боевой режим работает только на одноразовых машинах CI (CI=true и PROSTOR_LIVE_BAIT=1)"
			return rep
		}
	} else {
		root, err := os.MkdirTemp("", "prostor-bait-")
		if err != nil {
			rep.Error = err.Error()
			return rep
		}
		root, _ = filepath.EvalSymlinks(root)
		rep.Root = root
		defer func() {
			if rep.ok() {
				removeTree(root)
			}
		}()
		for k, v := range sandboxEnv(root) {
			os.Setenv(k, v)
		}
		sandboxMode = true
	}
	forbiddenRoots = nil
	initForbidden()

	pl := &planter{rnd: rand.New(rand.NewSource(time.Now().UnixNano())), junk: map[string][]string{}, old: time.Now().Add(-60 * 24 * time.Hour)}
	var ids []string
	for i := range targets {
		t := &targets[i]
		if t.Special == "recycle" && !live {
			rep.Skipped = append(rep.Skipped, t.ID+": Корзина не трогается в песочнице")
			continue
		}
		if t.Admin && live && !isAdmin {
			rep.Skipped = append(rep.Skipped, t.ID+": нужны права администратора")
			continue
		}
		ids = append(ids, t.ID)
		for _, pat := range t.Paths {
			for _, r := range concrete(pat) {
				pl.roots = append(pl.roots, lower(filepath.Clean(r)))
			}
		}
	}
	rep.Rules = len(ids)

	docs := filepath.Join(os.Getenv("USERPROFILE"), "Documents", "prostor-bait-linked")
	pl.bait(filepath.Join(docs, "prostor-bait за ссылкой.docx"), "документ, на который указывает ссылка из кэша", 300*24*time.Hour)
	pl.plantPrecious()
	for _, id := range ids {
		if t := targetByID(id); t != nil && t.Special != "recycle" {
			pl.plantRule(t, docs)
		}
	}
	if live {
		sr := os.Getenv("SystemRoot")
		pl.watchReal(filepath.Join(sr, "System32", "kernel32.dll"), filepath.Join(sr, "System32", "ntdll.dll"),
			filepath.Join(sr, "System32", "drivers", "etc", "hosts"), filepath.Join(sr, "explorer.exe"))
	}
	if pl.err != nil {
		rep.Error = pl.err.Error()
		return rep
	}
	rep.Baits = len(pl.baits)
	for _, js := range pl.junk {
		rep.Junk += len(js)
	}

	s := defaultSettings()
	// the exclusion is given in 8.3 form where the disk has short names: the
	// cleaner walks long names and must still recognise it
	excl := filepath.Join(os.Getenv("TEMP"), "prostor-bait-excluded")
	if sp := winapi.ShortPath(excl); sp != "" {
		excl = sp
	}
	s.Excludes = []string{excl}
	s.DeleteOnReboot = false
	j := newTestJob()
	admin := !live || isAdmin
	res := cleanTargets(j, ids, s, admin, "bait")
	rep.Freed = res.Freed
	rep.Journal = res.Journal

	for _, b := range pl.baits {
		if !baitIntact(b) {
			rep.Damaged = append(rep.Damaged, b)
		}
	}
	for id, files := range pl.junk {
		for _, f := range files {
			if _, err := os.Lstat(f); err == nil {
				rep.JunkLeft[id]++
			}
		}
	}
	// the journal must list every junk file that is gone
	if pg, err := readJournal(res.Journal, "", 0, 1); err == nil {
		gone := 0
		for _, files := range pl.junk {
			for _, f := range files {
				if _, err := os.Lstat(f); os.IsNotExist(err) {
					gone++
				}
			}
		}
		rep.JournalOK = pg.Counts["d"] >= int64(gone)
	}
	if live {
		for _, b := range pl.baits {
			if !b.Real {
				os.Remove(b.Path)
			}
		}
	}
	rep.Seconds = time.Since(start).Seconds()
	return rep
}

func printBaitReport(r *BaitReport) {
	p := func(f string, a ...any) { fmt.Printf(f+"\n", a...) }
	p("")
	p("Prostor: тест приманок (%s)", map[string]string{"sandbox": "в песочнице", "live": "на настоящей системе"}[r.Mode])
	p("Правил: %d, приманок: %d, мусорных файлов: %d, время: %s", r.Rules, r.Baits, r.Junk, secRu(r.Seconds))
	if r.Error != "" {
		p("ОШИБКА: %s", r.Error)
		return
	}
	if len(r.Damaged) == 0 {
		p("[ OK ] все приманки целы")
	} else {
		p("[FAIL] повреждено приманок: %d", len(r.Damaged))
		for _, b := range r.Damaged {
			p("       %s — %s", b.Path, b.Why)
		}
	}
	if len(r.JunkLeft) == 0 {
		p("[ OK ] каждое правило удалило свой мусор")
	} else {
		var ids []string
		for id := range r.JunkLeft {
			ids = append(ids, id)
		}
		sort.Strings(ids)
		p("[FAIL] правила не удалили свой мусор: %s", strings.Join(ids, ", "))
	}
	if r.JournalOK {
		p("[ OK ] журнал удалений записан полностью")
	} else {
		p("[FAIL] журнал удалений неполный")
	}
	for _, s := range r.Skipped {
		p("[ -- ] %s", s)
	}
}
