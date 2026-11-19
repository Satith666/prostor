package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime/debug"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"prostor/internal/winapi"
)

// ---------------------------------------------------------------- background jobs

type Job struct {
	Kind     string
	Done     atomic.Int64
	Total    atomic.Int64
	Bytes    atomic.Int64
	Files    atomic.Int64
	current  atomic.Value
	mu       sync.Mutex
	running  bool
	phase    string
	started  time.Time
	finished time.Time
	result   any
	log      []string
	err      string
	cancel   chan struct{}
	cancelOk sync.Once
	// Pct: the console tool prints percentages, they become the progress
	Pct bool
}

func (j *Job) SetPhase(p string)   { j.mu.Lock(); j.phase = p; j.mu.Unlock() }
func (j *Job) SetCurrent(s string) { j.current.Store(s) }
func (j *Job) Cancelled() bool {
	select {
	case <-j.cancel:
		return true
	default:
		return false
	}
}
func (j *Job) Cancel() { j.cancelOk.Do(func() { close(j.cancel) }) }

func (j *Job) Log(line string) {
	j.mu.Lock()
	j.log = append(j.log, line)
	if len(j.log) > 3000 {
		j.log = j.log[len(j.log)-2000:]
	}
	j.mu.Unlock()
}

func (j *Job) logReplace(line string) {
	j.mu.Lock()
	if len(j.log) > 0 {
		j.log[len(j.log)-1] = line
	} else {
		j.log = append(j.log, line)
	}
	j.mu.Unlock()
}

type jobSnapshot struct {
	Kind    string   `json:"kind"`
	Running bool     `json:"running"`
	Exists  bool     `json:"exists"`
	Phase   string   `json:"phase"`
	Current string   `json:"current"`
	Done    int64    `json:"done"`
	Total   int64    `json:"total"`
	Bytes   int64    `json:"bytes"`
	Files   int64    `json:"files"`
	Elapsed float64  `json:"elapsed"`
	Result  any      `json:"result"`
	Log     []string `json:"log"`
	LogLen  int      `json:"logLen"`
	Err     string   `json:"err"`
}

func (j *Job) Snapshot(logFrom int) jobSnapshot {
	j.mu.Lock()
	defer j.mu.Unlock()
	cur, _ := j.current.Load().(string)
	end := j.finished
	if j.running {
		end = time.Now()
	}
	s := jobSnapshot{
		Kind: j.Kind, Running: j.running, Exists: true, Phase: j.phase, Current: cur,
		Done: j.Done.Load(), Total: j.Total.Load(), Bytes: j.Bytes.Load(), Files: j.Files.Load(),
		Elapsed: end.Sub(j.started).Seconds(), Err: j.err, LogLen: len(j.log),
	}
	if !j.running {
		s.Result = j.result
	}
	if logFrom < 0 {
		logFrom = 0
	}
	if logFrom > len(j.log) {
		logFrom = 0 // log was trimmed; resend the tail
	}
	s.Log = append([]string(nil), j.log[logFrom:]...)
	return s
}

var (
	jobsMu sync.Mutex
	jobs   = map[string]*Job{}
)

var errBusy = errors.New("операция уже выполняется")

func startJob(kind string, fn func(j *Job) (any, error)) (*Job, error) {
	jobsMu.Lock()
	if old, ok := jobs[kind]; ok {
		old.mu.Lock()
		running := old.running
		old.mu.Unlock()
		if running {
			jobsMu.Unlock()
			return nil, errBusy
		}
	}
	j := &Job{Kind: kind, running: true, started: time.Now(), cancel: make(chan struct{})}
	j.current.Store("")
	jobs[kind] = j
	jobsMu.Unlock()
	go func() {
		defer func() {
			if r := recover(); r != nil {
				log.Printf("job %s panic: %v\n%s", kind, r, debug.Stack())
				j.mu.Lock()
				j.err = "внутренняя ошибка: " + toString(r)
				j.running = false
				j.finished = time.Now()
				j.mu.Unlock()
			}
		}()
		res, err := fn(j)
		j.mu.Lock()
		j.result = res
		if err != nil {
			j.err = err.Error()
		}
		j.running = false
		j.finished = time.Now()
		j.mu.Unlock()
	}()
	return j, nil
}

