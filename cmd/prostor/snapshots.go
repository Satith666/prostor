package main

// Disk snapshots: after every analysis the sizes of noticeable folders are
// saved (a few thousand lines, gzip). The next analysis of the same place is
// compared with an earlier snapshot to answer "what grew since last week".

import (
	"compress/gzip"
	"crypto/sha1"
	"encoding/hex"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"
)

var (
	snapMinSize  int64 = 10 << 20 // folders smaller than this are not remembered
	snapMaxDepth       = 8
	snapKeep           = 30 // snapshots per analysed place
	growthMin    int64 = 20 << 20
)

type snapDir struct {
	P string `json:"p"` // path relative to the root
	S int64  `json:"s"`
	F int64  `json:"f"`
}

type snapshot struct {
	Root   string    `json:"root"`
	Time   time.Time `json:"time"`
	Size   int64     `json:"size"`
	Files  int64     `json:"files"`
	Method string    `json:"method"`
	Dirs   []snapDir `json:"dirs"`
}

type snapInfo struct {
	ID     string    `json:"id"`
	Time   time.Time `json:"time"`
	Size   int64     `json:"size"`
	Method string    `json:"method"`
}

var snapNameRe = regexp.MustCompile(`[^A-Za-z0-9]+`)

func snapDirFor(root string) string {
	h := sha1.Sum([]byte(lower(filepath.Clean(root))))
	name := strings.Trim(snapNameRe.ReplaceAllString(root, "_"), "_")
	if len(name) > 40 {
		name = name[:40]
	}
	return filepath.Join(appDataDir(), "snapshots", name+"-"+hex.EncodeToString(h[:4]))
}

var snapIDRe = regexp.MustCompile(`^\d{8}-\d{6}$`)

// collectDirs lists folders worth remembering, relative to the root.
func (a *Analysis) collectDirs() map[string]snapDir {
	out := map[string]snapDir{}
	type item struct {
		n     *dirNode
		rel   string
		depth int
	}
	stack := []item{{a.root, "", 0}}
	for len(stack) > 0 {
		it := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		if it.rel != "" {
			out[lower(it.rel)] = snapDir{P: it.rel, S: it.n.size, F: it.n.files}
		}
		if it.depth >= snapMaxDepth {
			continue
		}
		for _, c := range it.n.children {
			if c.size >= snapMinSize {
				stack = append(stack, item{c, filepath.Join(it.rel, c.name), it.depth + 1})
			}
		}
	}
	return out
}

// saveSnapshot remembers the finished analysis and returns the snapshot id.
func (a *Analysis) saveSnapshot() (string, error) {
	a.treeMu.RLock()
	dirs := a.collectDirs()
	s := snapshot{Root: a.Root, Time: time.Now(), Size: a.root.size, Files: a.root.files, Method: a.method}
	a.treeMu.RUnlock()
	for _, d := range dirs {
		s.Dirs = append(s.Dirs, d)
	}
	sort.Slice(s.Dirs, func(i, j int) bool { return s.Dirs[i].P < s.Dirs[j].P })
	dir := snapDirFor(a.Root)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	id := s.Time.Format("20060102-150405")
	tmp := filepath.Join(dir, id+".tmp")
	f, err := os.Create(tmp)
	if err != nil {
		return "", err
	}
	gz := gzip.NewWriter(f)
	err = json.NewEncoder(gz).Encode(&s)
	if e := gz.Close(); err == nil {
		err = e
	}
	if e := f.Close(); err == nil {
		err = e
	}
	if err == nil {
		err = os.Rename(tmp, filepath.Join(dir, id+".json.gz"))
	}
	if err != nil {
		os.Remove(tmp)
		return "", err
	}
	// keep the newest few
	if list := listSnapshots(a.Root); len(list) > snapKeep {
		for _, old := range list[snapKeep:] {
			os.Remove(filepath.Join(dir, old.ID+".json.gz"))
		}
	}
	return id, nil
}

func loadSnapshot(root, id string) (*snapshot, error) {
	if !snapIDRe.MatchString(id) {
		return nil, errors.New("неверный снимок")
	}
	f, err := os.Open(filepath.Join(snapDirFor(root), id+".json.gz"))
	if err != nil {
		return nil, err
	}
	defer f.Close()
	gz, err := gzip.NewReader(f)
	if err != nil {
		return nil, err
	}
	var s snapshot
	if err := json.NewDecoder(gz).Decode(&s); err != nil {
		return nil, err
	}
	return &s, nil
}

// listSnapshots returns the snapshots of a place, newest first.
func listSnapshots(root string) []snapInfo {
	ents, _ := os.ReadDir(snapDirFor(root))
	var out []snapInfo
	for _, e := range ents {
		id, ok := strings.CutSuffix(e.Name(), ".json.gz")
		if !ok || !snapIDRe.MatchString(id) {
			continue
		}
		t, err := time.ParseInLocation("20060102-150405", id, time.Local)
		if err != nil {
			continue
		}
		info := snapInfo{ID: id, Time: t}
		if s, err := loadSnapshotHead(root, id); err == nil {
			info.Size, info.Method = s.Size, s.Method
		}
		out = append(out, info)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID > out[j].ID })
	return out
}

