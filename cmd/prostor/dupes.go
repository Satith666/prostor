package main

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"sync"

	"prostor/internal/winapi"
)

type dupFile struct {
	Path  string `json:"path"`
	MTime int64  `json:"mtime"`
}

type dupGroup struct {
	Size   int64     `json:"size"`
	Hash   string    `json:"hash"`
	Files  []dupFile `json:"files"`
	Wasted int64     `json:"wasted"`
}

type dupResult struct {
	Groups  []dupGroup `json:"groups"`
	Wasted  int64      `json:"wasted"`
	Scanned int64      `json:"scanned"`
	Root    string     `json:"root"`
}

func hashFile(path string, limit int64) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	var r io.Reader = f
	if limit > 0 {
		r = io.LimitReader(f, limit)
	}
	buf := make([]byte, 1<<20)
	if _, err := io.CopyBuffer(h, r, buf); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// groupBy hashes candidates in parallel and keeps groups with 2+ equal hashes.
func groupBy(j *Job, files []dupFile, limit int64) [][]dupFile {
	type res struct {
		f dupFile
		h string
	}
	out := make([]res, len(files))
	var wg sync.WaitGroup
	sem := make(chan struct{}, 4)
	for i, f := range files {
		if j.Cancelled() {
			break
		}
		wg.Add(1)
		sem <- struct{}{}
		go func(i int, f dupFile) {
			defer func() { <-sem; wg.Done() }()
			defer winapi.BackgroundThread()()
			h, err := hashFile(f.Path, limit)
			if err == nil {
				out[i] = res{f, h}
			}
		}(i, f)
	}
	wg.Wait()
	m := map[string][]dupFile{}
	for _, r := range out {
		if r.h != "" {
			m[r.h] = append(m[r.h], r.f)
		}
	}
	var groups [][]dupFile
	for _, g := range m {
		if len(g) > 1 {
			groups = append(groups, g)
		}
	}
	return groups
}

func findDuplicates(j *Job, root string, minSize int64) (*dupResult, error) {
	st, err := os.Stat(root)
	if err != nil || !st.IsDir() {
		return nil, errors.New("папка недоступна")
	}
	j.SetPhase("Поиск файлов")
	bySize := map[int64][]dupFile{}
	o := &walkOpts{cancel: j.Cancelled}
	var scanned int64
	o.walk(root, func(p string, fi fs.FileInfo) {
		scanned++
		j.Files.Store(scanned)
		if scanned%500 == 0 {
			j.SetCurrent(filepath.Dir(p))
		}
		if fi.Size() < minSize {
			return
		}
		bySize[fi.Size()] = append(bySize[fi.Size()], dupFile{Path: p, MTime: fi.ModTime().Unix()})
	}, nil)
	if j.Cancelled() {
		return nil, errors.New("поиск остановлен")
	}
	var sizes []int64
	var total int64
	for s, l := range bySize {
		if len(l) > 1 {
			sizes = append(sizes, s)
			total += int64(len(l))
		}
	}
	sort.Slice(sizes, func(a, b int) bool { return sizes[a] > sizes[b] })
	j.SetPhase("Сравнение содержимого")
	j.Total.Store(total)
	j.Done.Store(0)
	res := &dupResult{Root: root, Scanned: scanned}
	for _, size := range sizes {
		if j.Cancelled() {
			return nil, errors.New("поиск остановлен")
		}
		cands := bySize[size]
		j.SetCurrent(filepath.Base(cands[0].Path))
		// quick pass on the first 64 KB, then a full hash for files that still match
		quick := [][]dupFile{cands}
		if size > 64<<10 {
			quick = groupBy(j, cands, 64<<10)
		}
		for _, q := range quick {
			full := groupBy(j, q, 0)
			for _, g := range full {
				sort.Slice(g, func(a, b int) bool { return g[a].MTime > g[b].MTime })
				h, _ := hashFile(g[0].Path, 64<<10)
				if len(h) > 12 {
					h = h[:12]
				}
				dg := dupGroup{Size: size, Hash: h, Files: g, Wasted: size * int64(len(g)-1)}
				res.Groups = append(res.Groups, dg)
				res.Wasted += dg.Wasted
			}
		}
		j.Done.Add(int64(len(cands)))
		j.Bytes.Store(res.Wasted)
		if len(res.Groups) >= 3000 {
			break
		}
	}
	sort.Slice(res.Groups, func(a, b int) bool { return res.Groups[a].Wasted > res.Groups[b].Wasted })
	return res, nil
}
