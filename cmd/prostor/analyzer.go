package main

import (
	"container/heap"
	"errors"
	"log"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"prostor/internal/winapi"
)

// ---------------------------------------------------------------- disk analysis
//
// The analyzer walks a folder tree with a small pool of background-priority
// threads, reading each directory in one bulk call. Sizes are "size on disk"
// (cloud-only files count as zero), hard links inside the Windows folder are
// counted once, and for a whole drive the part that could not be listed is
// reported separately so the picture adds up to the drive's used space.

type dirNode struct {
	name     string
	parent   *dirNode
	children []*dirNode // sorted by size, biggest first, once the scan is done
	size     int64
	files    int64
	dirs     int64
	own      int64 // files directly in this folder
	ownFiles int64
	newest   int64 // unix seconds of the newest file inside
	denied   bool
	sys      bool // inside %SystemRoot% (hard links are deduplicated there)
}

type fileRec struct {
	Path  string `json:"path"`
	Size  int64  `json:"size"`
	MTime int64  `json:"mtime"`
	Cat   string `json:"cat"`
}

type topHeap []fileRec

func (h topHeap) Len() int           { return len(h) }
func (h topHeap) Less(i, j int) bool { return h[i].Size < h[j].Size }
func (h topHeap) Swap(i, j int)      { h[i], h[j] = h[j], h[i] }
func (h *topHeap) Push(x any)        { *h = append(*h, x.(fileRec)) }
func (h *topHeap) Pop() any          { o := *h; x := o[len(o)-1]; *h = o[:len(o)-1]; return x }

type stat struct {
	Size  int64 `json:"size"`
	Count int64 `json:"count"`
}

type Analysis struct {
	Root   string
	root   *dirNode
	mu     sync.Mutex   // guards top, cats, exts, ages
	treeMu sync.RWMutex // guards the finished tree against deletions
	top    topHeap
	topMin atomic.Int64
	old    topHeap // biggest files not changed for more than a year
	oldMin atomic.Int64
	cats   map[string]*stat
	exts   map[string]*stat
	ages   [5]stat

	cloud     atomic.Int64 // logical size of cloud-only files (not on disk)
	denied    atomic.Int64 // folders that could not be listed
	hardlinks atomic.Int64 // bytes skipped as extra hard links

	sysRoot string
	links   [16]linkShard

	isDrive   bool
	diskTotal int64
	diskUsed  int64
	other     int64 // used space the scan could not see (system metadata, no access)

	job    *Job
	now    int64
	done   bool
	took   float64
	method string // "mft" — read from the NTFS file table, "walk" — folder by folder
	snapID string // snapshot saved after this analysis
}

type linkShard struct {
	mu   sync.Mutex
	seen map[uint64]struct{}
}

// firstLink reports whether a file id is seen for the first time.
func (a *Analysis) firstLink(id uint64) bool {
	s := &a.links[id%uint64(len(a.links))]
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.seen == nil {
		s.seen = map[uint64]struct{}{}
	}
	if _, ok := s.seen[id]; ok {
		return false
	}
	s.seen[id] = struct{}{}
	return true
}

var (
	analysisMu sync.RWMutex
	analysis   *Analysis
)

const topN = 400

var catNames = map[string]string{
	"video": "Видео", "image": "Изображения", "audio": "Музыка и звук", "archive": "Архивы",
	"diskimage": "Образы дисков", "program": "Программы и библиотеки", "document": "Документы",
	"code": "Исходный код", "model": "3D и проекты", "data": "Данные и кэш", "other": "Прочее",
}

