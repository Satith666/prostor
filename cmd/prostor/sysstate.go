package main

// The "System" page: disk health (SMART, SSD wear, temperature, TRIM) and the
// space Windows itself takes: shadow copies, reserved storage, hibernation,
// page file, CompactOS and the virtual disks of WSL and Docker.

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"

	"prostor/internal/winapi"
)

type DiskHealth struct {
	Name     string `json:"name"`
	Media    string `json:"media"` // SSD | HDD | ""
	Bus      string `json:"bus"`
	Size     int64  `json:"size"`
	Health   string `json:"health"` // Healthy | Warning | Unhealthy | ""
	Wear     int    `json:"wear"`   // % of the SSD's rated life used, -1 unknown
	Temp     int    `json:"temp"`   // °C, 0 unknown
	TempMax  int    `json:"tempMax"`
	Hours    int64  `json:"hours"` // power-on hours
	ReadErr  int64  `json:"readErr"`
	WriteErr int64  `json:"writeErr"`
}

type ShadowInfo struct {
	Drive     string `json:"drive"`
	Used      int64  `json:"used"`
	Allocated int64  `json:"allocated"`
	Max       int64  `json:"max"` // -1: no limit
	Count     int    `json:"count"`
}

type VDisk struct {
	Path   string `json:"path"`
	Kind   string `json:"kind"` // wsl | docker
	Name   string `json:"name"`
	Size   int64  `json:"size"`
	Distro string `json:"distro,omitempty"`
}

type SystemState struct {
	Admin    bool         `json:"admin"`
	Disks    []DiskHealth `json:"disks"`
	Trim     string       `json:"trim"` // on | off | ""
	Shadows  []ShadowInfo `json:"shadows"`
	Reserved struct {
		Known   bool  `json:"known"`
		Enabled bool  `json:"enabled"`
		Size    int64 `json:"size"`
	} `json:"reserved"`
	Hiber     hiberInfo `json:"hiber"`
	Pagefile  int64     `json:"pagefile"` // pagefile.sys + swapfile.sys on disk
	PageAuto  bool      `json:"pageAuto"`
	PagePeak  int64     `json:"pagePeak"`
	CompactOS string    `json:"compactOS"` // on | off | ""
	VDisks    []VDisk   `json:"vdisks"`
	Errors    []string  `json:"errors,omitempty"`
}

