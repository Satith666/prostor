package main

// Модульные тесты ядра. Запуск: go test ./...
// Проверки из самопроверки (очистка, анализ, дубликаты) выполняются здесь
// в изолированной временной папке, плюс отдельные пограничные случаи.

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"image"
	"image/color"
	"image/jpeg"
	"image/png"
	"io"
	"os"
	"path/filepath"
	"prostor/internal/ntfs"
	"prostor/internal/rules"
	"strings"
	"testing"
	"time"
)

// isolate points every folder the program uses at a temporary directory.
func isolate(t *testing.T) string {
	t.Helper()
	root := t.TempDir()
	for _, kv := range [][2]string{
		{"HOME", "Users/user"}, {"USERPROFILE", "Users/user"}, {"LOCALAPPDATA", "Users/user/AppData/Local"},
		{"APPDATA", "Users/user/AppData/Roaming"}, {"TEMP", "Users/user/AppData/Local/Temp"},
		{"SystemRoot", "Windows"}, {"ProgramData", "ProgramData"},
	} {
		p := filepath.Join(root, filepath.FromSlash(kv[1]))
		os.MkdirAll(p, 0o755)
		t.Setenv(kv[0], p)
	}
	t.Setenv("SystemDrive", root)
	t.Setenv("TMPDIR", filepath.Join(root, "Users/user/AppData/Local/Temp"))
	forbiddenRoots = nil
	initForbidden()
	loadStore()
	return root
}

func runChecks(t *testing.T, fn func(st *selfTest)) {
	t.Helper()
	st := &selfTest{out: io.Discard, quick: true, dir: t.TempDir()}
	fn(st)
	if len(st.checks) == 0 {
		t.Fatal("no checks ran")
	}
	for _, c := range st.checks {
		switch c.Status {
		case stFail:
			t.Errorf("%s / %s: %s", c.Group, c.Name, c.Detail)
		default:
			t.Logf("%s %s / %s: %s", c.Status, c.Group, c.Name, c.Detail)
		}
	}
}

func TestCleanerSandbox(t *testing.T) {
	isolate(t)
	runChecks(t, (*selfTest).cleanerChecks)
}

func TestAnalyzer(t *testing.T) {
	isolate(t)
	runChecks(t, (*selfTest).analyzerChecks)
}

func TestDuplicates(t *testing.T) {
	isolate(t)
	runChecks(t, (*selfTest).dupesChecks)
}

func TestSafeRootRefusesDangerousRoots(t *testing.T) {
	root := isolate(t)
	bad := []string{"/", root + "/Windows", os.Getenv("USERPROFILE"), os.Getenv("LOCALAPPDATA"), os.Getenv("APPDATA"), filepath.Dir(os.Getenv("USERPROFILE"))}
	for _, p := range bad {
		if safeRoot(p) {
			t.Errorf("safeRoot(%q) = true, want false", p)
		}
	}
	if !safeRoot(filepath.Join(os.Getenv("LOCALAPPDATA"), "Temp")) {
		t.Errorf("Temp must be a valid cleanup root")
	}
}

func TestEveryTargetHasSaneDefinition(t *testing.T) {
	isolate(t)
	ids := map[string]bool{}
	cats := map[string]bool{}
	for _, c := range categories {
		cats[c.ID] = true
	}
	for _, tg := range targets {
		if tg.ID == "" || tg.Name == "" || tg.Desc == "" {
			t.Errorf("target %+v: empty id, name or description", tg.ID)
		}
		if ids[tg.ID] {
			t.Errorf("duplicate target id %s", tg.ID)
		}
		ids[tg.ID] = true
		if !cats[tg.Cat] {
			t.Errorf("target %s: unknown category %s", tg.ID, tg.Cat)
		}
		if len(tg.Locs) == 0 && tg.Special == "" {
			t.Errorf("target %s: no paths", tg.ID)
		}
		for _, l := range tg.Locs {
			for _, m := range l.Masks {
				if m != strings.ToLower(m) {
					t.Errorf("target %s: mask %q must be lowercase", tg.ID, m)
				}
				if _, err := filepath.Match(m, "x"); err != nil {
					t.Errorf("target %s: bad mask %q", tg.ID, m)
				}
			}
		}
	}
}

