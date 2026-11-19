package main

// Similar photos: series of shots and resized copies. Each picture gets a
// 64-bit difference hash (dHash) of its brightness pattern; pictures whose
// hashes differ in at most a few bits look alike. Camera JPEGs carry a small
// EXIF preview, which is decoded instead of the full image — that keeps the
// search fast on thousands of photos.

import (
	"bytes"
	"encoding/binary"
	"errors"
	"image"
	"image/jpeg"
	_ "image/png" // PNG screenshots and exports
	"io"
	"io/fs"
	"math/bits"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"sync"

	"prostor/internal/winapi"
)

type photoFile struct {
	Path  string `json:"path"`
	Size  int64  `json:"size"`
	MTime int64  `json:"mtime"`
	W     int    `json:"w"`
	H     int    `json:"h"`
	Best  bool   `json:"best"`
	hash  uint64
}

type photoGroup struct {
	Files  []photoFile `json:"files"`
	Wasted int64       `json:"wasted"` // everything but the best copy
}

type photoResult struct {
	Groups  []photoGroup `json:"groups"`
	Wasted  int64        `json:"wasted"`
	Scanned int64        `json:"scanned"`
	Root    string       `json:"root"`
}

var photoMinSize int64 = 20 << 10 // smaller pictures are icons and previews

const (
	photoMaxFiles = 30000
	photoMaxDist  = 5 // differing bits of 64
)

// exifThumb returns the JPEG preview stored in a JPEG's EXIF block, if any.
func exifThumb(head []byte) []byte {
	if len(head) < 4 || head[0] != 0xFF || head[1] != 0xD8 {
		return nil
	}
	for i := 2; i+4 <= len(head); {
		if head[i] != 0xFF {
			return nil
		}
		marker := head[i+1]
		ln := int(binary.BigEndian.Uint16(head[i+2:]))
		if marker == 0xDA || ln < 2 || i+2+ln > len(head) {
			return nil
		}
		seg := head[i+4 : i+2+ln]
		if marker == 0xE1 && len(seg) > 14 && string(seg[:6]) == "Exif\x00\x00" {
			return thumbFromTIFF(seg[6:])
		}
		i += 2 + ln
	}
	return nil
}

func thumbFromTIFF(t []byte) []byte {
	if len(t) < 8 {
		return nil
	}
	var bo binary.ByteOrder
	switch string(t[:2]) {
	case "II":
		bo = binary.LittleEndian
	case "MM":
		bo = binary.BigEndian
	default:
		return nil
	}
	ifd := int(bo.Uint32(t[4:]))
	// skip IFD0 to IFD1, which describes the thumbnail
	if ifd+2 > len(t) {
		return nil
	}
	n := int(bo.Uint16(t[ifd:]))
	next := ifd + 2 + 12*n
	if next+4 > len(t) {
		return nil
	}
	ifd1 := int(bo.Uint32(t[next:]))
	if ifd1 == 0 || ifd1+2 > len(t) {
		return nil
	}
	n = int(bo.Uint16(t[ifd1:]))
	var off, ln int
	for k := 0; k < n; k++ {
		e := ifd1 + 2 + 12*k
		if e+12 > len(t) {
			return nil
		}
		switch bo.Uint16(t[e:]) {
		case 0x0201:
			off = int(bo.Uint32(t[e+8:]))
		case 0x0202:
			ln = int(bo.Uint32(t[e+8:]))
		}
	}
	if off <= 0 || ln <= 0 || off+ln > len(t) {
		return nil
	}
	return t[off : off+ln]
}

