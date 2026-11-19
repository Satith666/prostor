package main

// The window runs without administrator rights. Anything that needs them is
// done by the helper: a second copy of Prostor started through UAC with
// --helper. The helper serves the same API (the same routes, but elevated)
// on its own loopback port, protected by a one-time secret; once it reports
// in, the window process forwards the program's API to it. So the elevated
// code paths are exactly the ones used when Prostor runs as administrator,
// and getting rights never needs a restart: the page just reloads.
//
// Lifetime: the helper checks every few seconds that the window process is
// alive and exits when it is gone; the window process tells it to exit when
// it closes.

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"time"

	"prostor/internal/winapi"
)

// ---------------------------------------------------------------- window side

type helperState struct {
	mu       sync.Mutex
	secret   string
	base     *url.URL // helper API, once it has reported in
	pid      int
	proxy    *httputil.ReverseProxy
	starting chan struct{} // closed when the current start attempt ends
	lastErr  error
}

var helper helperState

func randomHex(n int) string {
	b := make([]byte, n)
	rand.Read(b)
	return hex.EncodeToString(b)
}

func (h *helperState) active() bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	return h.base != nil
}

// canAdmin: this process is elevated, or an elevated helper is connected.
func canAdmin() bool { return isAdmin || helper.active() }

// forceHelper (tests): pretend to be unelevated and start the helper without UAC.
func forceHelper() bool { return os.Getenv("PROSTOR_FORCE_HELPER") == "1" }

// start launches the helper (one UAC prompt) and waits until it reports in.
func (h *helperState) start(uiURL string, wait time.Duration) error {
	h.mu.Lock()
	if h.base != nil {
		h.mu.Unlock()
		return nil
	}
	if ch := h.starting; ch != nil {
		h.mu.Unlock()
		select {
		case <-ch:
		case <-time.After(wait):
			return errors.New("помощник не ответил")
		}
		h.mu.Lock()
		defer h.mu.Unlock()
		if h.base != nil {
			return nil
		}
		return h.lastErr
	}
	h.secret = randomHex(24)
	h.starting = make(chan struct{})
	ch := h.starting
	secret := h.secret
	h.mu.Unlock()

	u, _ := url.Parse(uiURL)
	args := []string{"--helper", "--ui", "http://" + u.Host, "--token", u.Query().Get("t"), "--secret", secret, "--ppid", strconv.Itoa(os.Getpid())}
	var err error
	if forceHelper() {
		cmd := exec.Command(exePath, args...)
		cmd.Env = append(os.Environ(), "PROSTOR_HELPER_ADMIN=1")
		err = cmd.Start()
	} else {
		err = winapi.RunElevated(exePath, strings.Join(args, " "))
	}
	if err == nil {
		select {
		case <-ch:
		case <-time.After(wait):
			err = errors.New("помощник не ответил")
		}
	} else if errors.Is(err, winapi.ErrCancelled) {
		err = errors.New("запрос прав администратора отменён")
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.base != nil {
		return nil
	}
	if err == nil {
		err = errors.New("помощник не запустился")
	}
	h.lastErr = err
	select {
	case <-ch:
	default:
		close(ch)
	}
	h.starting = nil
	return err
}

// hello registers the helper's address.
func (h *helperState) hello(secret string, port, pid int) error {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.secret == "" || secret != h.secret {
		return errors.New("неизвестный помощник")
	}
	base, _ := url.Parse(fmt.Sprintf("http://127.0.0.1:%d", port))
	h.base, h.pid = base, pid
	p := httputil.NewSingleHostReverseProxy(base)
	sec := h.secret
	orig := p.Director
	p.Director = func(r *http.Request) {
		orig(r)
		r.Header.Del("X-Token")
		q := r.URL.Query()
		q.Del("t")
		r.URL.RawQuery = q.Encode()
		r.Header.Set("X-Helper", sec)
	}
	p.ErrorHandler = func(w http.ResponseWriter, r *http.Request, err error) {
		log.Printf("helper unreachable: %v", err)
		h.drop()
		writeErr(w, errors.New("помощник с правами администратора не отвечает; повторите действие"))
	}
	h.proxy = p
	if h.starting != nil {
		close(h.starting)
		h.starting = nil
	}
	log.Printf("helper connected: pid %d port %d", pid, port)
	return nil
}

func (h *helperState) drop() {
	h.mu.Lock()
	h.base, h.proxy = nil, nil
	h.mu.Unlock()
}

// stop asks the helper to exit (window closing).
func (h *helperState) stop() {
	h.mu.Lock()
	base, sec := h.base, h.secret
	h.base, h.proxy = nil, nil
	h.mu.Unlock()
	if base == nil {
		return
	}
	req, _ := http.NewRequest("POST", base.String()+"/api/helper/exit", nil)
	req.Header.Set("X-Helper", sec)
	c := &http.Client{Timeout: 2 * time.Second}
	if r, err := c.Do(req); err == nil {
		r.Body.Close()
	}
}

// localAPI lists paths the window process always answers itself.
func localAPI(p string) bool {
	return strings.HasPrefix(p, "/api/helper/") || p == "/api/elevate"
}

// withHelper forwards the API to the helper while it is connected.
func withHelper(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasPrefix(r.URL.Path, "/api/") && !localAPI(r.URL.Path) {
			helper.mu.Lock()
			p := helper.proxy
			helper.mu.Unlock()
			if p != nil {
				p.ServeHTTP(w, r)
				return
			}
		}
		next.ServeHTTP(w, r)
	})
}

