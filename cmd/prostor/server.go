package main

import (
	"embed"
	"errors"
	"io/fs"
	"net/http"
	"os"
	"os/exec"
	"os/user"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"

	"prostor/internal/winapi"
)

//go:embed ui
var uiFiles embed.FS

func ownPIDs() map[uint32]bool {
	if helperMode.on {
		return helperOwnPIDs()
	}
	if ui == nil {
		return map[uint32]bool{}
	}
	return ui.OwnPIDs()
}

func routes() http.Handler {
	mux := http.NewServeMux()
	helperRoutes(mux)
	sub, _ := fs.Sub(uiFiles, "ui")
	mux.Handle("/", http.FileServer(http.FS(sub)))

	// Method-qualified patterns (Go 1.22+ mux) so one path can have both GET and POST.
	get := func(path string, fn func(w http.ResponseWriter, r *http.Request)) {
		mux.HandleFunc("GET "+path, fn)
	}
	post := func(path string, fn func(w http.ResponseWriter, r *http.Request)) {
		mux.HandleFunc("POST "+path, fn)
	}
	ok := func(w http.ResponseWriter) { writeJSON(w, map[string]bool{"ok": true}) }

	// ------------------------------------------------------------ general
	get("/api/info", func(w http.ResponseWriter, r *http.Request) {
		total, avail := winapi.Memory()
		h := getHistory()
		var freed int64
		var last *time.Time
		cleans := 0
		for i := range h {
			freed += h[i].Freed
			if h[i].Mode != "delete" {
				cleans++
				last = &h[i].Time
			}
		}
		uname := os.Getenv("USERNAME")
		if uname == "" {
			if u, err := user.Current(); err == nil {
				uname = u.Username
			}
		}
		host, _ := os.Hostname()
		qsize, _ := quarantineSize()
		writeJSON(w, map[string]any{
			"version": version, "admin": isAdmin, "os": winapi.OSVersion(), "computer": host, "user": uname,
			"uptime": winapi.Uptime().Seconds(), "ramTotal": total, "ramAvail": avail, "drives": winapi.Drives(),
			"totalFreed": freed, "cleans": cleans, "lastClean": last,
			"settings": getSettings(), "isWindows": winapi.IsWindows, "helper": helperMode.on,
			"systemDrive": os.Getenv("SystemDrive"), "dataDir": appDataDir(), "quarantine": qsize,
		})
	})
	// Rights on demand: start the elevated helper; the page reloads and from
	// then on talks to it through this process.
	post("/api/elevate", func(w http.ResponseWriter, r *http.Request) {
		if isAdmin || ui == nil {
			ok(w)
			return
		}
		if err := helper.start(ui.URL, 2*time.Minute); err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})
	post("/api/selftest", func(w http.ResponseWriter, r *http.Request) {
		cmd := exec.Command(exePath, "--selftest", "--no-console")
		if err := cmd.Start(); err != nil {
			writeErr(w, errors.New("не удалось запустить проверку: "+err.Error()))
			return
		}
		go cmd.Wait()
		ok(w)
	})
	post("/api/quit", func(w http.ResponseWriter, r *http.Request) {
		ok(w)
		go func() { time.Sleep(200 * time.Millisecond); ui.Quit() }()
	})

	// ------------------------------------------------------------ jobs
	get("/api/job", func(w http.ResponseWriter, r *http.Request) {
		j := getJob(r.URL.Query().Get("kind"))
		if j == nil {
			writeJSON(w, jobSnapshot{Kind: r.URL.Query().Get("kind")})
			return
		}
		from, _ := strconv.Atoi(r.URL.Query().Get("log"))
		writeJSON(w, j.Snapshot(from))
	})
	post("/api/job/cancel", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Kind string }
		readJSON(r, &req)
		if j := getJob(req.Kind); j != nil {
			j.Cancel()
		}
		ok(w)
	})

	// ------------------------------------------------------------ cleaner
	get("/api/clean/targets", func(w http.ResponseWriter, r *http.Request) {
		s := getSettings()
		type tv struct {
			*Target
			Checked bool `json:"checked"`
		}
		var list []tv
		imported := 0
		for _, t := range offered(newDirCache()) {
			c := t.Default
			if v, ok := s.Selected[t.ID]; ok {
				c = v
			}
			if t.Source != "" {
				imported++
			}
			list = append(list, tv{t, c})
		}
		writeJSON(w, map[string]any{"categories": categories, "targets": list, "admin": isAdmin,
			"rules": map[string]any{"builtin": rulesInfo.Builtin, "imported": rulesInfo.Imported, "installed": imported,
				"w2Version": rulesInfo.W2Version, "w2Notice": rulesInfo.W2Notice}})
	})
	// files a program kept open during the last cleaning: delete at restart
	post("/api/clean/reboot", func(w http.ResponseWriter, r *http.Request) {
		if !isAdmin {
			writeErr(w, errors.New("нужны права администратора"))
			return
		}
		n, failed := 0, 0
		jr := openJournal("reboot")
		for _, p := range lastLocked() {
			if winapi.DeleteOnReboot(p) == nil {
				n++
				jr.add("s", p, 0, "reboot")
			} else {
				failed++
			}
		}
		jr.close()
		writeJSON(w, map[string]int{"scheduled": n, "failed": failed})
	})
	post("/api/clean/select", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Selected map[string]bool }
		if err := readJSON(r, &req); err != nil {
			writeErr(w, err)
			return
		}
		s := getSettings()
		for k, v := range req.Selected {
			s.Selected[k] = v
		}
		saveSettings(s)
		ok(w)
	})
	post("/api/clean/scan", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ IDs []string }
		readJSON(r, &req)
		s := getSettings()
		own := ownPIDs()
		_, err := startJob("scan", func(j *Job) (any, error) {
			j.SetPhase("Поиск ненужных файлов")
			return scanTargets(j, req.IDs, s, own), nil
		})
		if err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})
	post("/api/clean/run", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ IDs []string }
		readJSON(r, &req)
		s := getSettings()
		_, err := startJob("clean", func(j *Job) (any, error) {
			return cleanTargets(j, req.IDs, s, isAdmin, "manual"), nil
		})
		if err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})
	post("/api/procs/close", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Names []string }
		readJSON(r, &req)
		closeProcesses(req.Names, ownPIDs())
		ok(w)
	})

	// ------------------------------------------------------------ analyzer
	post("/api/analyze/start", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Path string }
		readJSON(r, &req)
		if err := startAnalysis(req.Path); err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})
	get("/api/analyze/view", func(w http.ResponseWriter, r *http.Request) {
		a := currentAnalysis()
		if a == nil {
			writeErr(w, errors.New("нет результатов анализа"))
			return
		}
		p := r.URL.Query().Get("path")
		if p == "" {
			p = a.Root
		}
		v, err := a.view(p)
		if err != nil {
			writeErr(w, err)
			return
		}
		writeJSON(w, v)
	})
	get("/api/analyze/top", func(w http.ResponseWriter, r *http.Request) {
		a := currentAnalysis()
		if a == nil {
			writeErr(w, errors.New("нет результатов анализа"))
			return
		}
		if r.URL.Query().Get("old") == "1" {
			writeJSON(w, a.oldFiles())
			return
		}
		writeJSON(w, a.topFiles())
	})
	// what grew since an earlier snapshot of the same place
	get("/api/analyze/growth", func(w http.ResponseWriter, r *http.Request) {
		a := currentAnalysis()
		if a == nil {
			writeErr(w, errors.New("нет результатов анализа"))
			return
		}
		all := listSnapshots(a.Root)
		var list []snapInfo
		for _, s := range all {
			if s.ID != a.snapID {
				list = append(list, s)
			}
		}
		id := r.URL.Query().Get("id")
		if id == "" {
			id = defaultSnapshot(all, a.snapID, a.method)
		}
		if id == "" {
			writeJSON(w, map[string]any{"none": true, "snapshots": []snapInfo{}})
			return
		}
		old, err := loadSnapshot(a.Root, id)
		if err != nil {
			writeErr(w, err)
			return
		}
		res := a.growth(old)
		res.Snapshots = list
		for _, s := range all {
			if s.ID == id {
				res.Snapshot = s
			}
		}
		writeJSON(w, res)
	})
	post("/api/analyze/export", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ What, Format string }
		readJSON(r, &req)
		a := currentAnalysis()
		if a == nil {
			writeErr(w, errors.New("нет результатов анализа"))
			return
		}
		p, err := a.export(req.What, req.Format)
		if err != nil {
			writeErr(w, err)
			return
		}
		writeJSON(w, map[string]string{"path": p})
	})
	post("/api/fs/compress", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Path string }
		readJSON(r, &req)
		if err := compressAllowed(req.Path); err != nil {
			writeErr(w, err)
			return
		}
		if _, err := startJob("compress", func(j *Job) (any, error) { return compressFolder(j, req.Path) }); err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})
	get("/api/analyze/types", func(w http.ResponseWriter, r *http.Request) {
		a := currentAnalysis()
		if a == nil {
			writeErr(w, errors.New("нет результатов анализа"))
			return
		}
		writeJSON(w, a.types())
	})
	get("/api/folders", func(w http.ResponseWriter, r *http.Request) {
		type qf struct {
			Name string `json:"name"`
			Path string `json:"path"`
			Kind string `json:"kind"`
		}
		var out []qf
		for _, d := range winapi.Drives() {
			name := "Диск " + strings.TrimRight(d.Root, `\`)
			if d.Label != "" {
				name += " · " + d.Label
			}
			out = append(out, qf{name, d.Root, "drive"})
		}
		for _, k := range []struct{ id, name string }{{"downloads", "Загрузки"}, {"desktop", "Рабочий стол"}, {"documents", "Документы"}, {"videos", "Видео"}, {"pictures", "Изображения"}} {
			if p := winapi.KnownFolder(k.id); p != "" {
				out = append(out, qf{k.name, p, "folder"})
			}
		}
		if up := os.Getenv("USERPROFILE"); up != "" {
			out = append(out, qf{"AppData", filepath.Join(up, "AppData"), "folder"})
		}
		writeJSON(w, out)
	})

	// ------------------------------------------------------------ files
	// Files picked by the user (duplicates, large files, folders on the map)
	// go to quarantine for settings.QuarantineDays days, or to the Recycle Bin
	// when quarantine is off or impossible on that drive.
	post("/api/fs/remove", func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			Paths  []string
			Source string
			Mode   string // "" = by settings | recycle
		}
		readJSON(r, &req)
		if req.Source == "" {
			req.Source = "Удаление вручную"
		}
		type info struct {
			size  int64
			isDir bool
		}
		infos := map[string]info{}
		var paths []string
		for _, p := range req.Paths {
			p = filepath.Clean(p)
			if !safeRoot(p) {
				continue
			}
			st, err := os.Lstat(p)
			if err != nil {
				continue
			}
			sz := st.Size()
			if st.IsDir() {
				sz, _ = dirSize(p, nil)
			}
			infos[p] = info{sz, st.IsDir()}
			paths = append(paths, p)
		}
		if len(paths) == 0 {
			writeErr(w, errors.New("нечего удалять"))
			return
		}
		days := getSettings().QuarantineDays
		if req.Mode == "recycle" {
			days = 0
		}
		_, err := startJob("delete", func(j *Job) (any, error) {
			jr := openJournal("delete")
			var held, freed int64
			var toBin []string
			quarantined := map[string]bool{}
			if days > 0 {
				j.SetPhase("Перемещение в карантин")
				q := quarantinePaths(paths, req.Source, days, jr)
				held = q.Size
				for _, p := range q.Moved {
					quarantined[p] = true
				}
				for p, why := range q.Failed {
					if why == "volume" || why == "quarantine" {
						toBin = append(toBin, p)
					}
				}
			} else {
				toBin = paths
			}
			var binErr error
			if len(toBin) > 0 {
				j.SetPhase("Перемещение в Корзину")
				binErr = winapi.MoveToRecycleBin(toBin)
			}
			var done []string
			for _, p := range paths {
				if _, e := os.Lstat(p); !os.IsNotExist(e) {
					if !quarantined[p] {
						jr.add("f", p, infos[p].size, req.Source)
					}
					continue
				}
				done = append(done, p)
				if !quarantined[p] {
					freed += infos[p].size
					jr.add("r", p, infos[p].size, req.Source)
				}
				if a := currentAnalysis(); a != nil {
					a.forget(p, infos[p].size, infos[p].isDir)
				}
			}
			id := jr.close()
			if len(done) > 0 {
				addHistory(HistoryEntry{Time: time.Now(), Mode: "delete", Freed: freed, Held: held, Files: int64(len(done)),
					Failed: int64(len(paths) - len(done)), Journal: id,
					Items: []HistoryItem{{Name: req.Source, Freed: freed + held, Files: int64(len(done))}}})
			}
			res := map[string]any{"freed": freed, "held": held, "days": days, "removed": done, "requested": len(paths),
				"quarantined": len(quarantined), "journal": id}
			if binErr != nil && len(done) == 0 {
				return res, errors.New("не удалось удалить: " + binErr.Error())
			}
			return res, nil
		})
		if err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})

	// ------------------------------------------------------------ quarantine
	get("/api/quarantine", func(w http.ResponseWriter, r *http.Request) {
		type batchView struct {
			*QBatch
			Count int  `json:"count"`
			More  bool `json:"more"`
		}
		var out []batchView
		var total int64
		for _, qb := range listQuarantine() {
			v := batchView{QBatch: qb, Count: len(qb.Items)}
			total += qb.Size
			if len(qb.Items) > 300 {
				cp := *qb
				cp.Items = qb.Items[:300]
				v.QBatch, v.More = &cp, true
			}
			out = append(out, v)
		}
		if out == nil {
			out = []batchView{}
		}
		writeJSON(w, map[string]any{"batches": out, "size": total, "days": getSettings().QuarantineDays})
	})
	post("/api/quarantine/restore", func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			ID    string
			Paths []string
		}
		readJSON(r, &req)
		res, err := restoreQuarantine(req.ID, req.Paths)
		if err != nil {
			writeErr(w, err)
			return
		}
		writeJSON(w, res)
	})
	post("/api/quarantine/purge", func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			ID  string
			All bool
		}
		readJSON(r, &req)
		if req.ID == "" && !req.All {
			writeErr(w, errors.New("не указано, что удалить"))
			return
		}
		freed, n := purgeQuarantine(req.ID, req.All)
		writeJSON(w, map[string]any{"freed": freed, "batches": n})
	})
	get("/api/journal/{id}", func(w http.ResponseWriter, r *http.Request) {
		q := r.URL.Query()
		off, _ := strconv.Atoi(q.Get("offset"))
		lim, _ := strconv.Atoi(q.Get("limit"))
		pg, err := readJournal(r.PathValue("id"), q.Get("q"), off, lim)
		if err != nil {
			writeErr(w, err)
			return
		}
		writeJSON(w, pg)
	})
	post("/api/fs/reveal", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Path string }
		readJSON(r, &req)
		winapi.Reveal(req.Path)
		ok(w)
	})
	post("/api/fs/open", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Path string }
		readJSON(r, &req)
		if req.Path == "" {
			writeErr(w, errors.New("пустой путь"))
			return
		}
		if err := winapi.ShellOpen(req.Path); err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})
	post("/api/fs/pick", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Title string }
		readJSON(r, &req)
		if req.Title == "" {
			req.Title = "Выберите папку"
		}
		writeJSON(w, map[string]string{"path": winapi.PickFolder(req.Title)})
	})

	// ------------------------------------------------------------ duplicates & advisor
	post("/api/dupes/photos", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Path string }
		readJSON(r, &req)
		_, err := startJob("photos", func(j *Job) (any, error) { return findSimilarPhotos(j, filepath.Clean(req.Path)) })
		if err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})
	get("/api/fs/thumb", func(w http.ResponseWriter, r *http.Request) {
		b, err := photoThumb(r.URL.Query().Get("path"))
		if err != nil {
			http.Error(w, err.Error(), http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "image/jpeg")
		w.Header().Set("Cache-Control", "private, max-age=600")
		w.Write(b)
	})
	post("/api/dupes/start", func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			Path  string
			MinMB float64
		}
		readJSON(r, &req)
		if req.MinMB <= 0 {
			req.MinMB = 1
		}
		_, err := startJob("dupes", func(j *Job) (any, error) {
			return findDuplicates(j, filepath.Clean(req.Path), int64(req.MinMB*1024*1024))
		})
		if err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})
	post("/api/advisor/start", func(w http.ResponseWriter, r *http.Request) {
		own := ownPIDs()
		_, err := startJob("advisor", func(j *Job) (any, error) { return runAdvisor(j, isAdmin, own) })
		if err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})

	// ------------------------------------------------------------ programs & startup
	get("/api/programs", func(w http.ResponseWriter, r *http.Request) { writeJSON(w, listPrograms()) })
	post("/api/programs/measure", func(w http.ResponseWriter, r *http.Request) {
		if _, err := startJob("measure", measurePrograms); err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})
	post("/api/programs/uninstall", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Key string }
		readJSON(r, &req)
		if err := uninstallProgram(req.Key); err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})
	get("/api/startup", func(w http.ResponseWriter, r *http.Request) { writeJSON(w, listStartup()) })
	post("/api/startup/toggle", func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			ID      string
			Enabled bool
		}
		readJSON(r, &req)
		if err := setStartup(req.ID, req.Enabled); err != nil {
			writeErr(w, errors.New("не удалось изменить: "+err.Error()))
			return
		}
		ok(w)
	})

	// ------------------------------------------------------------ tools
	get("/api/tools", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, map[string]any{"tools": tools, "hiber": hibernationInfo(), "admin": isAdmin})
	})
	post("/api/tools/run", func(w http.ResponseWriter, r *http.Request) {
		var req struct{ ID string }
		readJSON(r, &req)
		t := toolByID(req.ID)
		if t == nil {
			writeErr(w, errors.New("неизвестный инструмент"))
			return
		}
		if t.Admin && !isAdmin {
			writeErr(w, errors.New("нужны права администратора"))
			return
		}
		if t.Open {
			if err := openTool(req.ID); err != nil {
				writeErr(w, err)
				return
			}
			writeJSON(w, map[string]bool{"ok": true, "opened": true})
			return
		}
		if _, err := startJob("tool", func(j *Job) (any, error) { return runTool(j, req.ID) }); err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})

	// ------------------------------------------------------------ history & settings
	get("/api/history", func(w http.ResponseWriter, r *http.Request) { writeJSON(w, getHistory()) })
	post("/api/history/clear", func(w http.ResponseWriter, r *http.Request) { clearHistory(); ok(w) })
	get("/api/settings", func(w http.ResponseWriter, r *http.Request) { writeJSON(w, getSettings()) })
	post("/api/settings", func(w http.ResponseWriter, r *http.Request) {
		var s Settings
		if err := readJSON(r, &s); err != nil {
			writeErr(w, err)
			return
		}
		old := getSettings()
		if s.Selected == nil {
			s.Selected = old.Selected
		}
		if s.SkipRecentHours < 0 || s.SkipRecentHours > 24*30 {
			s.SkipRecentHours = 24
		}
		if s.Schedule != old.Schedule {
			if err := applySchedule(s.Schedule); err != nil {
				writeErr(w, err)
				return
			}
		}
		if err := saveSettings(s); err != nil {
			writeErr(w, err)
			return
		}
		ok(w)
	})
	return mux
}

var timeRe = regexp.MustCompile(`^([01]\d|2[0-3]):[0-5]\d$`)

const taskName = `Prostor\AutoClean`

func applySchedule(sc Schedule) error {
	if sc.Mode == "off" || sc.Mode == "" {
		if winapi.IsWindows {
			runQuiet("schtasks.exe", "/Delete", "/F", "/TN", taskName)
		}
		return nil
	}
	if !isAdmin {
		return errors.New("для автоочистки по расписанию запустите программу от имени администратора")
	}
	if !timeRe.MatchString(sc.Time) {
		return errors.New("время должно быть в формате ЧЧ:ММ")
	}
	args := []string{"/Create", "/F", "/TN", taskName, "/TR", `"` + exePath + `" --autoclean`, "/RL", "HIGHEST", "/ST", sc.Time}
	switch sc.Mode {
	case "daily":
		args = append(args, "/SC", "DAILY")
	case "weekly":
		days := map[string]bool{"MON": true, "TUE": true, "WED": true, "THU": true, "FRI": true, "SAT": true, "SUN": true}
		if !days[sc.Day] {
			return errors.New("неверный день недели")
		}
		args = append(args, "/SC", "WEEKLY", "/D", sc.Day)
	default:
		return errors.New("неизвестный режим расписания")
	}
	if !winapi.IsWindows {
		return nil
	}
	out, err := runQuiet("schtasks.exe", args...)
	if err != nil {
		return errors.New("не удалось создать задание: " + strings.TrimSpace(out))
	}
	return nil
}
