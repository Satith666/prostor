package main

// Choosing the window: the program's own WebView2 window when the runtime is
// present and has passed a trial run, otherwise the Edge app window.
//
// The trial run (Prostor.exe --webview2-probe) opens a hidden WebView2 window
// in a separate process. If anything in WebView2 misbehaves, only that
// process fails; the result is remembered per runtime and program version.

import (
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"time"

	"prostor/internal/webui"
	"prostor/internal/webview2"
	"prostor/internal/winapi"
)

type wv2Verdict struct {
	Runtime string `json:"runtime"`
	Program string `json:"program"`
	OK      bool   `json:"ok"`
	Error   string `json:"error,omitempty"`
	Time    string `json:"time"`
}

func wv2CachePath() string { return filepath.Join(appDataDir(), "webview2.json") }

func wv2DataDir() string {
	if isAdmin {
		return filepath.Join(appDataDir(), "WebView2-admin")
	}
	return filepath.Join(appDataDir(), "WebView2")
}

// probeWebView2 runs the trial in a child process.
func probeWebView2(timeout time.Duration) error {
	out := filepath.Join(os.TempDir(), fmt.Sprintf("prostor-wv2-%d.json", os.Getpid()))
	defer os.Remove(out)
	cmd := exec.Command(exePath, "--webview2-probe", "--json", out)
	winapi.HideWindow(cmd)
	if err := cmd.Start(); err != nil {
		return err
	}
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	select {
	case err := <-done:
		var v wv2Verdict
		if b, e := os.ReadFile(out); e == nil && json.Unmarshal(b, &v) == nil {
			if v.OK {
				return nil
			}
			return errors.New(v.Error)
		}
		if err != nil {
			return fmt.Errorf("пробный запуск завершился аварийно: %v", err)
		}
		return errors.New("пробный запуск не оставил результата")
	case <-time.After(timeout):
		cmd.Process.Kill()
		return errors.New("пробный запуск не уложился во время")
	}
}

// webview2Usable decides once per runtime/program version.
func webview2Usable() error {
	switch os.Getenv("PROSTOR_WINDOW") {
	case "edge":
		return errors.New("выбрано окно Edge")
	case "webview2":
		return nil
	}
	ver := webview2.Version()
	if ver == "" {
		return errors.New("среда WebView2 не установлена")
	}
	var c wv2Verdict
	if b, err := os.ReadFile(wv2CachePath()); err == nil && json.Unmarshal(b, &c) == nil && c.Runtime == ver && c.Program == version {
		if c.OK {
			return nil
		}
		return errors.New("пробный запуск ранее не удался: " + c.Error)
	}
	err := probeWebView2(30 * time.Second)
	c = wv2Verdict{Runtime: ver, Program: version, OK: err == nil, Time: time.Now().Format(time.RFC3339)}
	if err != nil {
		c.Error = err.Error()
	}
	writeFileAtomic(wv2CachePath(), c)
	log.Printf("webview2 %s probe: ok=%v %s", ver, c.OK, c.Error)
	return err
}

// darkWindow: dark title bar unless the user chose the light theme (or
// "as in Windows" with light apps).
func darkWindow() bool {
	switch getSettings().Theme {
	case "light":
		return false
	case "auto":
		v, ok := winapi.RegGetDword(winapi.HKCU, `Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`, "AppsUseLightTheme")
		return ok && v == 0
	}
	return true
}

func nativeWindow(url string) (webui.NativeWindow, error) {
	if !winapi.IsWindows {
		return nil, errors.New("не Windows")
	}
	if err := webview2Usable(); err != nil {
		return nil, err
	}
	w, err := webview2.Start(webview2.Options{
		Title: windowTitle, URL: url, Width: 1280, Height: 860, MinWidth: 900, MinHeight: 600,
		DataDir: wv2DataDir(), Dark: darkWindow(),
	})
	if err != nil {
		// remember the failure so the next start goes straight to Edge
		writeFileAtomic(wv2CachePath(), wv2Verdict{Runtime: webview2.Version(), Program: version, Error: err.Error(), Time: time.Now().Format(time.RFC3339)})
		return nil, err
	}
	return w, nil
}

// runWebView2Probe implements --webview2-probe: a hidden window that loads a
// page and closes. Exit code 0 means WebView2 works on this machine.
func runWebView2Probe() int {
	v := wv2Verdict{Runtime: webview2.Version(), Program: version, Time: time.Now().Format(time.RFC3339)}
	dir := filepath.Join(os.TempDir(), fmt.Sprintf("prostor-wv2-probe-%d", os.Getpid()))
	defer os.RemoveAll(dir)
	w, err := webview2.Start(webview2.Options{Title: "Prostor probe", URL: "about:blank", Width: 400, Height: 300,
		DataDir: dir, Hidden: true, Timeout: 20 * time.Second})
	if err == nil {
		v.OK = true
		w.Close()
		select {
		case <-w.Done():
		case <-time.After(5 * time.Second):
		}
	} else {
		v.Error = err.Error()
	}
	if out := argValue("--json"); out != "" {
		b, _ := json.Marshal(v)
		os.WriteFile(out, b, 0o644)
	}
	if v.OK {
		return 0
	}
	return 1
}
