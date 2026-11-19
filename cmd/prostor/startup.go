package main

// Everything that starts with Windows, beyond the Run keys and Startup
// folders: scheduled tasks with a logon or boot trigger, automatic services
// of other vendors and startup tasks of Microsoft Store apps. Plus the boot
// time Windows measures itself (Diagnostics-Performance log) and "delayed
// start": a program is launched by a task a minute and a half after logon
// instead of at the moment of logon.

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"

	"prostor/internal/winapi"
)

type BootInfo struct {
	Known    bool    `json:"known"`
	Last     float64 `json:"last"`    // seconds, the latest boot
	Main     float64 `json:"main"`    // until the desktop
	Post     float64 `json:"post"`    // desktop until the system is idle
	Typical  float64 `json:"typical"` // median of the recent boots
	Count    int     `json:"count"`
	LastTime string  `json:"lastTime"` // when
}

type StartupReport struct {
	Items []StartupItem `json:"items"`
	Boot  BootInfo      `json:"boot"`
	Admin bool          `json:"admin"`
}

const delayPath = `\Prostor\Delayed\`

var appxStartupBase = `Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData`

func startupReport() *StartupReport {
	rep := &StartupReport{Items: listStartup(), Admin: isAdmin}
	for i := range rep.Items {
		rep.Items[i].CanDelay = true
	}
	if !winapi.IsWindows {
		return rep
	}
	var ps struct {
		Tasks []struct {
			Path, Name, State, Exe, Args, Author string
			Boot                                 bool
		}
		Delayed []struct{ Name, Desc string }
		Boot    []struct {
			Boot, Main, Post int64
			Time             string
		}
		Slow []struct {
			Name string
			Degr int64
		}
	}
	script := `$r = [ordered]@{ Tasks=@(); Delayed=@(); Boot=@(); Slow=@() }
$r.Tasks = @(Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\Microsoft\*' -and $_.TaskPath -notlike '\Prostor\*' } | ForEach-Object {
  $t = $_; $kinds = @($t.Triggers | ForEach-Object { $_.CimClass.CimClassName })
  if ($kinds -contains 'MSFT_TaskLogonTrigger' -or $kinds -contains 'MSFT_TaskBootTrigger') {
    $a = @($t.Actions | Where-Object { $_.Execute })[0]
    [pscustomobject]@{ Path=$t.TaskPath + $t.TaskName; Name=[string]$t.TaskName; State=[string]$t.State; Exe=[string]$a.Execute; Args=[string]$a.Arguments;
      Author=[string]$t.Author; Boot=($kinds -contains 'MSFT_TaskBootTrigger') } } })