func systemState() *SystemState {
	st := &SystemState{Admin: isAdmin, Disks: []DiskHealth{}, Shadows: []ShadowInfo{}, VDisks: findVDisks()}
	st.Hiber = hibernationInfo()
	st.CompactOS = compactOSState()
	sd := sysDriveRoot()
	for _, f := range []string{"pagefile.sys", "swapfile.sys"} {
		if fi, err := os.Stat(filepath.Join(sd, f)); err == nil {
			st.Pagefile += fi.Size()
		}
	}
	if !winapi.IsWindows {
		return st
	}
	var raw struct {
		Disks []struct {
			Name, Media, Bus, Health       string
			Size, Hours, ReadErr, WriteErr int64
			Wear, Temp, TempMax            int
		}
		Shadows []struct {
			Drive                string
			Used, Allocated, Max int64
			Count                int
		}
		Reserved string
		PageAuto bool
		PagePeak int64
	}
	script := `$r = [ordered]@{ Disks=@(); Shadows=@(); Reserved=''; PageAuto=$false; PagePeak=0 }
$r.Disks = @(Get-PhysicalDisk | ForEach-Object {
  $c = $_ | Get-StorageReliabilityCounter
  $wear = -1; $temp = 0; $tmax = 0; $hours = 0; $re = 0; $we = 0
  if ($c) {
    if ($c.Wear -ne $null) { $wear = [int]$c.Wear }
    if ($c.Temperature) { $temp = [int]$c.Temperature }
    if ($c.TemperatureMax) { $tmax = [int]$c.TemperatureMax }
    if ($c.PowerOnHours) { $hours = [int64]$c.PowerOnHours }
    if ($c.ReadErrorsUncorrected) { $re = [int64]$c.ReadErrorsUncorrected }
    if ($c.WriteErrorsUncorrected) { $we = [int64]$c.WriteErrorsUncorrected }
  }
  [pscustomobject]@{ Name=[string]$_.FriendlyName; Media=[string]$_.MediaType; Bus=[string]$_.BusType; Health=[string]$_.HealthStatus;
    Size=[int64]$_.Size; Hours=$hours; ReadErr=$re; WriteErr=$we; Wear=$wear; Temp=$temp; TempMax=$tmax } })
$vols = @{}
Get-CimInstance Win32_Volume | ForEach-Object { $vols[$_.DeviceID] = $_.DriveLetter }
$copies = @(Get-CimInstance Win32_ShadowCopy)
$r.Shadows = @(Get-CimInstance Win32_ShadowStorage | ForEach-Object {
  $id = $_.Volume.DeviceID
  [pscustomobject]@{ Drive=[string]$vols[$id]; Used=[int64]$_.UsedSpace; Allocated=[int64]$_.AllocatedSpace; Max=[int64]$_.MaxSpace;
    Count=@($copies | Where-Object { $_.VolumeName -eq $id }).Count } })
$cs = Get-CimInstance Win32_ComputerSystem
$r.PageAuto = [bool]$cs.AutomaticManagedPagefile
$r.PagePeak = [int64](@(Get-CimInstance Win32_PageFileUsage | Measure-Object -Property PeakUsage -Sum).Sum) * 1MB
try { $r.Reserved = [string](Get-WindowsReservedStorageState -ErrorAction Stop).ReservedStorageState } catch {}
ConvertTo-Json -InputObject $r -Depth 4 -Compress | Out-File -Encoding utf8 $out`
	if err := psJSON(script, &raw); err != nil {
		st.Errors = append(st.Errors, err.Error())
	}
	for _, d := range raw.Disks {
		h := DiskHealth{Name: strings.TrimSpace(d.Name), Media: d.Media, Bus: d.Bus, Size: d.Size, Health: d.Health, Wear: d.Wear,
			Temp: d.Temp, TempMax: d.TempMax, Hours: d.Hours, ReadErr: d.ReadErr, WriteErr: d.WriteErr}
		if h.Media != "SSD" && h.Media != "HDD" {
			h.Media = ""
		}
		if h.Wear > 100 || h.Wear < -1 {
			h.Wear = -1
		}
		if h.Temp > 150 || h.Temp < 0 {
			h.Temp = 0
		}
		st.Disks = append(st.Disks, h)
	}
	for _, s := range raw.Shadows {
		st.Shadows = append(st.Shadows, ShadowInfo{Drive: s.Drive, Used: s.Used, Allocated: s.Allocated, Max: s.Max, Count: s.Count})
	}
	st.PageAuto, st.PagePeak = raw.PageAuto, raw.PagePeak
	switch raw.Reserved {
	case "Enabled":
		st.Reserved.Known, st.Reserved.Enabled = true, true
	case "Disabled":
		st.Reserved.Known = true
	}
	if st.Reserved.Enabled {
		st.Reserved.Size = reservedSize()
	}
	st.Trim = trimState()
	return st
}

