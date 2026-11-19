package main

// Самопроверка: Prostor.exe --selftest [--quick] [--no-console] [--no-open]
//
// Проверяет все части программы на этом компьютере и сохраняет HTML-отчёт.
// Удаляет только собственные тестовые файлы во временной папке; реальный
// мусор и системные папки только читаются.

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"html"
	"io"
	"log"
	"math/rand"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"runtime/debug"
	"sort"
	"strings"
	"sync"
	"time"

	"prostor/internal/cdp"
	"prostor/internal/rules"
	"prostor/internal/webui"
	"prostor/internal/webview2"
	"prostor/internal/winapi"
)

type stCheck struct {
	Group  string  `json:"group"`
	Name   string  `json:"name"`
	Status string  `json:"status"` // ok | warn | fail | skip
	Detail string  `json:"detail"`
	Secs   float64 `json:"secs"`
}

type stShot struct {
	Name string
	JPEG []byte
}

type selfTest struct {
	checks  []stCheck
	shots   []stShot
	env     [][2]string
	out     io.Writer
	quick   bool
	dir     string
	start   time.Time
	sysAn   *Analysis
	anDir   string
	anFiles map[string]int64
}

const (
	stOK   = "ok"
	stWarn = "warn"
	stFail = "fail"
	stSkip = "skip"
)

func runSelftest() int {
	quick := hasArg("--quick")
	created := false
	if !hasArg("--no-console") {
		_, created = winapi.Console(true, "Проверка Prostor")
	}
	if isAdmin {
		winapi.EnableBackupPrivilege()
	}
	t := &selfTest{out: os.Stdout, quick: quick, start: time.Now()}
	t.printf("\nПроверка Prostor %s\n", version)
	t.printf("Ничего, кроме собственных тестовых файлов, не удаляется.\n\n")
	log.Printf("selftest start quick=%v", quick)

	t.runAll()

	path, err := t.writeReport()
	var fails, warns int
	for _, c := range t.checks {
		switch c.Status {
		case stFail:
			fails++
		case stWarn:
			warns++
		}
	}
	t.printf("\nИтог: %d %s, ошибок: %d, предупреждений: %d, время: %s\n", len(t.checks),
		pluralRu(len(t.checks), "проверка", "проверки", "проверок"), fails, warns, durRu(time.Since(t.start)))
	if err != nil {
		t.printf("Не удалось сохранить отчёт: %v\n", err)
	} else {
		t.printf("Отчёт: %s\n", path)
		if !hasArg("--no-open") {
			winapi.ShellOpen(path)
		}
	}
	log.Printf("selftest done: %d checks, %d fails, %d warns, report %s", len(t.checks), fails, warns, path)
	if created {
		t.printf("\nНажмите Enter, чтобы закрыть окно.")
		winapi.WaitEnter()
	}
	if fails > 0 {
		return 1
	}
	return 0
}

// secRu formats seconds the Russian way: "0,4 с", "12 с", "2 мин 5 с".
func secRu(sec float64) string {
	switch {
	case sec < 10:
		return strings.Replace(fmt.Sprintf("%.1f с", sec), ".", ",", 1)
	case sec < 60:
		return fmt.Sprintf("%.0f с", sec)
	}
	return durRu(time.Duration(sec * float64(time.Second)))
}

func durRu(d time.Duration) string {
	if d < 10*time.Second {
		return secRu(d.Seconds())
	}
	s := int(d.Round(time.Second).Seconds())
	if s < 60 {
		return fmt.Sprintf("%d с", s)
	}
	if s%60 == 0 {
		return fmt.Sprintf("%d мин", s/60)
	}
	return fmt.Sprintf("%d мин %d с", s/60, s%60)
}

func pluralRu(n int, one, few, many string) string {
	a, b := n%100, n%10
	switch {
	case a > 10 && a < 20:
		return many
	case b == 1:
		return one
	case b > 1 && b < 5:
		return few
	}
	return many
}

func (t *selfTest) printf(f string, a ...any) { fmt.Fprintf(t.out, f, a...) }

// check runs one test, catching panics, and prints its line.
func (t *selfTest) check(group, name string, fn func() (string, string)) stCheck {
	st := time.Now()
	status, detail := func() (s, d string) {
		defer func() {
			if r := recover(); r != nil {
				s, d = stFail, fmt.Sprintf("внутренняя ошибка: %v", r)
				log.Printf("selftest panic in %q: %v\n%s", name, r, debug.Stack())
			}
		}()
		return fn()
	}()
	c := stCheck{Group: group, Name: name, Status: status, Detail: detail, Secs: time.Since(st).Seconds()}
	t.checks = append(t.checks, c)
	mark := map[string]string{stOK: "[ OK ]", stWarn: "[ !! ]", stFail: "[FAIL]", stSkip: "[SKIP]"}[status]
	line := fmt.Sprintf("%s %s: %s", mark, group, name)
	if detail != "" {
		line += " — " + detail
	}
	if c.Secs >= 1 {
		line += " (" + secRu(c.Secs) + ")"
	}
	t.printf("%s\n", line)
	return c
}

func okf(f string, a ...any) (string, string)   { return stOK, fmt.Sprintf(f, a...) }
func warnf(f string, a ...any) (string, string) { return stWarn, fmt.Sprintf(f, a...) }
func failf(f string, a ...any) (string, string) { return stFail, fmt.Sprintf(f, a...) }
func skipf(f string, a ...any) (string, string) { return stSkip, fmt.Sprintf(f, a...) }

func newTestJob() *Job {
	j := &Job{cancel: make(chan struct{})}
	j.current.Store("")
	return j
}

func (t *selfTest) runAll() {
	t.envChecks()
	dir, err := os.MkdirTemp("", "ProstorSelfTest-")
	if err != nil {
		t.check("Окружение", "Временная папка для тестов", func() (string, string) { return failf("%v", err) })
		return
	}
	t.dir = dir
	defer func() {
		t.check("Окружение", "Удаление тестовых файлов", func() (string, string) {
			if err := os.RemoveAll(dir); err != nil {
				return warnf("не удалось удалить %s: %v", dir, err)
			}
			return okf("тестовая папка удалена")
		})
	}()
	t.cleanerChecks()
	t.lockChecks()
	t.baitChecks()
	t.analyzerChecks()
	t.dupesChecks()
	t.systemChecks()
	t.apiAndUIChecks()
	t.windowChecks()
}

// baitChecks runs the bait test in a separate process (it redirects the
// whole environment, which must not leak into the other checks).
func (t *selfTest) baitChecks() {
	const g = "Безопасность очистки"
	t.check(g, "Тест приманок: все правила очистки", func() (string, string) {
		out := filepath.Join(t.dir, "bait.json")
		cmd := exec.Command(exePath, "--bait-test", "--no-console", "--json", out)
		winapi.HideWindow(cmd)
		done := make(chan error, 1)
		if err := cmd.Start(); err != nil {
			return failf("не удалось запустить: %v", err)
		}
		go func() { done <- cmd.Wait() }()
		select {
		case <-done:
		case <-time.After(5 * time.Minute):
			cmd.Process.Kill()
			return failf("тест приманок не уложился в 5 минут")
		}
		b, err := os.ReadFile(out)
		if err != nil {
			return failf("нет отчёта теста приманок: %v", err)
		}
		var r BaitReport
		if err := json.Unmarshal(b, &r); err != nil {
			return failf("отчёт не читается: %v", err)
		}
		if r.Error != "" {
			return failf("%s", r.Error)
		}
		if len(r.Damaged) > 0 {
			var names []string
			for i, d := range r.Damaged {
				if i == 5 {
					names = append(names, fmt.Sprintf("и ещё %d", len(r.Damaged)-5))
					break
				}
				names = append(names, d.Why+": "+d.Path)
			}
			return failf("очистка повредила приманки (%d): %s", len(r.Damaged), strings.Join(names, "; "))
		}
		if len(r.JunkLeft) > 0 {
			var ids []string
			for id := range r.JunkLeft {
				ids = append(ids, id)
			}
			sort.Strings(ids)
			return failf("правила не удалили свой мусор: %s", strings.Join(ids, ", "))
		}
		if !r.JournalOK {
			return failf("журнал удалений неполный")
		}
		return okf("%d правил, %d приманок целы, удалено %d мусорных файлов за %s", r.Rules, r.Baits, r.Junk, secRu(r.Seconds))
	})
}

