package main

import (
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"prostor/internal/winapi"
)

type RecItem struct {
	Label string `json:"label"`
	Path  string `json:"path,omitempty"`
	Size  int64  `json:"size"`
	Note  string `json:"note,omitempty"`
}

type RecAction struct {
	Label   string `json:"label"`
	Kind    string `json:"kind"` // clean | tool | open | analyze | nav | reveal
	Arg     string `json:"arg"`
	Primary bool   `json:"primary,omitempty"`
}

type Rec struct {
	ID      string      `json:"id"`
	Level   string      `json:"level"` // critical | high | medium | low | info
	Icon    string      `json:"icon"`
	Title   string      `json:"title"`
	Desc    string      `json:"desc"`
	Size    int64       `json:"size"`
	Items   []RecItem   `json:"items,omitempty"`
	Actions []RecAction `json:"actions,omitempty"`
}

type advice struct {
	Recs      []Rec   `json:"recs"`
	Potential int64   `json:"potential"`
	Score     int     `json:"score"`
	Took      float64 `json:"took"`
}

func human(b int64) string {
	f := float64(b)
	units := []string{"Б", "КБ", "МБ", "ГБ", "ТБ"}
	i := 0
	for f >= 1024 && i < len(units)-1 {
		f /= 1024
		i++
	}
	if i == 0 {
		return fmt.Sprintf("%d %s", b, units[0])
	}
	if f >= 100 {
		return fmt.Sprintf("%.0f %s", f, units[i])
	}
	return strings.Replace(fmt.Sprintf("%.1f %s", f, units[i]), ".", ",", 1)
}

const (
	gb = int64(1) << 30
	mb = int64(1) << 20
)

func levelBySize(s int64) string {
	switch {
	case s >= 10*gb:
		return "high"
	case s >= 2*gb:
		return "medium"
	case s >= 300*mb:
		return "low"
	}
	return "info"
}

// childSizes returns the sizes of each direct subfolder of dir.
func childSizes(j *Job, dir string) []RecItem {
	ents, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var out []RecItem
	var mu sync.Mutex
	var wg sync.WaitGroup
	sem := make(chan struct{}, 3)
	for _, e := range ents {
		if !e.IsDir() {
			continue
		}
		fi, err := e.Info()
		if err != nil || isLink(fi) {
			continue
		}
		p := filepath.Join(dir, e.Name())
		wg.Add(1)
		sem <- struct{}{}
		go func(name, p string) {
			defer func() { <-sem; wg.Done() }()
			defer winapi.BackgroundThread()()
			s, _ := dirSize(p, j.Cancelled)
			mu.Lock()
			out = append(out, RecItem{Label: name, Path: p, Size: s})
			mu.Unlock()
		}(e.Name(), p)
	}
	wg.Wait()
	sort.Slice(out, func(a, b int) bool { return out[a].Size > out[b].Size })
	return out
}

