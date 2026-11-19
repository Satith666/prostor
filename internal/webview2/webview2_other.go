//go:build !windows

package webview2

import (
	"errors"
	"time"
)

type Options struct {
	Title     string
	URL       string
	Width     int
	Height    int
	MinWidth  int
	MinHeight int
	DataDir   string
	Dark      bool
	Hidden    bool
	Timeout   time.Duration
}

type Window struct{ done chan struct{} }

func (w *Window) Done() <-chan struct{} { return w.done }
func (w *Window) Close()                {}
func (w *Window) BrowserPID() uint32    { return 0 }

var errUnsupported = errors.New("WebView2 есть только в Windows")

func Start(Options) (*Window, error)            { return nil, errUnsupported }
func Runtime() (version, dll string, err error) { return "", "", errUnsupported }
func Version() string                           { return "" }