func TestKidsSkipSingleFolderChains(t *testing.T) {
	leaf := &dirNode{name: "AppData", size: 70, own: 70}
	user := &dirNode{name: "user", size: 100, own: 0, children: []*dirNode{{name: "Videos", size: 30, own: 30}, leaf}}
	users := &dirNode{name: "Users", size: 100, children: []*dirNode{user}}
	sortNodes(user.children)
	k := kidsOf(users)
	if len(k) != 2 || k[0].Name != "user"+string(filepath.Separator)+"AppData" || k[0].Size != 70 {
		t.Fatalf("kidsOf = %+v", k)
	}
}

func TestForgetKeepsTotalsConsistent(t *testing.T) {
	isolate(t)
	dir := t.TempDir()
	for i, rel := range []string{"a/1.bin", "a/2.bin", "b/3.bin"} {
		if err := writeTestFile(filepath.Join(dir, filepath.FromSlash(rel)), 64<<10*(i+1), 1, int64(i)); err != nil {
			t.Fatal(err)
		}
	}
	a := newAnalysis(dir, newTestJob())
	a.run()
	before := a.root.size
	a.forget(filepath.Join(dir, "a", "2.bin"), 128<<10, false)
	if a.root.size != before-128<<10 {
		t.Fatalf("after forgetting a file total = %d, want %d", a.root.size, before-128<<10)
	}
	a.forget(filepath.Join(dir, "b"), 0, true)
	if a.root.size != 64<<10 || len(a.root.children) != 1 {
		t.Fatalf("after forgetting a folder total = %d, children = %d", a.root.size, len(a.root.children))
	}
}

func TestThemeDefaultsToDark(t *testing.T) {
	isolate(t)
	for _, c := range []struct{ saved, want string }{
		{`{"theme":"auto"}`, "dark"},                      // saved by 1.1.0 without asking
		{`{}`, "dark"},                                    // no theme at all
		{`{"theme":"light","themeChosen":true}`, "light"}, // the user's choice stays
		{`{"theme":"auto","themeChosen":true}`, "auto"},
	} {
		os.MkdirAll(appDataDir(), 0o755)
		if err := os.WriteFile(settingsPath(), []byte(c.saved), 0o644); err != nil {
			t.Fatal(err)
		}
		loadStore()
		if got := getSettings().Theme; got != c.want {
			t.Errorf("settings %s: theme %q, want %q", c.saved, got, c.want)
		}
	}
}