// dhash: brightness differences between neighbours on a 9×8 grid.
func dhash(img image.Image) uint64 {
	b := img.Bounds()
	w, h := b.Dx(), b.Dy()
	if w < 9 || h < 8 {
		return 0
	}
	var g [8][9]float64
	const s = 4 // samples per cell side
	for y := 0; y < 8; y++ {
		for x := 0; x < 9; x++ {
			var sum float64
			for sy := 0; sy < s; sy++ {
				for sx := 0; sx < s; sx++ {
					px := b.Min.X + (x*s+sx)*w/(9*s)
					py := b.Min.Y + (y*s+sy)*h/(8*s)
					r, gg, bb, _ := img.At(px, py).RGBA()
					sum += 0.299*float64(r) + 0.587*float64(gg) + 0.114*float64(bb)
				}
			}
			g[y][x] = sum
		}
	}
	var hash uint64
	for y := 0; y < 8; y++ {
		for x := 0; x < 8; x++ {
			hash <<= 1
			if g[y][x] < g[y][x+1] {
				hash |= 1
			}
		}
	}
	return hash
}

// photoImage decodes the picture to hash: the EXIF preview when there is one.
func photoImage(path string) (img image.Image, w, h int, err error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, 0, 0, err
	}
	defer f.Close()
	head := make([]byte, 128<<10)
	n, _ := io.ReadFull(f, head)
	head = head[:n]
	cfg, _, err := image.DecodeConfig(bytes.NewReader(head))
	if err != nil {
		f.Seek(0, io.SeekStart)
		if cfg, _, err = image.DecodeConfig(f); err != nil {
			return nil, 0, 0, err
		}
	}
	if t := exifThumb(head); t != nil {
		if ti, err := jpeg.Decode(bytes.NewReader(t)); err == nil {
			return ti, cfg.Width, cfg.Height, nil
		}
	}
	if _, err := f.Seek(0, io.SeekStart); err != nil {
		return nil, 0, 0, err
	}
	img, _, err = image.Decode(f)
	return img, cfg.Width, cfg.Height, err
}

func isPhoto(name string) bool {
	switch lower(filepath.Ext(name)) {
	case ".jpg", ".jpeg", ".png":
		return true
	}
	return false
}

var lastPhotos struct {
	sync.Mutex
	paths map[string]bool
}

func findSimilarPhotos(j *Job, root string) (*photoResult, error) {
	j.SetPhase("Поиск фотографий")
	var files []photoFile
	err := filepath.WalkDir(root, func(p string, d fs.DirEntry, err error) error {
		if j.Cancelled() {
			return errors.New("остановлено")
		}
		if err != nil {
			return nil
		}
		if d.Type()&fs.ModeSymlink != 0 || d.Type()&fs.ModeIrregular != 0 {
			if d.IsDir() {
				return fs.SkipDir
			}
			return nil
		}
		if d.IsDir() || !isPhoto(d.Name()) {
			return nil
		}
		fi, err := d.Info()
		if err != nil || fi.Size() < photoMinSize || winapi.FileAttributes(fi)&attrCloudMask != 0 {
			return nil
		}
		files = append(files, photoFile{Path: p, Size: fi.Size(), MTime: fi.ModTime().Unix()})
		if len(files) >= photoMaxFiles {
			return errors.New("limit")
		}
		return nil
	})
	if j.Cancelled() {
		return nil, errors.New("остановлено")
	}
	_ = err
	j.Total.Store(int64(len(files)))
	j.SetPhase("Сравнение изображений")
	var wg sync.WaitGroup
	next := make(chan int)
	for w := 0; w < max(2, runtime.NumCPU()); w++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			defer winapi.BackgroundThread()()
			for i := range next {
				f := &files[i]
				if img, w, h, err := photoImage(f.Path); err == nil {
					f.hash, f.W, f.H = dhash(img), w, h
				}
				j.Done.Add(1)
				j.Files.Add(1)
			}
		}()
	}
	for i := range files {
		if j.Cancelled() {
			break
		}
		next <- i
	}
	close(next)
	wg.Wait()
	if j.Cancelled() {
		return nil, errors.New("остановлено")
	}

	// group pictures whose hashes are close (union-find)
	var cand []int
	for i := range files {
		// flat pictures (one colour, gradients) hash to almost all zeros or ones
		if c := bits.OnesCount64(files[i].hash); c >= 6 && c <= 58 {
			cand = append(cand, i)
		}
	}
	parent := make([]int, len(files))
	for i := range parent {
		parent[i] = i
	}
	var find func(int) int
	find = func(x int) int {
		for parent[x] != x {
			parent[x] = parent[parent[x]]
			x = parent[x]
		}
		return x
	}
	for a := 0; a < len(cand); a++ {
		ha := files[cand[a]].hash
		for b := a + 1; b < len(cand); b++ {
			if bits.OnesCount64(ha^files[cand[b]].hash) <= photoMaxDist {
				ra, rb := find(cand[a]), find(cand[b])
				if ra != rb {
					parent[ra] = rb
				}
			}
		}
	}
	byRoot := map[int][]photoFile{}
	for _, i := range cand {
		r := find(i)
		byRoot[r] = append(byRoot[r], files[i])
	}
	res := &photoResult{Root: root, Scanned: int64(len(files))}
	keep := map[string]bool{}
	for _, g := range byRoot {
		if len(g) < 2 {
			continue
		}
		// the best copy: most pixels, then the bigger file, then the older one
		sort.SliceStable(g, func(a, b int) bool {
			pa, pb := g[a].W*g[a].H, g[b].W*g[b].H
			if pa != pb {
				return pa > pb
			}
			if g[a].Size != g[b].Size {
				return g[a].Size > g[b].Size
			}
			return g[a].MTime < g[b].MTime
		})
		g[0].Best = true
		pg := photoGroup{Files: g}
		for _, f := range g[1:] {
			pg.Wasted += f.Size
		}
		for _, f := range g {
			keep[lower(f.Path)] = true
		}
		res.Groups = append(res.Groups, pg)
		res.Wasted += pg.Wasted
	}
	sort.Slice(res.Groups, func(a, b int) bool { return res.Groups[a].Wasted > res.Groups[b].Wasted })
	if len(res.Groups) > 500 {
		res.Groups = res.Groups[:500]
	}
	j.Bytes.Store(res.Wasted)
	lastPhotos.Lock()
	lastPhotos.paths = keep
	lastPhotos.Unlock()
	return res, nil
}

