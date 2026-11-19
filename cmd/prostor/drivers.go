package main

// Old driver packages in the Windows driver store. Every driver update leaves
// the previous package behind; Prostor keeps the package devices use now and
// the newest previous one of each driver, so a rollback stays possible.
// Packages are removed with "pnputil /delete-driver" without /force: Windows
// refuses to remove a package that is still in use.

import (
	"errors"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"prostor/internal/winapi"
)

type DriverPkg struct {
	Inf      string `json:"inf"`      // oem12.inf
	Original string `json:"original"` // nv_dispi.inf
	Provider string `json:"provider"`
	Class    string `json:"class"`
	Version  string `json:"version"`
	Date     string `json:"date"`
	Size     int64  `json:"size"`
	InUse    bool   `json:"inUse"`
	Old      bool   `json:"old"` // safe to remove
	Why      string `json:"why"`
}

type DriverReport struct {
	Packages []DriverPkg `json:"packages"`
	Old      int         `json:"old"`
	OldSize  int64       `json:"oldSize"`
}

var lastDrivers struct {
	sync.Mutex
	rep *DriverReport
}

var oemInfRe = regexp.MustCompile(`(?i)^oem\d+\.inf$`)

func listDrivers(j *Job) (any, error) {
	if !winapi.IsWindows {
		return &DriverReport{Packages: []DriverPkg{}}, nil
	}
	if !isAdmin {
		return nil, errors.New("нужны права администратора")
	}
	j.SetPhase("Чтение хранилища драйверов")
	var raw []struct {
		Driver, OriginalFileName, ProviderName, ClassName, Version, Date string
		Used                                                             bool
	}
	script := `$used = @{}
Get-CimInstance Win32_PnPSignedDriver | ForEach-Object { if ($_.InfName) { $used[$_.InfName.ToLower()] = $true } }
$list = Get-WindowsDriver -Online | ForEach-Object {
  [pscustomobject]@{ Driver=[string]$_.Driver; OriginalFileName=[string]$_.OriginalFileName; ProviderName=[string]$_.ProviderName;
    ClassName=[string]$_.ClassName; Version=[string]$_.Version; Date=$_.Date.ToString('yyyy-MM-dd'); Used=[bool]$used[$_.Driver.ToLower()] } }
ConvertTo-Json -InputObject @($list) -Compress | Out-File -Encoding utf8 $out`
	if err := psJSON(script, &raw); err != nil {
		return nil, err
	}
	j.SetPhase("Подсчёт размеров")
	j.Total.Store(int64(len(raw)))
	var pk []DriverPkg
	for _, r := range raw {
		j.Done.Add(1)
		if !oemInfRe.MatchString(r.Driver) {
			continue
		}
		p := DriverPkg{Inf: lower(r.Driver), Original: filepath.Base(r.OriginalFileName), Provider: r.ProviderName, Class: r.ClassName,
			Version: r.Version, Date: r.Date, InUse: r.Used}
		if r.OriginalFileName != "" {
			p.Size, _ = dirSize(filepath.Dir(r.OriginalFileName), j.Cancelled)
		}
		pk = append(pk, p)
	}
	rep := &DriverReport{Packages: markOldDrivers(pk)}
	for _, p := range rep.Packages {
		if p.Old {
			rep.Old++
			rep.OldSize += p.Size
		}
	}
	return rep, nil
}

// markOldDrivers groups packages of one driver (same .inf name, provider and
// class) and marks all but the one in use and the newest other version.
func markOldDrivers(pk []DriverPkg) []DriverPkg {
	groups := map[string][]int{}
	for i, p := range pk {
		k := lower(p.Original + "|" + p.Provider + "|" + p.Class)
		groups[k] = append(groups[k], i)
	}
	for _, idx := range groups {
		sort.Slice(idx, func(a, b int) bool { return newerDriver(pk[idx[a]], pk[idx[b]]) })
		kept := 0
		for _, i := range idx {
			if pk[i].InUse {
				pk[i].Why = "используется устройством"
				continue
			}
			// the newest unused version is a rollback point (or the driver of
			// a device that is unplugged now)
			if kept < 1 {
				kept++
				pk[i].Why = "предыдущая версия, оставлена для отката"
				if len(idx) == 1 {
					pk[i].Why = "единственная версия, устройство может быть отключено"
				}
				continue
			}
			pk[i].Old = true
			pk[i].Why = "устаревшая версия"
		}
	}
	sort.SliceStable(pk, func(a, b int) bool {
		if pk[a].Old != pk[b].Old {
			return pk[a].Old
		}
		return pk[a].Size > pk[b].Size
	})
	return pk
}

func newerDriver(a, b DriverPkg) bool {
	if c := compareVersions(a.Version, b.Version); c != 0 {
		return c > 0
	}
	return a.Date > b.Date
}

func compareVersions(a, b string) int {
	pa, pb := strings.Split(a, "."), strings.Split(b, ".")
	for i := 0; i < len(pa) || i < len(pb); i++ {
		var x, y int
		if i < len(pa) {
			x, _ = strconv.Atoi(pa[i])
		}
		if i < len(pb) {
			y, _ = strconv.Atoi(pb[i])
		}
		if x != y {
			if x > y {
				return 1
			}
			return -1
		}
	}
	return 0
}

// removeDrivers deletes the chosen packages; only ones the last listing
// marked old are accepted.
func removeDrivers(j *Job, infs []string, last *DriverReport) (any, error) {
	if !isAdmin {
		return nil, errors.New("нужны права администратора")
	}
	if last == nil {
		return nil, errors.New("сначала найдите старые драйверы")
	}
	ok := map[string]DriverPkg{}
	for _, p := range last.Packages {
		if p.Old {
			ok[p.Inf] = p
		}
	}
	type result struct {
		Removed int      `json:"removed"`
		Freed   int64    `json:"freed"`
		Failed  []string `json:"failed"`
	}
	res := result{Failed: []string{}}
	j.Total.Store(int64(len(infs)))
	for _, inf := range infs {
		if j.Cancelled() {
			break
		}
		j.Done.Add(1)
		p, good := ok[lower(inf)]
		if !good {
			continue
		}
		j.SetCurrent(p.Original + " " + p.Version)
		if out, err := runQuiet("pnputil.exe", "/delete-driver", p.Inf); err != nil {
			res.Failed = append(res.Failed, p.Original+" "+p.Version+": "+lastLine(out))
			continue
		}
		res.Removed++
		res.Freed += p.Size
		j.Log("Удалён " + p.Inf + " (" + p.Original + " " + p.Version + ")")
	}
	if res.Freed > 0 {
		addHistory(HistoryEntry{Time: time.Now(), Mode: "tool", Freed: res.Freed, Items: []HistoryItem{{Name: "Старые драйверы", Freed: res.Freed}}})
	}
	return res, nil
}

func lastLine(s string) string {
	lines := strings.Split(strings.TrimSpace(strings.ReplaceAll(s, "\r", "")), "\n")
	for i := len(lines) - 1; i >= 0; i-- {
		if t := strings.TrimSpace(lines[i]); t != "" {
			return t
		}
	}
	return ""
}