func TestQuarantineRoundTrip(t *testing.T) {
	root := isolate(t)
	data := filepath.Join(root, "Users", "user", "Data")
	a, b := filepath.Join(data, "a.bin"), filepath.Join(data, "b.bin")
	folder := filepath.Join(data, "folder")
	for i, p := range []string{a, b, filepath.Join(folder, "x.bin"), filepath.Join(folder, "sub", "y.bin")} {
		if err := writeTestFile(p, 4096*(i+1), 3, int64(i)); err != nil {
			t.Fatal(err)
		}
	}
	jr := openJournal("test")
	res := quarantinePaths([]string{a, b, folder}, "Тест", 7, jr)
	id := jr.close()
	if len(res.Moved) != 3 || len(res.Failed) != 0 {
		t.Fatalf("moved %v failed %v", res.Moved, res.Failed)
	}
	if res.Size != 4096+8192+12288+16384 {
		t.Fatalf("quarantined size %d", res.Size)
	}
	for _, p := range []string{a, b, folder} {
		if fileExists(p) {
			t.Fatalf("%s still in place", p)
		}
	}
	list := listQuarantine()
	if len(list) != 1 || len(list[0].Items) != 3 || list[0].Source != "Тест" {
		t.Fatalf("list = %+v", list)
	}
	pg, err := readJournal(id, "", 0, 10)
	if err != nil || pg.Counts["q"] != 3 {
		t.Fatalf("journal %v %+v", err, pg)
	}

	// a new file took the place of b: restoring must not overwrite it
	if err := os.WriteFile(b, []byte("new"), 0o644); err != nil {
		t.Fatal(err)
	}
	rr, err := restoreQuarantine(list[0].ID, []string{b, folder})
	if err != nil || len(rr.Restored) != 2 || len(rr.Failed) != 0 {
		t.Fatalf("restore %v %+v", err, rr)
	}
	if got, _ := os.ReadFile(b); string(got) != "new" {
		t.Fatal("restore overwrote a newer file")
	}
	if alt := rr.Renamed[b]; alt == "" || !fileExists(alt) {
		t.Fatalf("renamed copy missing: %+v", rr.Renamed)
	}
	if !fileExists(filepath.Join(folder, "sub", "y.bin")) {
		t.Fatal("folder not restored with its contents")
	}
	if l := listQuarantine(); len(l) != 1 || len(l[0].Items) != 1 {
		t.Fatalf("after partial restore: %+v", l)
	}
	// not expired yet: an ordinary purge keeps it, a full purge removes it
	if _, n := purgeQuarantine("", false); n != 0 {
		t.Fatal("purged a batch before its term")
	}
	if freed, n := purgeQuarantine("", true); n != 1 || freed != 4096 {
		t.Fatalf("full purge freed %d in %d batches", freed, n)
	}
	if fileExists(a) || len(listQuarantine()) != 0 {
		t.Fatal("purge left something behind")
	}
}

func TestQuarantineExpires(t *testing.T) {
	root := isolate(t)
	p := filepath.Join(root, "Users", "user", "old.bin")
	if err := writeTestFile(p, 1000, 1, 1); err != nil {
		t.Fatal(err)
	}
	if r := quarantinePaths([]string{p}, "Тест", 0, nil); len(r.Moved) != 1 {
		t.Fatalf("not moved: %+v", r)
	}
	if freed, n := purgeQuarantine("", false); n != 1 || freed != 1000 {
		t.Fatalf("expired batch not purged: %d %d", freed, n)
	}
}

func TestJournalSearchAndPaging(t *testing.T) {
	isolate(t)
	jr := openJournal("test")
	for i := 0; i < 450; i++ {
		jr.add("d", filepath.Join("C:", "Temp", "file"+strings.Repeat("x", i%3)+".tmp"), int64(i), "user_temp")
	}
	jr.add("f", `C:\Busy\locked.dat`, 10, "user_temp")
	id := jr.close()
	pg, err := readJournal(id, "", 400, 100)
	if err != nil || pg.Total != 451 || len(pg.Records) != 51 || pg.Counts["d"] != 450 || pg.Counts["f"] != 1 {
		t.Fatalf("page: %v total=%d n=%d counts=%v", err, pg.Total, len(pg.Records), pg.Counts)
	}
	pg, _ = readJournal(id, "LOCKED", 0, 100)
	if pg.Total != 1 || pg.Records[0].A != "f" {
		t.Fatalf("search: %+v", pg)
	}
	if _, err := readJournal("../settings", "", 0, 10); err == nil {
		t.Fatal("journal id with a path was accepted")
	}
}

func TestCleanTargetWritesJournal(t *testing.T) {
	root := isolate(t)
	cache := filepath.Join(root, "Users", "user", "AppData", "Local", "TestCache")
	for i := 0; i < 5; i++ {
		if err := writeTestFile(filepath.Join(cache, "c", fmt.Sprintf("%d.bin", i)), 100, 3, int64(i)); err != nil {
			t.Fatal(err)
		}
	}
	tg := &Target{ID: "testcache", Name: "Тестовый кэш", Locs: []rules.Loc{{Path: `%LOCALAPPDATA%\TestCache`}}}
	jr := openJournal("clean")
	ci := cleanTarget(newTestJob(), tg, defaultSettings(), false, jr)
	id := jr.close()
	if ci.Files != 5 || ci.Freed != 500 {
		t.Fatalf("cleaned %+v", ci)
	}
	pg, err := readJournal(id, "", 0, 100)
	if err != nil || pg.Counts["d"] != 5 || pg.Bytes["d"] != 500 || pg.Records[0].T != "testcache" {
		t.Fatalf("journal %v %+v", err, pg)
	}
}

