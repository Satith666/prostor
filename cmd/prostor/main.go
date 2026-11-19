// Prostor — Windows cleanup and disk analysis tool.
package main

import (
	"encoding/json"
	"io"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"

	"prostor/internal/webui"
	"prostor/internal/winapi"
	"time"
)

var (
	version = "1.3.0"
	exePath string
	isAdmin bool
	ui      *webui.Server
)

const windowTitle = "Prostor"

func hasArg(name string) bool {
	for _, a := range os.Args[1:] {
		if strings.EqualFold(a, name) {
			return true
		}
	}
	return false
}

func main() {
	exePath, _ = os.Executable()
	longEnv()
	initForbidden()
	loadStore()
	isAdmin = winapi.IsAdmin() && !forceHelper()
	openLog()
	log.Printf("Prostor %s: start admin=%v args=%q", version, isAdmin, os.Args[1:])

	switch {
	case hasArg("--uninstall"):
		runUninstall(hasArg("--quiet"))
		return
	case hasArg("--autoclean"):
		purgeQuarantine("", false)
		runAutoClean()
		return
	}

	if hasArg("--helper") {
		os.Exit(runHelper())
	}
	if hasArg("--bait-test") {
		os.Exit(runBaitTest())
	}
	if hasArg("--webview2-probe") {
		os.Exit(runWebView2Probe())
	}
	if hasArg("--selftest") {
		os.Exit(runSelftest())
	}

	if winapi.FocusWindow(windowTitle) {
		return
	}

	go purgeQuarantine("", false) // batches past their term

	if isAdmin && winapi.EnableBackupPrivilege() {
		log.Printf("backup privilege enabled (analysis can list protected folders)")
	}

	profile := "webview"
	if isAdmin {
		profile = "webview-admin"
	}
	var err error
	ui, err = webui.New(webui.Options{
		Title:      windowTitle,
		Width:      1280,
		Height:     860,
		ProfileDir: filepath.Join(appDataDir(), profile),
		Handler:    withHelper(routes()),
		IsBusy:     busy,
		Native:     nativeWindow,
		BeforeWindow: func() {
			// the window itself never runs elevated: system work goes to the helper
			if (winapi.IsWindows || forceHelper()) && !isAdmin && !hasArg("--no-elevate") && getSettings().AutoElevate {
				if err := helper.start(ui.URL, 2*time.Minute); err != nil {
					log.Printf("helper not started: %v — limited mode", err)
				}
			}
		},
	})
	if err != nil {
		log.Printf("ui init failed: %v", err)
		winapi.MessageBox("Prostor", "Не удалось запустить интерфейс: "+err.Error(), winapi.MB_ICONERROR)
		return
	}
	err = ui.Run()
	helper.stop()
	log.Printf("exit (%v)", err)
}

// openLog keeps a small diagnostic log in %LOCALAPPDATA%\Prostor\prostor.log.
var logFile *os.File

func openLog() {
	p := filepath.Join(appDataDir(), "prostor.log")
	if st, err := os.Stat(p); err == nil && st.Size() > 512<<10 {
		os.Remove(p)
	}
	if f, err := os.OpenFile(p, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644); err == nil {
		logFile = f
		log.SetOutput(f)
		log.SetFlags(log.Ldate | log.Ltime)
	}
}

func runAutoClean() {
	s := getSettings()
	ids := selectedIDs(s, isAdmin, true)
	j := &Job{Kind: "clean", cancel: make(chan struct{})}
	j.current.Store("")
	cleanTargets(j, ids, s, isAdmin, "auto")
}

// ---------------------------------------------------------------- uninstall

type installInfo struct {
	Version   string   `json:"version"`
	Dir       string   `json:"dir"`
	Scope     string   `json:"scope"` // machine | user
	Shortcuts []string `json:"shortcuts"`
}

const uninstallKey = `Software\Microsoft\Windows\CurrentVersion\Uninstall\Prostor`

func runUninstall(quiet bool) {
	dir := filepath.Dir(exePath)
	var info installInfo
	if b, err := os.ReadFile(filepath.Join(dir, "install.json")); err == nil {
		json.Unmarshal(b, &info)
	}
	if info.Scope == "machine" && !isAdmin && !hasArg("--elevated") {
		args := "--uninstall --elevated"
		if quiet {
			args += " --quiet"
		}
		if err := winapi.RunElevated(exePath, args); err != nil && !quiet {
			winapi.MessageBox("Удаление Prostor", "Для удаления нужны права администратора.", winapi.MB_ICONWARNING)
		}
		return
	}
	if !quiet {
		r := winapi.MessageBox("Удаление Prostor",
			"Удалить Prostor с этого компьютера?\n\nНастройки, история очисток и расписание автоочистки тоже будут удалены.",
			winapi.MB_YESNO|winapi.MB_ICONQUESTION)
		if r != winapi.IDYES {
			return
		}
	}
	if !winapi.IsWindows {
		return
	}
	// stop other running copies and their windows
	me := os.Getpid()
	for _, p := range winapi.Processes() {
		if strings.EqualFold(p.Name, filepath.Base(exePath)) && int(p.PID) != me {
			c := exec.Command("taskkill", "/PID", strconv.Itoa(int(p.PID)), "/F")
			winapi.HideWindow(c)
			c.Run()
		}
	}
	powershell(`Get-CimInstance Win32_Process -Filter "Name='msedge.exe' OR Name='chrome.exe'" | Where-Object { $_.CommandLine -like '*\Prostor\webview*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }`)
	runQuiet("schtasks.exe", "/Delete", "/F", "/TN", taskName)
	for _, sc := range info.Shortcuts {
		if strings.HasSuffix(lower(sc), ".lnk") {
			os.Remove(sc)
		}
	}
	winapi.RegDeleteTree(winapi.HKLM, uninstallKey)
	winapi.RegDeleteTree(winapi.HKCU, uninstallKey)
	if logFile != nil { // release the log so the data folder can be removed
		log.SetOutput(io.Discard)
		logFile.Close()
	}
	os.RemoveAll(appDataDir())

	if !quiet {
		winapi.MessageBox("Удаление Prostor", "Prostor удалён. Спасибо, что пользовались!", winapi.MB_ICONINFORMATION)
	}
	// Delete our own files after this process exits. Only known files are
	// removed; the folder goes away only if it is then empty.
	if _, err := os.Stat(filepath.Join(dir, "install.json")); err == nil {
		line := `cmd.exe /c ping -n 3 127.0.0.1 > nul & del /f /q "` + exePath + `" & del /f /q "` +
			filepath.Join(dir, "install.json") + `" & del /f /q "` + filepath.Join(dir, "Проверка Prostor.cmd") + `" & rd "` + dir + `"`
		c := exec.Command("cmd.exe")
		winapi.SetCmdLine(c, line)
		winapi.HideWindow(c)
		c.Dir = os.TempDir()
		c.Start()
	}
}
