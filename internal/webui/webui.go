// Package webui hosts an embedded HTML interface on a private loopback server
// and shows it in a chromeless Edge/Chrome "app" window.
package webui

import (
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"log"
	"mime"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"prostor/internal/winapi"
)

type Options struct {
	Title      string
	Width      int
	Height     int
	ProfileDir string       // browser profile directory for the app window
	Handler    http.Handler // app routes; /api/* gets token protection
	IsBusy     func() bool  // keep running while true even if the window is gone
	// BeforeWindow runs once the server is listening, before any window opens.
	BeforeWindow func()
	// Native opens the program's own window; on error the Edge app window is used.
	Native func(url string) (NativeWindow, error)
}

// NativeWindow is a window owned by this process (WebView2).
type NativeWindow interface {
	Done() <-chan struct{}
	Close()
}

type Server struct {
	opts      Options
	Token     string
	URL       string
	listener  net.Listener
	lastPing  atomic.Int64
	byeAt     atomic.Int64
	quit      chan struct{}
	quitOnce  sync.Once
	browserMu sync.Mutex
	rootPID   uint32
	cmd       *exec.Cmd
	native    NativeWindow
}

func randomToken() string {
	b := make([]byte, 24)
	rand.Read(b)
	return hex.EncodeToString(b)
}

func New(opts Options) (*Server, error) {
	// On Windows Go reads MIME types from the registry, where .js is sometimes
	// mapped to text/plain; with nosniff the browser would refuse to run it.
	for ext, typ := range map[string]string{
		".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
		".css": "text/css; charset=utf-8", ".png": "image/png", ".svg": "image/svg+xml",
		".json": "application/json", ".ico": "image/x-icon",
	} {
		mime.AddExtensionType(ext, typ)
	}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	s := &Server{opts: opts, Token: randomToken(), listener: ln, quit: make(chan struct{})}
	port := ln.Addr().(*net.TCPAddr).Port
	s.URL = fmt.Sprintf("http://127.0.0.1:%d/?t=%s", port, s.Token)
	s.lastPing.Store(time.Now().Add(60 * time.Second).UnixNano()) // grace period for browser start
	return s, nil
}

// Quit asks Run to return.
func (s *Server) Quit() { s.quitOnce.Do(func() { close(s.quit) }) }

func (s *Server) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	host := r.Host
	if h, _, err := net.SplitHostPort(host); err == nil {
		host = h
	}
	if host != "127.0.0.1" && host != "localhost" {
		http.Error(w, "forbidden", http.StatusForbidden)
		return
	}
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("Referrer-Policy", "no-referrer")
	w.Header().Set("Cache-Control", "no-store")
	if strings.HasPrefix(r.URL.Path, "/api/") {
		tok := r.Header.Get("X-Token")
		if tok == "" {
			tok = r.URL.Query().Get("t")
		}
		if tok != s.Token {
			http.Error(w, "bad token", http.StatusForbidden)
			return
		}
		switch r.URL.Path {
		case "/api/ping":
			s.lastPing.Store(time.Now().UnixNano())
			s.byeAt.Store(0)
			w.Write([]byte(`{"ok":true}`))
			return
		case "/api/bye":
			s.byeAt.Store(time.Now().UnixNano())
			w.Write([]byte(`{"ok":true}`))
			return
		}
	}
	s.opts.Handler.ServeHTTP(w, r)
}

// ServeOnly serves the interface without opening a window (self-test).
// It returns the full URL with the access token and a stop function.
func (s *Server) ServeOnly() (string, func()) {
	srv := &http.Server{Handler: s, ReadHeaderTimeout: 10 * time.Second}
	go srv.Serve(s.listener)
	return s.URL, func() { srv.Close() }
}

// Run serves the UI, opens the window and blocks until the window is closed.
func (s *Server) Run() error {
	srv := &http.Server{Handler: s, ReadHeaderTimeout: 10 * time.Second}
	go srv.Serve(s.listener)
	defer srv.Close()

	exited := make(chan struct{})
	if s.opts.BeforeWindow != nil {
		s.opts.BeforeWindow()
	}
	if os.Getenv("PROSTOR_NOBROWSER") == "" {
		opened := false
		if s.opts.Native != nil {
			if nw, err := s.opts.Native(s.URL); err == nil {
				s.browserMu.Lock()
				s.native = nw
				s.browserMu.Unlock()
				go func() { <-nw.Done(); close(exited) }()
				opened = true
				log.Printf("native window opened")
			} else {
				log.Printf("native window unavailable (%v), using Edge", err)
			}
		}
		if !opened {
			if err := s.openWindow(exited); err != nil {
				log.Printf("app window failed (%v), opening default browser", err)
				winapi.ShellOpen(s.URL)
			}
		}
	} else {
		fmt.Println("UI:", s.URL)
	}

	t := time.NewTicker(time.Second)
	defer t.Stop()
	windowGone := false
	for {
		select {
		case <-s.quit:
			log.Printf("quit requested")
			s.killBrowser()
			return nil
		case <-exited:
			windowGone = true
			exited = nil
		case <-t.C:
		}
		busy := s.opts.IsBusy != nil && s.opts.IsBusy()
		now := time.Now().UnixNano()
		if bye := s.byeAt.Load(); bye != 0 && now-bye > int64(6*time.Second) {
			windowGone = true
		}
		if now-s.lastPing.Load() > int64(150*time.Second) {
			windowGone = true
		}
		if windowGone && !busy {
			log.Printf("window closed, exiting")
			s.killBrowser()
			return nil
		}
	}
}