func getJob(kind string) *Job {
	jobsMu.Lock()
	defer jobsMu.Unlock()
	return jobs[kind]
}

// busy reports whether a job that must not be interrupted is running.
func busy() bool {
	jobsMu.Lock()
	defer jobsMu.Unlock()
	for kind, j := range jobs {
		if kind != "clean" && kind != "tool" && kind != "delete" {
			continue
		}
		j.mu.Lock()
		r := j.running
		j.mu.Unlock()
		if r {
			return true
		}
	}
	return false
}

func toString(v any) string {
	switch x := v.(type) {
	case string:
		return x
	case error:
		return x.Error()
	}
	b, _ := json.Marshal(v)
	return string(b)
}

// ---------------------------------------------------------------- commands

// runLogged runs a console tool, streaming decoded output lines into the job log.
func runLogged(j *Job, name string, args ...string) error {
	cmd := exec.Command(name, args...)
	winapi.HideWindow(cmd)
	pr, pw := io.Pipe()
	cmd.Stdout = pw
	cmd.Stderr = pw
	if err := cmd.Start(); err != nil {
		pw.Close()
		return err
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		buf := make([]byte, 4096)
		var line []byte
		replace := false
		emit := func(rep bool) {
			s := strings.TrimRight(decodeLine(line, winapi.DecodeConsole), " \t")
			if v, ok := progressOf(s); ok && j.Pct {
				j.Total.Store(100)
				j.Done.Store(int64(v))
			}
			if strings.TrimSpace(s) != "" {
				if replace {
					j.logReplace(s)
				} else {
					j.Log(s)
				}
				replace = rep
			}
			line = line[:0]
		}
		for {
			n, err := pr.Read(buf)
			for _, c := range buf[:n] {
				switch c {
				case '\n':
					emit(false)
				case '\r':
					emit(true)
				default:
					line = append(line, c)
				}
			}
			if err != nil {
				if len(line) > 0 {
					emit(false)
				}
				return
			}
		}
	}()
	waitDone := make(chan error, 1)
	go func() { waitDone <- cmd.Wait(); pw.Close() }()
	var err error
	select {
	case err = <-waitDone:
	case <-j.cancel:
		cmd.Process.Kill()
		err = <-waitDone
	}
	<-done
	return err
}

// runQuiet runs a command and returns its decoded combined output.
func runQuiet(name string, args ...string) (string, error) {
	cmd := exec.Command(name, args...)
	winapi.HideWindow(cmd)
	var b bytes.Buffer
	cmd.Stdout = &b
	cmd.Stderr = &b
	err := cmd.Run()
	return winapi.DecodeConsole(b.Bytes()), err
}

// powershell runs a script via -EncodedCommand (safe for any characters).
func powershell(script string) (string, error) {
	u := make([]byte, 0, len(script)*2)
	for _, r := range script {
		if r > 0xFFFF {
			r -= 0x10000
			hi, lo := 0xD800+(r>>10), 0xDC00+(r&0x3FF)
			u = append(u, byte(hi), byte(hi>>8), byte(lo), byte(lo>>8))
			continue
		}
		u = append(u, byte(r), byte(r>>8))
	}
	enc := base64.StdEncoding.EncodeToString(u)
	return runQuiet("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", enc)
}

// ---------------------------------------------------------------- http helpers

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	json.NewEncoder(w).Encode(v)
}

func writeErr(w http.ResponseWriter, err error) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(http.StatusBadRequest)
	json.NewEncoder(w).Encode(map[string]string{"error": err.Error()})
}

func readJSON(r *http.Request, v any) error {
	defer r.Body.Close()
	return json.NewDecoder(io.LimitReader(r.Body, 8<<20)).Decode(v)
}

// ---------------------------------------------------------------- paths

func appDataDir() string {
	base := os.Getenv("LOCALAPPDATA")
	if base == "" {
		h, _ := os.UserHomeDir()
		base = filepath.Join(h, ".local", "share")
	}
	d := filepath.Join(base, "Prostor")
	os.MkdirAll(d, 0o755)
	return d
}

func lower(s string) string { return strings.ToLower(s) }