func runAdvisor(j *Job, admin bool, own map[uint32]bool) (any, error) {
	start := time.Now()
	s := getSettings()
	var recs []Rec
	add := func(r Rec) { recs = append(recs, r) }
	steps := int64(8)
	j.Total.Store(steps)
	step := func(name string) {
		j.SetPhase(name)
		j.Done.Add(1)
	}

	// 1. drives -----------------------------------------------------------
	step("Проверка дисков")
	sysDrive := strings.ToUpper(os.Getenv("SystemDrive"))
	if sysDrive == "" {
		sysDrive = "C:"
	}
	drives := winapi.Drives()
	var sysFreePct float64 = 100
	var bestOther *winapi.Drive
	for i, d := range drives {
		if d.Type != "fixed" || d.Total == 0 {
			continue
		}
		pct := float64(d.Free) / float64(d.Total) * 100
		isSys := strings.HasPrefix(strings.ToUpper(d.Root), sysDrive)
		if isSys {
			sysFreePct = pct
		} else if bestOther == nil || d.Free > bestOther.Free {
			bestOther = &drives[i]
		}
		name := "Диск " + strings.TrimRight(d.Root, `\`)
		if d.Label != "" {
			name += " (" + d.Label + ")"
		}
		switch {
		case pct < 7 || d.Free < 5<<30:
			add(Rec{ID: "drive_" + d.Root, Level: "critical", Icon: "disk",
				Title:   name + ": почти нет места",
				Desc:    fmt.Sprintf("Свободно %s из %s (%.0f%%). При нехватке места Windows тормозит, не ставит обновления, программы могут падать. Начните с очистки и анализа диска.", human(int64(d.Free)), human(int64(d.Total)), pct),
				Actions: []RecAction{{Label: "Анализ диска", Kind: "analyze", Arg: d.Root, Primary: true}}})
		case pct < 15:
			add(Rec{ID: "drive_" + d.Root, Level: "high", Icon: "disk",
				Title:   name + ": мало свободного места",
				Desc:    fmt.Sprintf("Свободно %s из %s (%.0f%%). Рекомендуется держать свободными хотя бы 15%% диска.", human(int64(d.Free)), human(int64(d.Total)), pct),
				Actions: []RecAction{{Label: "Анализ диска", Kind: "analyze", Arg: d.Root, Primary: true}}})
		}
	}

	// 2. junk -------------------------------------------------------------
	step("Поиск мусора")
	ids := selectedIDs(s, admin, true)
	items := cachedScan(ids, 3*time.Minute)
	if items == nil {
		items = scanTargets(&Job{cancel: j.cancel}, ids, s, own)
	}
	var junk int64
	var junkItems []RecItem
	var cleanIDs []string
	for _, it := range items {
		if it.Size <= 0 || it.ID == "recycle" {
			continue
		}
		t := targetByID(it.ID)
		junk += it.Size
		cleanIDs = append(cleanIDs, it.ID)
		ri := RecItem{Label: t.Name, Size: it.Size}
		if len(it.Running) > 0 {
			ri.Note = "программа запущена — часть файлов будет пропущена"
		}
		junkItems = append(junkItems, ri)
	}
	sort.Slice(junkItems, func(a, b int) bool { return junkItems[a].Size > junkItems[b].Size })
	if len(junkItems) > 6 {
		junkItems = junkItems[:6]
	}
	if junk > 50*mb {
		add(Rec{ID: "junk", Level: levelBySize(junk), Icon: "broom", Size: junk,
			Title: "Временные файлы и кэш: " + human(junk),
			Desc:  "Безопасно удаляемые файлы: временные папки, кэш браузеров и программ, отчёты об ошибках. Всё это создаётся заново по мере необходимости.",
			Items: junkItems,
			Actions: []RecAction{{Label: "Очистить", Kind: "clean", Arg: strings.Join(cleanIDs, ","), Primary: true},
				{Label: "Выбрать вручную", Kind: "nav", Arg: "clean"}}})
	}
	for _, it := range items {
		if it.ID == "recycle" && it.Size > 200*mb {
			add(Rec{ID: "recycle", Level: levelBySize(it.Size), Icon: "trash", Size: it.Size,
				Title: "Корзина занимает " + human(it.Size),
				Desc:  fmt.Sprintf("В корзине %d объектов. Перед очисткой убедитесь, что там нет нужного.", it.Files),
				Actions: []RecAction{{Label: "Очистить корзину", Kind: "clean", Arg: "recycle", Primary: true},
					{Label: "Открыть корзину", Kind: "open", Arg: "shell:RecycleBinFolder"}}})
		}
	}

	// 3. caution targets that are often huge ------------------------------
	step("Проверка остатков обновлений")
	for _, id := range []string{"windows_old", "upgrade_leftovers", "nvidia_cache", "amd_cache", "steam_shaders", "adobe_media", "gradle"} {
		t := targetByID(id)
		if t == nil || j.Cancelled() {
			continue
		}
		it := scanTarget(t, s, j.Cancelled)
		if it.Size < 1*gb {
			continue
		}
		r := Rec{ID: id, Level: levelBySize(it.Size), Icon: "layers", Size: it.Size, Title: t.Name + ": " + human(it.Size), Desc: t.Desc,
			Actions: []RecAction{{Label: "Удалить", Kind: "clean", Arg: id, Primary: true}}}
		if id == "windows_old" || id == "upgrade_leftovers" {
			r.Level = "high"
			r.Actions = append(r.Actions, RecAction{Label: "Через «Очистку диска»", Kind: "tool", Arg: "cleanmgr"})
		}
		add(r)
	}

	// 4. hibernation & pagefile --------------------------------------------
	step("Системные файлы")
	if h := hibernationInfo(); h.Size > 1*gb {
		add(Rec{ID: "hiberfil", Level: levelBySize(h.Size / 2), Icon: "moon", Size: h.Size,
			Title: "Файл гибернации занимает " + human(h.Size),
			Desc:  "hiberfil.sys хранит содержимое памяти для гибернации и быстрого запуска. Если вы не пользуетесь гибернацией, файл можно уменьшить примерно вдвое (быстрый запуск сохранится) или удалить полностью.",
			Actions: []RecAction{{Label: "Уменьшить", Kind: "tool", Arg: "hiber_reduced", Primary: true},
				{Label: "Отключить гибернацию", Kind: "tool", Arg: "hiber_off"}}})
	}
	if st, err := os.Stat(sysDrive + `\pagefile.sys`); err == nil && st.Size() > 12*gb {
		total, _ := winapi.Memory()
		if int64(total) >= 16*gb {
			add(Rec{ID: "pagefile", Level: "info", Icon: "memory", Size: 0,
				Title:   "Файл подкачки: " + human(st.Size()),
				Desc:    fmt.Sprintf("При %s оперативной памяти большой файл подкачки обычно не нужен. Его размер можно ограничить вручную (не отключайте полностью — некоторые программы и игры без него падают).", human(int64(total))),
				Actions: []RecAction{{Label: "Настроить", Kind: "tool", Arg: "pagefile"}}})
		}
	}

	// 5. downloads -----------------------------------------------------------
	step("Анализ папки «Загрузки»")
	if dl := winapi.KnownFolder("downloads"); dl != "" {
		var total, old, installers, archives int64
		var big []RecItem
		cut := time.Now().Add(-90 * 24 * time.Hour)
		o := &walkOpts{cancel: j.Cancelled}
		o.walk(dl, func(p string, fi fs.FileInfo) {
			sz := fi.Size()
			total += sz
			if fi.ModTime().Before(cut) {
				old += sz
			}
			switch categoryOf(fi.Name()) {
			case "program":
				installers += sz
			case "archive", "diskimage":
				archives += sz
			}
			if sz > 200*mb {
				big = append(big, RecItem{Label: fi.Name(), Path: p, Size: sz})
			}
		}, nil)
		sort.Slice(big, func(a, b int) bool { return big[a].Size > big[b].Size })
		if len(big) > 6 {
			big = big[:6]
		}
		if total > 2*gb {
			desc := fmt.Sprintf("Всего %s. Файлы старше 3 месяцев: %s. Установщики: %s, архивы и образы: %s — обычно их можно удалить после установки или распаковки.",
				human(total), human(old), human(installers), human(archives))
			add(Rec{ID: "downloads", Level: levelBySize(old + installers/2), Icon: "download", Size: old,
				Title: "Папка «Загрузки» занимает " + human(total), Desc: desc, Items: big,
				Actions: []RecAction{{Label: "Разобрать в анализаторе", Kind: "analyze", Arg: dl, Primary: true},
					{Label: "Найти дубликаты", Kind: "dupes", Arg: dl},
					{Label: "Открыть папку", Kind: "open", Arg: dl}}})
		}
		tg := filepath.Join(dl, "Telegram Desktop")
		if st, err := os.Stat(tg); err == nil && st.IsDir() {
			if sz, n := dirSize(tg, j.Cancelled); sz > 1*gb {
				add(Rec{ID: "tg_downloads", Level: "low", Icon: "send", Size: 0,
					Title:   "Файлы, скачанные из Telegram: " + human(sz),
					Desc:    fmt.Sprintf("В папке «Загрузки\\Telegram Desktop» %d файлов. Это ваши файлы, поэтому автоматически они не удаляются — просмотрите и удалите ненужное.", n),
					Actions: []RecAction{{Label: "Разобрать", Kind: "analyze", Arg: tg, Primary: true}, {Label: "Открыть папку", Kind: "open", Arg: tg}}})
			}
		}
	}

	// 6. AppData heavy hitters -------------------------------------------------
	step("Анализ данных программ (AppData)")
	var appItems []RecItem
	for _, base := range []string{os.Getenv("LOCALAPPDATA"), os.Getenv("APPDATA"), placeholder("locallow")} {
		if base == "" || j.Cancelled() {
			continue
		}
		for _, it := range childSizes(j, base) {
			if it.Size >= 700*mb && lower(it.Label) != "temp" && lower(it.Label) != "packages" {
				it.Label = filepath.Base(filepath.Dir(it.Path)) + `\` + it.Label
				appItems = append(appItems, it)
			}
		}
	}
	sort.Slice(appItems, func(a, b int) bool { return appItems[a].Size > appItems[b].Size })
	if len(appItems) > 8 {
		appItems = appItems[:8]
	}
	if len(appItems) > 0 {
		var sum int64
		for _, it := range appItems {
			sum += it.Size
		}
		add(Rec{ID: "appdata", Level: "info", Icon: "folder", Size: 0,
			Title:   "Крупные папки данных программ: " + human(sum),
			Desc:    "Здесь программы хранят настройки, кэш и загруженные данные. Если программа уже удалена, её папка — чистый мусор. Иначе проверьте настройки самой программы (часто там есть «Очистить кэш» или выбор папки хранения).",
			Items:   appItems,
			Actions: []RecAction{{Label: "Анализировать AppData", Kind: "analyze", Arg: filepath.Dir(os.Getenv("LOCALAPPDATA")), Primary: true}}})
	}

	// 7. user folders vs other drives ---------------------------------------
	step("Пользовательские папки")
	var userBig []RecItem
	for _, k := range []struct{ id, name string }{{"videos", "Видео"}, {"documents", "Документы"}, {"desktop", "Рабочий стол"}, {"pictures", "Изображения"}, {"music", "Музыка"}} {
		p := winapi.KnownFolder(k.id)
		if p == "" || !strings.HasPrefix(strings.ToUpper(p), sysDrive) {
			continue
		}
		if sz, _ := dirSize(p, j.Cancelled); sz > 5*gb {
			userBig = append(userBig, RecItem{Label: k.name, Path: p, Size: sz})
		}
	}
	sort.Slice(userBig, func(a, b int) bool { return userBig[a].Size > userBig[b].Size })
	if len(userBig) > 0 && sysFreePct < 25 && bestOther != nil && bestOther.Free > 50*uint64(gb) {
		add(Rec{ID: "move_folders", Level: "medium", Icon: "move",
			Title: "Перенесите личные папки на диск " + strings.TrimRight(bestOther.Root, `\`),
			Desc: fmt.Sprintf("На системном диске мало места, а на %s свободно %s. Нажмите правой кнопкой на папку → Свойства → Расположение → Переместить — Windows перенесёт файлы и сохранит все ссылки.",
				strings.TrimRight(bestOther.Root, `\`), human(int64(bestOther.Free))),
			Items: userBig})
	} else if len(userBig) > 0 && userBig[0].Size > 20*gb {
		add(Rec{ID: "user_folders", Level: "info", Icon: "folder",
			Title:   "Личные файлы занимают много места",
			Desc:    "Это ваши файлы — программа их не удаляет. Посмотрите в анализаторе, что из этого можно перенести в облако, на внешний диск или удалить.",
			Items:   userBig,
			Actions: []RecAction{{Label: "Анализировать", Kind: "analyze", Arg: userBig[0].Path, Primary: true}}})
	}

	// 8. programs, storage sense, maintenance -----------------------------------
	step("Программы и настройки")
	progs := listPrograms()
	var bigProgs []RecItem
	for _, p := range progs {
		if p.Size >= 2*gb && len(bigProgs) < 6 {
			bigProgs = append(bigProgs, RecItem{Label: p.Name, Size: p.Size})
		}
	}
	if len(bigProgs) > 0 {
		add(Rec{ID: "programs", Level: "info", Icon: "apps",
			Title:   "Самые тяжёлые программы",
			Desc:    "Если чем-то из списка давно не пользуетесь — удаление программы освободит больше всего места.",
			Items:   bigProgs,
			Actions: []RecAction{{Label: "Открыть список программ", Kind: "nav", Arg: "programs", Primary: true}}})
	}
	if v, ok := winapi.RegGetDword(winapi.HKCU, `Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy`, "01"); winapi.IsWindows && (!ok || v == 0) {
		add(Rec{ID: "storagesense", Level: "info", Icon: "auto",
			Title: "Включите автоматическую очистку",
			Desc:  "Можно настроить регулярную очистку прямо здесь (Настройки → Автоочистка) или включить «Контроль памяти» Windows.",
			Actions: []RecAction{{Label: "Настроить автоочистку", Kind: "nav", Arg: "settings", Primary: true},
				{Label: "Контроль памяти", Kind: "tool", Arg: "storagesense"}}})
	}
	if admin {
		add(Rec{ID: "winsxs", Level: "info", Icon: "layers",
			Title: "Очистка хранилища компонентов Windows",
			Desc:  "После обновлений в WinSxS копятся старые версии системных файлов — обычно это от 1 до 5 ГБ. Очистка безопасна, но занимает время.",
			Actions: []RecAction{{Label: "Проверить размер", Kind: "tool", Arg: "dism_analyze", Primary: true},
				{Label: "Очистить", Kind: "tool", Arg: "dism_cleanup"}}})
	} else {
		add(Rec{ID: "admin", Level: "medium", Icon: "shield",
			Title:   "Нужны права администратора",
			Desc:    "Без них недоступна очистка системных папок: кэша обновлений, Windows\\Temp, дампов памяти, Windows.old и хранилища компонентов. Программу перезапускать не нужно.",
			Actions: []RecAction{{Label: "Получить права", Kind: "elevate", Primary: true}}})
	}

	order := map[string]int{"critical": 0, "high": 1, "medium": 2, "low": 3, "info": 4}
	sort.SliceStable(recs, func(a, b int) bool {
		if order[recs[a].Level] != order[recs[b].Level] {
			return order[recs[a].Level] < order[recs[b].Level]
		}
		return recs[a].Size > recs[b].Size
	})
	var potential int64
	score := 100
	for _, r := range recs {
		if r.ID != "pagefile" && r.ID != "tg_downloads" && r.ID != "appdata" && r.ID != "downloads" {
			potential += r.Size
		}
		switch r.Level {
		case "critical":
			score -= 30
		case "high":
			score -= 14
		case "medium":
			score -= 7
		case "low":
			score -= 3
		}
	}
	if score < 5 {
		score = 5
	}
	return advice{Recs: recs, Potential: potential, Score: score, Took: time.Since(start).Seconds()}, nil
}
