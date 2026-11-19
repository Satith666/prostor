package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sync"
	"time"
)

type Schedule struct {
	Mode string `json:"mode"` // off | daily | weekly
	Time string `json:"time"` // HH:MM
	Day  string `json:"day"`  // MON..SUN
}

type Settings struct {
	Theme           string          `json:"theme"`       // dark | light | auto
	ThemeChosen     bool            `json:"themeChosen"` // the user picked the theme (otherwise dark is used)
	SkipRecentHours int             `json:"skipRecentHours"`
	DeleteOnReboot  bool            `json:"deleteOnReboot"`
	Excludes        []string        `json:"excludes"`
	Selected        map[string]bool `json:"selected"`
	Schedule        Schedule        `json:"schedule"`
	MinDupMB        int             `json:"minDupMB"`
	AutoElevate     bool            `json:"autoElevate"`
	QuarantineDays  int             `json:"quarantineDays"` // 0 = straight to the Recycle Bin
}

type HistoryItem struct {
	Name  string `json:"name"`
	Freed int64  `json:"freed"`
	Files int64  `json:"files"`
}

type HistoryEntry struct {
	Time    time.Time     `json:"time"`
	Mode    string        `json:"mode"` // manual | auto | tool
	Freed   int64         `json:"freed"`
	Files   int64         `json:"files"`
	Failed  int64         `json:"failed"`
	Held    int64         `json:"held,omitempty"` // bytes moved to quarantine (not freed yet)
	Journal string        `json:"journal,omitempty"`
	Items   []HistoryItem `json:"items"`
}

var (
	storeMu  sync.Mutex
	settings = defaultSettings()
	history  []HistoryEntry
)

func defaultSettings() Settings {
	return Settings{
		Theme:           "dark",
		SkipRecentHours: 24,
		Selected:        map[string]bool{},
		Schedule:        Schedule{Mode: "off", Time: "12:00", Day: "SUN"},
		MinDupMB:        1,
		AutoElevate:     true,
		QuarantineDays:  7,
	}
}

func settingsPath() string { return filepath.Join(appDataDir(), "settings.json") }
func historyPath() string  { return filepath.Join(appDataDir(), "history.json") }

func loadStore() {
	storeMu.Lock()
	defer storeMu.Unlock()
	if b, err := os.ReadFile(settingsPath()); err == nil {
		s := defaultSettings()
		if json.Unmarshal(b, &s) == nil {
			if s.Selected == nil {
				s.Selected = map[string]bool{}
			}
			if s.SkipRecentHours < 0 {
				s.SkipRecentHours = 0
			}
			if s.QuarantineDays < 0 || s.QuarantineDays > 90 {
				s.QuarantineDays = 7
			}
			if !s.ThemeChosen || (s.Theme != "light" && s.Theme != "auto") {
				s.Theme = "dark" // 1.1.0 saved "auto" without asking; dark is the default now
			}
			settings = s
		}
	}
	if b, err := os.ReadFile(historyPath()); err == nil {
		json.Unmarshal(b, &history)
	}
}

func writeFileAtomic(path string, v any) error {
	b, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func getSettings() Settings {
	storeMu.Lock()
	defer storeMu.Unlock()
	s := settings
	s.Selected = map[string]bool{}
	for k, v := range settings.Selected {
		s.Selected[k] = v
	}
	s.Excludes = append([]string(nil), settings.Excludes...)
	return s
}

func saveSettings(s Settings) error {
	storeMu.Lock()
	defer storeMu.Unlock()
	if s.Selected == nil {
		s.Selected = map[string]bool{}
	}
	settings = s
	return writeFileAtomic(settingsPath(), settings)
}

func addHistory(e HistoryEntry) {
	storeMu.Lock()
	defer storeMu.Unlock()
	history = append(history, e)
	if len(history) > 300 {
		history = history[len(history)-300:]
	}
	writeFileAtomic(historyPath(), history)
}

func getHistory() []HistoryEntry {
	storeMu.Lock()
	defer storeMu.Unlock()
	return append([]HistoryEntry(nil), history...)
}

func clearHistory() {
	storeMu.Lock()
	defer storeMu.Unlock()
	history = nil
	os.Remove(historyPath())
}