$r.Delayed = @(Get-ScheduledTask -TaskPath '` + delayPath + `' | ForEach-Object { [pscustomobject]@{ Name=[string]$_.TaskName; Desc=[string]$_.Description } })
try {
  $r.Boot = @(Get-WinEvent -FilterHashtable @{ LogName='Microsoft-Windows-Diagnostics-Performance/Operational'; Id=100 } -MaxEvents 15 -ErrorAction Stop | ForEach-Object {
    $d = @{}; ([xml]$_.ToXml()).Event.EventData.Data | ForEach-Object { $d[$_.Name] = $_.'#text' }
    [pscustomobject]@{ Boot=[int64]$d['BootTime']; Main=[int64]$d['MainPathBootTime']; Post=[int64]$d['BootPostBootTime']; Time=$_.TimeCreated.ToString('s') } })
  $r.Slow = @(Get-WinEvent -FilterHashtable @{ LogName='Microsoft-Windows-Diagnostics-Performance/Operational'; Id=101 } -MaxEvents 60 -ErrorAction Stop | ForEach-Object {
    $d = @{}; ([xml]$_.ToXml()).Event.EventData.Data | ForEach-Object { $d[$_.Name] = $_.'#text' }
    [pscustomobject]@{ Name=[string]$d['Name']; Degr=[int64]$d['DegradationTime'] } })
} catch {}
ConvertTo-Json -InputObject $r -Depth 4 -Compress | Out-File -Encoding utf8 $out`
	psJSON(script, &ps)

	for _, t := range ps.Tasks {
		exe := winapi.ExpandEnv(strings.Trim(t.Exe, `"`))
		_, err := os.Stat(exe)
		src := "Планировщик заданий: при входе"
		if t.Boot {
			src = "Планировщик заданий: при включении"
		}
		rep.Items = append(rep.Items, StartupItem{ID: "task|" + t.Path, Kind: "task", Name: t.Name, Command: strings.TrimSpace(t.Exe + " " + t.Args),
			Path: exe, Source: src, Scope: "machine", Enabled: t.State != "Disabled", Exists: exe == "" || err == nil, NeedsAdm: true, taskPath: t.Path})
	}
	rep.Items = append(rep.Items, autoServices()...)
	rep.Items = append(rep.Items, appxStartup()...)

	// delayed items: the task's description holds the item ID
	delayed := map[string]bool{}
	for _, d := range ps.Delayed {
		delayed[d.Desc] = true
	}
	for i := range rep.Items {
		if delayed[rep.Items[i].ID] {
			rep.Items[i].Delayed = true
			rep.Items[i].Enabled = true
		}
	}
	// programs Windows found slowing the boot down
	slow := map[string]int64{}
	for _, s := range ps.Slow {
		k := lower(filepath.Base(s.Name))
		slow[k] = max(slow[k], s.Degr)
	}
	for i := range rep.Items {
		if it := &rep.Items[i]; it.Path != "" {
			it.Slow = slow[lower(filepath.Base(it.Path))]
		}
	}
	if len(ps.Boot) > 0 {
		b := ps.Boot[0] // newest first
		rep.Boot = BootInfo{Known: true, Last: float64(b.Boot) / 1000, Main: float64(b.Main) / 1000, Post: float64(b.Post) / 1000, Count: len(ps.Boot), LastTime: b.Time}
		var all []float64
		for _, x := range ps.Boot {
			all = append(all, float64(x.Boot)/1000)
		}
		sort.Float64s(all)
		rep.Boot.Typical = all[len(all)/2]
	}
	sort.SliceStable(rep.Items, func(i, j int) bool { return lower(rep.Items[i].Name) < lower(rep.Items[j].Name) })
	return rep
}

// ---------------------------------------------------------------- services

// services Prostor switched to manual start: they stay in the list
type svcMemo map[string]string // name -> "auto" | "delayed"

var svcMu sync.Mutex

func svcMemoPath() string { return filepath.Join(appDataDir(), "startup-services.json") }

func loadSvcMemo() svcMemo {
	m := svcMemo{}
	if b, err := os.ReadFile(svcMemoPath()); err == nil {
		json.Unmarshal(b, &m)
	}
	return m
}