// OwnPIDs returns the process ids of the browser window hosting our UI, so
// cleaners can ignore them when checking whether Edge/Chrome is running.
func (s *Server) OwnPIDs() map[uint32]bool {
	s.browserMu.Lock()
	root := s.rootPID
	s.browserMu.Unlock()
	out := map[uint32]bool{}
	if root == 0 {
		return out
	}
	procs := winapi.Processes()
	children := map[uint32][]uint32{}
	for _, p := range procs {
		children[p.Parent] = append(children[p.Parent], p.PID)
	}
	stack := []uint32{root}
	for len(stack) > 0 {
		pid := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		if out[pid] {
			continue
		}
		out[pid] = true
		stack = append(stack, children[pid]...)
	}
	return out
}

func (s *Server) killBrowser() {
	s.browserMu.Lock()
	root, native := s.rootPID, s.native
	s.browserMu.Unlock()
	if native != nil {
		native.Close()
		select {
		case <-native.Done():
		case <-time.After(3 * time.Second):
		}
		return
	}
	if root == 0 || !winapi.IsWindows {
		return
	}
	cmd := exec.Command("taskkill", "/PID", strconv.Itoa(int(root)), "/T", "/F")
	winapi.HideWindow(cmd)
	cmd.Run()
}

// FindBrowser returns Edge (or Chrome) used to show the interface.
// PROSTOR_BROWSER overrides it (used by tests).
func FindBrowser() string { return findBrowser() }

func findBrowser() string {
	if p := os.Getenv("PROSTOR_BROWSER"); p != "" {
		return p
	}
	if !winapi.IsWindows {
		for _, p := range []string{"/usr/bin/google-chrome", "/usr/bin/chromium"} {
			if _, err := os.Stat(p); err == nil {
				return p
			}
		}
		return ""
	}
	var cands []string
	for _, env := range []string{"ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA"} {
		if v := os.Getenv(env); v != "" {
			cands = append(cands, filepath.Join(v, `Microsoft\Edge\Application\msedge.exe`))
		}
	}
	if p, ok := winapi.RegGetString(winapi.HKLM, `SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe`, ""); ok {
		cands = append(cands, p)
	}
	for _, env := range []string{"ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"} {
		if v := os.Getenv(env); v != "" {
			cands = append(cands, filepath.Join(v, `Google\Chrome\Application\chrome.exe`))
		}
	}
	if p, ok := winapi.RegGetString(winapi.HKLM, `SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe`, ""); ok {
		cands = append(cands, p)
	}
	for _, c := range cands {
		c = strings.Trim(c, `"`)
		if st, err := os.Stat(c); err == nil && !st.IsDir() {
			return c
		}
	}
	return ""
}

func (s *Server) openWindow(exited chan struct{}) error {
	browser := findBrowser()
	if browser == "" {
		return fmt.Errorf("no browser")
	}
	sw, sh := winapi.ScreenSize()
	w, h := s.opts.Width, s.opts.Height
	if sw > 0 && w > sw*92/100 {
		w = sw * 92 / 100
	}
	if sh > 0 && h > sh*88/100 {
		h = sh * 88 / 100
	}
	x, y := (sw-w)/2, (sh-h)/2-20
	if y < 0 {
		y = 0
	}
	os.MkdirAll(s.opts.ProfileDir, 0o755)
	args := []string{
		"--app=" + s.URL,
		"--user-data-dir=" + s.opts.ProfileDir,
		fmt.Sprintf("--window-size=%d,%d", w, h),
		fmt.Sprintf("--window-position=%d,%d", x, y),
		"--no-first-run",
		"--no-default-browser-check",
		"--disable-extensions",
		"--disable-sync",
		"--disable-translate",
		"--disable-features=Translate,msEdgeTranslate,msUndersideButton,msEdgeShopping,msImplicitSignin",
		"--hide-crash-restore-bubble",
		"--disable-background-mode",
		"--no-service-autorun",
	}
	if winapi.IsAdmin() && strings.EqualFold(filepath.Base(browser), "msedge.exe") {
		// Edge relaunches itself without admin rights by default; keep our app
		// window attached to this process so its lifetime can be tracked.
		args = append(args, "--do-not-de-elevate")
	}
	log.Printf("browser: %s %q", browser, args[1:])
	cmd := exec.Command(browser, args...)
	if err := cmd.Start(); err != nil {
		return err
	}
	started := time.Now()
	s.browserMu.Lock()
	s.cmd = cmd
	s.rootPID = uint32(cmd.Process.Pid)
	s.browserMu.Unlock()
	go func() {
		cmd.Wait()
		log.Printf("browser process exited after %v", time.Since(started).Round(time.Millisecond))
		if time.Since(started) < 5*time.Second {
			// The launcher handed the window to another process (e.g. Edge
			// de-elevating). Find the real browser process for our profile and
			// fall back to heartbeat-based lifetime.
			s.browserMu.Lock()
			s.rootPID = 0
			s.browserMu.Unlock()
			go s.findHandoffPID(filepath.Base(browser))
			return
		}
		close(exited)
	}()
	return nil
}

func (s *Server) findHandoffPID(exe string) {
	if !winapi.IsWindows {
		return
	}
	time.Sleep(2 * time.Second)
	prof := strings.ReplaceAll(s.opts.ProfileDir, "'", "''")
	script := fmt.Sprintf(`Get-CimInstance Win32_Process -Filter "Name='%s'" | Where-Object { $_.CommandLine -like '*%s*' -and $_.CommandLine -notlike '*--type=*' } | Select-Object -First 1 -ExpandProperty ProcessId`, exe, prof)
	cmd := exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", script)
	winapi.HideWindow(cmd)
	out, err := cmd.Output()
	if err != nil {
		return
	}
	if pid, err := strconv.Atoi(strings.TrimSpace(string(out))); err == nil && pid > 0 {
		s.browserMu.Lock()
		s.rootPID = uint32(pid)
		s.browserMu.Unlock()
	}
}