func TestEveryTargetHasRisk(t *testing.T) {
	for _, tg := range targets {
		switch tg.Risk {
		case "safe", "caution", "expert":
		default:
			t.Errorf("%s: risk %q", tg.ID, tg.Risk)
		}
		if tg.Risk == "expert" && tg.Default {
			t.Errorf("%s: an irreversible item must not be ticked by default", tg.ID)
		}
	}
}

// runBait runs the sandbox bait test with the current rules and restores the
// environment afterwards.
func runBait(t *testing.T) *BaitReport {
	t.Helper()
	isolate(t)
	for k := range sandboxEnv("x") {
		t.Setenv(k, os.Getenv(k))
	}
	t.Cleanup(func() { sandboxMode = false; forbiddenRoots = nil; initForbidden() })
	return baitTest(false)
}

// withTargets lets a test change the rules (deep copy of folders, so the
// change does not leak into other tests).
func withTargets(t *testing.T, change func()) {
	saved := targets
	targets = make([]Target, len(saved))
	for i, tg := range saved {
		tg.Locs = append([]rules.Loc(nil), tg.Locs...)
		targets[i] = tg
	}
	t.Cleanup(func() { targets = saved; indexTargets() })
	indexTargets()
	change()
	indexTargets()
}

func TestBaitSandboxPasses(t *testing.T) {
	r := runBait(t)
	if !r.ok() {
		t.Fatalf("bait test failed: err=%q damaged=%d junkLeft=%v journal=%v", r.Error, len(r.Damaged), r.JunkLeft, r.JournalOK)
	}
	if r.Rules < 500 || r.Baits < 1000 || r.Junk < 3000 {
		t.Fatalf("too little was exercised: %+v", r)
	}
}

func TestBaitCatchesTooBroadRule(t *testing.T) {
	withTargets(t, func() {
		targets = append(targets, Target{ID: "evil", Cat: "browsers", Name: "Слишком широкое правило",
			Locs: []rules.Loc{{Path: `%LOCALAPPDATA%\Google\Chrome\User Data\Default`}}, Risk: "safe"})
	})
	r := runBait(t)
	if len(r.Damaged) == 0 {
		t.Fatal("a rule that wipes a browser profile was not caught")
	}
}

func TestBaitCatchesBrokenFilter(t *testing.T) {
	cases := map[string]func(*Target){
		"user_temp": func(t *Target) { t.Recent = false }, // fresh installer files
		"recent": func(t *Target) { // pinned jump lists
			for i := range t.Locs {
				t.Locs[i].Flat, t.Locs[i].Masks = false, nil
			}
		},
		"prefetch": func(t *Target) { // Layout.ini
			for i := range t.Locs {
				t.Locs[i].Masks = nil
			}
		},
		"win_logs": func(t *Target) { t.MinAge = 0 }, // logs being written
	}
	for id, mutate := range cases {
		t.Run(id, func(t *testing.T) {
			withTargets(t, func() { mutate(targetByID(id)) })
			if r := runBait(t); len(r.Damaged) == 0 {
				t.Fatalf("rule %s without its guard was not caught", id)
			}
		})
	}
}

func TestBaitCatchesIneffectiveRule(t *testing.T) {
	withTargets(t, func() {
		for i := range targets {
			if targets[i].ID == "pip" {
				targets[i].MinAge = 100000
			}
		}
	})
	r := runBait(t)
	if r.JunkLeft["pip"] == 0 {
		t.Fatalf("a rule that removes nothing was not caught: %+v", r.JunkLeft)
	}
}