// windowChecks tries the program's own WebView2 window in a separate process.
func (t *selfTest) windowChecks() {
	t.check("Интерфейс", "Собственное окно (WebView2)", func() (string, string) {
		if !winapi.IsWindows {
			return skipf("только в Windows")
		}
		ver := webview2.Version()
		if ver == "" {
			return warnf("среда WebView2 не установлена, используется окно Edge")
		}
		start := time.Now()
		if err := probeWebView2(40 * time.Second); err != nil {
			return warnf("WebView2 %s не открылся (%v), используется окно Edge", ver, err)
		}
		return okf("WebView2 %s: окно открылось и загрузило страницу за %s", ver, secRu(time.Since(start).Seconds()))
	})
}

// ---------------------------------------------------------------- окружение

func (t *selfTest) envChecks() {
	g := "Окружение"
	host, _ := os.Hostname()
	t.env = append(t.env, [2]string{"Версия Prostor", version}, [2]string{"Windows", winapi.OSVersion()},
		[2]string{"Компьютер", host}, [2]string{"Процессоров", fmt.Sprint(runtime.NumCPU())})
	total, avail := winapi.Memory()
	t.env = append(t.env, [2]string{"Память", fmt.Sprintf("%s, свободно %s", human(int64(total)), human(int64(avail)))})
	t.check(g, "Права администратора", func() (string, string) {
		t.env = append(t.env, [2]string{"Права администратора", map[bool]string{true: "да", false: "нет"}[isAdmin]})
		if !isAdmin {
			return warnf("нет: системные папки не проверяются полностью. Запустите проверку от имени администратора")
		}
		return okf("есть")
	})
	t.check(g, "Папка данных программы", func() (string, string) {
		d := appDataDir()
		p := filepath.Join(d, "selftest.tmp")
		if err := os.WriteFile(p, []byte("ok"), 0o644); err != nil {
			return failf("нельзя записать в %s: %v", d, err)
		}
		b, err := os.ReadFile(p)
		os.Remove(p)
		if err != nil || string(b) != "ok" {
			return failf("файл в %s читается неверно", d)
		}
		t.env = append(t.env, [2]string{"Данные программы", d})
		return okf("%s", d)
	})
	t.check(g, "Настройки", func() (string, string) {
		s := getSettings()
		if err := saveSettings(s); err != nil {
			return failf("не сохраняются: %v", err)
		}
		loadStore()
		s2 := getSettings()
		if s2.Theme != s.Theme || s2.SkipRecentHours != s.SkipRecentHours || len(s2.Excludes) != len(s.Excludes) {
			return failf("после сохранения настройки читаются иначе")
		}
		return okf("сохраняются и читаются")
	})
	t.check(g, "Браузер для окна программы", func() (string, string) {
		b := webui.FindBrowser()
		if b == "" {
			return failf("Edge и Chrome не найдены: окно откроется в браузере по умолчанию")
		}
		t.env = append(t.env, [2]string{"Браузер", b})
		return okf("%s", b)
	})
	t.check(g, "Свободное место на системном диске", func() (string, string) {
		d, ok := systemDriveInfo()
		if !ok {
			return failf("системный диск не найден")
		}
		pct := float64(d.Free) / float64(d.Total) * 100
		t.env = append(t.env, [2]string{"Системный диск", fmt.Sprintf("%s: свободно %s из %s", d.Root, human(int64(d.Free)), human(int64(d.Total)))})
		if pct < 10 {
			return warnf("свободно %s (%.0f%%): места мало", human(int64(d.Free)), pct)
		}
		return okf("свободно %s из %s", human(int64(d.Free)), human(int64(d.Total)))
	})
}

func systemDriveInfo() (winapi.Drive, bool) {
	sd := os.Getenv("SystemDrive")
	if sd == "" {
		sd = "C:"
	}
	drives := winapi.Drives()
	for _, d := range drives {
		if strings.HasPrefix(lower(d.Root), lower(sd)) {
			return d, true
		}
	}
	if len(drives) > 0 && !winapi.IsWindows {
		return drives[0], true
	}
	return winapi.Drive{}, false
}

// ---------------------------------------------------------------- тестовые файлы

// writeTestFile writes size bytes of deterministic pseudo-random data.
func writeTestFile(path string, size int, ageDays float64, seed int64) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	buf := make([]byte, size)
	rand.New(rand.NewSource(seed)).Read(buf)
	if err := os.WriteFile(path, buf, 0o644); err != nil {
		return err
	}
	mt := time.Now().Add(-time.Duration(ageDays * float64(24*time.Hour)))
	return os.Chtimes(path, mt, mt)
}

func exists(p string) bool { _, err := os.Lstat(p); return err == nil }

// makeLink creates a directory junction (Windows) or symlink to target.
func makeLink(link, target string) error {
	if winapi.IsWindows {
		cmd := exec.Command("cmd", "/c", "mklink", "/J", link, target)
		winapi.HideWindow(cmd)
		if out, err := cmd.CombinedOutput(); err != nil {
			return fmt.Errorf("%v: %s", err, strings.TrimSpace(winapi.DecodeConsole(out)))
		}
		return nil
	}
	return os.Symlink(target, link)
}

// ---------------------------------------------------------------- очистка

// lockChecks: a file another program keeps open is reported with that program.
func (t *selfTest) lockChecks() {
	t.check("Безопасность очистки", "Программа, которая держит файл, определяется", func() (string, string) {
		if !winapi.IsWindows {
			return skipf("только в Windows")
		}
		p := filepath.Join(t.dir, "held.tmp")
		if err := os.WriteFile(p, make([]byte, 4096), 0o644); err != nil {
			return failf("не удалось создать файл: %v", err)
		}
		release, err := winapi.HoldFile(p)
		if err != nil {
			return failf("не удалось открыть файл: %v", err)
		}
		rmErr := removeFile(p)
		hs := lockHolders([]string{p})
		release()
		if rmErr == nil {
			return failf("занятый файл удалился — проверка не имеет смысла")
		}
		if !winapi.InUse(rmErr) {
			return failf("ошибка удаления не распознана как «файл занят»: %v", rmErr)
		}
		for _, h := range hs {
			if h.PID == uint32(os.Getpid()) {
				os.Remove(p)
				return okf("файл держит %s (PID %d) — так и есть", h.App, h.PID)
			}
		}
		return failf("Restart Manager не назвал программу: %+v", hs)
	})
}