var extCats = func() map[string]string {
	m := map[string]string{}
	add := func(cat string, exts string) {
		for _, e := range strings.Fields(exts) {
			m["."+e] = cat
		}
	}
	add("video", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts mts 3gp vob")
	add("image", "jpg jpeg png gif bmp webp tif tiff heic heif raw cr2 cr3 nef arw dng psd svg ico dds tga exr kra xcf")
	add("audio", "mp3 wav flac aac ogg m4a wma opus aiff aif mid midi")
	add("archive", "zip rar 7z tar gz tgz bz2 xz zst cab lz4")
	add("diskimage", "iso img vhd vhdx vmdk vdi qcow2 wim esd swm")
	add("program", "exe dll sys msi msix msixbundle appx appxbundle drv ocx mui")
	add("document", "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf epub djvu fb2 md csv")
	add("code", "py js ts tsx jsx go c cpp h hpp cs java kt json xml html htm css scss lua luau rs php rb sh bat cmd ps1 yml yaml toml ipynb")
	add("model", "blend blend1 fbx obj glb gltf stl max 3ds dae usd usdz rbxl rbxlx rbxm rbxmx unitypackage uasset umap")
	add("data", "db sqlite sqlite3 ldb log tmp dat cache bin pak vpk etl edb dmp bak old")
	return m
}()

func categoryOf(name string) string {
	ext := lower(filepath.Ext(name))
	if c, ok := extCats[ext]; ok {
		return c
	}
	return "other"
}

func ageBucket(now, mtime int64) int {
	d := (now - mtime) / 86400
	switch {
	case d < 30:
		return 0
	case d < 180:
		return 1
	case d < 365:
		return 2
	case d < 3*365:
		return 3
	}
	return 4
}

func newAnalysis(root string, j *Job) *Analysis {
	a := &Analysis{
		Root: root, job: j, root: &dirNode{name: root},
		cats: map[string]*stat{}, exts: map[string]*stat{}, now: time.Now().Unix(),
	}
	if sr := os.Getenv("SystemRoot"); sr != "" {
		a.sysRoot = lower(filepath.Clean(sr))
	}
	a.root.sys = a.sysRoot != "" && lower(filepath.Clean(root)) == a.sysRoot
	for _, d := range winapi.Drives() {
		if samePath(d.Root, root) {
			a.isDrive = true
			a.diskTotal = int64(d.Total)
			a.diskUsed = int64(d.Total - d.Free)
		}
	}
	return a
}

func samePath(a, b string) bool {
	clean := func(p string) string { return strings.TrimRight(lower(filepath.Clean(p)), `\/`) }
	return clean(a) == clean(b)
}

func analyzerWorkers() int {
	n := runtime.NumCPU()
	if n > 6 {
		n = 6
	}
	if n < 2 {
		n = 2
	}
	return n
}

type walkTask struct {
	n    *dirNode
	path string
}

func (a *Analysis) run() {
	start := time.Now()
	if a.mftPossible() {
		err := a.runMFT()
		if err == nil {
			a.method = "mft"
			a.finish(start)
			return
		}
		if a.job.Cancelled() {
			a.finish(start)
			return
		}
		log.Printf("mft %s: %v, falling back to the walk", a.Root, err)
		a.job.Done.Store(0)
		a.job.Total.Store(0)
		a.job.Files.Store(0)
		a.job.Bytes.Store(0)
		a.job.SetPhase("Сканирование " + a.Root)
	}
	a.method = "walk"
	var (
		mu      sync.Mutex
		cond    = sync.NewCond(&mu)
		stack   = []walkTask{{a.root, a.Root}}
		pending = 1
	)
	push := func(ts []walkTask) {
		if len(ts) == 0 {
			return
		}
		mu.Lock()
		stack = append(stack, ts...)
		pending += len(ts)
		mu.Unlock()
		cond.Broadcast()
	}
	var wg sync.WaitGroup
	for i := 0; i < analyzerWorkers(); i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			defer winapi.BackgroundThread()()
			for {
				mu.Lock()
				for len(stack) == 0 && pending > 0 {
					cond.Wait()
				}
				if pending == 0 {
					mu.Unlock()
					return
				}
				t := stack[len(stack)-1]
				stack = stack[:len(stack)-1]
				mu.Unlock()

				push(a.scanDir(t))

				mu.Lock()
				pending--
				last := pending == 0
				mu.Unlock()
				if last {
					cond.Broadcast()
				}
			}
		}()
	}
	wg.Wait()
	a.finish(start)
}

func (a *Analysis) finish(start time.Time) {
	a.finalize()
	a.took = time.Since(start).Seconds()
	if a.isDrive && a.diskUsed > a.root.size {
		a.other = a.diskUsed - a.root.size
	}
	for i := range a.links {
		a.links[i].seen = nil // free memory
	}
	a.done = true
}

const (
	attrCloudMask = attrOffline | attrRecallOpen | attrRecallData
)