// An imported rule that keeps files (Winapp2 ExcludeKey) must keep them:
// with the walker ignoring rule exclusions the bait test must fail.
func TestBaitCatchesIgnoredRuleExclusion(t *testing.T) {
	testIgnoreKeep = true
	t.Cleanup(func() { testIgnoreKeep = false })
	r := runBait(t)
	hit := false
	for _, b := range r.Damaged {
		if strings.Contains(b.Why, "исключение правила") {
			hit = true
		}
	}
	if !hit {
		t.Fatalf("ignored rule exclusions were not caught: %d damaged", len(r.Damaged))
	}
}

// Rules from Winapp2 count: the sandbox exercises hundreds of them.
func TestImportedRulesExercised(t *testing.T) {
	if rulesInfo.Imported < 500 {
		t.Fatalf("imported rules: %d", rulesInfo.Imported)
	}
	n := 0
	for _, tg := range targets {
		if tg.Source == "winapp2" {
			n++
			if tg.Risk != "safe" || len(tg.Locs) == 0 {
				t.Errorf("%s: risk %s, %d folders", tg.ID, tg.Risk, len(tg.Locs))
			}
		}
	}
	if n != rulesInfo.Imported {
		t.Fatalf("loaded %d of %d imported rules", n, rulesInfo.Imported)
	}
}

// The MFT path builds the same tree as the walk: folders by parent record,
// NTFS metadata and links left out, hard links counted once.
func TestBuildFromMFT(t *testing.T) {
	recs := make([]ntfs.Rec, 40)
	recs[ntfs.RecRoot] = ntfs.Rec{Dir: true, Name: ".", Parent: ntfs.RecRoot}
	recs[ntfs.RecExtend] = ntfs.Rec{Dir: true, Name: "$Extend", Parent: ntfs.RecRoot}
	recs[2] = ntfs.Rec{Name: "$LogFile", Parent: ntfs.RecRoot, Alloc: 64 << 20}
	recs[16] = ntfs.Rec{Dir: true, Name: "Users", Parent: ntfs.RecRoot}
	recs[17] = ntfs.Rec{Dir: true, Name: "Ann", Parent: 16}
	recs[18] = ntfs.Rec{Name: "video.mp4", Parent: 17, Size: 5000, Alloc: 8192, MTime: time.Now().Unix()}
	recs[19] = ntfs.Rec{Name: "notes.txt", Parent: 17, Size: 100, Alloc: 0}
	recs[20] = ntfs.Rec{Name: "shared.dll", Parent: 16, Size: 4096, Alloc: 4096, Links: 3}
	recs[21] = ntfs.Rec{Dir: true, Name: "Link", Parent: 17, Link: true}
	recs[22] = ntfs.Rec{Name: "behind-link.bin", Parent: 21, Alloc: 1 << 20}
	recs[23] = ntfs.Rec{Dir: true, Name: "$Deleted", Parent: ntfs.RecExtend}
	recs[24] = ntfs.Rec{Name: "journal", Parent: 23, Alloc: 32 << 20}
	recs[25] = ntfs.Rec{Name: "orphan.bin", Parent: 39, Alloc: 1 << 20} // parent record is unused
	recs[26] = ntfs.Rec{Dir: true, Name: "LoopA", Parent: 27}
	recs[27] = ntfs.Rec{Dir: true, Name: "LoopB", Parent: 26} // a corrupt cycle
	recs[28] = ntfs.Rec{Name: "cloud.docx", Parent: 17, Size: 900000, Alloc: 0, Attr: attrRecallData}
	recs[29] = ntfs.Rec{Name: "root.sys", Parent: ntfs.RecRoot, Size: 1 << 30, Alloc: 1 << 30}

	a := newAnalysis(t.TempDir(), newTestJob())
	a.buildFromMFT(recs)
	a.finalize()
	if a.root.files != 5 || a.root.size != 8192+4096+(1<<30) {
		t.Fatalf("root: %d files, %d bytes", a.root.files, a.root.size)
	}
	if len(a.root.children) != 1 || a.root.children[0].name != "Users" {
		t.Fatalf("root folders: %+v", a.root.children)
	}
	users := a.root.children[0]
	if len(users.children) != 1 || users.children[0].name != "Ann" || users.children[0].files != 3 || users.children[0].size != 8192 {
		t.Fatalf("Users: %+v", users.children[0])
	}
	if a.hardlinks.Load() != 2*4096 || a.cloud.Load() != 900000 {
		t.Fatalf("hard links %d, cloud %d", a.hardlinks.Load(), a.cloud.Load())
	}
	top := a.topFiles()
	if len(top) != 5 || top[0].Path != filepath.Join(a.Root, "root.sys") || top[1].Path != filepath.Join(a.Root, "Users", "Ann", "video.mp4") {
		t.Fatalf("top files: %+v", top)
	}
	if a.cats["video"] == nil || a.cats["video"].Size != 8192 {
		t.Fatalf("categories: %+v", a.cats)
	}
}

