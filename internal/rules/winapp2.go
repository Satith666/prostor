package rules

// Converting Winapp2.ini (https://github.com/MoscaDotTo/Winapp2, CC BY-SA 4.0).
//
// Winapp2 is written for cleaners that also wipe history, cookies, sessions
// and settings. Prostor does not: it removes caches, logs, temporary files and
// crash dumps only. So every FileKey goes through Check, which accepts a
// folder only when it is plainly one of those (a "cache", "logs", "temp",
// "crash…" folder below the program's own folder, or log/temp/dump file
// masks), and rejects anything that names personal data. Registry keys are
// never imported. What survives is tested by the bait test like any other
// rule.

import (
	"bufio"
	"fmt"
	"io"
	"regexp"
	"sort"
	"strconv"
	"strings"
)

// Rejected explains why an entry or a FileKey was left out.
type Rejected struct {
	Entry  string `json:"entry"`
	Key    string `json:"key,omitempty"`
	Reason string `json:"reason"`
}

type w2Entry struct {
	name   string
	keys   [][2]string // in file order
	header string      // comment line above (group)
}

func parseINI(r io.Reader) (version string, entries []w2Entry, err error) {
	sc := bufio.NewScanner(r)
	sc.Buffer(make([]byte, 1<<20), 1<<20)
	var cur *w2Entry
	for sc.Scan() {
		line := strings.TrimSpace(strings.TrimPrefix(sc.Text(), "\uFEFF"))
		switch {
		case line == "":
		case strings.HasPrefix(line, ";"):
			if v, ok := strings.CutPrefix(line, "; Version:"); ok {
				version = strings.TrimSpace(v)
			}
		case strings.HasPrefix(line, "[") && strings.HasSuffix(line, "]"):
			entries = append(entries, w2Entry{name: strings.TrimSuffix(strings.TrimSpace(line[1:len(line)-1]), " *")})
			cur = &entries[len(entries)-1]
		default:
			k, v, ok := strings.Cut(line, "=")
			if ok && cur != nil {
				cur.keys = append(cur.keys, [2]string{strings.TrimSpace(k), strings.TrimSpace(v)})
			}
		}
	}
	return version, entries, sc.Err()
}

// variables Winapp2 uses → Prostor's; nil = not allowed (personal folders).
var w2Vars = map[string][]string{
	"%localappdata%":       {"%LOCALAPPDATA%"},
	"%appdata%":            {"%APPDATA%"},
	"%programfiles%":       {"%ProgramFiles%", "%ProgramFiles(x86)%"},
	"%programdata%":        {"%ProgramData%"},
	"%commonappdata%":      {"%ProgramData%"},
	"%userprofile%":        {"%USERPROFILE%"},
	"%windir%":             {"%SystemRoot%"},
	"%systemroot%":         {"%SystemRoot%"},
	"%systemdrive%":        {"%SystemDrive%"},
	"%commonprogramfiles%": {"%CommonProgramFiles%", "%CommonProgramFiles(x86)%"},
	"%locallowappdata%":    {"{locallow}"},
}

// adminVars need administrator rights to clean.
var adminVars = map[string]bool{"%programfiles%": true, "%windir%": true, "%systemroot%": true, "%systemdrive%": true, "%commonprogramfiles%": true}