// helperRoutes are served by the window process for the helper.
func helperRoutes(mux *http.ServeMux) {
	mux.HandleFunc("POST /api/helper/hello", func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			Secret string
			Port   int
			PID    int
		}
		readJSON(r, &req)
		if err := helper.hello(req.Secret, req.Port, req.PID); err != nil {
			http.Error(w, err.Error(), http.StatusForbidden)
			return
		}
		writeJSON(w, map[string]bool{"ok": true})
	})
	// the helper asks which browser processes belong to our own window
	mux.HandleFunc("GET /api/helper/own", func(w http.ResponseWriter, r *http.Request) {
		helper.mu.Lock()
		okSecret := helper.secret != "" && r.Header.Get("X-Helper") == helper.secret
		helper.mu.Unlock()
		if !okSecret {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		var pids []uint32
		for p := range ownPIDs() {
			pids = append(pids, p)
		}
		writeJSON(w, pids)
	})
	mux.HandleFunc("GET /api/helper/alive", func(w http.ResponseWriter, r *http.Request) { w.Write([]byte(`{"ok":true}`)) })
}

// ---------------------------------------------------------------- helper side

var helperMode struct {
	on     bool
	ui     string
	token  string
	secret string
}

// helperOwnPIDs asks the window process which browser processes are its own.
func helperOwnPIDs() map[uint32]bool {
	out := map[uint32]bool{}
	req, _ := http.NewRequest("GET", helperMode.ui+"/api/helper/own?t="+url.QueryEscape(helperMode.token), nil)
	req.Header.Set("X-Helper", helperMode.secret)
	c := &http.Client{Timeout: 3 * time.Second}
	r, err := c.Do(req)
	if err != nil {
		return out
	}
	defer r.Body.Close()
	var pids []uint32
	json.NewDecoder(r.Body).Decode(&pids)
	for _, p := range pids {
		out[p] = true
	}
	return out
}

func runHelper() int {
	helperMode.on = true
	helperMode.ui = argValue("--ui")
	helperMode.token = argValue("--token")
	helperMode.secret = argValue("--secret")
	if helperMode.ui == "" || helperMode.secret == "" || !strings.HasPrefix(helperMode.ui, "http://127.0.0.1:") {
		log.Printf("helper: bad arguments")
		return 2
	}
	if os.Getenv("PROSTOR_HELPER_ADMIN") == "1" {
		isAdmin = true
	}
	if isAdmin && winapi.EnableBackupPrivilege() {
		log.Printf("helper: backup privilege enabled")
	}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		log.Printf("helper: listen: %v", err)
		return 1
	}
	app := routes()
	quit := make(chan struct{})
	var quitOnce sync.Once
	mux := http.NewServeMux()
	mux.HandleFunc("POST /api/helper/exit", func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte(`{"ok":true}`))
		quitOnce.Do(func() { close(quit) })
	})
	mux.Handle("/", app)
	srv := &http.Server{ReadHeaderTimeout: 10 * time.Second, Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if host, _, _ := net.SplitHostPort(r.RemoteAddr); host != "127.0.0.1" || r.Header.Get("X-Helper") != helperMode.secret {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		mux.ServeHTTP(w, r)
	})}
	go srv.Serve(ln)

	c := &http.Client{Timeout: 5 * time.Second}
	body, _ := json.Marshal(map[string]any{"secret": helperMode.secret, "port": ln.Addr().(*net.TCPAddr).Port, "pid": os.Getpid()})
	resp, err := c.Post(helperMode.ui+"/api/helper/hello?t="+url.QueryEscape(helperMode.token), "application/json", bytes.NewReader(body))
	if err != nil || resp.StatusCode != 200 {
		log.Printf("helper: hello failed: %v", err)
		return 1
	}
	resp.Body.Close()
	log.Printf("helper: serving on %s", ln.Addr())

	// exit when the window process is gone (and no job is running)
	fails := 0
	t := time.NewTicker(3 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-quit:
			log.Printf("helper: exit requested")
			waitJobs(10 * time.Minute)
			return 0
		case <-t.C:
		}
		r, err := c.Get(helperMode.ui + "/api/helper/alive?t=" + url.QueryEscape(helperMode.token))
		if err == nil {
			r.Body.Close()
			fails = 0
			continue
		}
		if fails++; fails >= 4 && !busy() {
			log.Printf("helper: window process is gone, exiting")
			return 0
		}
	}
}

// waitJobs lets a running cleaning finish before the helper exits.
func waitJobs(max time.Duration) {
	deadline := time.Now().Add(max)
	for busy() && time.Now().Before(deadline) {
		time.Sleep(500 * time.Millisecond)
	}
}