// scanDir lists one folder and returns its subfolders to visit.
func (a *Analysis) scanDir(t walkTask) []walkTask {
	if a.job.Cancelled() {
		return nil
	}
	n := t.n
	ents, err := winapi.ReadDir(t.path)
	if err != nil {
		n.denied = true
		a.denied.Add(1)
		return nil
	}
	a.job.SetCurrent(t.path)
	type local struct{ size, count int64 }
	cats := map[string]*local{}
	exts := map[string]*local{}
	var ages [5]local
	var tops, olds []fileRec
	var next []walkTask
	min := a.topMin.Load()
	oldMin := a.oldMin.Load()
	for _, e := range ents {
		if e.Link {
			continue
		}
		if e.IsDir {
			p := filepath.Join(t.path, e.Name)
			c := &dirNode{name: e.Name, parent: n, sys: n.sys}
			if !c.sys && a.sysRoot != "" && lower(p) == a.sysRoot {
				c.sys = true
			}
			n.children = append(n.children, c)
			next = append(next, walkTask{c, p})
			continue
		}
		size := e.Alloc
		if e.Attr&attrCloudMask != 0 && e.Alloc < e.Size {
			a.cloud.Add(e.Size - e.Alloc)
		}
		if n.sys && e.ID != 0 && !a.firstLink(e.ID) {
			a.hardlinks.Add(size)
			continue // another hard link of an already counted file
		}
		n.own += size
		n.ownFiles++
		if e.MTime > n.newest {
			n.newest = e.MTime
		}
		cat := categoryOf(e.Name)
		if l := cats[cat]; l != nil {
			l.size += size
			l.count++
		} else {
			cats[cat] = &local{size, 1}
		}
		ext := lower(filepath.Ext(e.Name))
		if ext == "" || len(ext) > 12 {
			ext = "(без расширения)"
		}
		if l := exts[ext]; l != nil {
			l.size += size
			l.count++
		} else {
			exts[ext] = &local{size, 1}
		}
		b := ageBucket(a.now, e.MTime)
		ages[b].size += size
		ages[b].count++
		if size > min {
			tops = append(tops, fileRec{Path: filepath.Join(t.path, e.Name), Size: size, MTime: e.MTime, Cat: cat})
		}
		if size > oldMin && e.MTime > 0 && a.now-e.MTime > oldAge {
			olds = append(olds, fileRec{Path: filepath.Join(t.path, e.Name), Size: size, MTime: e.MTime, Cat: cat})
		}
	}
	a.job.Files.Add(n.ownFiles)
	a.job.Bytes.Add(n.own)
	a.job.Done.Add(1)

	a.mu.Lock()
	for k, v := range cats {
		s := a.cats[k]
		if s == nil {
			s = &stat{}
			a.cats[k] = s
		}
		s.Size += v.size
		s.Count += v.count
	}
	for k, v := range exts {
		s := a.exts[k]
		if s == nil {
			s = &stat{}
			a.exts[k] = s
		}
		s.Size += v.size
		s.Count += v.count
	}
	for i := range ages {
		a.ages[i].Size += ages[i].size
		a.ages[i].Count += ages[i].count
	}
	for _, r := range tops {
		if len(a.top) < topN {
			heap.Push(&a.top, r)
		} else if r.Size > a.top[0].Size {
			a.top[0] = r
			heap.Fix(&a.top, 0)
		}
	}
	if len(a.top) >= topN {
		a.topMin.Store(a.top[0].Size)
	}
	for _, r := range olds {
		a.pushOld(r)
	}
	a.mu.Unlock()
	return next
}

// oldAge: files not changed for this long are offered in "not used for a year".
const oldAge = 365 * 86400

// pushOld adds a candidate to the list of big old files (a.mu held).
func (a *Analysis) pushOld(r fileRec) {
	if len(a.old) < topN {
		heap.Push(&a.old, r)
	} else if r.Size > a.old[0].Size {
		a.old[0] = r
		heap.Fix(&a.old, 0)
	}
	if len(a.old) >= topN {
		a.oldMin.Store(a.old[0].Size)
	}
}