// "What grew": a snapshot after the first analysis, files added, the second
// analysis compared with it. Growth is shown where it happened, not on every
// parent folder above it.
func TestSnapshotGrowth(t *testing.T) {
	isolate(t)
	saved := [2]int64{snapMinSize, growthMin}
	snapMinSize, growthMin = 1<<20, 1<<20
	t.Cleanup(func() { snapMinSize, growthMin = saved[0], saved[1] })
	root := t.TempDir()
	mk := func(rel string, size int) {
		if err := writeTestFile(filepath.Join(root, rel), size, 30, int64(size)); err != nil {
			t.Fatal(err)
		}
	}
	mk("Games/save.bin", 2<<20)
	mk("Old/big.iso", 3<<20)
	analyse := func() *Analysis {
		a := newAnalysis(root, newTestJob())
		a.run()
		id, err := a.saveSnapshot()
		if err != nil {
			t.Fatal(err)
		}
		a.snapID = id
		return a
	}
	first := analyse()
	time.Sleep(1100 * time.Millisecond) // snapshot ids have one-second resolution
	mk("Games/Shooter/Cache/shaders.bin", 5<<20)
	mk("Downloads/movie.mkv", 4<<20)
	os.RemoveAll(filepath.Join(root, "Old"))
	second := analyse()

	list := listSnapshots(root)
	if len(list) != 2 || list[0].ID != second.snapID || list[1].ID != first.snapID {
		t.Fatalf("snapshots: %+v", list)
	}
	id := defaultSnapshot(list, second.snapID, second.method)
	if id != first.snapID {
		t.Fatalf("default snapshot %q, want %q", id, first.snapID)
	}
	old, err := loadSnapshot(root, id)
	if err != nil {
		t.Fatal(err)
	}
	g := second.growth(old)
	names := map[string]growthItem{}
	for _, it := range g.Grew {
		names[it.Name] = it
	}
	if _, ok := names["Cache"]; !ok || len(g.Grew) != 2 {
		t.Fatalf("grew: %+v", g.Grew)
	}
	if d := names["Downloads"]; !d.Added || d.Delta < 4<<20 {
		t.Fatalf("new folder: %+v", d)
	}
	if _, ok := names["Games"]; ok {
		t.Fatalf("a parent that grew only through its subfolder is listed: %+v", g.Grew)
	}
	if len(g.Shrank) != 1 || g.Shrank[0].Name != "Old" || !g.Shrank[0].Gone {
		t.Fatalf("shrank: %+v", g.Shrank)
	}
}