func (t *selfTest) cleanerChecks() {
	g := "Безопасность очистки"
	t.check(g, "Все пути очистки раскрываются", func() (string, string) {
		var bad []string
		n := 0
		for i := range targets {
			for _, l := range targets[i].Locs {
				n++
				e := expandPath(l.Path)
				if e != "" && !filepath.IsAbs(e) {
					bad = append(bad, targets[i].Name+": "+l.Path)
				}
			}
		}
		if len(bad) > 0 {
			return failf("относительные пути: %s", strings.Join(bad, "; "))
		}
		return okf("%d %s в %d пунктах", n, pluralRu(n, "путь", "пути", "путей"), len(targets))
	})
	t.check(g, "Ни один пункт не затрагивает личные и системные папки", func() (string, string) {
		var protect []string
		for _, k := range []string{"desktop", "documents", "downloads", "pictures", "videos", "music"} {
			if p := winapi.KnownFolder(k); p != "" {
				protect = append(protect, lower(filepath.Clean(p)))
			}
		}
		for _, e := range []string{"SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "USERPROFILE"} {
			if v := os.Getenv(e); v != "" {
				protect = append(protect, lower(filepath.Clean(v)))
			}
		}
		var bad []string
		roots := 0
		dc := newDirCache()
		for i := range targets {
			for _, r := range resolveRoots(&targets[i], dc) {
				roots++
				lp := lower(r.path)
				for _, p := range protect {
					// a cleanup root must not be a protected folder or contain one
					if lp == p || strings.HasPrefix(p, lp+string(filepath.Separator)) {
						bad = append(bad, targets[i].Name+": "+r.path)
					}
				}
			}
		}
		if len(bad) > 0 {
			return failf("опасные пути: %s", strings.Join(bad, "; "))
		}
		return okf("проверено папок: %d", roots)
	})

	// песочница: что удаляется и что обязано уцелеть
	base := filepath.Join(t.dir, "clean")
	victim := filepath.Join(t.dir, "victim", "precious.txt")
	type tf struct {
		rel    string
		size   int
		age    float64
		delete bool
	}
	files := []tf{
		{"old1.tmp", 128 << 10, 10, true},
		{"sub/old2.tmp", 256 << 10, 10, true},
		{"sub/deeper/old3.log", 64 << 10, 40, true},
		{"new.tmp", 64 << 10, 0, false},
		{"keep/old4.tmp", 64 << 10, 10, false},
	}
	var setupErr error
	var expect int64
	for i, f := range files {
		if err := writeTestFile(filepath.Join(base, filepath.FromSlash(f.rel)), f.size, f.age, int64(100+i)); err != nil {
			setupErr = err
		}
		if f.delete {
			expect += int64(f.size)
		}
	}
	if err := writeTestFile(victim, 64<<10, 10, 99); err != nil {
		setupErr = err
	}
	linkErr := makeLink(filepath.Join(base, "link"), filepath.Dir(victim))
	tg := &Target{ID: "selftest", Name: "Тестовая папка", Locs: []rules.Loc{{Path: base}}, Recent: true}
	s := defaultSettings()
	s.SkipRecentHours = 24
	s.Excludes = []string{filepath.Join(base, "keep")}

	t.check(g, "Поиск мусора в тестовой папке", func() (string, string) {
		if setupErr != nil {
			return failf("не удалось создать тестовые файлы: %v", setupErr)
		}
		it := scanTarget(tg, s, nil, nil, nil)
		if it.Size != expect || it.Files != 3 {
			return failf("найдено %s в %d файлах, ожидалось %s в 3", human(it.Size), it.Files, human(expect))
		}
		return okf("найдено ровно то, что нужно: %s в 3 файлах", human(it.Size))
	})
	var res CleanItem
	t.check(g, "Удаление в тестовой папке", func() (string, string) {
		if setupErr != nil {
			return skipf("нет тестовых файлов")
		}
		res = cleanTarget(newTestJob(), tg, s, isAdmin, nil)
		var problems []string
		for _, f := range files {
			p := filepath.Join(base, filepath.FromSlash(f.rel))
			if f.delete && exists(p) {
				problems = append(problems, "не удалён "+f.rel)
			}
			if !f.delete && !exists(p) {
				problems = append(problems, "удалён лишний файл "+f.rel)
			}
		}
		if exists(filepath.Join(base, "sub")) {
			problems = append(problems, "осталась опустевшая папка sub")
		}
		if !exists(base) {
			problems = append(problems, "удалена сама корневая папка")
		}
		if res.Freed != expect {
			problems = append(problems, fmt.Sprintf("отчёт об освобождении %s вместо %s", human(res.Freed), human(expect)))
		}
		if len(problems) > 0 {
			return failf("%s", strings.Join(problems, "; "))
		}
		return okf("удалены 3 старых файла, опустевшие папки убраны")
	})
	t.check(g, "Свежие файлы и исключения не трогаются", func() (string, string) {
		if setupErr != nil {
			return skipf("нет тестовых файлов")
		}
		if !exists(filepath.Join(base, "new.tmp")) {
			return failf("удалён свежий файл")
		}
		if !exists(filepath.Join(base, "keep", "old4.tmp")) {
			return failf("удалён файл из исключённой папки")
		}
		return okf("свежий файл и исключённая папка на месте")
	})
	t.check(g, "Ссылки на другие папки не открываются", func() (string, string) {
		if linkErr != nil {
			return skipf("не удалось создать ссылку: %v", linkErr)
		}
		if !exists(victim) {
			return failf("удалён файл за ссылкой: очистка прошла по ссылке в чужую папку")
		}
		return okf("файл за ссылкой цел")
	})
	os.Remove(filepath.Join(base, "link")) // only the link itself

	t.check("Поиск мусора", "Сканирование всех пунктов очистки", func() (string, string) {
		st := time.Now()
		list := offered(newDirCache())
		var ids []string
		imported := 0
		for _, tg := range list {
			ids = append(ids, tg.ID)
			if tg.Source != "" {
				imported++
			}
		}
		items := scanTargets(newTestJob(), ids, getSettings(), map[uint32]bool{})
		var total, files int64
		found := 0
		for _, it := range items {
			total += it.Size
			files += it.Files
			if it.Found {
				found++
			}
		}
		took := time.Since(st)
		if len(items) != len(ids) {
			return failf("результатов %d из %d", len(items), len(ids))
		}
		msg := fmt.Sprintf("правил %d (из базы Winapp2 подошли к этому компьютеру %d из %d), найдено %s в %d файлах за %s",
			len(ids), imported, rulesInfo.Imported, human(total), files, secRu(took.Seconds()))
		switch {
		case took > 15*time.Second:
			return failf("%s — цель до 5 с", msg)
		case took > 5*time.Second:
			return warnf("%s — дольше цели в 5 с", msg)
		}
		return okf("%s", msg)
	})
}

// ---------------------------------------------------------------- анализ диска

