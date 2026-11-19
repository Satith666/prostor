package main

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
	"time"

	"prostor/internal/winapi"
)

type Tool struct {
	ID      string `json:"id"`
	Group   string `json:"group"`
	Name    string `json:"name"`
	Desc    string `json:"desc"`
	Admin   bool   `json:"admin"`
	Caution bool   `json:"caution"`
	Open    bool   `json:"open"` // just opens a system window
	Confirm string `json:"confirm,omitempty"`
}

var tools = []Tool{
	{ID: "dism_analyze", Group: "deep", Name: "Анализ хранилища компонентов", Admin: true,
		Desc: "Показывает реальный размер папки WinSxS и можно ли её сжать. Занимает 1–3 минуты."},
	{ID: "dism_cleanup", Group: "deep", Name: "Очистка хранилища компонентов (WinSxS)", Admin: true,
		Desc:    "Удаляет устаревшие версии системных компонентов после обновлений. Безопасно, может идти 5–20 минут.",
		Confirm: "Запустить очистку хранилища компонентов? Операция может занять до 20 минут, не выключайте компьютер."},
	{ID: "dism_resetbase", Group: "deep", Name: "Глубокая очистка WinSxS (ResetBase)", Admin: true, Caution: true,
		Desc:    "Освобождает больше места, но установленные обновления станет невозможно удалить.",
		Confirm: "После ResetBase нельзя будет откатить уже установленные обновления Windows. Продолжить?"},
	{ID: "hiber_reduced", Group: "deep", Name: "Уменьшить файл гибернации", Admin: true,
		Desc: "Сжимает hiberfil.sys примерно вдвое. Быстрый запуск Windows продолжит работать, режим гибернации — нет."},
	{ID: "hiber_off", Group: "deep", Name: "Отключить гибернацию", Admin: true, Caution: true,
		Desc:    "Полностью удаляет hiberfil.sys (обычно 40–75% объёма ОЗУ). Отключит гибернацию и быстрый запуск.",
		Confirm: "Отключить гибернацию? Файл hiberfil.sys будет удалён, быстрый запуск Windows перестанет работать."},
	{ID: "hiber_on", Group: "deep", Name: "Включить гибернацию обратно", Admin: true,
		Desc: "Восстанавливает гибернацию и быстрый запуск с полным файлом."},
	{ID: "eventlogs", Group: "deep", Name: "Очистить журналы событий", Admin: true, Caution: true,
		Desc:    "Очищает все журналы «Просмотра событий». Полезно, но удалит историю диагностики сбоев.",
		Confirm: "Очистить все журналы событий Windows? История диагностики будет удалена."},

	{ID: "restore_point", Group: "service", Name: "Создать точку восстановления", Admin: true,
		Desc: "Снимок системных файлов перед серьёзными изменениями. Нужна включённая защита системы."},
	{ID: "trim", Group: "service", Name: "Оптимизировать системный диск", Admin: true,
		Desc: "TRIM для SSD или дефрагментация для HDD — Windows сама выберет нужное."},
	{ID: "flushdns", Group: "service", Name: "Очистить DNS-кэш",
		Desc: "Помогает, если сайты не открываются после смены DNS или VPN."},
	{ID: "wsreset", Group: "service", Name: "Сбросить кэш Microsoft Store", Open: true,
		Desc: "Исправляет зависшие загрузки и ошибки магазина."},

	{ID: "cleanmgr", Group: "open", Name: "Встроенная «Очистка диска»", Open: true,
		Desc: "Классическая утилита Windows — для пунктов, которые доступны только ей."},
	{ID: "storagesense", Group: "open", Name: "Контроль памяти", Open: true,
		Desc: "Автоматическая очистка средствами Windows."},
	{ID: "sysprotect", Group: "open", Name: "Защита системы и точки восстановления", Open: true,
		Desc: "Настройка места под точки восстановления."},
	{ID: "pagefile", Group: "open", Name: "Файл подкачки", Open: true,
		Desc: "Параметры быстродействия → Дополнительно → Виртуальная память."},
	{ID: "apps", Group: "open", Name: "Приложения Windows", Open: true,
		Desc: "Удаление приложений из Microsoft Store и системных компонентов."},
	{ID: "diskmgmt", Group: "open", Name: "Управление дисками", Open: true,
		Desc: "Разделы, буквы дисков, расширение тома."},
}

func toolByID(id string) *Tool {
	for i := range tools {
		if tools[i].ID == id {
			return &tools[i]
		}
	}
	return nil
}

