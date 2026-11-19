package main

// Analysing a whole NTFS drive by reading its master file table: one
// sequential read instead of listing hundreds of thousands of folders.
// Needs administrator rights (the elevated helper has them); on any problem
// the analyzer falls back to the ordinary walk.

import (
	"container/heap"
	"errors"
	"log"
	"os"
	"path/filepath"
	"runtime/debug"
	"strings"

	"prostor/internal/ntfs"
	"prostor/internal/winapi"
)

// mftPossible: Windows, administrator, a whole NTFS drive.
func (a *Analysis) mftPossible() bool {
	if !winapi.IsWindows || !isAdmin || !a.isDrive || os.Getenv("PROSTOR_NO_MFT") == "1" {
		return false
	}
	for _, d := range winapi.Drives() {
		if samePath(d.Root, a.Root) {
			return strings.EqualFold(d.FS, "NTFS")
		}
	}
	return false
}

func (a *Analysis) runMFT() error {
	r, closeVol, err := winapi.OpenVolume(a.Root)
	if err != nil {
		return err
	}
	defer closeVol()
	v, err := ntfs.Open(r)
	if err != nil {
		return err
	}
	a.job.SetPhase("Чтение таблицы файлов " + strings.TrimRight(a.Root, `\`))
	a.job.Total.Store(v.Records())
	recs, err := v.Scan(func(done, total int64) { a.job.Done.Store(done) }, a.job.Cancelled)
	if err != nil {
		return err
	}
	if len(recs) <= ntfs.RecRoot || !recs[ntfs.RecRoot].Dir {
		return errors.New("нет корневой папки")
	}
	a.job.SetPhase("Подсчёт размеров")
	a.buildFromMFT(recs)
	recs = nil
	debug.FreeOSMemory()
	return nil
}

// mftTop is a candidate for the biggest files; its path is built at the end.
type mftTop struct {
	node  *dirNode
	name  string
	size  int64
	mtime int64
	cat   string
}

type mftHeap []mftTop

func (h mftHeap) Len() int           { return len(h) }
func (h mftHeap) Less(i, j int) bool { return h[i].size < h[j].size }
func (h mftHeap) Swap(i, j int)      { h[i], h[j] = h[j], h[i] }
func (h *mftHeap) Push(x any)        { *h = append(*h, x.(mftTop)) }
func (h *mftHeap) Pop() any          { o := *h; x := o[len(o)-1]; *h = o[:len(o)-1]; return x }

func (a *Analysis) nodePath(n *dirNode) string {
	var parts []string
	for x := n; x != nil && x != a.root; x = x.parent {
		parts = append(parts, x.name)
	}
	p := a.Root
	for i := len(parts) - 1; i >= 0; i-- {
		p = filepath.Join(p, parts[i])
	}
	return p
}

// buildFromMFT turns file records into the same tree and statistics the walk
// produces. NTFS metadata ($MFT, $LogFile, $Extend…) stays out: it is part of
// the "system and inaccessible" remainder, as with the walk.
func (a *Analysis) buildFromMFT(recs []ntfs.Rec) {
	n := len(recs)
	nodes := make([]*dirNode, n)
	nodes[ntfs.RecRoot] = a.root
	for i := ntfs.FirstUser; i < n; i++ {
		r := &recs[i]
		if r.Dir && r.Name != "" && !r.Link {
			nodes[i] = &dirNode{name: r.Name}
		}
	}
	// a folder is kept when its parents lead to the root (not through $Extend)
	const (
		unknown = 0
		keep    = 1
		drop    = 2
	)
	state := make([]uint8, n)
	state[ntfs.RecRoot] = keep
	var path []int
	for i := ntfs.FirstUser; i < n; i++ {
		if nodes[i] == nil || state[i] != unknown {
			continue
		}
		path = path[:0]
		res := uint8(drop)
		for j, steps := i, 0; ; steps++ {
			if state[j] != unknown {
				res = state[j]
				break
			}
			if nodes[j] == nil || steps > 4096 {
				break
			}
			path = append(path, j)
			p := int(recs[j].Parent)
			if p == j || p >= n || p == ntfs.RecExtend {
				break
			}
			j = p
		}
		for _, k := range path {
			state[k] = res
		}
	}
	for i := ntfs.FirstUser; i < n; i++ {
		if nodes[i] == nil {
			continue
		}
		if state[i] != keep {
			nodes[i] = nil
			continue
		}
		p := nodes[recs[i].Parent]
		nodes[i].parent = p
		p.children = append(p.children, nodes[i])
	}

	// files
	cats := map[string]*stat{}
	exts := map[string]*stat{}
	var ages [5]stat
	var top mftHeap
	var files, bytes int64
	for i := ntfs.FirstUser; i < n; i++ {
		r := &recs[i]
		if r.Dir || r.Name == "" || r.Link {
			continue
		}
		p := int(r.Parent)
		if p >= n || nodes[p] == nil {
			continue
		}
		nd := nodes[p]
		size := r.Alloc
		if r.Attr&attrCloudMask != 0 && r.Alloc < r.Size {
			a.cloud.Add(r.Size - r.Alloc)
		}
		if r.Links > 1 && size > 0 {
			a.hardlinks.Add(size * int64(r.Links-1)) // counted once, as it takes space once
		}
		nd.own += size
		nd.ownFiles++
		if r.MTime > nd.newest {
			nd.newest = r.MTime
		}
		files++
		bytes += size
		cat := categoryOf(r.Name)
		if s := cats[cat]; s != nil {
			s.Size += size
			s.Count++
		} else {
			cats[cat] = &stat{size, 1}
		}
		ext := lower(filepath.Ext(r.Name))
		if ext == "" || len(ext) > 12 {
			ext = "(без расширения)"
		}
		if s := exts[ext]; s != nil {
			s.Size += size
			s.Count++
		} else {
			exts[ext] = &stat{size, 1}
		}
		b := ageBucket(a.now, r.MTime)
		ages[b].Size += size
		ages[b].Count++
		if len(top) < topN {
			heap.Push(&top, mftTop{nd, r.Name, size, r.MTime, cat})
		} else if size > top[0].size {
			top[0] = mftTop{nd, r.Name, size, r.MTime, cat}
			heap.Fix(&top, 0)
		}
	}
	a.mu.Lock()
	a.cats, a.exts, a.ages = cats, exts, ages
	a.top = a.top[:0]
	for _, t := range top {
		a.top = append(a.top, fileRec{Path: filepath.Join(a.nodePath(t.node), t.name), Size: t.size, MTime: t.mtime, Cat: t.cat})
	}
	heap.Init(&a.top)
	if len(a.top) >= topN {
		a.topMin.Store(a.top[0].Size)
	}
	a.mu.Unlock()
	a.job.Files.Store(files)
	a.job.Bytes.Store(bytes)
	log.Printf("mft %s: %d records, %d files", a.Root, n, files)
}