var (
	junkExact = map[string]bool{
		"temp": true, "tmp": true, "temporary": true, "temp files": true, "temporary files": true,
		"log": true, "logs": true, "logfiles": true, "log files": true, "logging": true,
		"crash": true, "crashes": true, "crashdumps": true, "crash dumps": true, "crashpad": true, "crash reports": true,
		"crashreports": true, "crashlogs": true, "crashreporter": true, "dumps": true, "dump": true, "minidump": true, "minidumps": true,
		"thumbnails": true, "thumbs": true, "error reports": true, "errorreports": true,
	}
	// these hold data even though the name says "cache"
	notJunk = map[string]bool{"localcache": true}

	denyParts = []string{
		"cookie", "history", "login", "password", "passwd", "credential", "web data", "local storage", "session",
		"indexeddb", "databases", "bookmark", "favicon", "top sites", "visited", "autofill", "wallet", "keychain",
		"key3", "key4", "cert8", "cert9", "places.sqlite", "formhistory", "tdata", "sync data", "backup", "licen",
		"activation", "vault", "secret", ".ssh", "gnupg", "keystore", "token", "account", "profile.ini", "profiles.ini",
		"quarantine", "virus chest", "recovery", "restore", "snapshot", "archive",
		// installers kept for repair and uninstall, Windows component stores
		"package cache", "setupcache", "msocache", "installer", "downloaded installations", "winsxs", "driverstore", "catroot", "servicing",
	}
	denyExact = map[string]bool{
		"documents": true, "my documents": true, "desktop": true, "pictures": true, "my pictures": true, "music": true,
		"my music": true, "videos": true, "my videos": true, "downloads": true, "onedrive": true, "dropbox": true,
		"google drive": true, "saved games": true, "contacts": true, "favorites": true, "links": true, "searches": true,
		"extensions": true, "plugins": true, "keys": true, "mail": true, "inbox": true,
	}
	nameDeny   = regexp.MustCompile(`(?i)cookie|histor|password|username|session|autofill|bookmark|preference|\btabs\b|\bsync\b|pinned|extension|favicon|search engine|secure|login|credential|\bmru\b|recent|jump ?list|registry|\bdrm\b|wallet|saved|setting|backup|licen`)
	junkMaskRe = regexp.MustCompile(`\.(log|logs|log\.|tmp|temp|dmp|mdmp|hdmp|etl|lo_|trace|crash)$`)
)

func stripWild(s string) string {
	return strings.Trim(strings.NewReplacer("*", "", "?", "").Replace(s), " .")
}

// junkSegment: 2 = plainly a cache/log/temp folder ("Cache", "GPUCache",
// "Logs", "Temp"), 1 = only mentions a cache ("CacheStorage", "cache2"),
// 0 = not junk.
func junkSegment(seg string) int {
	s := strings.ToLower(stripWild(seg))
	switch {
	case s == "" || notJunk[s]:
		return 0
	case junkExact[s] || strings.HasSuffix(s, "cache") || strings.HasSuffix(s, "caches"):
		return 2
	case strings.Contains(s, "cache"):
		return 1
	}
	return 0
}

func junkMask(m string) bool {
	m = strings.ToLower(strings.TrimRight(m, "*?0123456789"))
	return junkMaskRe.MatchString(m) || strings.HasSuffix(m, "log.txt") || strings.HasSuffix(m, "log.old")
}

func denied(part string) string {
	p := strings.ToLower(part)
	if denyExact[strings.Trim(p, "* ")] {
		return part
	}
	if strings.Contains(p, "save") && strings.Trim(p, "* ") != "saved" {
		return part
	}
	for _, d := range denyParts {
		if strings.Contains(p, d) {
			return part
		}
	}
	return ""
}

