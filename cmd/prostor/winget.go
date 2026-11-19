package main

// Program updates through winget (Windows Package Manager): the list of
// programs with a newer version and a silent update of the chosen ones.

import (
	"bytes"
	"errors"
	"os/exec"
	"regexp"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"
)

type Upgrade struct {
	Name      string `json:"name"`
	ID        string `json:"id"`
	Version   string `json:"version"`
	Available string `json:"available"`
	Source    string `json:"source"`
}

func wingetPath() string {
	if p, err := exec.LookPath("winget.exe"); err == nil {
		return p
	}
	return ""
}

func listUpgrades(j *Job) (any, error) {
	w := wingetPath()
	if w == "" {
		return nil, errors.New("winget не установлен: его ставит «Установщик приложений» из Microsoft Store")
	}
	j.SetPhase("Проверка обновлений")
	out, err := runQuiet(w, "upgrade", "--include-unknown", "--accept-source-agreements", "--disable-interactivity")
	list := parseWingetTable(out)
	if err != nil && len(list) == 0 && !strings.Contains(out, "---") {
		return nil, errors.New("winget: " + lastLine(out))
	}
	return list, nil
}

var wingetIDRe = regexp.MustCompile(`^[\w.+-]+$`)

// parseWingetTable reads the table winget prints. Column headers are
// localized but are single words, and the dashes line follows them; columns
// are found by the positions of the header words.
func parseWingetTable(out string) []Upgrade {
	lines := strings.Split(strings.ReplaceAll(out, "\r\n", "\n"), "\n")
	for i := range lines {
		if k := strings.LastIndex(lines[i], "\r"); k >= 0 { // spinner and progress
			lines[i] = lines[i][k+1:]
		}
	}
	res := []Upgrade{}
	for i := 1; i < len(lines); i++ {
		dash := strings.TrimSpace(lines[i])
		if len(dash) < 10 || strings.Trim(dash, "-") != "" {
			continue
		}
		head := []rune(lines[i-1])
		var starts []int
		for k, r := range head {
			if !unicode.IsSpace(r) && (k == 0 || unicode.IsSpace(head[k-1])) {
				starts = append(starts, k)
			}
		}
		if len(starts) < 4 {
			continue
		}
		for _, l := range lines[i+1:] {
			if strings.TrimSpace(l) == "" {
				break
			}
			rs := []rune(l)
			if len(rs) < starts[len(starts)-1] {
				break // the summary line under the table
			}
			col := func(n int) string {
				if n >= len(starts) || starts[n] >= len(rs) {
					return ""
				}
				end := len(rs)
				if n+1 < len(starts) && starts[n+1] < end {
					end = starts[n+1]
				}
				return strings.TrimSpace(string(rs[starts[n]:end]))
			}
			u := Upgrade{Name: col(0), ID: col(1), Version: col(2), Available: col(3)}
			if len(starts) > 4 {
				u.Source = col(4)
			}
			if u.ID == "" || !wingetIDRe.MatchString(u.ID) || u.Available == "" {
				continue // cut by winget ("…") or not a row
			}
			res = append(res, u)
		}
		break
	}
	return res
}

func runUpgrades(j *Job, ids []string) (any, error) {
	w := wingetPath()
	if w == "" {
		return nil, errors.New("winget не установлен")
	}
	type item struct {
		ID string `json:"id"`
		OK bool   `json:"ok"`
	}
	var res []item
	j.Total.Store(int64(len(ids)))
	for _, id := range ids {
		if j.Cancelled() {
			break
		}
		if !wingetIDRe.MatchString(id) {
			continue
		}
		j.SetPhase("Обновление " + id)
		start := time.Now()
		err := runLogged(j, w, "upgrade", "--id", id, "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity")
		j.Log(id + ": " + map[bool]string{true: "обновлено", false: "не удалось"}[err == nil] + " за " + time.Since(start).Round(time.Second).String())
		res = append(res, item{ID: id, OK: err == nil})
		j.Done.Add(1)
	}
	programsMu.Lock()
	programsCache = nil
	programsMu.Unlock()
	return res, nil
}

// decodeLine turns a console line into text: some system tools (sfc) write
// UTF-16 when their output is redirected.
func decodeLine(b []byte, decode func([]byte) string) string {
	if len(b) > 1 && b[0] == 0 {
		b = b[1:] // the high byte of the previous line's '\n'
	}
	// 8-bit console text never contains NUL; UTF-16 has it in every ASCII
	// character (spaces, digits, punctuation)
	if len(b) >= 2 && bytes.IndexByte(b, 0) >= 0 {
		u := make([]rune, 0, len(b)/2)
		for i := 0; i+1 < len(b); i += 2 {
			r := rune(b[i]) | rune(b[i+1])<<8
			if r != 0 {
				u = append(u, r)
			}
		}
		return string(u)
	}
	s := decode(b)
	if !utf8.ValidString(s) {
		s = strings.ToValidUTF8(s, "")
	}
	return strings.ReplaceAll(s, "\x00", "")
}

var percentRe = regexp.MustCompile(`(\d{1,3})(?:[.,]\d+)?\s?%`)

// progressOf finds the last percentage in a line of a console tool (sfc,
// DISM, chkdsk, defrag): their wording is localized, the number is not.
func progressOf(s string) (int, bool) {
	m := percentRe.FindAllStringSubmatch(s, -1)
	if m == nil {
		return 0, false
	}
	v := 0
	for _, c := range m[len(m)-1][1] {
		v = v*10 + int(c-'0')
	}
	if v > 100 {
		return 0, false
	}
	return v, true
}