// finalize adds up folder totals bottom-up and sorts every folder's children.
func (a *Analysis) finalize() {
	var order []*dirNode
	stack := []*dirNode{a.root}
	for len(stack) > 0 {
		n := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		order = append(order, n)
		stack = append(stack, n.children...)
	}
	for i := len(order) - 1; i >= 0; i-- {
		n := order[i]
		n.size, n.files, n.dirs = n.own, n.ownFiles, int64(len(n.children))
		for _, c := range n.children {
			n.size += c.size
			n.files += c.files
			n.dirs += c.dirs
			if c.newest > n.newest {
				n.newest = c.newest
			}
		}
		sortNodes(n.children)
	}
}

func sortNodes(ns []*dirNode) {
	sort.Slice(ns, func(i, j int) bool {
		if ns[i].size != ns[j].size {
			return ns[i].size > ns[j].size
		}
		return ns[i].name < ns[j].name
	})
}

// ---------------------------------------------------------------- queries

func (a *Analysis) find(path string) *dirNode {
	rp := filepath.Clean(a.Root)
	p := filepath.Clean(path)
	if samePath(p, rp) {
		return a.root
	}
	prefix := rp
	if !strings.HasSuffix(prefix, string(filepath.Separator)) {
		prefix += string(filepath.Separator)
	}
	if !strings.HasPrefix(lower(p), lower(prefix)) {
		return nil
	}
	n := a.root
	for _, part := range strings.Split(p[len(prefix):], string(filepath.Separator)) {
		var next *dirNode
		for _, c := range n.children {
			if c.name == part {
				next = c
				break
			}
		}
		if next == nil {
			lp := lower(part)
			for _, c := range n.children {
				if lower(c.name) == lp {
					next = c
					break
				}
			}
		}
		if next == nil {
			return nil
		}
		n = next
	}
	return n
}

func (a *Analysis) pathOf(n *dirNode) string {
	var parts []string
	for x := n; x != nil && x.parent != nil; x = x.parent {
		parts = append(parts, x.name)
	}
	p := a.Root
	for i := len(parts) - 1; i >= 0; i-- {
		p = filepath.Join(p, parts[i])
	}
	return p
}

// kid is one block inside a folder's tile on the map.
type kid struct {
	Name  string `json:"name"`
	Size  int64  `json:"size"`
	IsDir bool   `json:"isDir,omitempty"`
	Kind  string `json:"kind,omitempty"` // files | rest
}

type nodeEntry struct {
	Name   string `json:"name"`
	Path   string `json:"path"`
	Size   int64  `json:"size"`
	Files  int64  `json:"files"`
	Dirs   int64  `json:"dirs"`
	IsDir  bool   `json:"isDir"`
	Denied bool   `json:"denied,omitempty"`
	MTime  int64  `json:"mtime"`
	Cat    string `json:"cat,omitempty"`
	Group  bool   `json:"group,omitempty"`
	Kids   []kid  `json:"kids,omitempty"`
}

type crumb struct {
	Name string `json:"name"`
	Path string `json:"path"`
}

type nodeView struct {
	Path      string      `json:"path"`
	Name      string      `json:"name"`
	Root      string      `json:"root"`
	Parent    string      `json:"parent"`
	Size      int64       `json:"size"`
	Files     int64       `json:"files"`
	Dirs      int64       `json:"dirs"`
	Items     []nodeEntry `json:"items"`
	Crumbs    []crumb     `json:"crumbs"`
	Other     int64       `json:"other,omitempty"`     // drive root only: space the scan could not see
	Free      int64       `json:"free,omitempty"`      // drive root only
	DiskTotal int64       `json:"diskTotal,omitempty"` // drive root only
}

const maxKids = 14

// kidsOf lists what a folder's tile shows inside. A chain of single
// subfolders (Users → user) is skipped so the map shows real content.
func kidsOf(n *dirNode) []kid {
	prefix := ""
	for depth := 0; depth < 4 && len(n.children) == 1 && n.own == 0 && n.children[0].size > 0; depth++ {
		n = n.children[0]
		prefix += n.name + string(filepath.Separator)
	}
	var out []kid
	var rest int64
	for i, c := range n.children {
		if c.size <= 0 {
			continue
		}
		if i < maxKids {
			out = append(out, kid{Name: prefix + c.name, Size: c.size, IsDir: true})
		} else {
			rest += c.size
		}
	}
	if n.own > 0 {
		out = append(out, kid{Name: "Файлы", Size: n.own, Kind: "files"})
	}
	if rest > 0 {
		out = append(out, kid{Name: "Остальное", Size: rest, Kind: "rest"})
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].Size > out[j].Size })
	return out
}