func (t *selfTest) analyzerChecks() {
	g := "Анализ диска"
	root := filepath.Join(t.dir, "an")
	t.anDir = root
	files := map[string]int64{
		"a/1.bin":         640 << 10,
		"a/2.bin":         192 << 10,
		"a/deep/x/y/3.db": 1 << 20,
		"b/4.iso":         2 << 20,
		"b/5.mp4":         1536 << 10,
		"6.txt":           64 << 10,
	}
	t.anFiles = files
	var setupErr error
	var expect int64
	i := 0
	for rel, size := range files {
		i++
		if err := writeTestFile(filepath.Join(root, filepath.FromSlash(rel)), int(size), float64(i*40), int64(i)); err != nil {
			setupErr = err
		}
		expect += size
	}
	os.MkdirAll(filepath.Join(root, "c", "empty"), 0o755)
	linkPath := filepath.Join(root, "links", "1-copy.bin")
	os.MkdirAll(filepath.Dir(linkPath), 0o755)
	linkErr := os.Link(filepath.Join(root, "a", "1.bin"), linkPath)

	t.check(g, "Быстрое чтение папок", func() (string, string) {
		if setupErr != nil {
			return failf("не удалось создать тестовые файлы: %v", setupErr)
		}
		fast, err := winapi.ReadDir(filepath.Join(root, "b"))
		if err != nil {
			return failf("%v", err)
		}
		slow, _ := os.ReadDir(filepath.Join(root, "b"))
		if len(fast) != len(slow) {
			return failf("прочитано %d записей вместо %d", len(fast), len(slow))
		}
		ids := 0
		for _, e := range fast {
			want := files["b/"+e.Name]
			if e.Size != want {
				return failf("%s: размер %d вместо %d", e.Name, e.Size, want)
			}
			if e.Alloc < 0 || e.MTime <= 0 {
				return failf("%s: неверные сведения о файле", e.Name)
			}
			if e.ID != 0 {
				ids++
			}
		}
		if ids == 0 {
			return warnf("файловая система не сообщает номера файлов: жёсткие ссылки не распознаются")
		}
		return okf("размеры, даты и номера файлов читаются верно")
	})

	runAn := func(dedupe bool) *Analysis {
		a := newAnalysis(root, newTestJob())
		if dedupe {
			a.sysRoot = lower(filepath.Clean(root))
			a.root.sys = true
		} else {
			a.sysRoot = ""
			a.root.sys = false
		}
		a.run()
		return a
	}
	t.check(g, "Подсчёт размеров", func() (string, string) {
		if setupErr != nil {
			return skipf("нет тестовых файлов")
		}
		a := runAn(true)
		if a.root.files != int64(len(files)) {
			return failf("файлов %d вместо %d", a.root.files, len(files))
		}
		var problems []string
		if a.root.size != expect {
			// size on disk may differ on compressed folders; compare with what the disk reports
			var alloc int64
			for rel := range files {
				ents, _ := winapi.ReadDir(filepath.Dir(filepath.Join(root, filepath.FromSlash(rel))))
				for _, e := range ents {
					if e.Name == filepath.Base(rel) {
						alloc += e.Alloc
					}
				}
			}
			if a.root.size != alloc {
				problems = append(problems, fmt.Sprintf("итог %s, ожидалось %s", human(a.root.size), human(expect)))
			}
		}
		// every folder must equal its files plus its subfolders
		var walk func(n *dirNode)
		walk = func(n *dirNode) {
			s := n.own
			for _, c := range n.children {
				s += c.size
				walk(c)
			}
			if s != n.size {
				problems = append(problems, fmt.Sprintf("папка %s не сходится", n.name))
			}
			for k := 1; k < len(n.children); k++ {
				if n.children[k].size > n.children[k-1].size {
					problems = append(problems, fmt.Sprintf("папка %s отсортирована неверно", n.name))
					break
				}
			}
		}
		walk(a.root)
		top := a.topFiles()
		if len(top) != len(files) || top[0].Size < top[len(top)-1].Size || !strings.HasSuffix(top[0].Path, "4.iso") {
			problems = append(problems, "список крупных файлов неверен")
		}
		tv := a.types()
		var cs int64
		for _, c := range tv.Cats {
			cs += c["size"].(int64)
		}
		if cs != a.root.size {
			problems = append(problems, "сумма по типам не равна итогу")
		}
		v, err := a.view(root)
		if err != nil {
			problems = append(problems, "просмотр корня: "+err.Error())
		} else {
			var vs int64
			for _, it := range v.Items {
				vs += it.Size
			}
			if vs != v.Size {
				problems = append(problems, fmt.Sprintf("элементы карты дают %s вместо %s", human(vs), human(v.Size)))
			}
		}
		if _, err := a.view(filepath.Join(root, "a", "deep")); err != nil {
			problems = append(problems, "просмотр вложенной папки: "+err.Error())
		}
		if len(problems) > 0 {
			return failf("%s", strings.Join(problems, "; "))
		}
		return okf("итог, папки, типы файлов и карта сходятся: %s", human(a.root.size))
	})
	t.check(g, "Жёсткие ссылки считаются один раз", func() (string, string) {
		if linkErr != nil {
			return skipf("не удалось создать жёсткую ссылку: %v", linkErr)
		}
		plain := runAn(false)
		dedup := runAn(true)
		if dedup.hardlinks.Load() == 0 {
			return failf("ссылка не распознана, файл посчитан дважды")
		}
		if plain.root.size-dedup.root.size != dedup.hardlinks.Load() {
			return failf("разница %s не совпадает с размером ссылки %s", human(plain.root.size-dedup.root.size), human(dedup.hardlinks.Load()))
		}
		return okf("повтор %s не посчитан второй раз", human(dedup.hardlinks.Load()))
	})

	d, ok := systemDriveInfo()
	// MFT reading is fast, so with it the system drive is analysed in the quick mode too
	mft := ok && (&Analysis{Root: normalizeRoot(d.Root), isDrive: true}).mftPossible()
	if t.quick && !mft {
		t.check(g, "Анализ системного диска", func() (string, string) { return skipf("быстрый режим (--quick)") })
		return
	}
	mftDir, mftLinked, mftSetup := t.mftTestTree()
	t.check(g, "Анализ системного диска", func() (string, string) {
		if !ok {
			return failf("системный диск не найден")
		}
		j := newTestJob()
		timer := time.AfterFunc(15*time.Minute, j.Cancel)
		defer timer.Stop()
		a := newAnalysis(normalizeRoot(d.Root), j)
		stop := make(chan struct{})
		go func() { // прогресс в консоли
			tk := time.NewTicker(5 * time.Second)
			defer tk.Stop()
			for {
				select {
				case <-stop:
					return
				case <-tk.C:
					t.printf("       … просмотрено %d файлов, %s\n", j.Files.Load(), human(j.Bytes.Load()))
				}
			}
		}()
		a.run()
		close(stop)
		if j.Cancelled() {
			return failf("не закончился за 15 минут (просмотрено %d файлов)", j.Files.Load())
		}
		t.sysAn = a
		var ms runtime.MemStats
		runtime.ReadMemStats(&ms)
		used := a.diskUsed
		how := "обходом папок"
		if a.method == "mft" {
			how = "из таблицы файлов NTFS (MFT)"
		}
		msg := fmt.Sprintf("%s в %d файлах за %s %s, служебное и недоступное %s, папок без доступа %d, память %s",
			human(a.root.size), a.root.files, secRu(a.took), how, human(a.other), a.denied.Load(), human(int64(ms.Sys)))
		if a.hardlinks.Load() > 0 {
			msg += fmt.Sprintf(", общие файлы Windows учтены один раз (%s)", human(a.hardlinks.Load()))
		}
		if used > 0 && float64(a.root.size) > float64(used)*1.02 {
			return failf("насчитано %s при занятых %s — двойной подсчёт. %s", human(a.root.size), human(used), msg)
		}
		if used > 0 && float64(a.root.size) < float64(used)*0.5 && isAdmin {
			return warnf("учтена только %.0f%% занятого места. %s", float64(a.root.size)/float64(used)*100, msg)
		}
		return okf("%s", msg)
	})
	t.check(g, "Данные карты системного диска сходятся", func() (string, string) {
		a := t.sysAn
		if a == nil {
			return skipf("анализ диска не выполнен")
		}
		v, err := a.view(a.Root)
		if err != nil {
			return failf("%v", err)
		}
		var vs int64
		for k, it := range v.Items {
			if it.Size < 0 {
				return failf("отрицательный размер у %s", it.Name)
			}
			if k > 0 && it.Size > v.Items[k-1].Size {
				return failf("элементы не отсортированы по размеру")
			}
			var ks int64
			for _, kd := range it.Kids {
				ks += kd.Size
			}
			if it.IsDir && ks > it.Size {
				return failf("содержимое папки %s больше самой папки", it.Name)
			}
			vs += it.Size
		}
		diff := vs - v.Size
		if diff < 0 {
			diff = -diff
		}
		// файлы в корне читаются заново и могли чуть измениться (pagefile, журналы)
		if diff > 256<<20 && float64(diff) > float64(v.Size)*0.005 {
			return failf("элементы карты дают %s, а итог %s", human(vs), human(v.Size))
		}
		return okf("%d элементов в корне, сумма совпадает с итогом", len(v.Items))
	})
	t.mftChecks(mft, mftDir, mftLinked, mftSetup)
}