// CheckLoc decides whether a Winapp2 folder may be cleaned by Prostor.
// path starts with a Winapp2 variable; masks are lowercase, empty = all files.
func CheckLoc(path string, masks []string) error {
	segs := strings.Split(strings.Trim(path, `\`), `\`)
	v := strings.ToLower(segs[0])
	if _, ok := w2Vars[v]; !ok {
		return fmt.Errorf("папка %s не разрешена", segs[0])
	}
	rest := segs[1:]
	for _, s := range rest {
		if strings.Contains(s, "%") {
			return fmt.Errorf("переменная внутри пути")
		}
		if d := denied(s); d != "" {
			return fmt.Errorf("личные данные: %s", d)
		}
	}
	for _, m := range masks {
		if d := denied(m); d != "" {
			return fmt.Errorf("личные данные: %s", d)
		}
	}
	// a cache/log/temp folder below the program's own folder: plainly named
	// anywhere in the path, or only mentioning a cache as the last folder
	last := -1
	for i, s := range rest {
		if stripWild(s) != "" {
			last = i
		}
	}
	for i, s := range rest {
		if k := junkSegment(s); k == 2 || (k == 1 && i == last) {
			if i == 0 {
				return fmt.Errorf("общая папка %s, а не папка программы", s)
			}
			return nil
		}
	}
	// or log/temp/dump files by mask inside the program's folder
	if len(masks) == 0 {
		return fmt.Errorf("все файлы папки, которая не похожа на кэш или журналы")
	}
	if len(rest) == 0 || stripWild(rest[0]) == "" {
		return fmt.Errorf("корень системной папки")
	}
	if (v == "%windir%" || v == "%systemroot%") && len(rest) == 1 {
		return fmt.Errorf("системная папка %s", rest[0])
	}
	for _, m := range masks {
		if !junkMask(m) {
			return fmt.Errorf("маска %s не похожа на журналы или временные файлы", m)
		}
	}
	return nil
}

func splitMasks(s string) []string {
	var out []string
	for _, m := range strings.Split(s, ";") {
		m = strings.ToLower(strings.TrimSpace(m))
		if m == "" {
			continue
		}
		if m == "*" || m == "*.*" {
			return nil
		}
		out = append(out, m)
	}
	return out
}

// mapPath replaces the leading Winapp2 variable.
func mapPath(p string) []string {
	p = strings.TrimRight(p, `\`)
	seg, rest, _ := strings.Cut(p, `\`)
	vs := w2Vars[strings.ToLower(seg)]
	out := make([]string, 0, len(vs))
	for _, v := range vs {
		if rest == "" {
			out = append(out, v)
		} else {
			out = append(out, v+`\`+rest)
		}
	}
	return out
}

var slugRe = regexp.MustCompile(`[^a-z0-9]+`)

// skip entries for programs Prostor already covers with its own rules
var w2Skip = regexp.MustCompile(`(?i)^(google chrome|microsoft edge|mozilla firefox|opera|brave|vivaldi|yandex|discord|telegram|spotify|visual studio code|steam|epic games|nvidia|amd |intel |minecraft|roblox|java|npm|pip|gradle|cargo|nuget|go |microsoft teams|onedrive|windows )`)

// ConvertWinapp2 turns Winapp2.ini into Prostor rules.
func ConvertWinapp2(r io.Reader) (*File, []Rejected, error) {
	version, entries, err := parseINI(r)
	if err != nil {
		return nil, nil, err
	}
	f := &File{Source: "winapp2", Version: version, License: "CC BY-SA 4.0",
		Notice: "Правила получены из Winapp2.ini (https://github.com/MoscaDotTo/Winapp2), лицензия CC BY-SA 4.0. Изменены: оставлены только кэш, журналы, временные файлы и дампы; переведены в формат Prostor."}
	var rej []Rejected
	used := map[string]bool{}
	for _, e := range entries {
		if w2Skip.MatchString(e.name) {
			rej = append(rej, Rejected{Entry: e.name, Reason: "есть собственное правило Prostor"})
			continue
		}
		if nameDeny.MatchString(e.name) {
			rej = append(rej, Rejected{Entry: e.name, Reason: "личные данные или настройки"})
			continue
		}
		ru := Rule{Source: "winapp2", Default: true}
		section, lang := "", 0
		admin := false
		var bad bool
		for _, kv := range e.keys {
			k, v := strings.ToLower(kv[0]), kv[1]
			switch {
			case k == "section":
				section = v
			case k == "langsecref":
				lang, _ = strconv.Atoi(v)
			case k == "warning":
				rej = append(rej, Rejected{Entry: e.name, Reason: "у записи есть предупреждение: " + v})
				bad = true
			case strings.HasPrefix(k, "detectfile"):
				ru.DetectFile = append(ru.DetectFile, mapPath(v)...)
			case strings.HasPrefix(k, "detect") && !strings.HasPrefix(k, "detectos"):
				if root, _, _ := strings.Cut(v, `\`); root == "HKCU" || root == "HKLM" {
					ru.Detect = append(ru.Detect, v)
				}
			case strings.HasPrefix(k, "specialdetect"):
				ru.DetectFile = append(ru.DetectFile, specialDetect[strings.ToUpper(v)]...)
			case strings.HasPrefix(k, "filekey"):
				parts := strings.Split(v, "|")
				if len(parts) < 2 {
					continue
				}
				masks := splitMasks(parts[1])
				flag := ""
				if len(parts) > 2 {
					flag = strings.ToUpper(strings.TrimSpace(parts[2]))
				}
				if err := CheckLoc(parts[0], masks); err != nil {
					rej = append(rej, Rejected{Entry: e.name, Key: v, Reason: err.Error()})
					continue
				}
				if adminVars[strings.ToLower(strings.SplitN(parts[0], `\`, 2)[0])] {
					admin = true
				}
				for _, p := range mapPath(parts[0]) {
					l := Loc{Path: p, Masks: masks}
					switch flag {
					case "RECURSE":
						l.KeepDirs = true
					case "REMOVESELF":
					default:
						l.Flat = true
					}
					ru.Locs = append(ru.Locs, l)
				}
			case strings.HasPrefix(k, "excludekey"):
				parts := strings.Split(v, "|")
				if len(parts) < 3 || (parts[0] != "FILE" && parts[0] != "PATH") {
					continue
				}
				for _, p := range mapPath(parts[1]) {
					ru.Excludes = append(ru.Excludes, Exclude{Path: p, Masks: splitMasks(parts[2]), Recurse: parts[0] == "PATH"})
				}
			}
		}
		if bad {
			continue
		}
		if len(ru.Locs) == 0 {
			continue
		}
		ru.Name = e.name
		ru.Admin = admin
		ru.Cat = w2Category(section, lang)
		ru.Desc = w2Desc(ru.Locs)
		id := "w2-" + strings.Trim(slugRe.ReplaceAllString(strings.ToLower(e.name), "-"), "-")
		for n := 2; used[id]; n++ {
			id = fmt.Sprintf("%s-%d", strings.TrimRight(id, "-0123456789"), n)
		}
		used[id] = true
		ru.ID = id
		f.Rules = append(f.Rules, ru)
	}
	sort.SliceStable(f.Rules, func(a, b int) bool { return strings.ToLower(f.Rules[a].Name) < strings.ToLower(f.Rules[b].Name) })
	return f, rej, nil
}

var specialDetect = map[string][]string{
	"DET_CHROME":      {`%LOCALAPPDATA%\Google\Chrome*`},
	"DET_MOZILLA":     {`%APPDATA%\Mozilla\Firefox`},
	"DET_THUNDERBIRD": {`%APPDATA%\Thunderbird`},
	"DET_OPERA":       {`%APPDATA%\Opera Software`},
}

func w2Category(section string, lang int) string {
	switch {
	case strings.EqualFold(section, "Games"):
		return "games"
	case strings.Contains(section, "Web Browser") || (lang >= 3026 && lang <= 3029):
		return "browsers"
	case lang == 3025 || lang == 3031:
		return "system"
	}
	return "apps"
}

func w2Desc(locs []Loc) string {
	var cache, logs, temp, crash bool
	for _, l := range locs {
		s := strings.ToLower(l.Path + " " + strings.Join(l.Masks, " "))
		switch {
		case strings.Contains(s, "crash") || strings.Contains(s, "dump") || strings.Contains(s, ".dmp"):
			crash = true
		case strings.Contains(s, "cache") || strings.Contains(s, "thumb"):
			cache = true
		case strings.Contains(s, "log"):
			logs = true
		default:
			temp = true
		}
	}
	var parts []string
	if cache {
		parts = append(parts, "кэш")
	}
	if logs {
		parts = append(parts, "журналы")
	}
	if temp {
		parts = append(parts, "временные файлы")
	}
	if crash {
		parts = append(parts, "отчёты о сбоях")
	}
	s := strings.Join(parts, ", ")
	if i := strings.LastIndex(s, ", "); i >= 0 {
		s = s[:i] + " и " + s[i+2:]
	}
	r := []rune(s)
	return strings.ToUpper(string(r[:1])) + string(r[1:]) + " программы."
}