func openTool(id string) error {
	switch id {
	case "wsreset":
		return winapi.ShellRun("wsreset.exe", "")
	case "cleanmgr":
		return winapi.ShellRun("cleanmgr.exe", "")
	case "storagesense":
		return winapi.ShellOpen("ms-settings:storagesense")
	case "sysprotect":
		return winapi.ShellRun("SystemPropertiesProtection.exe", "")
	case "pagefile":
		return winapi.ShellRun("SystemPropertiesPerformance.exe", "")
	case "apps":
		return winapi.ShellOpen("ms-settings:appsfeatures")
	case "diskmgmt":
		return winapi.ShellRun("diskmgmt.msc", "")
	}
	return errors.New("неизвестный инструмент")
}

func runTool(j *Job, id string) (any, error) {
	t := toolByID(id)
	if t == nil {
		return nil, errors.New("неизвестный инструмент")
	}
	j.SetPhase(t.Name)
	sysDrive := os.Getenv("SystemDrive")
	if sysDrive == "" {
		sysDrive = "C:"
	}
	_, freeBefore, _ := winapi.DiskSpace(sysDrive + `\`)
	var err error
	switch id {
	case "dism_analyze":
		err = runLogged(j, "Dism.exe", "/Online", "/Cleanup-Image", "/AnalyzeComponentStore")
	case "dism_cleanup":
		err = runLogged(j, "Dism.exe", "/Online", "/Cleanup-Image", "/StartComponentCleanup")
	case "dism_resetbase":
		err = runLogged(j, "Dism.exe", "/Online", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase")
	case "hiber_off":
		err = runLogged(j, "powercfg.exe", "/hibernate", "off")
	case "hiber_reduced":
		runLogged(j, "powercfg.exe", "/hibernate", "on")
		err = runLogged(j, "powercfg.exe", "/hibernate", "/type", "reduced")
	case "hiber_on":
		runLogged(j, "powercfg.exe", "/hibernate", "on")
		err = runLogged(j, "powercfg.exe", "/hibernate", "/type", "full")
	case "flushdns":
		err = runLogged(j, "ipconfig.exe", "/flushdns")
	case "trim":
		err = runLogged(j, "defrag.exe", sysDrive, "/O", "/U")
	case "restore_point":
		err = runLogged(j, "powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
			`Checkpoint-Computer -Description 'Prostor' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop; 'Точка восстановления создана.'`)
		if err != nil {
			j.Log("Не удалось создать точку. Проверьте, что защита системы включена (кнопка «Защита системы»). Windows разрешает создавать не больше одной точки в сутки.")
		}
	case "eventlogs":
		out, e := runQuiet("wevtutil.exe", "el")
		if e != nil {
			err = e
			break
		}
		logs := strings.Split(strings.ReplaceAll(out, "\r", ""), "\n")
		j.Total.Store(int64(len(logs)))
		cleared := 0
		for _, l := range logs {
			if j.Cancelled() {
				break
			}
			l = strings.TrimSpace(l)
			j.Done.Add(1)
			if l == "" {
				continue
			}
			j.SetCurrent(l)
			if _, e := runQuiet("wevtutil.exe", "cl", l); e == nil {
				cleared++
			}
		}
		j.Log("Очищено журналов: " + itoa(cleared))
	default:
		return nil, errors.New("этот инструмент открывается отдельным окном")
	}
	_, freeAfter, _ := winapi.DiskSpace(sysDrive + `\`)
	freed := int64(freeAfter) - int64(freeBefore)
	if freed < 0 {
		freed = 0
	}
	if err != nil {
		j.Log("Команда завершилась с ошибкой: " + err.Error())
	} else {
		j.Log("Готово.")
	}
	if freed > 0 && (strings.HasPrefix(id, "dism_c") || strings.HasPrefix(id, "dism_r") || strings.HasPrefix(id, "hiber")) {
		addHistory(HistoryEntry{Time: time.Now(), Mode: "tool", Freed: freed, Items: []HistoryItem{{Name: t.Name, Freed: freed}}})
	}
	return map[string]any{"freed": freed, "ok": err == nil}, nil
}

type hiberInfo struct {
	Enabled bool  `json:"enabled"`
	Size    int64 `json:"size"`
}

func hibernationInfo() hiberInfo {
	sd := os.Getenv("SystemDrive")
	if sd == "" {
		sd = "C:"
	}
	var h hiberInfo
	if st, err := os.Stat(filepath.Join(sd+`\`, "hiberfil.sys")); err == nil {
		h.Size = st.Size()
		h.Enabled = true
	}
	if v, ok := winapi.RegGetDword(winapi.HKLM, `SYSTEM\CurrentControlSet\Control\Power`, "HibernateEnabled"); ok {
		h.Enabled = v == 1
	}
	return h
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var b [20]byte
	i := len(b)
	for n > 0 {
		i--
		b[i] = byte('0' + n%10)
		n /= 10
	}
	if neg {
		i--
		b[i] = '-'
	}
	return string(b[i:])
}
