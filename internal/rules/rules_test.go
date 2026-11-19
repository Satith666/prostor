package rules

import (
	"strings"
	"testing"
)

func TestCheckLoc(t *testing.T) {
	ok := []struct{ path, masks string }{
		{`%LocalAppData%\Vendor\App\Cache`, ""},
		{`%AppData%\Vendor\GPUCache`, ""},
		{`%LocalAppData%\Google\Chrome*\User Data\*\Code Cache`, ""},
		{`%AppData%\Vendor\App`, "*.log"},
		{`%ProgramData%\Vendor\Logs\Old`, ""},
		{`%LocalAppData%\Vendor\App`, "output_log.txt"},
		{`%AppData%\Vendor\App\CacheStorage`, ""},
	}
	for _, c := range ok {
		if err := CheckLoc(c.path, splitMasks(c.masks)); err != nil {
			t.Errorf("%s|%s отклонено: %v", c.path, c.masks, err)
		}
	}
	bad := []struct{ path, masks string }{
		{`%LocalAppData%\Vendor\App`, ""},                      // whole program folder
		{`%LocalAppData%\Temp`, ""},                            // shared folder
		{`%UserProfile%\Documents\Cache`, ""},                  // personal folder
		{`%Documents%\App\Cache`, ""},                          // personal variable
		{`%LocalAppData%\Vendor\User Data\Default`, "Cookies"}, // cookies
		{`%AppData%\Vendor\Local Storage\Cache`, ""},           // site data
		{`%AppData%\Vendor\App`, "*.txt"},                      // not logs
		{`%AppData%\Vendor\CacheApp\UserFiles`, ""},            // "cache" only in a name, not last
		{`%ProgramData%\Package Cache\Logs`, ""},               // installers
		{`%SystemRoot%\WinSxS\Temp`, ""},                       // component store
		{`%LocalAppData%\Packages\X\LocalCache\Roaming`, ""},   // app data of packaged apps
		{`%AppData%\Game\Saves\Cache`, ""},                     // saved games
		{`%ProgramData%\Comodo\Quarantine\Temp`, ""},           // antivirus quarantine
		{`%LocalAppData%`, "*.log"},                            // root of a system folder
	}
	for _, c := range bad {
		if err := CheckLoc(c.path, splitMasks(c.masks)); err == nil {
			t.Errorf("%s|%s пропущено, а должно быть отклонено", c.path, c.masks)
		}
	}
}

func TestConvertWinapp2(t *testing.T) {
	ini := `; Version: 1
[Good App *]
LangSecRef=3021
Detect=HKCU\Software\Good
DetectFile=%ProgramFiles%\Good
FileKey1=%LocalAppData%\Good\Cache|*.*|RECURSE
FileKey2=%LocalAppData%\Good|*.log
FileKey3=%LocalAppData%\Good\User Data|Cookies
ExcludeKey1=FILE|%LocalAppData%\Good\Cache\|keep.db
RegKey1=HKCU\Software\Good\Recent

[Good App Cookies *]
FileKey1=%LocalAppData%\Good\Cache|*.*

[Only Settings *]
FileKey1=%AppData%\Only|*.ini
`
	f, rej, err := ConvertWinapp2(strings.NewReader(ini))
	if err != nil {
		t.Fatal(err)
	}
	if len(f.Rules) != 1 {
		t.Fatalf("правил %d, ожидалось 1: %+v", len(f.Rules), f.Rules)
	}
	r := f.Rules[0]
	if r.ID != "w2-good-app" || r.Cat != "apps" || len(r.Locs) != 2 || len(r.Excludes) != 1 || len(r.Detect) != 1 || len(r.DetectFile) != 2 {
		t.Fatalf("неверное правило: %+v", r)
	}
	if r.Locs[0].Flat || !r.Locs[0].KeepDirs || r.Locs[0].Masks != nil || !r.Locs[1].Flat || r.Locs[1].Masks[0] != "*.log" {
		t.Fatalf("неверные папки: %+v", r.Locs)
	}
	if len(rej) < 3 {
		t.Fatalf("отклонено %d, ожидалось не меньше 3: %+v", len(rej), rej)
	}
}