func (a *Analysis) view(path string) (*nodeView, error) {
	a.treeMu.RLock()
	defer a.treeMu.RUnlock()
	n := a.find(path)
	if n == nil {
		return nil, errors.New("папка не найдена в результатах анализа")
	}
	p := a.pathOf(n)
	v := &nodeView{Path: p, Name: n.name, Root: a.Root, Size: n.size, Files: n.files, Dirs: n.dirs}
	if n.parent != nil {
		v.Parent = a.pathOf(n.parent)
	}
	for x := n; x != nil; x = x.parent {
		v.Crumbs = append([]crumb{{Name: x.name, Path: a.pathOf(x)}}, v.Crumbs...)
	}
	const maxDirs = 400
	for i, c := range n.children {
		if i >= maxDirs {
			break
		}
		v.Items = append(v.Items, nodeEntry{Name: c.name, Path: filepath.Join(p, c.name), Size: c.size, Files: c.files,
			Dirs: c.dirs, IsDir: true, Denied: c.denied, MTime: c.newest, Kids: kidsOf(c)})
	}
	// files of this folder, read live (the tree keeps only folder totals)
	if n.ownFiles > 0 {
		if ents, err := winapi.ReadDir(p); err == nil {
			var files []nodeEntry
			for _, e := range ents {
				if e.IsDir || e.Link {
					continue
				}
				files = append(files, nodeEntry{Name: e.Name, Path: filepath.Join(p, e.Name), Size: e.Alloc,
					Files: 1, MTime: e.MTime, Cat: categoryOf(e.Name)})
			}
			sort.Slice(files, func(i, j int) bool { return files[i].Size > files[j].Size })
			const maxFiles = 150
			if len(files) > maxFiles {
				rest := nodeEntry{Name: "Остальные файлы", Group: true}
				for _, f := range files[maxFiles:] {
					rest.Size += f.Size
					rest.Files++
				}
				files = append(files[:maxFiles], rest)
			}
			v.Items = append(v.Items, files...)
		}
	}
	sort.SliceStable(v.Items, func(i, j int) bool { return v.Items[i].Size > v.Items[j].Size })
	if n == a.root && a.isDrive {
		v.Other = a.other
		v.DiskTotal = a.diskTotal
		v.Free = a.diskTotal - a.diskUsed
	}
	return v, nil
}

type typeView struct {
	Cats      []map[string]any `json:"cats"`
	Exts      []map[string]any `json:"exts"`
	Ages      []map[string]any `json:"ages"`
	Total     int64            `json:"total"`
	Cloud     int64            `json:"cloud"`
	Denied    int64            `json:"denied"`
	Hardlinks int64            `json:"hardlinks"`
	Other     int64            `json:"other"`
}

func (a *Analysis) types() typeView {
	a.mu.Lock()
	defer a.mu.Unlock()
	tv := typeView{Total: a.root.size, Cloud: a.cloud.Load(), Denied: a.denied.Load(), Hardlinks: a.hardlinks.Load(), Other: a.other}
	for k, s := range a.cats {
		tv.Cats = append(tv.Cats, map[string]any{"id": k, "name": catNames[k], "size": s.Size, "count": s.Count})
	}
	sort.Slice(tv.Cats, func(i, j int) bool { return tv.Cats[i]["size"].(int64) > tv.Cats[j]["size"].(int64) })
	type kv struct {
		k string
		s *stat
	}
	var list []kv
	for k, s := range a.exts {
		list = append(list, kv{k, s})
	}
	sort.Slice(list, func(i, j int) bool { return list[i].s.Size > list[j].s.Size })
	if len(list) > 30 {
		list = list[:30]
	}
	for _, e := range list {
		tv.Exts = append(tv.Exts, map[string]any{"ext": e.k, "size": e.s.Size, "count": e.s.Count, "cat": categoryOf("x" + e.k)})
	}
	labels := []string{"За последний месяц", "1–6 месяцев назад", "6–12 месяцев назад", "1–3 года назад", "Больше 3 лет назад"}
	for i, s := range a.ages {
		tv.Ages = append(tv.Ages, map[string]any{"name": labels[i], "size": s.Size, "count": s.Count})
	}
	return tv
}

