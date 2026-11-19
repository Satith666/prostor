package main

// Exporting analysis results to CSV (opens in Excel: semicolons, UTF-8 with
// BOM) or a self-contained HTML page. Files go to the Downloads folder.

import (
	"bytes"
	"errors"
	"fmt"
	"html"
	"os"
	"path/filepath"
	"strings"
	"time"

	"prostor/internal/winapi"
)

func exportDir() string {
	if p := winapi.KnownFolder("downloads"); p != "" {
		return p
	}
	if up := os.Getenv("USERPROFILE"); up != "" {
		return filepath.Join(up, "Downloads")
	}
	return appDataDir()
}

type exportRow []string

func (a *Analysis) exportRows(what string) (title string, head exportRow, rows []exportRow, err error) {
	date := func(t int64) string {
		if t <= 0 {
			return ""
		}
		return time.Unix(t, 0).Format("2006-01-02")
	}
	files := func(list []fileRec) {
		head = exportRow{"Путь", "Размер на диске, байт", "Размер", "Изменён", "Тип"}
		for _, f := range list {
			rows = append(rows, exportRow{f.Path, fmt.Sprint(f.Size), human(f.Size), date(f.MTime), catNames[f.Cat]})
		}
	}
	switch what {
	case "top":
		title = "Самые крупные файлы"
		files(a.topFiles())
	case "old":
		title = "Крупные файлы, не менявшиеся больше года"
		files(a.oldFiles())
	case "folders":
		title = "Папки"
		head = exportRow{"Путь", "Размер на диске, байт", "Размер", "Файлов", "Последнее изменение внутри", "Доля, %"}
		a.treeMu.RLock()
		total := a.root.size
		type item struct {
			n     *dirNode
			depth int
		}
		stack := []item{{a.root, 0}}
		for len(stack) > 0 {
			it := stack[len(stack)-1]
			stack = stack[:len(stack)-1]
			share := 0.0
			if total > 0 {
				share = float64(it.n.size) / float64(total) * 100
			}
			rows = append(rows, exportRow{a.pathOf(it.n), fmt.Sprint(it.n.size), human(it.n.size), fmt.Sprint(it.n.files), date(it.n.newest), fmt.Sprintf("%.1f", share)})
			if it.depth >= 4 {
				continue
			}
			for i := len(it.n.children) - 1; i >= 0; i-- {
				if c := it.n.children[i]; c.size >= 1<<20 {
					stack = append(stack, item{c, it.depth + 1})
				}
			}
		}
		a.treeMu.RUnlock()
	default:
		err = errors.New("неизвестный отчёт")
	}
	return
}

func csvField(s string) string {
	if strings.ContainsAny(s, ";\"\r\n") {
		return `"` + strings.ReplaceAll(s, `"`, `""`) + `"`
	}
	return s
}

// export writes a report and returns its path.
func (a *Analysis) export(what, format string) (string, error) {
	title, head, rows, err := a.exportRows(what)
	if err != nil {
		return "", err
	}
	label := filepath.Base(a.Root)
	if a.isDrive {
		label = "диск " + strings.TrimRight(a.Root, `\/:`)
	}
	stamp := time.Now().Format("2006-01-02 15-04")
	var buf bytes.Buffer
	switch format {
	case "csv":
		buf.WriteString("\uFEFF")
		for _, r := range append([]exportRow{head}, rows...) {
			for i, c := range r {
				if i > 0 {
					buf.WriteByte(';')
				}
				buf.WriteString(csvField(c))
			}
			buf.WriteString("\r\n")
		}
	case "html":
		esc := html.EscapeString
		fmt.Fprintf(&buf, `<!doctype html><html lang="ru"><head><meta charset="utf-8"><title>%s — %s</title>
<style>body{font:14px/1.45 "Segoe UI",system-ui,sans-serif;margin:32px;color:#1b1d22}h1{font-size:22px;margin:0 0 4px}
p{color:#5b606b;margin:0 0 20px}table{border-collapse:collapse;width:100%%}th,td{padding:6px 10px;border-bottom:1px solid #e3e5ea;text-align:left;vertical-align:top}
th{font-weight:600;background:#f4f5f7;position:sticky;top:0}td.n{text-align:right;white-space:nowrap;font-variant-numeric:tabular-nums}td.p{word-break:break-all}</style></head><body>
<h1>%s</h1><p>%s · %s · Prostor %s</p><table><thead><tr>`, esc(title), esc(a.Root), esc(title), esc(a.Root), time.Now().Format("02.01.2006 15:04"), esc(version))
		for i, h := range head {
			if i == 1 {
				continue // bytes: the human-readable column follows
			}
			fmt.Fprintf(&buf, "<th>%s</th>", esc(h))
		}
		buf.WriteString("</tr></thead><tbody>")
		for _, r := range rows {
			buf.WriteString("<tr>")
			for i, c := range r {
				switch {
				case i == 1:
				case i == 0:
					fmt.Fprintf(&buf, `<td class="p">%s</td>`, esc(c))
				case i == 2 || (what == "folders" && (i == 3 || i == 5)):
					fmt.Fprintf(&buf, `<td class="n">%s</td>`, esc(c))
				default:
					fmt.Fprintf(&buf, "<td>%s</td>", esc(c))
				}
			}
			buf.WriteString("</tr>")
		}
		buf.WriteString("</tbody></table></body></html>\n")
	default:
		return "", errors.New("неизвестный формат")
	}
	dir := exportDir()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	name := fmt.Sprintf("Prostor %s %s %s.%s", label, map[string]string{"top": "крупные файлы", "old": "старые файлы", "folders": "папки"}[what], stamp, format)
	p := filepath.Join(dir, name)
	return p, os.WriteFile(p, buf.Bytes(), 0o644)
}
