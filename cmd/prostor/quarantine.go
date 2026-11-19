package main

// Quarantine: instead of deleting, files are renamed into a hidden folder on
// the same volume (instant, no copying) and kept for a few days. The user can
// put them back with one click; after the term they are deleted for good.
//
// Layout: <qroot>\<batch id>\manifest.json + the moved items, where qroot is
// %LOCALAPPDATA%\Prostor\Quarantine for the volume that holds the profile and
// <volume>\ProstorQuarantine on other volumes. The index in the app data
// folder lists batch folders, so batches on other drives are found again.

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"prostor/internal/winapi"
)

type QEntry struct {
	Orig   string `json:"orig"`
	Stored string `json:"stored"` // name inside the batch folder
	Size   int64  `json:"size"`
	Dir    bool   `json:"dir"`
}

type QBatch struct {
	ID      string    `json:"id"`
	Time    time.Time `json:"time"`
	Expires time.Time `json:"expires"`
	Source  string    `json:"source"`
	Dir     string    `json:"dir"`
	Size    int64     `json:"size"`
	Items   []QEntry  `json:"items"`
}

var qMu sync.Mutex

func qIndexPath() string { return filepath.Join(appDataDir(), "quarantine.json") }

func qRootFor(path string) string {
	home := filepath.Join(appDataDir(), "Quarantine")
	if winapi.SameVolume(path, appDataDir()) {
		return home
	}
	vr := winapi.VolumeRoot(path)
	if vr == "" {
		return ""
	}
	return filepath.Join(vr, "ProstorQuarantine")
}

func qLoadIndex() []string {
	var dirs []string
	if b, err := os.ReadFile(qIndexPath()); err == nil {
		json.Unmarshal(b, &dirs)
	}
	return dirs
}

func qSaveIndex(dirs []string) {
	sort.Strings(dirs)
	writeFileAtomic(qIndexPath(), dirs)
}

func qReadBatch(dir string) (*QBatch, error) {
	b, err := os.ReadFile(filepath.Join(dir, "manifest.json"))
	if err != nil {
		return nil, err
	}
	var qb QBatch
	if err := json.Unmarshal(b, &qb); err != nil {
		return nil, err
	}
	qb.Dir = dir
	return &qb, nil
}

func qWriteBatch(qb *QBatch) error {
	qb.Size = 0
	for _, e := range qb.Items {
		qb.Size += e.Size
	}
	return writeFileAtomic(filepath.Join(qb.Dir, "manifest.json"), qb)
}

func newBatchID() string {
	b := make([]byte, 3)
	rand.Read(b)
	return time.Now().Format("20060102-150405") + "-" + hex.EncodeToString(b)
}

// safeStoredName keeps the original name readable but short.
func safeStoredName(i int, base string) string {
	r := []rune(base)
	if len(r) > 80 {
		ext := filepath.Ext(base)
		if len([]rune(ext)) > 16 {
			ext = ""
		}
		r = append(r[:80-len([]rune(ext))], []rune(ext)...)
	}
	return fmt.Sprintf("%05d_%s", i, string(r))
}

// QResult reports what happened to each requested path.
type QResult struct {
	Moved   []string          `json:"moved"`
	Failed  map[string]string `json:"failed"`
	Size    int64             `json:"size"`
	Batches []string          `json:"batches"`
}

// quarantinePaths moves paths into quarantine for the given number of days.
// Paths on a volume where no quarantine folder can be made are reported as
// failed with reason "volume", so the caller can fall back to the Recycle Bin.
func quarantinePaths(paths []string, source string, days int, jr *journal) QResult {
	qMu.Lock()
	defer qMu.Unlock()
	res := QResult{Failed: map[string]string{}}
	groups := map[string][]string{}
	var order []string
	for _, p := range paths {
		p = filepath.Clean(winapi.FullNames(p))
		root := qRootFor(p)
		if root == "" {
			res.Failed[p] = "volume"
			continue
		}
		if lp, lr := lower(p), lower(root); lp == lr || strings.HasPrefix(lr, lp+string(filepath.Separator)) || strings.HasPrefix(lp, lr+string(filepath.Separator)) {
			res.Failed[p] = "quarantine"
			continue
		}
		if _, ok := groups[root]; !ok {
			order = append(order, root)
		}
		groups[root] = append(groups[root], p)
	}
	index := qLoadIndex()
	now := time.Now()
	for _, root := range order {
		created := false
		if _, err := os.Stat(root); err != nil {
			created = true
		}
		dir := filepath.Join(root, newBatchID())
		if err := os.MkdirAll(dir, 0o755); err != nil {
			for _, p := range groups[root] {
				res.Failed[p] = "volume"
			}
			continue
		}
		if created && !strings.HasPrefix(lower(root), lower(appDataDir())) {
			winapi.SetHidden(root)
		}
		qb := &QBatch{ID: filepath.Base(dir), Time: now, Expires: now.Add(time.Duration(days) * 24 * time.Hour), Source: source, Dir: dir}
		for i, p := range groups[root] {
			fi, err := os.Lstat(p)
			if err != nil {
				res.Failed[p] = "missing"
				continue
			}
			size := fi.Size()
			if fi.IsDir() {
				size, _ = dirSize(p, nil)
			}
			name := safeStoredName(i+1, fi.Name())
			if err := os.Rename(p, filepath.Join(dir, name)); err != nil {
				res.Failed[p] = "busy"
				jr.add("f", p, size, source)
				continue
			}
			qb.Items = append(qb.Items, QEntry{Orig: p, Stored: name, Size: size, Dir: fi.IsDir()})
			res.Moved = append(res.Moved, p)
			res.Size += size
			jr.add("q", p, size, source)
		}
		if len(qb.Items) == 0 {
			os.Remove(dir)
			continue
		}
		if err := qWriteBatch(qb); err != nil {
			// without a manifest the files could not be restored: put them back
			for _, e := range qb.Items {
				os.Rename(filepath.Join(dir, e.Stored), e.Orig)
				res.Failed[e.Orig] = "manifest"
			}
			res.Moved, res.Size = nil, 0
			os.Remove(dir)
			continue
		}
		index = append(index, dir)
		res.Batches = append(res.Batches, qb.ID)
	}
	qSaveIndex(index)
	return res
}

