package main

// Модульные тесты ядра. Запуск: go test ./...
// Проверки из самопроверки (очистка, анализ, дубликаты) выполняются здесь
// в изолированной временной папке, плюс отдельные пограничные случаи.

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"prostor/internal/rules"
	"strings"
	"testing"
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