// Similar photos: a picture, its smaller copy and a slightly brighter copy
// form one group with the biggest one kept; an unrelated picture stays out.
func TestSimilarPhotos(t *testing.T) {
	dir := t.TempDir()
	saved := photoMinSize
	photoMinSize = 1 << 10 // the synthetic pictures compress very well
	t.Cleanup(func() { photoMinSize = saved })
	scene := func(w, h int, bright int, seed int) image.Image {
		img := image.NewRGBA(image.Rect(0, 0, w, h))
		for y := 0; y < h; y++ {
			for x := 0; x < w; x++ {
				// a few big shapes on a gradient: the brightness pattern dHash looks at
				v := 40 + x*120/w + y*40/h
				if (x/(w/5)+y/(h/4)+seed)%3 == 0 {
					v += 60
				}
				c := min(255, v+bright)
				img.Set(x, y, color.RGBA{uint8(c), uint8(c * 9 / 10), uint8(c * 7 / 10), 255})
			}
		}
		return img
	}
	save := func(name string, img image.Image) {
		f, err := os.Create(filepath.Join(dir, name))
		if err != nil {
			t.Fatal(err)
		}
		defer f.Close()
		if strings.HasSuffix(name, ".png") {
			png.Encode(f, img)
		} else {
			jpeg.Encode(f, img, &jpeg.Options{Quality: 92})
		}
	}
	save("IMG_0001.jpg", scene(1200, 900, 0, 1))
	save("IMG_0001 (копия).jpg", scaleDown(scene(1200, 900, 0, 1), 600))
	save("IMG_0001 edited.png", scene(1200, 900, 6, 1))
	save("other.jpg", scene(1200, 900, 0, 2))
	res, err := findSimilarPhotos(newTestJob(), dir)
	if err != nil {
		t.Fatal(err)
	}
	if res.Scanned != 4 || len(res.Groups) != 1 || len(res.Groups[0].Files) != 3 {
		t.Fatalf("groups: %+v", res.Groups)
	}
	g := res.Groups[0]
	if !g.Files[0].Best || g.Files[0].W != 1200 || g.Files[1].Best || g.Wasted != g.Files[1].Size+g.Files[2].Size {
		t.Fatalf("best copy: %+v", g)
	}
	for _, f := range g.Files {
		if strings.Contains(f.Path, "other") {
			t.Fatal("an unrelated picture was grouped")
		}
	}
	if b, err := photoThumb(g.Files[0].Path); err != nil || len(b) < 100 {
		t.Fatalf("preview: %v", err)
	}
	if _, err := photoThumb(filepath.Join(dir, "other.jpg")); err == nil {
		t.Fatal("preview of a file outside the results")
	}
}

// The EXIF preview inside a camera JPEG is found and used.
func TestExifThumb(t *testing.T) {
	var thumb bytes.Buffer
	jpeg.Encode(&thumb, image.NewRGBA(image.Rect(0, 0, 16, 12)), nil)
	// TIFF: header, IFD0 with no entries, IFD1 with the thumbnail's offset and length
	tiff := []byte("II*\x00\x08\x00\x00\x00")
	tiff = append(tiff, 0, 0, 14, 0, 0, 0) // IFD0: 0 entries, next IFD at 14
	ifd1 := []byte{2, 0}
	off := 14 + 2 + 2*12 + 4
	e := func(tag uint16, v uint32) []byte {
		b := make([]byte, 12)
		binary.LittleEndian.PutUint16(b, tag)
		binary.LittleEndian.PutUint16(b[2:], 4)
		binary.LittleEndian.PutUint32(b[4:], 1)
		binary.LittleEndian.PutUint32(b[8:], v)
		return b
	}
	ifd1 = append(ifd1, e(0x0201, uint32(off))...)
	ifd1 = append(ifd1, e(0x0202, uint32(thumb.Len()))...)
	ifd1 = append(ifd1, 0, 0, 0, 0)
	tiff = append(append(tiff, ifd1...), thumb.Bytes()...)
	app1 := append([]byte("Exif\x00\x00"), tiff...)
	file := []byte{0xFF, 0xD8, 0xFF, 0xE1, byte((len(app1) + 2) >> 8), byte(len(app1) + 2)}
	file = append(append(file, app1...), 0xFF, 0xDA, 0, 2)
	got := exifThumb(file)
	if !bytes.Equal(got, thumb.Bytes()) {
		t.Fatalf("thumbnail not found (%d bytes)", len(got))
	}
}