// ---------------------------------------------------------------- дубликаты

func (t *selfTest) dupesChecks() {
	g := "Дубликаты"
	root := filepath.Join(t.dir, "dupes")
	var setupErr error
	w := func(rel string, size int, seed int64) {
		if err := writeTestFile(filepath.Join(root, filepath.FromSlash(rel)), size, 5, seed); err != nil {
			setupErr = err
		}
	}
	w("photos/x.jpg", 192<<10, 1)
	w("telegram/x copy.jpg", 192<<10, 1)
	w("x (1).jpg", 192<<10, 1)
	w("docs/y.pdf", 128<<10, 2)
	w("y-final.pdf", 128<<10, 2)
	w("z.jpg", 192<<10, 3) // тот же размер, другое содержимое
	w("w.zip", 320<<10, 4)
	t.check(g, "Поиск одинаковых файлов", func() (string, string) {
		if setupErr != nil {
			return failf("не удалось создать тестовые файлы: %v", setupErr)
		}
		res, err := findDuplicates(newTestJob(), root, 64<<10)
		if err != nil {
			return failf("%v", err)
		}
		if len(res.Groups) != 2 {
			return failf("групп %d вместо 2", len(res.Groups))
		}
		sort.Slice(res.Groups, func(i, j int) bool { return res.Groups[i].Size > res.Groups[j].Size })
		if len(res.Groups[0].Files) != 3 || len(res.Groups[1].Files) != 2 {
			return failf("в группах %d и %d файлов вместо 3 и 2", len(res.Groups[0].Files), len(res.Groups[1].Files))
		}
		if want := int64(2*192<<10 + 128<<10); res.Wasted != want {
			return failf("лишнего %s вместо %s", human(res.Wasted), human(want))
		}
		return okf("найдены обе группы, файл того же размера с другим содержимым не спутан")
	})
}

// ---------------------------------------------------------------- система