func autoServices() []StartupItem {
	var out []StartupItem
	memo := loadSvcMemo()
	sysRoot := lower(os.Getenv("SystemRoot"))
	for _, name := range winapi.RegSubKeys(winapi.HKLM, servicesKey) {
		k := servicesKey + `\` + name
		typ, _ := winapi.RegGetDword(winapi.HKLM, k, "Type")
		if typ&0x30 == 0 { // drivers and file systems are not programs
			continue
		}
		start, ok := winapi.RegGetDword(winapi.HKLM, k, "Start")
		if !ok {
			continue
		}
		_, remembered := memo[name]
		if start != 2 && !(start == 3 && remembered) {
			continue
		}
		img, _ := winapi.RegGetString(winapi.HKLM, k, "ImagePath")
		exe, _ := splitCommand(winapi.ExpandEnv(img))
		le := lower(exe)
		if exe == "" || (sysRoot != "" && strings.HasPrefix(le, sysRoot+`\`)) || strings.HasPrefix(le, `\systemroot\`) ||
			strings.Contains(le, `\windows defender\`) || strings.Contains(le, `\microsoft\windows defender`) {
			continue // Windows itself
		}
		disp, _ := winapi.RegGetString(winapi.HKLM, k, "DisplayName")
		if disp == "" || strings.HasPrefix(disp, "@") {
			disp = name
		}
		delayedAuto, _ := winapi.RegGetDword(winapi.HKLM, k, "DelayedAutostart")
		_, err := os.Stat(exe)
		out = append(out, StartupItem{ID: "svc|" + name, Kind: "service", Name: disp, Command: winapi.ExpandEnv(img), Path: exe,
			Source: "Служба", Scope: "machine", Enabled: start == 2, Exists: err == nil, NeedsAdm: true, CanDelay: true,
			Delayed: start == 2 && delayedAuto == 1, svcName: name})
	}
	return out
}

func setService(name string, mode string) error {
	if !isAdmin {
		return errors.New("нужны права администратора")
	}
	sc := map[string]string{"manual": "demand", "auto": "auto", "delayed": "delayed-auto"}[mode]
	if sc == "" {
		return errors.New("неизвестный режим")
	}
	if out, err := runQuiet("sc.exe", "config", name, "start=", sc); err != nil {
		return errors.New(lastLine(out))
	}
	svcMu.Lock()
	defer svcMu.Unlock()
	m := loadSvcMemo()
	if mode == "manual" {
		if _, ok := m[name]; !ok {
			m[name] = "auto"
		}
	} else {
		delete(m, name)
	}
	return writeFileAtomic(svcMemoPath(), m)
}

// ---------------------------------------------------------------- Store apps

func appxStartup() []StartupItem {
	var out []StartupItem
	for _, fam := range winapi.RegSubKeys(winapi.HKCU, appxStartupBase) {
		for _, task := range winapi.RegSubKeys(winapi.HKCU, appxStartupBase+`\`+fam) {
			st, ok := winapi.RegGetDword(winapi.HKCU, appxStartupBase+`\`+fam+`\`+task, "State")
			if !ok || (st != 1 && st != 2 && st != 4) {
				continue
			}
			out = append(out, StartupItem{ID: "appx|" + fam + "|" + task, Kind: "appx", Name: appxName(fam), Command: fam + "!" + task,
				Source: "Приложение Microsoft Store", Scope: "user", Enabled: st == 2 || st == 4, Exists: true, NeedsAdm: st == 4,
				appxKey: appxStartupBase + `\` + fam + `\` + task})
		}
	}
	return out
}

var camelRe = regexp.MustCompile(`([a-z])([A-Z])`)

// appxName makes "SpotifyAB.SpotifyMusic_zpdnekdrzrea0" readable: "Spotify Music".
func appxName(family string) string {
	n, _, _ := strings.Cut(family, "_")
	if i := strings.Index(n, "."); i >= 0 && i < len(n)-1 {
		n = n[i+1:]
	}
	return camelRe.ReplaceAllString(n, "$1 $2")
}

// ---------------------------------------------------------------- changes

func findStartup(id string) (*StartupItem, error) {
	for _, it := range startupReport().Items {
		if it.ID == id {
			it := it
			return &it, nil
		}
	}
	return nil, errors.New("элемент автозагрузки не найден")
}

func setStartup(id string, enabled bool) error {
	it, err := findStartup(id)
	if err != nil {
		return err
	}
	switch it.Kind {
	case "task":
		flag := "/DISABLE"
		if enabled {
			flag = "/ENABLE"
		}
		if out, err := runQuiet("schtasks.exe", "/Change", "/TN", it.taskPath, flag); err != nil {
			return errors.New(lastLine(out))
		}
		return nil
	case "service":
		if enabled {
			return setService(it.svcName, "auto")
		}
		return setService(it.svcName, "manual")
	case "appx":
		v := uint32(1)
		if enabled {
			v = 2
		}
		return winapi.RegSetDword(winapi.HKCU, it.appxKey, "State", v)
	}
	if it.Delayed && !enabled {
		return undelay(it, false)
	}
	return setApproval(it, enabled)
}

// setDelay moves an item to delayed start (or back to start at logon).
func setDelay(id string, delayed bool) error {
	it, err := findStartup(id)
	if err != nil {
		return err
	}
	if it.Kind == "service" {
		if delayed {
			return setService(it.svcName, "delayed")
		}
		return setService(it.svcName, "auto")
	}
	if it.Kind != "run" && it.Kind != "folder" {
		return errors.New("этот элемент нельзя отложить")
	}
	if !delayed {
		return undelay(it, true)
	}
	exe, args := it.Path, ""
	if it.Kind == "run" {
		exe, args = splitCommand(it.Command)
	} else {
		exe, args = filepath.Join(os.Getenv("SystemRoot"), "explorer.exe"), `"`+it.Path+`"`
	}
	q := func(s string) string { return "'" + strings.ReplaceAll(s, "'", "''") + "'" }
	trigger := `$t = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$p = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited`
	if it.Scope == "machine" {
		trigger = `$t = New-ScheduledTaskTrigger -AtLogOn
$p = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited`
	}
	argPart := ""
	if args != "" {
		argPart = " -Argument " + q(args)
	}
	script := `$ErrorActionPreference = 'Stop'
$a = New-ScheduledTaskAction -Execute ` + q(exe) + argPart + `
` + trigger + `
$t.Delay = 'PT90S'
$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskPath ` + q(delayPath) + ` -TaskName ` + q(delayTaskName(it)) + ` -Action $a -Trigger $t -Principal $p -Settings $s -Description ` + q(it.ID) + ` -Force | Out-Null
'ok' | ConvertTo-Json | Out-File -Encoding utf8 $out`
	var ok string
	if err := psJSON(script, &ok); err != nil || ok != "ok" {
		if err == nil {
			err = errors.New("задание не создано")
		}
		return errors.New("не удалось создать отложенный запуск: " + err.Error())
	}
	return setApproval(it, false)
}

func undelay(it *StartupItem, restore bool) error {
	script := `Unregister-ScheduledTask -TaskPath '` + delayPath + `' -TaskName '` + strings.ReplaceAll(delayTaskName(it), "'", "''") + `' -Confirm:$false
'ok' | ConvertTo-Json | Out-File -Encoding utf8 $out`
	var ok string
	psJSON(script, &ok)
	if restore {
		return setApproval(it, true)
	}
	return nil
}

var badTaskChars = regexp.MustCompile(`[\\/:*?"<>|]+`)

func delayTaskName(it *StartupItem) string {
	n := strings.TrimSpace(badTaskChars.ReplaceAllString(it.Name, " "))
	if n == "" {
		n = "item"
	}
	if it.Scope == "machine" {
		n += " (все пользователи)"
	}
	return n
}

// delayRoundTrip (self-test on CI): a Run item goes to delayed start, the
// task appears and the item is disabled at logon; then everything is back.
func delayRoundTrip() (string, string) {
	const name = "ProstorDelayTest"
	const run = `Software\Microsoft\Windows\CurrentVersion\Run`
	cmd := `"` + filepath.Join(os.Getenv("SystemRoot"), "System32", "notepad.exe") + `" /test`
	if err := winapi.RegSetString(winapi.HKCU, run, name, cmd); err != nil {
		return failf("не удалось создать тестовый элемент: %v", err)
	}
	defer winapi.RegDeleteValue(winapi.HKCU, run, name)
	id := "Реестр (пользователь)|" + name
	if err := setDelay(id, true); err != nil {
		return failf("отложить: %v", err)
	}
	it, err := findStartup(id)
	if err != nil {
		return failf("%v", err)
	}
	if !it.Delayed || !it.Enabled || approvalState(winapi.HKCU, "Run", name) {
		undelay(it, true)
		return failf("после «Отложить»: delayed=%v enabled=%v, запуск при входе не отключён", it.Delayed, it.Enabled)
	}
	if err := setDelay(id, false); err != nil {
		return failf("вернуть: %v", err)
	}
	it, _ = findStartup(id)
	if it == nil || it.Delayed || !approvalState(winapi.HKCU, "Run", name) {
		return failf("отложенный запуск не отменился")
	}
	return okf("задание создано и снято, элемент снова запускается при входе")
}
