// Package cdp drives a headless Edge/Chrome through the DevTools protocol
// (a minimal WebSocket client plus JSON-RPC), so Prostor can test its own
// interface on the user's machine without extra software.
package cdp

import (
	"bufio"
	"crypto/rand"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"prostor/internal/winapi"
)

// ---------------------------------------------------------------- browser

type Browser struct {
	cmd  *exec.Cmd
	Port int
	dir  string
}

// Launch starts a headless browser with a throw-away profile.
func Launch(exe string, width, height int) (*Browser, error) {
	dir, err := os.MkdirTemp("", "prostor-cdp-")
	if err != nil {
		return nil, err
	}
	args := []string{
		"--headless=new", "--remote-debugging-port=0", "--user-data-dir=" + dir,
		"--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync",
		"--disable-background-networking", "--disable-component-update", "--disable-default-apps",
		"--mute-audio", "--hide-scrollbars", fmt.Sprintf("--window-size=%d,%d", width, height),
		"--force-device-scale-factor=1",
	}
	if winapi.IsWindows && winapi.IsAdmin() {
		args = append(args, "--do-not-de-elevate")
	}
	// as root, and on CI runners where AppArmor blocks Chrome's sandbox
	if !winapi.IsWindows && (os.Geteuid() == 0 || os.Getenv("CI") == "true") {
		args = append(args, "--no-sandbox")
	}
	args = append(args, "about:blank")
	cmd := exec.Command(exe, args...)
	winapi.HideWindow(cmd)
	if err := cmd.Start(); err != nil {
		os.RemoveAll(dir)
		return nil, err
	}
	go cmd.Wait()
	b := &Browser{cmd: cmd, dir: dir}
	deadline := time.Now().Add(30 * time.Second)
	for time.Now().Before(deadline) {
		if data, err := os.ReadFile(filepath.Join(dir, "DevToolsActivePort")); err == nil {
			line := strings.SplitN(strings.TrimSpace(string(data)), "\n", 2)[0]
			if p, err := strconv.Atoi(strings.TrimSpace(line)); err == nil && p > 0 {
				b.Port = p
				return b, nil
			}
		}
		time.Sleep(150 * time.Millisecond)
	}
	b.Close()
	return nil, errors.New("браузер не открыл порт отладки за 30 секунд")
}

func (b *Browser) endpoint(method, path string) ([]byte, error) {
	req, _ := http.NewRequest(method, fmt.Sprintf("http://127.0.0.1:%d%s", b.Port, path), nil)
	c := &http.Client{Timeout: 10 * time.Second}
	resp, err := c.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(resp.Body)
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf("%s %s: %s", method, path, resp.Status)
	}
	return data, err
}

// NewPage opens a blank tab and connects to it.
func (b *Browser) NewPage() (*Conn, error) {
	data, err := b.endpoint("PUT", "/json/new?about:blank")
	if err != nil {
		if data, err = b.endpoint("GET", "/json/new?about:blank"); err != nil {
			return nil, err
		}
	}
	var t struct {
		WS string `json:"webSocketDebuggerUrl"`
	}
	if err := json.Unmarshal(data, &t); err != nil || t.WS == "" {
		return nil, errors.New("браузер не вернул адрес вкладки")
	}
	return Dial(t.WS)
}

// Close shuts the browser down (politely first) and removes its profile.
func (b *Browser) Close() {
	if data, err := b.endpoint("GET", "/json/version"); err == nil {
		var v struct {
			WS string `json:"webSocketDebuggerUrl"`
		}
		if json.Unmarshal(data, &v) == nil && v.WS != "" {
			if c, err := Dial(v.WS); err == nil {
				c.Call("Browser.close", nil, 3*time.Second)
				c.Close()
			}
		}
	}
	time.Sleep(500 * time.Millisecond)
	if b.cmd != nil && b.cmd.Process != nil {
		if runtime.GOOS == "windows" {
			k := exec.Command("taskkill", "/PID", strconv.Itoa(b.cmd.Process.Pid), "/T", "/F")
			winapi.HideWindow(k)
			k.Run()
		} else {
			b.cmd.Process.Kill()
		}
	}
	go func(dir string) {
		for i := 0; i < 10; i++ {
			if os.RemoveAll(dir) == nil {
				return
			}
			time.Sleep(time.Second)
		}
	}(b.dir)
}

// ---------------------------------------------------------------- websocket + JSON-RPC