func (t *selfTest) systemChecks() {
	g := "Система"
	timed := func(name string, warnAfter time.Duration, fn func() (string, string)) {
		t.check(g, name, func() (string, string) {
			st := time.Now()
			s, d := fn()
			if s == stOK && time.Since(st) > warnAfter {
				return warnf("%s, но медленно: %s", d, time.Since(st).Round(100*time.Millisecond))
			}
			return s, d
		})
	}
	timed("Список дисков", 2*time.Second, func() (string, string) {
		ds := winapi.Drives()
		if _, ok := systemDriveInfo(); !ok {
			return failf("системный диск не найден среди %d дисков", len(ds))
		}
		var names []string
		for _, d := range ds {
			names = append(names, strings.TrimRight(d.Root, `\`))
		}
		return okf("%s", strings.Join(names, ", "))
	})
	timed("Установленные программы", 5*time.Second, func() (string, string) {
		list := listPrograms()
		if len(list) == 0 && winapi.IsWindows {
			return warnf("список пуст")
		}
		var total int64
		for _, p := range list {
			total += p.Size
			if p.Name == "" || p.Key == "" {
				return failf("программа без названия или ключа")
			}
		}
		return okf("%d, заявленный размер %s", len(list), human(total))
	})
	timed("Автозагрузка", 3*time.Second, func() (string, string) {
		list := listStartup()
		on := 0
		for _, s := range list {
			if s.Enabled {
				on++
			}
		}
		return okf("%d элементов, включено %d", len(list), on)
	})
	timed("Запущенные процессы", 2*time.Second, func() (string, string) {
		n := len(winapi.Processes())
		if n < 5 && winapi.IsWindows {
			return failf("видно только %d процессов", n)
		}
		return okf("%d", n)
	})
	timed("Корзина", 3*time.Second, func() (string, string) {
		b, n := winapi.RecycleBin()
		return okf("%s, объектов: %d", human(b), n)
	})
	t.check(g, "Известные папки", func() (string, string) {
		var miss []string
		for _, k := range []string{"downloads", "documents", "desktop"} {
			p := winapi.KnownFolder(k)
			if p == "" || !exists(p) {
				miss = append(miss, k)
			}
		}
		if len(miss) > 0 && winapi.IsWindows {
			return warnf("не найдены: %s", strings.Join(miss, ", "))
		}
		return okf("Загрузки, Документы и Рабочий стол на месте")
	})
	t.check(g, "Инструменты Windows", func() (string, string) {
		if !winapi.IsWindows {
			return skipf("только в Windows")
		}
		sys := filepath.Join(os.Getenv("SystemRoot"), "System32")
		var miss []string
		for _, f := range []string{"schtasks.exe", "Dism.exe", "cleanmgr.exe", "powercfg.exe", "wevtutil.exe"} {
			if !exists(filepath.Join(sys, f)) {
				miss = append(miss, f)
			}
		}
		for _, tl := range tools {
			if toolByID(tl.ID) == nil {
				return failf("инструмент %s не находится", tl.ID)
			}
		}
		if len(miss) > 0 {
			return warnf("нет программ: %s — часть инструментов не сработает", strings.Join(miss, ", "))
		}
		return okf("%d инструментов, нужные программы Windows на месте", len(tools))
	})
}

// ---------------------------------------------------------------- API и интерфейс

func (t *selfTest) apiAndUIChecks() {
	g := "Интерфейс"
	srv, err := webui.New(webui.Options{Title: windowTitle, Handler: routes(), ProfileDir: filepath.Join(t.dir, "profile")})
	if err != nil {
		t.check(g, "Внутренний сервер", func() (string, string) { return failf("%v", err) })
		return
	}
	ui = srv
	base, stop := srv.ServeOnly()
	defer stop()
	root := strings.SplitN(base, "?", 2)[0]
	token := srv.Token

	// результат анализа системного диска показываем в интерфейсе как готовую задачу
	if t.sysAn != nil {
		a := t.sysAn
		analysisMu.Lock()
		analysis = a
		analysisMu.Unlock()
		startJob("analyze", func(j *Job) (any, error) { return a.summary(), nil })
		time.Sleep(100 * time.Millisecond)
	}

	t.check(g, "Ответы программы", func() (string, string) {
		c := &http.Client{Timeout: 60 * time.Second}
		type ep struct{ path, ctype string }
		eps := []ep{{"/", "text/html"}, {"/app.js", "javascript"}, {"/icons.js", "javascript"}, {"/app.css", "text/css"}, {"/icon.png", "image/png"},
			{"/api/info", "json"}, {"/api/clean/targets", "json"}, {"/api/folders", "json"}, {"/api/programs", "json"},
			{"/api/startup", "json"}, {"/api/tools", "json"}, {"/api/history", "json"}, {"/api/settings", "json"}, {"/api/job?kind=scan", "json"}}
		var slow, bad []string
		for _, e := range eps {
			req, _ := http.NewRequest("GET", root+strings.TrimPrefix(e.path, "/"), nil)
			req.Header.Set("X-Token", token)
			st := time.Now()
			resp, err := c.Do(req)
			if err != nil {
				bad = append(bad, e.path+": "+err.Error())
				continue
			}
			body, _ := io.ReadAll(resp.Body)
			resp.Body.Close()
			took := time.Since(st)
			if resp.StatusCode != 200 {
				bad = append(bad, fmt.Sprintf("%s: %s", e.path, resp.Status))
				continue
			}
			if !strings.Contains(resp.Header.Get("Content-Type"), e.ctype) {
				bad = append(bad, fmt.Sprintf("%s: тип %q", e.path, resp.Header.Get("Content-Type")))
			}
			if e.ctype == "json" && !json.Valid(body) {
				bad = append(bad, e.path+": неверный JSON")
			}
			if took > 2*time.Second && e.path != "/api/programs" {
				slow = append(slow, e.path+" "+secRu(took.Seconds()))
			}
		}
		req, _ := http.NewRequest("GET", root+"api/info", nil)
		if resp, err := c.Do(req); err == nil {
			resp.Body.Close()
			if resp.StatusCode != http.StatusForbidden {
				bad = append(bad, "запрос без ключа доступа не отклонён")
			}
		}
		if len(bad) > 0 {
			return failf("%s", strings.Join(bad, "; "))
		}
		if len(slow) > 0 {
			return warnf("медленно: %s", strings.Join(slow, ", "))
		}
		return okf("%d адресов отвечают, доступ без ключа закрыт", len(eps))
	})

	browser := webui.FindBrowser()
	if browser == "" {
		t.check(g, "Окно программы", func() (string, string) { return skipf("Edge и Chrome не найдены") })
		return
	}
	var b *cdp.Browser
	var page *cdp.Conn
	var mu sync.Mutex
	var jsErrs []string
	t.check(g, "Запуск браузера для проверки", func() (string, string) {
		var err error
		b, err = cdp.Launch(browser, 1280, 860)
		if err != nil {
			return failf("%v", err)
		}
		page, err = b.NewPage()
		if err != nil {
			return failf("%v", err)
		}
		page.OnEvent(func(e cdp.Event) {
			var p struct {
				Type string `json:"type"`
				Args []struct {
					Value       any    `json:"value"`
					Description string `json:"description"`
				} `json:"args"`
				Details struct {
					Text      string `json:"text"`
					Exception struct {
						Description string `json:"description"`
					} `json:"exception"`
				} `json:"exceptionDetails"`
				Entry struct {
					Level string `json:"level"`
					Text  string `json:"text"`
					URL   string `json:"url"`
				} `json:"entry"`
			}
			json.Unmarshal(e.Params, &p)
			msg := ""
			switch e.Method {
			case "Runtime.exceptionThrown":
				msg = p.Details.Exception.Description
				if msg == "" {
					msg = p.Details.Text
				}
			case "Runtime.consoleAPICalled":
				if p.Type == "error" {
					var parts []string
					for _, a := range p.Args {
						if a.Value != nil {
							parts = append(parts, fmt.Sprint(a.Value))
						} else {
							parts = append(parts, a.Description)
						}
					}
					msg = strings.Join(parts, " ")
				}
			case "Log.entryAdded":
				if p.Entry.Level == "error" {
					msg = p.Entry.Text + " " + p.Entry.URL
				}
			}
			if msg != "" {
				mu.Lock()
				jsErrs = append(jsErrs, strings.SplitN(msg, "\n", 2)[0])
				mu.Unlock()
			}
		})
		for _, m := range []string{"Runtime.enable", "Log.enable", "Page.enable"} {
			if _, err := page.Call(m, nil, 10*time.Second); err != nil {
				return failf("%v", err)
			}
		}
		page.Call("Emulation.setDeviceMetricsOverride", map[string]any{"width": 1280, "height": 860, "deviceScaleFactor": 1, "mobile": false}, 10*time.Second)
		return okf("%s", filepath.Base(browser))
	})
	if page == nil {
		if b != nil {
			b.Close()
		}
		return
	}
	defer b.Close()
	defer page.Close()

	shot := func(name string) {
		if img, err := page.Screenshot(); err == nil {
			t.shots = append(t.shots, stShot{name, img})
		}
	}
	readyJS := func(p string) string {
		return fmt.Sprintf(`document.querySelector('#main') && document.querySelector('#main').dataset.ready === %q`, p)
	}
	t.check(g, "Окно программы открывается", func() (string, string) {
		st := time.Now()
		if _, err := page.Call("Page.navigate", map[string]any{"url": base}, 15*time.Second); err != nil {
			return failf("%v", err)
		}
		if err := page.WaitFor(readyJS("overview"), 30*time.Second); err != nil {
			return failf("главная страница не появилась: %v", err)
		}
		first := time.Since(st)
		if err := page.WaitFor(`window.__prostor && __prostor.S.scanAt > 0`, 120*time.Second); err != nil {
			return warnf("поиск мусора не закончился за 2 минуты")
		}
		page.Eval(`__prostor.render(false)`, nil, 5*time.Second)
		time.Sleep(300 * time.Millisecond)
		shot("Обзор")
		if first > 3*time.Second {
			return warnf("главная страница появилась через %s", secRu(first.Seconds()))
		}
		return okf("главная страница за %s, поиск мусора за %s", secRu(first.Seconds()), secRu(time.Since(st).Seconds()))
	})

	pages := []struct{ id, name string }{{"clean", "Очистка"}, {"advice", "Рекомендации"}, {"analyze", "Анализ диска"}, {"dupes", "Дубликаты"},
		{"programs", "Программы"}, {"startup", "Автозагрузка"}, {"tools", "Инструменты"}, {"history", "История"}, {"settings", "Настройки"}}
	var renders []string
	var slowest float64
	for _, p := range pages {
		p := p
		t.check(g, "Страница «"+p.name+"»", func() (string, string) {
			if err := page.Eval(fmt.Sprintf(`__prostor.go(%q)`, p.id), nil, 5*time.Second); err != nil {
				return failf("%v", err)
			}
			wait := 30 * time.Second
			if p.id == "advice" {
				wait = 4 * time.Minute
			}
			if p.id == "analyze" && t.sysAn != nil {
				page.WaitFor(`document.querySelector('#tm')`, 30*time.Second)
			}
			broken := `((document.querySelector('.empty h3')||{}).textContent==='Ошибка интерфейса')`
			if err := page.WaitFor(readyJS(p.id)+" || "+broken, wait); err != nil {
				shot(p.name + " (не загрузилась)")
				return failf("не загрузилась за %.0f с", wait.Seconds())
			}
			var ms float64
			page.Eval(`window.__lastRender || 0`, &ms, 5*time.Second)
			if ms > slowest {
				slowest = ms
			}
			renders = append(renders, fmt.Sprintf("%s %.0f мс", p.name, ms))
			time.Sleep(250 * time.Millisecond)
			shot(p.name)
			var err string
			page.Eval(`(document.querySelector('.empty h3')||{}).textContent==='Ошибка интерфейса' ? document.querySelector('.empty p').textContent : ''`, &err, 5*time.Second)
			if err != "" {
				return failf("ошибка интерфейса: %s", err)
			}
			if ms > 250 {
				return warnf("построение страницы %.0f мс", ms)
			}
			return okf("построена за %.0f мс", ms)
		})
	}

	geometry := func(label string) (string, string) {
		var geo struct {
			Tiles, L1, Out, Overlap int
			Coverage, Worst         float64
			Empty                   bool
		}
		js := `(() => { const t = window.__tm; if (!t) return {Empty:true};
			const L1 = t.rects.filter(r => r.level === 1); let out = 0, overlap = 0;
			for (const r of t.rects) if (r.x0 < 0 || r.y0 < 0 || r.x1 > t.w || r.y1 > t.h || r.x1 <= r.x0 || r.y1 <= r.y0) out++;
			for (let i = 0; i < L1.length; i++) for (let j = i + 1; j < L1.length; j++) { const a = L1[i], c = L1[j];
				if (a.x0 < c.x1 && c.x0 < a.x1 && a.y0 < c.y1 && c.y0 < a.y1) overlap++; }
			const area = L1.reduce((s, r) => s + (r.x1 - r.x0) * (r.y1 - r.y0), 0) / (t.w * t.h);
			let worst = 0; for (const r of L1) { const sh = r.size / t.total; if (sh > 0.08) {
				const ar = (r.x1 - r.x0) * (r.y1 - r.y0) / (t.w * t.h); worst = Math.max(worst, Math.abs(ar / sh - 1)); } }
			return {Tiles: t.rects.length, L1: L1.length, Out: out, Overlap: overlap, Coverage: area, Worst: worst, Empty: L1.length === 0}; })()`
		if err := page.Eval(js, &geo, 10*time.Second); err != nil {
			return failf("%v", err)
		}
		if geo.Empty {
			return failf("карта %s пустая", label)
		}
		var bad []string
		if geo.Out > 0 {
			bad = append(bad, fmt.Sprintf("%d блоков выходят за край", geo.Out))
		}
		if geo.Overlap > 0 {
			bad = append(bad, fmt.Sprintf("%d наложений блоков", geo.Overlap))
		}
		if geo.Coverage < 0.85 {
			bad = append(bad, fmt.Sprintf("заполнено только %.0f%% площади", geo.Coverage*100))
		}
		if geo.Worst > 0.15 {
			bad = append(bad, fmt.Sprintf("площадь крупного блока не соответствует размеру на %.0f%%", geo.Worst*100))
		}
		if len(bad) > 0 {
			return failf("карта %s: %s", label, strings.Join(bad, "; "))
		}
		return okf("%d блоков, без наложений, площади пропорциональны размерам", geo.Tiles)
	}
	if t.sysAn != nil {
		t.check(g, "Карта системного диска рисуется правильно", func() (string, string) {
			page.Eval(`__prostor.go('analyze')`, nil, 5*time.Second)
			if err := page.WaitFor(`window.__tm && window.__tm.rects.length > 0`, 30*time.Second); err != nil {
				return failf("карта не появилась: %v", err)
			}
			return geometry("системного диска")
		})
	}
	t.check(g, "Анализ папки из интерфейса", func() (string, string) {
		js := fmt.Sprintf(`startAnalysis(%q)`, t.anDir)
		page.Eval(`window.__tm = null`, nil, 5*time.Second)
		if err := page.Eval(js+`, true`, nil, 5*time.Second); err != nil {
			return failf("%v", err)
		}
		if err := page.WaitFor(`window.__tm && window.__tm.rects.length > 0 && __prostor.S.an && !__prostor.S.an.running`, 60*time.Second); err != nil {
			return failf("карта не появилась: %v", err)
		}
		time.Sleep(300 * time.Millisecond)
		shot("Анализ тестовой папки")
		var total float64
		page.Eval(`__prostor.S.an.summary.size`, &total, 5*time.Second)
		var want int64
		for _, s := range t.anFiles {
			want += s
		}
		s, d := geometry("тестовой папки")
		if s != stOK {
			return s, d
		}
		if int64(total) != want && int64(total) != want-t.anFiles["a/1.bin"] && int64(total) != want+t.anFiles["a/1.bin"] {
			return warnf("%s, итог %s вместо %s", d, human(int64(total)), human(want))
		}
		return s, d
	})
	t.check(g, "Галочки на странице очистки", func() (string, string) {
		page.Eval(`__prostor.go('clean')`, nil, 5*time.Second)
		if err := page.WaitFor(readyJS("clean"), 30*time.Second); err != nil {
			return failf("страница не загрузилась")
		}
		var r struct {
			Before, Mid, After string
			Ms                 float64
		}
		js := `(() => { const c = document.querySelector('.item .ck:not(:disabled)'); if (!c) return {Before:'нет'};
			const s = () => document.querySelector('#cleanBar .sum b').textContent; const before = s();
			const t0 = performance.now(); c.click(); const ms = performance.now() - t0; const mid = s(); c.click();
			return {Before: before, Mid: mid, After: s(), Ms: ms}; })()`
		if err := page.Eval(js, &r, 5*time.Second); err != nil {
			return failf("%v", err)
		}
		if r.Before == "нет" {
			return skipf("нет доступных пунктов")
		}
		if r.Before != r.After {
			return failf("итог не вернулся: %s → %s", r.Before, r.After)
		}
		if r.Ms > 50 {
			return warnf("отклик %.0f мс", r.Ms)
		}
		return okf("итог пересчитывается за %.1f мс", r.Ms)
	})
	t.check(g, "Скорость на слабом компьютере", func() (string, string) {
		if _, err := page.Call("Emulation.setCPUThrottlingRate", map[string]any{"rate": 4}, 5*time.Second); err != nil {
			return skipf("браузер не поддерживает замедление: %v", err)
		}
		defer page.Call("Emulation.setCPUThrottlingRate", map[string]any{"rate": 1}, 5*time.Second)
		worst, worstName := 0.0, ""
		for _, p := range pages {
			if p.id == "advice" {
				continue
			}
			page.Eval(fmt.Sprintf(`__prostor.go(%q)`, p.id), nil, 5*time.Second)
			if err := page.WaitFor(readyJS(p.id), 20*time.Second); err != nil {
				return failf("страница «%s» не открылась", p.name)
			}
			var ms float64
			page.Eval(`window.__lastRender || 0`, &ms, 5*time.Second)
			if ms > worst {
				worst, worstName = ms, p.name
			}
		}
		if worst > 200 {
			return warnf("при замедленном вчетверо процессоре страница «%s» строится %.0f мс", worstName, worst)
		}
		return okf("при замедленном вчетверо процессоре самая долгая страница («%s») строится %.0f мс", worstName, worst)
	})
	themeBg := func(theme string) string {
		page.Eval(`document.documentElement.dataset.theme = '`+theme+`'; __prostor.go('overview')`, nil, 5*time.Second)
		page.WaitFor(readyJS("overview"), 10*time.Second)
		time.Sleep(250 * time.Millisecond)
		var bg string
		page.Eval(`getComputedStyle(document.body).backgroundColor`, &bg, 5*time.Second)
		return bg
	}
	t.check(g, "Тёмная тема по умолчанию", func() (string, string) {
		if defaultSettings().Theme != "dark" {
			return failf("в настройках по умолчанию тема «%s»", defaultSettings().Theme)
		}
		var attr string
		page.Eval(`document.documentElement.getAttribute('data-theme') || ''`, &attr, 5*time.Second)
		bg := themeBg("dark")
		if bg != "rgb(22, 23, 27)" {
			return failf("фон тёмной темы %s", bg)
		}
		if attr == "" {
			return failf("у страницы не задана тема")
		}
		return okf("фон %s, сейчас выбрана тема «%s»", bg, attr)
	})
	t.check(g, "Светлая тема", func() (string, string) {
		bg := themeBg("light")
		shot("Светлая тема")
		page.Eval(`document.documentElement.dataset.theme = (__prostor.S.settings || {}).theme || 'dark'`, nil, 5*time.Second)
		if bg != "rgb(243, 244, 246)" {
			return failf("фон не сменился: %s", bg)
		}
		return okf("фон %s", bg)
	})
	t.check(g, "Узкое окно", func() (string, string) {
		page.Call("Emulation.setDeviceMetricsOverride", map[string]any{"width": 900, "height": 700, "deviceScaleFactor": 1, "mobile": false}, 10*time.Second)
		page.Eval(`__prostor.go('clean')`, nil, 5*time.Second)
		page.WaitFor(readyJS("clean"), 10*time.Second)
		time.Sleep(200 * time.Millisecond)
		var over float64
		page.Eval(`document.querySelector('#main').scrollWidth - document.querySelector('#main').clientWidth`, &over, 5*time.Second)
		shot("Узкое окно")
		page.Call("Emulation.setDeviceMetricsOverride", map[string]any{"width": 1280, "height": 860, "deviceScaleFactor": 1, "mobile": false}, 10*time.Second)
		if over > 2 {
			return failf("страница шире окна на %.0f px", over)
		}
		return okf("без горизонтальной прокрутки при ширине 900 px")
	})
	t.check(g, "Ошибки JavaScript", func() (string, string) {
		mu.Lock()
		defer mu.Unlock()
		var real []string
		for _, e := range jsErrs {
			if strings.Contains(e, "favicon") {
				continue
			}
			real = append(real, e)
		}
		if len(real) > 0 {
			if len(real) > 5 {
				real = real[:5]
			}
			return failf("%s", strings.Join(real, "; "))
		}
		return okf("нет, самая долгая страница строилась %.0f мс", slowest)
	})
	log.Printf("selftest render times: %s", strings.Join(renders, ", "))
}

// ---------------------------------------------------------------- отчёт

func (t *selfTest) writeReport() (string, error) {
	dir := filepath.Join(appDataDir(), "selftest")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	// храним последние 5 отчётов
	if old, _ := filepath.Glob(filepath.Join(dir, "report-*.html")); len(old) > 4 {
		sort.Strings(old)
		for _, p := range old[:len(old)-4] {
			os.Remove(p)
			os.Remove(strings.TrimSuffix(p, ".html") + ".json")
		}
	}
	stamp := t.start.Format("2006-01-02_15-04-05")
	path := filepath.Join(dir, "report-"+stamp+".html")
	var fails, warns, oks, skips int
	for _, c := range t.checks {
		switch c.Status {
		case stFail:
			fails++
		case stWarn:
			warns++
		case stOK:
			oks++
		default:
			skips++
		}
	}
	e := html.EscapeString
	var b bytes.Buffer
	verdict := "Ошибок не найдено"
	if fails > 0 {
		verdict = fmt.Sprintf("Найдено ошибок: %d", fails)
	}
	fmt.Fprintf(&b, `<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Проверка Prostor %s</title><style>
:root{--ink:#eceef1;--ink2:#a6aab3;--ink3:#737882;--line:#2a2d33;--klein:#7b93ff;--red:#ff8a80;--amber:#e6b450;--bg:#18191d;color-scheme:dark}
body{margin:0;font:14px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;color:var(--ink);background:var(--bg)}
.wrap{max-width:1040px;margin:0 auto;padding:40px 32px 80px}
h1{font:600 26px/1.2 "Segoe UI Variable Display","Segoe UI",sans-serif;margin:0}
.mark{width:24px;height:24px;vertical-align:-3px;margin-right:10px;fill:none;stroke-width:4.2}.mark .u{stroke:var(--ink)}.mark .f{stroke:var(--klein)}
.verdict{font:300 44px/1.1 Bahnschrift,"Segoe UI",sans-serif;margin:28px 0 6px}.verdict.bad{color:var(--red)}
.sub{color:var(--ink2)}.counts{display:flex;gap:28px;margin:18px 0 34px;color:var(--ink2)}.counts b{font:500 20px Bahnschrift,"Segoe UI",sans-serif;color:var(--ink);margin-right:6px}
h2{font:600 15px/1.3 "Segoe UI",sans-serif;margin:34px 0 8px}
table{width:100%%;border-collapse:collapse}td,th{padding:8px 12px 8px 0;border-bottom:1px solid var(--line);vertical-align:top;text-align:left}
th{font-size:12px;font-weight:500;color:var(--ink3)}td.s{width:96px;white-space:nowrap;font-weight:600}td.t{width:64px;color:var(--ink3);text-align:right;white-space:nowrap}
.ok{color:var(--ink)}.warn{color:var(--amber)}.fail{color:var(--red)}.skip{color:var(--ink3)}
td.n{width:300px}.d{color:var(--ink2)}
.env td:first-child{width:220px;color:var(--ink2)}
.shots{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:18px}
.shots figure{margin:0}.shots img{width:100%%;border:1px solid var(--line);display:block}.shots figcaption{font-size:12.5px;color:var(--ink2);margin-top:6px}
@media print{:root{--ink:#16171b;--ink2:#5d616b;--ink3:#8f939c;--line:#e7e8ec;--klein:#002fa7;--red:#b3261e;--amber:#8a5a00;--bg:#fff;color-scheme:light}}
</style></head><body><div class="wrap">
<h1><svg class="mark" viewBox="0 0 24 24" aria-hidden="true"><path class="u" d="M12.78 3.13A8.9 8.9 0 1 1 3.13 12.78"/><path class="f" d="M3.13 11.22A8.9 8.9 0 0 1 11.22 3.13"/></svg>Проверка Prostor %s</h1>
<p class="sub">%s, длилась %s</p>
<div class="verdict %s">%s</div>
<div class="counts"><span><b>%d</b>пройдено</span><span><b>%d</b>с предупреждениями</span><span><b>%d</b>с ошибками</span><span><b>%d</b>пропущено</span></div>`,
		e(version), e(version), t.start.Format("02.01.2006 15:04"), durRu(time.Since(t.start)),
		map[bool]string{true: "bad", false: ""}[fails > 0], verdict, oks, warns, fails, skips)

	group := ""
	b.WriteString(`<table>`)
	names := map[string]string{stOK: "пройдено", stWarn: "внимание", stFail: "ошибка", stSkip: "пропущено"}
	for _, c := range t.checks {
		if c.Group != group {
			group = c.Group
			fmt.Fprintf(&b, `<tr><th colspan="4"><h2>%s</h2></th></tr>`, e(group))
		}
		secs := ""
		if c.Secs >= 0.1 {
			secs = secRu(c.Secs)
		}
		fmt.Fprintf(&b, `<tr><td class="s %s">%s</td><td class="n">%s</td><td class="d">%s</td><td class="t">%s</td></tr>`,
			c.Status, names[c.Status], e(c.Name), e(c.Detail), secs)
	}
	b.WriteString(`</table><h2>Компьютер</h2><table class="env">`)
	for _, kv := range t.env {
		fmt.Fprintf(&b, `<tr><td>%s</td><td>%s</td></tr>`, e(kv[0]), e(kv[1]))
	}
	b.WriteString(`</table>`)
	if len(t.shots) > 0 {
		b.WriteString(`<h2>Как выглядит интерфейс на этом компьютере</h2><div class="shots">`)
		for _, s := range t.shots {
			fmt.Fprintf(&b, `<figure><img alt="%s" src="data:image/jpeg;base64,%s"><figcaption>%s</figcaption></figure>`,
				e(s.Name), base64.StdEncoding.EncodeToString(s.JPEG), e(s.Name))
		}
		b.WriteString(`</div>`)
	}
	if tail := logTail(40); tail != "" {
		fmt.Fprintf(&b, `<h2>Последние записи журнала</h2><pre style="white-space:pre-wrap;font:12px Consolas,monospace;color:var(--ink2)">%s</pre>`, e(tail))
	}
	b.WriteString(`</div></body></html>`)
	if err := os.WriteFile(path, b.Bytes(), 0o644); err != nil {
		return "", err
	}
	js, _ := json.MarshalIndent(map[string]any{"version": version, "started": t.start, "checks": t.checks, "env": t.env}, "", "  ")
	os.WriteFile(strings.TrimSuffix(path, ".html")+".json", js, 0o644)
	return path, nil
}

func logTail(n int) string {
	data, err := os.ReadFile(filepath.Join(appDataDir(), "prostor.log"))
	if err != nil {
		return ""
	}
	lines := strings.Split(strings.TrimRight(string(data), "\n"), "\n")
	if len(lines) > n {
		lines = lines[len(lines)-n:]
	}
	return strings.Join(lines, "\n")
}
