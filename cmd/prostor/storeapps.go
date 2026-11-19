package main

// Microsoft Store apps (packaged apps) of the current user: list with sizes,
// removal through PowerShell's Appx cmdlets. Frameworks, system and
// non-removable packages are not offered.

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"prostor/internal/winapi"
)

// psJSON runs a PowerShell script that writes JSON to $out and decodes it.
func psJSON(script string, v any) error {
	f, err := os.CreateTemp("", "prostor-ps-*.json")
	if err != nil {
		return err
	}
	path := f.Name()
	f.Close()
	defer os.Remove(path)
	full := "$ErrorActionPreference='SilentlyContinue'\n$out='" + strings.ReplaceAll(path, "'", "''") + "'\n" + script
	if out, err := powershell(full); err != nil {
		return fmt.Errorf("PowerShell: %v %s", err, strings.TrimSpace(out))
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	b = bytes.TrimPrefix(b, []byte("\xEF\xBB\xBF"))
	if len(bytes.TrimSpace(b)) == 0 {
		return nil
	}
	return json.Unmarshal(b, v)
}

type StoreApp struct {
	Name      string `json:"name"`      // what the Start menu shows
	ID        string `json:"id"`        // package name, e.g. Microsoft.ZuneMusic
	Full      string `json:"full"`      // PackageFullName
	Family    string `json:"family"`    // PackageFamilyName
	Publisher string `json:"publisher"` // CN of the signer
	Version   string `json:"version"`
	Location  string `json:"location"`
	Size      int64  `json:"size"` // program files
	Data      int64  `json:"data"` // the app's data in LocalAppData\Packages
	Store     bool   `json:"store"`
}

var cnRe = regexp.MustCompile(`CN=([^,]+)`)

func listStoreApps() ([]StoreApp, error) {
	if !winapi.IsWindows {
		return []StoreApp{}, nil
	}
	var raw []struct {
		Name, PackageFullName, PackageFamilyName, Publisher, Version, InstallLocation, SignatureKind string
		IsFramework, NonRemovable                                                                  bool
		Display                                                                                    string
	}
	script := `$start = @{}
Get-StartApps | ForEach-Object { $f = ($_.AppID -split '!')[0]; if (-not $start.ContainsKey($f)) { $start[$f] = $_.Name } }
$list = Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' } | ForEach-Object {
  [pscustomobject]@{ Name=$_.Name; PackageFullName=$_.PackageFullName; PackageFamilyName=$_.PackageFamilyName; Publisher=$_.Publisher;
    Version=[string]$_.Version; InstallLocation=$_.InstallLocation; SignatureKind=[string]$_.SignatureKind; IsFramework=$_.IsFramework;
    NonRemovable=$_.NonRemovable; Display=$start[$_.PackageFamilyName] } }
ConvertTo-Json -InputObject @($list) -Depth 3 -Compress | Out-File -Encoding utf8 $out`
	if err := psJSON(script, &raw); err != nil {
		return nil, err
	}
	la := os.Getenv("LOCALAPPDATA")
	var out []StoreApp
	for _, r := range raw {
		if r.Display == "" {
			continue // no Start menu entry: a background component, not an app people use
		}
		a := StoreApp{Name: r.Display, ID: r.Name, Full: r.PackageFullName, Family: r.PackageFamilyName,
			Version: r.Version, Location: r.InstallLocation, Store: r.SignatureKind == "Store"}
		if m := cnRe.FindStringSubmatch(r.Publisher); m != nil {
			a.Publisher = strings.TrimSpace(m[1])
		}
		if a.Location != "" {
			a.Size, _ = dirSize(a.Location, nil)
		}
		if la != "" && a.Family != "" {
			a.Data, _ = dirSize(filepath.Join(la, "Packages", a.Family), nil)
		}
		out = append(out, a)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Size+out[i].Data > out[j].Size+out[j].Data })
	return out, nil
}

var pkgNameRe = regexp.MustCompile(`^[A-Za-z0-9._~-]+$`)

// removeStoreApp removes a package for the current user; allUsers also stops
// Windows from installing it for new accounts (needs administrator rights).
func removeStoreApp(full string, provisioned bool) error {
	if !winapi.IsWindows {
		return errors.New("только в Windows")
	}
	if !pkgNameRe.MatchString(full) {
		return errors.New("неверное имя пакета")
	}
	script := "Remove-AppxPackage -Package '" + full + "' -ErrorAction Stop\n"
	if provisioned {
		name := strings.SplitN(full, "_", 2)[0]
		script += "Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq '" + name + "' } | Remove-AppxProvisionedPackage -Online | Out-Null\n"
	}
	script += "'ok' | Out-File -Encoding utf8 $out"
	var res string
	if err := psJSON(script, &res); err != nil {
		return err
	}
	return nil
}