func sysDriveRoot() string {
	sd := os.Getenv("SystemDrive")
	if sd == "" {
		sd = "C:"
	}
	return sd + `\`
}

// trimState reads "DisableDeleteNotify = 0|1" for NTFS; the text around the
// number is localized, the name of the setting is not.
func trimState() string {
	out, err := runQuiet("fsutil.exe", "behavior", "query", "DisableDeleteNotify")
	if err != nil {
		return ""
	}
	for _, l := range strings.Split(out, "\n") {
		if !strings.Contains(l, "NTFS") || !strings.Contains(l, "DisableDeleteNotify") {
			continue
		}
		if m := regexp.MustCompile(`=\s*(\d)`).FindStringSubmatch(l); m != nil {
			if m[1] == "0" {
				return "on"
			}
			return "off"
		}
	}
	return ""
}

var hexSizeRe = regexp.MustCompile(`0x([0-9a-fA-F]+)\s*\(`)

// reservedSize: "fsutil storagereserve query C:" lists each reserve with two
// sizes, guaranteed and used ("0x1b6b0d000 (7019.05 MB)"). The labels are
// localized, so the values are taken in order.
func reservedSize() int64 {
	out, err := runQuiet("fsutil.exe", "storagereserve", "query", strings.TrimSuffix(sysDriveRoot(), `\`))
	if err != nil {
		return 0
	}
	var vals []int64
	for _, m := range hexSizeRe.FindAllStringSubmatch(out, -1) {
		v, _ := strconv.ParseInt(m[1], 16, 64)
		vals = append(vals, v)
	}
	var total int64
	for i := 0; i+1 < len(vals); i += 2 {
		total += max(vals[i], vals[i+1])
	}
	return total
}

// compactOSState: with CompactOS on, the programs in System32 are compressed
// by Windows (their reparse tag is WOF). Language independent, needs no rights.
func compactOSState() string {
	if !winapi.IsWindows {
		return ""
	}
	ents, err := winapi.ReadDir(filepath.Join(os.Getenv("SystemRoot"), "System32"))
	if err != nil {
		return ""
	}
	exes, wof := 0, 0
	for _, e := range ents {
		if e.IsDir || !strings.HasSuffix(lower(e.Name), ".exe") {
			continue
		}
		exes++
		if e.Tag == winapi.TagWOF {
			wof++
		}
	}
	if exes == 0 {
		return ""
	}
	if wof*2 > exes {
		return "on"
	}
	return "off"
}

// ---------------------------------------------------------------- WSL and Docker disks

func findVDisks() []VDisk {
	out := []VDisk{}
	seen := map[string]bool{}
	add := func(p, kind, name, distro string) {
		k := lower(p)
		if seen[k] {
			return
		}
		fi, err := os.Stat(p)
		if err != nil || fi.IsDir() {
			return
		}
		seen[k] = true
		out = append(out, VDisk{Path: p, Kind: kind, Name: name, Size: fi.Size(), Distro: distro})
	}
	// WSL distributions are registered per user
	const lxss = `Software\Microsoft\Windows\CurrentVersion\Lxss`
	for _, id := range winapi.RegSubKeys(winapi.HKCU, lxss) {
		base, _ := winapi.RegGetString(winapi.HKCU, lxss+`\`+id, "BasePath")
		name, _ := winapi.RegGetString(winapi.HKCU, lxss+`\`+id, "DistributionName")
		if base == "" {
			continue
		}
		base = strings.TrimPrefix(base, `\\?\`)
		vhd, _ := winapi.RegGetString(winapi.HKCU, lxss+`\`+id, "VhdFileName")
		if vhd == "" {
			vhd = "ext4.vhdx"
		}
		kind := "wsl"
		if strings.HasPrefix(lower(name), "docker-desktop") {
			kind = "docker"
		}
		add(filepath.Join(base, vhd), kind, name, name)
	}
	la := os.Getenv("LOCALAPPDATA")
	if la != "" {
		for _, rel := range []string{`Docker\wsl\disk\docker_data.vhdx`, `Docker\wsl\data\ext4.vhdx`, `Docker\wsl\main\ext4.vhdx`, `Docker\wsl\distro\ext4.vhdx`} {
			add(filepath.Join(la, rel), "docker", "Docker Desktop", "")
		}
	}
	return out
}

func vdiskByPath(p string) *VDisk {
	for _, v := range findVDisks() {
		if strings.EqualFold(v.Path, p) {
			v := v
			return &v
		}
	}
	return nil
}

// compactVDisk shrinks a WSL or Docker disk: free blocks are trimmed inside a
// WSL distribution, WSL is stopped and diskpart compacts the file.
func compactVDisk(j *Job, path string) (any, error) {
	v := vdiskByPath(path)
	if v == nil {
		return nil, errors.New("диск не найден")
	}
	if !isAdmin {
		return nil, errors.New("нужны права администратора")
	}
	before := v.Size
	if v.Distro != "" && v.Kind == "wsl" {
		j.SetPhase("Освобождение пустого места внутри " + v.Distro)
		runLogged(j, "wsl.exe", "-d", v.Distro, "-u", "root", "--", "fstrim", "-a")
	}
	j.SetPhase("Остановка WSL")
	runLogged(j, "wsl.exe", "--shutdown")
	time.Sleep(3 * time.Second)
	j.SetPhase("Сжатие диска " + filepath.Base(v.Path))
	script := filepath.Join(os.TempDir(), "prostor-compact.txt")
	body := fmt.Sprintf("select vdisk file=\"%s\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\nexit\r\n", v.Path)
	if err := os.WriteFile(script, []byte(body), 0o644); err != nil {
		return nil, err
	}
	defer os.Remove(script)
	err := runLogged(j, "diskpart.exe", "/s", script)
	after := before
	if fi, e := os.Stat(v.Path); e == nil {
		after = fi.Size()
	}
	freed := max(before-after, 0)
	if err != nil {
		j.Log("diskpart завершился с ошибкой: " + err.Error())
	} else {
		j.Log("Готово.")
	}
	if freed > 0 {
		addHistory(HistoryEntry{Time: time.Now(), Mode: "tool", Freed: freed, Items: []HistoryItem{{Name: "Сжатие диска " + v.Name, Freed: freed}}})
	}
	return map[string]any{"freed": freed, "ok": err == nil, "before": before, "after": after}, nil
}

// ---------------------------------------------------------------- shadow copies, reserved storage

var driveRe = regexp.MustCompile(`^[A-Za-z]:$`)

// trimShadows deletes all restore points of a drive except the newest one.
func trimShadows(j *Job, drive string) (any, error) {
	if !driveRe.MatchString(drive) {
		return nil, errors.New("неверный диск")
	}
	if !isAdmin {
		return nil, errors.New("нужны права администратора")
	}
	_, freeBefore, _ := winapi.DiskSpace(drive + `\`)
	count := func() int {
		var n int
		script := `$v = (Get-CimInstance Win32_Volume | Where-Object { $_.DriveLetter -eq '` + drive + `' }).DeviceID
@(Get-CimInstance Win32_ShadowCopy | Where-Object { $_.VolumeName -eq $v }).Count | ConvertTo-Json | Out-File -Encoding utf8 $out`
		psJSON(script, &n)
		return n
	}
	n := count()
	j.Total.Store(int64(max(n-1, 0)))
	for i := 0; i < n-1 && !j.Cancelled(); i++ {
		j.SetPhase(fmt.Sprintf("Удаление старой точки восстановления: %d из %d", i+1, n-1))
		if err := runLogged(j, "vssadmin.exe", "delete", "shadows", "/for="+drive, "/oldest", "/quiet"); err != nil {
			j.Log("vssadmin: " + err.Error())
			break
		}
		j.Done.Add(1)
	}
	_, freeAfter, _ := winapi.DiskSpace(drive + `\`)
	freed := max(int64(freeAfter)-int64(freeBefore), 0)
	if freed > 0 {
		addHistory(HistoryEntry{Time: time.Now(), Mode: "tool", Freed: freed, Items: []HistoryItem{{Name: "Старые точки восстановления", Freed: freed}}})
	}
	return map[string]any{"freed": freed, "ok": true, "left": count()}, nil
}

// limitShadows caps the space restore points may take on a drive.
func limitShadows(drive string, percent int) error {
	if !driveRe.MatchString(drive) || percent < 2 || percent > 50 {
		return errors.New("неверные параметры")
	}
	out, err := runQuiet("vssadmin.exe", "resize", "shadowstorage", "/for="+drive, "/on="+drive, fmt.Sprintf("/maxsize=%d%%", percent))
	if err != nil {
		return errors.New(strings.TrimSpace(out))
	}
	return nil
}

func setReservedStorage(enabled bool) error {
	if !isAdmin {
		return errors.New("нужны права администратора")
	}
	state := "Disabled"
	if enabled {
		state = "Enabled"
	}
	out, err := runQuiet("Dism.exe", "/Online", "/Set-ReservedStorageState", "/State:"+state, "/NoRestart")
	if err != nil {
		msg := strings.TrimSpace(out)
		if i := strings.LastIndex(msg, "\n"); i >= 0 && len(msg)-i > 10 {
			msg = strings.TrimSpace(msg[i:])
		}
		return errors.New("Windows не разрешила изменить зарезервированное хранилище, обычно из-за незавершённого обновления. " + msg)
	}
	return nil
}