// listQuarantine returns all batches, newest first; index entries whose
// folder is gone are dropped.
func listQuarantine() []*QBatch {
	qMu.Lock()
	defer qMu.Unlock()
	return qListLocked()
}

func qListLocked() []*QBatch {
	var out []*QBatch
	var keep []string
	for _, dir := range qLoadIndex() {
		qb, err := qReadBatch(dir)
		if err != nil {
			if _, e := os.Stat(dir); os.IsNotExist(e) {
				continue // the drive is gone for good or the folder was deleted
			}
			keep = append(keep, dir) // e.g. a removable drive that is not plugged in
			continue
		}
		keep = append(keep, dir)
		out = append(out, qb)
	}
	qSaveIndex(keep)
	sort.Slice(out, func(a, b int) bool { return out[a].Time.After(out[b].Time) })
	return out
}

func qFind(id string) (*QBatch, error) {
	for _, qb := range qListLocked() {
		if qb.ID == id {
			return qb, nil
		}
	}
	return nil, errors.New("в карантине нет такой записи")
}

// uniqueRestorePath avoids overwriting a file that took the old place.
func uniqueRestorePath(p string) string {
	if _, err := os.Lstat(p); os.IsNotExist(err) {
		return p
	}
	ext := filepath.Ext(p)
	base := strings.TrimSuffix(p, ext)
	for i := 1; ; i++ {
		suffix := " (восстановлено)"
		if i > 1 {
			suffix = fmt.Sprintf(" (восстановлено %d)", i)
		}
		c := base + suffix + ext
		if _, err := os.Lstat(c); os.IsNotExist(err) {
			return c
		}
	}
}

type RestoreResult struct {
	Restored []string          `json:"restored"`
	Renamed  map[string]string `json:"renamed"` // original -> where it went because the place was taken
	Failed   map[string]string `json:"failed"`
}

// restoreQuarantine puts items of a batch back; all items when origs is empty.
func restoreQuarantine(id string, origs []string) (RestoreResult, error) {
	qMu.Lock()
	defer qMu.Unlock()
	res := RestoreResult{Renamed: map[string]string{}, Failed: map[string]string{}}
	qb, err := qFind(id)
	if err != nil {
		return res, err
	}
	want := map[string]bool{}
	for _, o := range origs {
		want[lower(filepath.Clean(o))] = true
	}
	var left []QEntry
	for _, e := range qb.Items {
		if len(want) > 0 && !want[lower(e.Orig)] {
			left = append(left, e)
			continue
		}
		dst := uniqueRestorePath(e.Orig)
		os.MkdirAll(filepath.Dir(dst), 0o755)
		if err := os.Rename(filepath.Join(qb.Dir, e.Stored), dst); err != nil {
			res.Failed[e.Orig] = err.Error()
			left = append(left, e)
			continue
		}
		res.Restored = append(res.Restored, e.Orig)
		if dst != e.Orig {
			res.Renamed[e.Orig] = dst
		}
	}
	qb.Items = left
	if len(left) == 0 {
		removeTree(qb.Dir)
		qDropFromIndex(qb.Dir)
	} else {
		qWriteBatch(qb)
	}
	return res, nil
}

func qDropFromIndex(dir string) {
	var keep []string
	for _, d := range qLoadIndex() {
		if !strings.EqualFold(d, dir) {
			keep = append(keep, d)
		}
	}
	qSaveIndex(keep)
}

// purgeQuarantine deletes batches for good: expired ones (all=false), or
// every batch / the one with the given id.
func purgeQuarantine(id string, all bool) (freed int64, n int) {
	qMu.Lock()
	defer qMu.Unlock()
	now := time.Now()
	for _, qb := range qListLocked() {
		if id != "" && qb.ID != id {
			continue
		}
		if id == "" && !all && now.Before(qb.Expires) {
			continue
		}
		if removeTree(qb.Dir) {
			qDropFromIndex(qb.Dir)
			freed += qb.Size
			n++
		}
	}
	return
}

// removeTree deletes a folder with everything in it, clearing read-only
// flags; links inside are removed as links, never followed.
func removeTree(dir string) bool {
	var dirs []string
	filepath.WalkDir(dir, func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			return nil
		}
		if d.IsDir() {
			dirs = append(dirs, p)
			return nil
		}
		removeFile(p)
		return nil
	})
	for i := len(dirs) - 1; i >= 0; i-- {
		if err := os.Remove(dirs[i]); err != nil {
			os.Chmod(dirs[i], 0o777)
			os.Remove(dirs[i])
		}
	}
	_, err := os.Lstat(dir)
	return os.IsNotExist(err)
}

func quarantineSize() (size int64, batches int) {
	for _, qb := range listQuarantine() {
		size += qb.Size
		batches++
	}
	return
}