type Event struct {
	Method string          `json:"method"`
	Params json.RawMessage `json:"params"`
}

type response struct {
	ID     int64           `json:"id"`
	Result json.RawMessage `json:"result"`
	Error  *struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
	} `json:"error"`
	Method string          `json:"method"`
	Params json.RawMessage `json:"params"`
}

type Conn struct {
	nc      net.Conn
	br      *bufio.Reader
	wmu     sync.Mutex
	id      atomic.Int64
	mu      sync.Mutex
	pending map[int64]chan response
	onEvent func(Event)
	closed  chan struct{}
	once    sync.Once
}

// Dial opens a DevTools WebSocket (ws://host:port/devtools/...).
func Dial(wsURL string) (*Conn, error) {
	u, err := url.Parse(wsURL)
	if err != nil {
		return nil, err
	}
	nc, err := net.DialTimeout("tcp", u.Host, 10*time.Second)
	if err != nil {
		return nil, err
	}
	key := make([]byte, 16)
	rand.Read(key)
	req := "GET " + u.RequestURI() + " HTTP/1.1\r\nHost: " + u.Host + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
		"Sec-WebSocket-Key: " + base64.StdEncoding.EncodeToString(key) + "\r\nSec-WebSocket-Version: 13\r\n\r\n"
	nc.SetDeadline(time.Now().Add(10 * time.Second))
	if _, err := nc.Write([]byte(req)); err != nil {
		nc.Close()
		return nil, err
	}
	br := bufio.NewReaderSize(nc, 1<<16)
	resp, err := http.ReadResponse(br, nil)
	if err != nil {
		nc.Close()
		return nil, err
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusSwitchingProtocols {
		nc.Close()
		return nil, fmt.Errorf("websocket: %s", resp.Status)
	}
	nc.SetDeadline(time.Time{})
	c := &Conn{nc: nc, br: br, pending: map[int64]chan response{}, closed: make(chan struct{})}
	go c.readLoop()
	return c, nil
}

// OnEvent sets the handler for protocol events (called from the reader goroutine).
func (c *Conn) OnEvent(fn func(Event)) { c.mu.Lock(); c.onEvent = fn; c.mu.Unlock() }

func (c *Conn) Close() {
	c.once.Do(func() {
		c.writeFrame(0x8, nil)
		c.nc.Close()
		close(c.closed)
	})
}

func (c *Conn) writeFrame(op byte, payload []byte) error {
	c.wmu.Lock()
	defer c.wmu.Unlock()
	hdr := []byte{0x80 | op}
	n := len(payload)
	switch {
	case n < 126:
		hdr = append(hdr, 0x80|byte(n))
	case n <= 0xFFFF:
		hdr = append(hdr, 0x80|126, byte(n>>8), byte(n))
	default:
		hdr = append(hdr, 0x80|127)
		var b [8]byte
		binary.BigEndian.PutUint64(b[:], uint64(n))
		hdr = append(hdr, b[:]...)
	}
	mask := make([]byte, 4)
	rand.Read(mask)
	hdr = append(hdr, mask...)
	data := make([]byte, n)
	for i := range payload {
		data[i] = payload[i] ^ mask[i%4]
	}
	c.nc.SetWriteDeadline(time.Now().Add(10 * time.Second))
	if _, err := c.nc.Write(append(hdr, data...)); err != nil {
		return err
	}
	return nil
}

func (c *Conn) readMessage() ([]byte, error) {
	var msg []byte
	for {
		var h [2]byte
		if _, err := io.ReadFull(c.br, h[:]); err != nil {
			return nil, err
		}
		fin, op := h[0]&0x80 != 0, h[0]&0x0F
		n := int64(h[1] & 0x7F)
		switch n {
		case 126:
			var b [2]byte
			if _, err := io.ReadFull(c.br, b[:]); err != nil {
				return nil, err
			}
			n = int64(binary.BigEndian.Uint16(b[:]))
		case 127:
			var b [8]byte
			if _, err := io.ReadFull(c.br, b[:]); err != nil {
				return nil, err
			}
			n = int64(binary.BigEndian.Uint64(b[:]))
		}
		var mask [4]byte
		if h[1]&0x80 != 0 {
			if _, err := io.ReadFull(c.br, mask[:]); err != nil {
				return nil, err
			}
		}
		if n > 256<<20 {
			return nil, errors.New("websocket: message too large")
		}
		payload := make([]byte, n)
		if _, err := io.ReadFull(c.br, payload); err != nil {
			return nil, err
		}
		if h[1]&0x80 != 0 {
			for i := range payload {
				payload[i] ^= mask[i%4]
			}
		}
		switch op {
		case 0x8:
			return nil, io.EOF
		case 0x9:
			c.writeFrame(0xA, payload)
			continue
		case 0xA:
			continue
		}
		msg = append(msg, payload...)
		if fin {
			return msg, nil
		}
	}
}

func (c *Conn) readLoop() {
	defer func() {
		c.mu.Lock()
		for id, ch := range c.pending {
			close(ch)
			delete(c.pending, id)
		}
		c.mu.Unlock()
		c.Close()
	}()
	for {
		data, err := c.readMessage()
		if err != nil {
			return
		}
		var r response
		if json.Unmarshal(data, &r) != nil {
			continue
		}
		if r.ID != 0 {
			c.mu.Lock()
			ch := c.pending[r.ID]
			delete(c.pending, r.ID)
			c.mu.Unlock()
			if ch != nil {
				ch <- r
			}
			continue
		}
		c.mu.Lock()
		fn := c.onEvent
		c.mu.Unlock()
		if fn != nil && r.Method != "" {
			fn(Event{Method: r.Method, Params: r.Params})
		}
	}
}

// Call sends a protocol command and waits for its result.
func (c *Conn) Call(method string, params any, timeout time.Duration) (json.RawMessage, error) {
	id := c.id.Add(1)
	msg := map[string]any{"id": id, "method": method}
	if params != nil {
		msg["params"] = params
	}
	data, _ := json.Marshal(msg)
	ch := make(chan response, 1)
	c.mu.Lock()
	c.pending[id] = ch
	c.mu.Unlock()
	if err := c.writeFrame(0x1, data); err != nil {
		return nil, err
	}
	select {
	case r, ok := <-ch:
		if !ok {
			return nil, errors.New("соединение с браузером закрыто")
		}
		if r.Error != nil {
			return nil, fmt.Errorf("%s: %s", method, r.Error.Message)
		}
		return r.Result, nil
	case <-time.After(timeout):
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
		return nil, fmt.Errorf("%s: нет ответа за %v", method, timeout)
	case <-c.closed:
		return nil, errors.New("соединение с браузером закрыто")
	}
}

// Eval runs JavaScript in the page and decodes its (awaited) value into out.
func (c *Conn) Eval(expr string, out any, timeout time.Duration) error {
	res, err := c.Call("Runtime.evaluate", map[string]any{
		"expression": expr, "returnByValue": true, "awaitPromise": true,
	}, timeout)
	if err != nil {
		return err
	}
	var r struct {
		Result struct {
			Value json.RawMessage `json:"value"`
		} `json:"result"`
		Exception *struct {
			Text      string `json:"text"`
			Exception struct {
				Description string `json:"description"`
			} `json:"exception"`
		} `json:"exceptionDetails"`
	}
	if err := json.Unmarshal(res, &r); err != nil {
		return err
	}
	if r.Exception != nil {
		d := r.Exception.Exception.Description
		if d == "" {
			d = r.Exception.Text
		}
		return errors.New(strings.SplitN(d, "\n", 2)[0])
	}
	if out != nil && len(r.Result.Value) > 0 {
		return json.Unmarshal(r.Result.Value, out)
	}
	return nil
}

// WaitFor polls a boolean JavaScript expression.
func (c *Conn) WaitFor(expr string, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	for {
		var ok bool
		err := c.Eval("!!("+expr+")", &ok, 5*time.Second)
		if err == nil && ok {
			return nil
		}
		if time.Now().After(deadline) {
			if err != nil {
				return err
			}
			return fmt.Errorf("не дождались: %s", expr)
		}
		time.Sleep(120 * time.Millisecond)
	}
}

// Screenshot returns a JPEG of the viewport.
func (c *Conn) Screenshot() ([]byte, error) {
	res, err := c.Call("Page.captureScreenshot", map[string]any{"format": "jpeg", "quality": 72}, 20*time.Second)
	if err != nil {
		return nil, err
	}
	var r struct {
		Data string `json:"data"`
	}
	if err := json.Unmarshal(res, &r); err != nil {
		return nil, err
	}
	return base64.StdEncoding.DecodeString(r.Data)
}