// photoThumb makes a small JPEG preview for the interface (only for pictures
// the last search found).
var thumbCache = struct {
	sync.Mutex
	m     map[string][]byte
	order []string
}{m: map[string][]byte{}}

func photoThumb(path string) ([]byte, error) {
	lastPhotos.Lock()
	allowed := lastPhotos.paths[lower(path)]
	lastPhotos.Unlock()
	if !allowed {
		return nil, errors.New("нет такого фото в результатах")
	}
	thumbCache.Lock()
	if b, ok := thumbCache.m[path]; ok {
		thumbCache.Unlock()
		return b, nil
	}
	thumbCache.Unlock()
	img, _, _, err := photoImage(path)
	if err != nil {
		return nil, err
	}
	small := scaleDown(img, 240)
	var buf bytes.Buffer
	if err := jpeg.Encode(&buf, small, &jpeg.Options{Quality: 80}); err != nil {
		return nil, err
	}
	thumbCache.Lock()
	thumbCache.m[path] = buf.Bytes()
	thumbCache.order = append(thumbCache.order, path)
	if len(thumbCache.order) > 600 {
		delete(thumbCache.m, thumbCache.order[0])
		thumbCache.order = thumbCache.order[1:]
	}
	thumbCache.Unlock()
	return buf.Bytes(), nil
}

// scaleDown shrinks a picture so its longer side is at most n pixels.
func scaleDown(img image.Image, n int) image.Image {
	b := img.Bounds()
	w, h := b.Dx(), b.Dy()
	if w <= n && h <= n {
		return img
	}
	nw, nh := n, h*n/w
	if h > w {
		nw, nh = w*n/h, n
	}
	if nw < 1 {
		nw = 1
	}
	if nh < 1 {
		nh = 1
	}
	out := image.NewRGBA(image.Rect(0, 0, nw, nh))
	for y := 0; y < nh; y++ {
		for x := 0; x < nw; x++ {
			out.Set(x, y, img.At(b.Min.X+x*w/nw, b.Min.Y+y*h/nh))
		}
	}
	return out
}