// loadSnapshotHead reads only the totals (the list of folders comes last).
func loadSnapshotHead(root, id string) (*snapshot, error) {
	f, err := os.Open(filepath.Join(snapDirFor(root), id+".json.gz"))
	if err != nil {
		return nil, err
	}
	defer f.Close()
	gz, err := gzip.NewReader(f)
	if err != nil {
		return nil, err
	}
	buf := make([]byte, 512)
	n, _ := gz.Read(buf)
	var s snapshot
	head := string(buf[:n])
	if i := strings.Index(head, `,"dirs"`); i > 0 {
		json.Unmarshal([]byte(head[:i]+"}"), &s)
	}
	return &s, nil
}

// defaultSnapshot picks what to compare with: a week-old snapshot made the
// same way if there is one, otherwise the oldest earlier one.
func defaultSnapshot(list []snapInfo, current, method string) string {
	var earlier []snapInfo
	for _, s := range list {
		if s.ID != current && (current == "" || s.ID < current) {
			earlier = append(earlier, s)
		}
	}
	if len(earlier) == 0 {
		return ""
	}
	week := time.Now().Add(-6 * 24 * time.Hour)
	for _, s := range earlier { // newest first
		if !s.Time.After(week) && (s.Method == method || s.Method == "") {
			return s.ID
		}
	}
	for _, s := range earlier {
		if !s.Time.After(week) {
			return s.ID
		}
	}
	return earlier[len(earlier)-1].ID
}

type growthItem struct {
	Path  string `json:"path"`
	Name  string `json:"name"`
	Old   int64  `json:"old"`
	New   int64  `json:"new"`
	Delta int64  `json:"delta"`
	Added bool   `json:"added,omitempty"`
	Gone  bool   `json:"gone,omitempty"`
}

type growthResult struct {
	Snapshot  snapInfo     `json:"snapshot"`
	Snapshots []snapInfo   `json:"snapshots"`
	OldSize   int64        `json:"oldSize"`
	NewSize   int64        `json:"newSize"`
	Grew      []growthItem `json:"grew"`
	Shrank    []growthItem `json:"shrank"`
	MethodNow string       `json:"methodNow"`
}

// growth compares the current analysis with a snapshot. Chains of parents
// that grew only because one subfolder did are folded into that subfolder.
func (a *Analysis) growth(old *snapshot) growthResult {
	a.treeMu.RLock()
	cur := a.collectDirs()
	res := growthResult{OldSize: old.Size, NewSize: a.root.size, MethodNow: a.method}
	a.treeMu.RUnlock()
	prev := map[string]snapDir{}
	for _, d := range old.Dirs {
		prev[lower(d.P)] = d
	}
	min := growthMin
	if total := res.NewSize - res.OldSize; total/50 > min {
		min = total / 50
	}
	type cand struct {
		key string
		it  growthItem
	}
	pick := func(items []cand, sign int64) []growthItem {
		best := map[string]int64{} // biggest change among a folder's own candidates below it
		for _, c := range items {
			p := filepath.Dir(c.key)
			if d := c.it.Delta * sign; d > best[p] {
				best[p] = d
			}
		}
		var out []growthItem
		for _, c := range items {
			if float64(best[c.key]) >= 0.8*float64(c.it.Delta*sign) {
				continue // explained by one subfolder, which is listed itself
			}
			out = append(out, c.it)
		}
		sort.Slice(out, func(i, j int) bool { return out[i].Delta*sign > out[j].Delta*sign })
		return out
	}
	var up, down []cand
	for k, d := range cur {
		o := prev[k]
		delta := d.S - o.S
		it := growthItem{Path: filepath.Join(a.Root, d.P), Name: filepath.Base(d.P), Old: o.S, New: d.S, Delta: delta, Added: o.P == ""}
		if delta >= min {
			up = append(up, cand{k, it})
		} else if -delta >= min {
			down = append(down, cand{k, it})
		}
	}
	for k, o := range prev {
		if _, ok := cur[k]; !ok && o.S >= min {
			if fi, err := os.Stat(filepath.Join(a.Root, o.P)); err == nil && fi.IsDir() {
				continue // still there, just smaller than remembered folders
			}
			down = append(down, cand{k, growthItem{Path: filepath.Join(a.Root, o.P), Name: filepath.Base(o.P), Old: o.S, Delta: -o.S, Gone: true}})
		}
	}
	res.Grew = pick(up, 1)
	res.Shrank = pick(down, -1)
	if len(res.Grew) > 40 {
		res.Grew = res.Grew[:40]
	}
	if len(res.Shrank) > 15 {
		res.Shrank = res.Shrank[:15]
	}
	return res
}
