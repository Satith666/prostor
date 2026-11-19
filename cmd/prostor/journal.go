package main

// Journal of every file a cleaning run touched: deleted, moved to quarantine,
// failed or scheduled for deletion on reboot. One gzip-compressed JSON-lines
// file per run, kept for the last journalKeep runs, so "what exactly was
// deleted?" always has an answer.

import (
	"bufio"
	"compress/gzip"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

const journalKeep = 60

// JRec is one line of a journal.
type JRec struct {
	A string `json:"a"`           // d = deleted, q = to quarantine, r = to Recycle Bin, f = failed, s = on reboot
	P string `json:"p"`           // path
	S int64  `json:"s,omitempty"` // size in bytes
	T string `json:"t,omitempty"` // target id or source
}

type journal struct {
	mu   sync.Mutex
	id   string
	f    *os.File
	gz   *gzip.Writer
	bw   *bufio.Writer
	enc  *json.Encoder
	n    int64
	fail bool
}

func journalDir() string { return filepath.Join(appDataDir(), "journal") }

var journalSeq struct {
	sync.Mutex
	last string
}

func newJournalID(kind string) string {
	journalSeq.Lock()
	defer journalSeq.Unlock()
	base := time.Now().Format("20060102-150405") + "-" + kind
	id := base
	for i := 2; id == journalSeq.last || fileExists(filepath.Join(journalDir(), id+".jsonl.gz")); i++ {
		id = fmt.Sprintf("%s-%d", base, i)
	}
	journalSeq.last = id
	return id
}

func fileExists(p string) bool { _, err := os.Lstat(p); return err == nil }

// openJournal starts a journal; it never fails: on error records are dropped.
func openJournal(kind string) *journal {
	j := &journal{id: newJournalID(kind)}
	if err := os.MkdirAll(journalDir(), 0o755); err != nil {
		j.fail = true
		return j
	}
	f, err := os.Create(filepath.Join(journalDir(), j.id+".jsonl.gz"))
	if err != nil {
		j.fail = true
		return j
	}
	j.f = f
	j.gz = gzip.NewWriter(f)
	j.bw = bufio.NewWriterSize(j.gz, 64<<10)
	j.enc = json.NewEncoder(j.bw)
	return j
}

func (j *journal) add(action, path string, size int64, tag string) {
	if j == nil {
		return
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	if j.fail || j.enc == nil {
		return
	}
	if j.enc.Encode(JRec{A: action, P: path, S: size, T: tag}) != nil {
		j.fail = true
		return
	}
	j.n++
}

// close finishes the file and returns its id ("" when nothing was written).
func (j *journal) close() string {
	if j == nil {
		return ""
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	if j.f == nil {
		return ""
	}
	j.bw.Flush()
	j.gz.Close()
	j.f.Close()
	j.f = nil
	if j.n == 0 {
		os.Remove(filepath.Join(journalDir(), j.id+".jsonl.gz"))
		return ""
	}
	pruneJournals()
	return j.id
}

func pruneJournals() {
	ents, err := os.ReadDir(journalDir())
	if err != nil {
		return
	}
	var names []string
	for _, e := range ents {
		if strings.HasSuffix(e.Name(), ".jsonl.gz") {
			names = append(names, e.Name())
		}
	}
	sort.Strings(names)
	for len(names) > journalKeep {
		os.Remove(filepath.Join(journalDir(), names[0]))
		names = names[1:]
	}
}

func validJournalID(id string) bool {
	if id == "" || len(id) > 64 {
		return false
	}
	for _, c := range id {
		if !(c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c == '-') {
			return false
		}
	}
	return true
}

// JournalPage is one page of a journal, filtered by a search query.
type JournalPage struct {
	ID      string           `json:"id"`
	Total   int64            `json:"total"`   // records matching the query
	Records []JRec           `json:"records"` // the requested page
	Counts  map[string]int64 `json:"counts"`  // per action, whole journal
	Bytes   map[string]int64 `json:"bytes"`   // per action, whole journal
}

func readJournal(id, query string, offset, limit int) (*JournalPage, error) {
	if !validJournalID(id) {
		return nil, fmt.Errorf("неверный журнал")
	}
	f, err := os.Open(filepath.Join(journalDir(), id+".jsonl.gz"))
	if err != nil {
		return nil, fmt.Errorf("журнал не найден: возможно, он удалён как старый")
	}
	defer f.Close()
	gz, err := gzip.NewReader(f)
	if err != nil {
		return nil, err
	}
	defer gz.Close()
	if limit <= 0 || limit > 1000 {
		limit = 200
	}
	q := lower(strings.TrimSpace(query))
	pg := &JournalPage{ID: id, Records: []JRec{}, Counts: map[string]int64{}, Bytes: map[string]int64{}}
	dec := json.NewDecoder(bufio.NewReaderSize(gz, 64<<10))
	for {
		var r JRec
		if err := dec.Decode(&r); err != nil {
			if err == io.EOF || err == io.ErrUnexpectedEOF {
				break
			}
			break // a journal cut short (crash) still shows what it has
		}
		pg.Counts[r.A]++
		pg.Bytes[r.A] += r.S
		if q != "" && !strings.Contains(lower(r.P), q) && !strings.Contains(lower(r.T), q) {
			continue
		}
		if pg.Total >= int64(offset) && len(pg.Records) < limit {
			pg.Records = append(pg.Records, r)
		}
		pg.Total++
	}
	return pg, nil
}
