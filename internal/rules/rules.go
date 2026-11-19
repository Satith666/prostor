// Package rules describes cleanup rules as data: what to delete (folders,
// name masks, depth), what to keep (exclusions) and how to tell whether the
// program a rule belongs to is installed. Rules ship as JSON files inside
// Prostor; the Winapp2 database is converted into the same format by
// cmd/rulegen after a safety check (see Check).
package rules

import (
	"encoding/json"
	"fmt"
	"io"
)

// Loc is one place a rule cleans.
type Loc struct {
	Path     string   `json:"path"`               // %ENV% variables, * wildcards, {placeholders}
	Masks    []string `json:"masks,omitempty"`    // lowercase name masks; empty = every file
	Flat     bool     `json:"flat,omitempty"`     // files directly in the folder only
	KeepDirs bool     `json:"keepDirs,omitempty"` // keep emptied subfolders
}

// Exclude keeps files inside a rule's folders.
type Exclude struct {
	Path    string   `json:"path"`
	Masks   []string `json:"masks,omitempty"`   // empty = every file
	Recurse bool     `json:"recurse,omitempty"` // also in subfolders
}

// Rule is one cleanup item as stored in a rules file.
type Rule struct {
	ID         string    `json:"id"`
	Cat        string    `json:"cat"`
	Name       string    `json:"name"`
	Desc       string    `json:"desc"`
	Admin      bool      `json:"admin,omitempty"`
	Default    bool      `json:"default,omitempty"`
	Caution    bool      `json:"caution,omitempty"`
	Procs      []string  `json:"procs,omitempty"`
	Locs       []Loc     `json:"locs,omitempty"`
	Excludes   []Exclude `json:"excludes,omitempty"`
	MinAge     int       `json:"minAge,omitempty"` // days
	Recent     bool      `json:"recent,omitempty"` // honour "skip recent files"
	Special    string    `json:"special,omitempty"`
	Detect     []string  `json:"detect,omitempty"`     // registry keys, any of them
	DetectFile []string  `json:"detectFile,omitempty"` // files or folders, any of them
	Source     string    `json:"source,omitempty"`     // "" = Prostor, "winapp2"
}

// File is a rules file.
type File struct {
	Source  string `json:"source"`
	Version string `json:"version,omitempty"`
	License string `json:"license,omitempty"`
	Notice  string `json:"notice,omitempty"`
	Rules   []Rule `json:"rules"`
}

// Decode reads a rules file and checks that ids are unique and every rule
// has somewhere to clean.
func Decode(r io.Reader) (*File, error) {
	var f File
	if err := json.NewDecoder(r).Decode(&f); err != nil {
		return nil, err
	}
	seen := map[string]bool{}
	for i := range f.Rules {
		ru := &f.Rules[i]
		if ru.ID == "" || seen[ru.ID] {
			return nil, fmt.Errorf("правило %d: пустой или повторный id %q", i, ru.ID)
		}
		seen[ru.ID] = true
		if len(ru.Locs) == 0 && ru.Special == "" {
			return nil, fmt.Errorf("правило %s: нет папок для очистки", ru.ID)
		}
		if ru.Source == "" {
			ru.Source = f.Source
		}
	}
	return &f, nil
}