func (a *Analysis) topFiles() []fileRec {
	a.mu.Lock()
	out := append([]fileRec(nil), a.top...)
	a.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].Size > out[j].Size })
	return out
}

func (a *Analysis) oldFiles() []fileRec {
	a.mu.Lock()
	out := append([]fileRec(nil), a.old...)
	a.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].Size > out[j].Size })
	return out
}

func (a *Analysis) summary() map[string]any {
	return map[string]any{"root": a.Root, "size": a.root.size, "files": a.root.files, "dirs": a.root.dirs,
		"took": a.took, "denied": a.denied.Load(), "hardlinks": a.hardlinks.Load(), "other": a.other,
		"isDrive": a.isDrive, "diskTotal": a.diskTotal, "diskUsed": a.diskUsed, "method": a.method,
		"snapshots": len(listSnapshots(a.Root))}
}

// forget updates the tree after a file or folder was deleted.
func (a *Analysis) forget(path string, size int64, isDir bool) {
	a.treeMu.Lock()
	defer a.treeMu.Unlock()
	parent := a.find(filepath.Dir(path))
	if parent == nil {
		return
	}
	var files int64
	if isDir {
		name := filepath.Base(path)
		found := false
		for i, c := range parent.children {
			if strings.EqualFold(c.name, name) {
				size, files = c.size, c.files
				parent.children = append(parent.children[:i], parent.children[i+1:]...)
				found = true
				break
			}
		}
		if !found {
			return
		}
	} else {
		if size > parent.own {
			size = parent.own
		}
		parent.own -= size
		parent.ownFiles--
		files = 1
	}
	for x := parent; x != nil; x = x.parent {
		x.size -= size
		x.files -= files
		if x.parent != nil {
			sortNodes(x.parent.children)
		}
	}
	a.mu.Lock()
	lp := lower(path)
	kept := a.top[:0]
	for _, f := range a.top {
		lf := lower(f.Path)
		if lf == lp || strings.HasPrefix(lf, lp+string(filepath.Separator)) {
			continue
		}
		kept = append(kept, f)
	}
	a.top = kept
	heap.Init(&a.top)
	keptOld := a.old[:0]
	for _, f := range a.old {
		lf := lower(f.Path)
		if lf == lp || strings.HasPrefix(lf, lp+string(filepath.Separator)) {
			continue
		}
		keptOld = append(keptOld, f)
	}
	a.old = keptOld
	heap.Init(&a.old)
	a.mu.Unlock()
}

// resize changes a folder's size after something inside it shrank (compression).
func (a *Analysis) resize(path string, delta int64) {
	a.treeMu.Lock()
	defer a.treeMu.Unlock()
	n := a.find(path)
	if n == nil {
		return
	}
	n.own += delta
	if n.own < 0 {
		n.own = 0
	}
	for x := n; x != nil; x = x.parent {
		x.size += delta
		if x.parent != nil {
			sortNodes(x.parent.children)
		}
	}
}

func normalizeRoot(path string) string {
	path = filepath.Clean(strings.TrimSpace(path))
	if winapi.IsWindows && len(path) == 2 && path[1] == ':' {
		path += `\`
	}
	return path
}

func startAnalysis(path string) error {
	path = normalizeRoot(path)
	st, err := os.Stat(path)
	if err != nil || !st.IsDir() {
		return errors.New("папка недоступна: " + path)
	}
	_, err = startJob("analyze", func(j *Job) (any, error) {
		a := newAnalysis(path, j)
		j.SetPhase("Сканирование " + path)
		analysisMu.Lock()
		analysis = a
		analysisMu.Unlock()
		a.run()
		if j.Cancelled() {
			return nil, errors.New("анализ остановлен")
		}
		if id, err := a.saveSnapshot(); err == nil {
			a.snapID = id
		} else {
			log.Printf("snapshot: %v", err)
		}
		return a.summary(), nil
	})
	return err
}

func currentAnalysis() *Analysis {
	analysisMu.RLock()
	defer analysisMu.RUnlock()
	if analysis == nil || !analysis.done {
		return nil
	}
	return analysis
}
