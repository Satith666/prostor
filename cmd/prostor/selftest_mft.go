package main

// Self-test of the MFT reader on the real system drive: a folder with known
// contents must come out exactly as the walk sees it, the Windows folder
// within 1 %, and a million files must take under 15 seconds.

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"prostor/internal/ntfs"
	"prostor/internal/winapi"
)

// mftTestTree lays out files whose sizes the MFT reader must reproduce:
// a file kept inside its record, an empty one, a hard link, an extra data
// stream and a link to a folder outside.
func (t *selfTest) mftTestTree() (dir string, linked string, err error) {
	if !winapi.IsWindows {
		return "", "", nil
	}
	dir = filepath.Join(t.dir, "mft")
	files := map[string]int{
		"a.bin": 1 << 20, "tiny.txt": 100, "empty.dat": 0, "mid.dat": 70000,
		`sub\deep.log`: 300000, `sub\sub2\x.json`: 5000, `sub\sub2\Длинное имя файла с пробелами.docx`: 12345,
	}
	for rel, size := range files {
		if err = writeTestFile(filepath.Join(dir, rel), size, 40, int64(size)); err != nil {
			return
		}
	}
	linked = filepath.Join(dir, "a.bin")
	if err = os.Link(linked, filepath.Join(dir, "a-link.bin")); err != nil {
		return
	}
	// an alternate data stream is not part of the file's size in folder listings
	if err = os.WriteFile(filepath.Join(dir, "mid.dat")+":prostor", make([]byte, 50000), 0o644); err != nil {
		return
	}
	target := filepath.Join(t.dir, "mft-target")
	if err = writeTestFile(filepath.Join(target, "behind.bin"), 2<<20, 40, 7); err != nil {
		return
	}
	err = makeLink(filepath.Join(dir, "junction"), target)
	return
}

func (t *selfTest) mftChecks(mft bool, dir, linked string, setupErr error) {
	g := "Анализ диска"
	a := t.sysAn
	defer t.wofChecks(mft)
	t.check(g, "Чтение MFT: скорость", func() (string, string) {
		switch {
		case !winapi.IsWindows:
			return skipf("только в Windows")
		case !mft:
			return skipf("нужны права администратора и диск NTFS")
		case a == nil:
			return failf("анализ системного диска не выполнен")
		case a.method != "mft":
			return failf("таблица файлов не прочитана, использован обход папок (см. журнал работы)")
		}
		perM := a.took / (float64(a.root.files) / 1e6)
		msg := fmt.Sprintf("%d файлов за %s — %s на миллион файлов", a.root.files, secRu(a.took), secRu(perM))
		if a.root.files > 300000 && perM > 15 {
			return warnf("%s, цель — до 15 с", msg)
		}
		return okf("%s", msg)
	})
	t.check(g, "Чтение MFT: точность на тестовой папке", func() (string, string) {
		if !mft || a == nil || a.method != "mft" {
			return skipf("таблица файлов не читалась")
		}
		if setupErr != nil {
			return failf("не удалось создать тестовые файлы: %v", setupErr)
		}
		a.treeMu.RLock()
		n := a.find(dir)
		var mFiles, mSize int64
		if n != nil {
			mFiles, mSize = n.files, n.size
		}
		a.treeMu.RUnlock()
		if n == nil {
			return failf("папка %s не найдена в таблице файлов", dir)
		}
		w := newAnalysis(dir, newTestJob())
		w.run()
		var linkAlloc int64
		if ents, err := winapi.ReadDir(dir); err == nil {
			for _, e := range ents {
				if strings.EqualFold(e.Name, filepath.Base(linked)) {
					linkAlloc = e.Alloc
				}
			}
		}
		// the walk counts the second hard link again (outside Windows), MFT once
		wantFiles, wantSize := w.root.files-1, w.root.size-linkAlloc
		if mFiles != wantFiles || mSize != wantSize {
			return failf("MFT: %d файлов, %d байт; обход без второй жёсткой ссылки: %d файлов, %d байт. По файлам (обход / запись MFT): %s",
				mFiles, mSize, wantFiles, wantSize, mftFileDiffs(dir))
		}
		return okf("%d файлов, %s — как при обходе; жёсткая ссылка учтена один раз, поток данных и ссылка на папку не посчитаны", mFiles, human(mSize))
	})
	t.check(g, "Чтение MFT: сверка с обходом папки Windows", func() (string, string) {
		if !mft || a == nil || a.method != "mft" {
			return skipf("таблица файлов не читалась")
		}
		sr := os.Getenv("SystemRoot")
		a.treeMu.RLock()
		n := a.find(sr)
		var mFiles, mSize int64
		if n != nil {
			mFiles, mSize = n.files, n.size
		}
		a.treeMu.RUnlock()
		if n == nil {
			return failf("папка %s не найдена в таблице файлов", sr)
		}
		// the reference: every file asked for its own size on disk (the folder
		// listing keeps outdated sizes for files Windows compressed later)
		start := time.Now()
		cmp := &recCompare{recs: a.mftRecs}
		tFiles, tSize, lSize, denied := trueFolderSize(sr, 10*time.Minute, cmp)
		a.mftRecs = nil
		if denied < 0 {
			return warnf("обход папки Windows не закончился за 10 минут")
		}
		df := pctDiff(mFiles, tFiles)
		ds := pctDiff(mSize, tSize)
		msg := fmt.Sprintf("MFT: %d файлов, %s; обход с размером от каждого файла: %d файлов, %s (по списку папок %s, папок без доступа %d) — расхождение %.2f%% по числу и %.2f%% по размеру, обход занял %s",
			mFiles, human(mSize), tFiles, human(tSize), human(lSize), denied, df, ds, secRu(time.Since(start).Seconds()))
		msg += ". " + cmp.report()
		if df > 1 || ds > 1 {
			return failf("%s, допустимо до 1%%", msg)
		}
		return okf("%s", msg)
	})
}

// recCompare compares each file's size from its own handle with its MFT
// record from the drive scan, grouped by what kind of file it is.
type recCompare struct {
	recs  []ntfs.Rec
	kinds map[string]*[3]int64 // files, handle bytes, MFT bytes
	worst []recDiff
}

type recDiff struct {
	path      string
	file, mft int64
	kind      string
}

func (c *recCompare) add(path string, e winapi.DirEnt, handleAlloc int64) {
	if c == nil || len(c.recs) == 0 {
		return
	}
	id := e.ID & 0xFFFFFFFFFFFF
	if id >= uint64(len(c.recs)) {
		return
	}
	r := c.recs[id]
	var kind string
	switch {
	case r.WOF:
		kind = "сжатые Windows"
	case e.Attr&0x800 != 0:
		kind = "сжатые NTFS"
	case e.Attr&0x200 != 0:
		kind = "разреженные"
	case e.Tag != 0:
		kind = fmt.Sprintf("точка повторной обработки %#x", e.Tag)
	case r.Links > 1:
		kind = "с жёсткими ссылками"
	case r.Name == "":
		kind = "запись не найдена"
	default:
		kind = "обычные"
	}
	if c.kinds == nil {
		c.kinds = map[string]*[3]int64{}
	}
	k := c.kinds[kind]
	if k == nil {
		k = &[3]int64{}
		c.kinds[kind] = k
	}
	k[0]++
	k[1] += handleAlloc
	k[2] += r.Alloc
	if handleAlloc != r.Alloc {
		c.worst = append(c.worst, recDiff{path, handleAlloc, r.Alloc, kind})
		sort.Slice(c.worst, func(i, j int) bool {
			return abs64(c.worst[i].file-c.worst[i].mft) > abs64(c.worst[j].file-c.worst[j].mft)
		})
		if len(c.worst) > 6 {
			c.worst = c.worst[:6]
		}
	}
}

func (c *recCompare) report() string {
	if c == nil || len(c.kinds) == 0 {
		return "сравнение по файлам недоступно"
	}
	var parts []string
	for k, v := range c.kinds {
		if v[1] != v[2] {
			parts = append(parts, fmt.Sprintf("%s: %d файлов, файл %s / MFT %s", k, v[0], human(v[1]), human(v[2])))
		}
	}
	sort.Strings(parts)
	var ws []string
	for _, w := range c.worst {
		ws = append(ws, fmt.Sprintf("%s (%s) %d/%d", w.path, w.kind, w.file, w.mft))
	}
	if len(parts) == 0 {
		return "по каждому файлу размер из MFT совпадает с размером от самого файла"
	}
	return "Расходятся: " + strings.Join(parts, "; ") + ". Больше всего: " + strings.Join(ws, "; ")
}

func abs64(x int64) int64 {
	if x < 0 {
		return -x
	}
	return x
}

// trueFolderSize walks a folder, counts every file once (hard links by file
// id, as the MFT does) and takes each file's size on disk from the file
// itself. lSize is the same by the folder listing. denied < 0: time ran out.
func trueFolderSize(root string, limit time.Duration, cmp *recCompare) (files, size, lSize int64, denied int) {
	deadline := time.Now().Add(limit)
	seen := map[uint64]bool{}
	stack := []string{root}
	for len(stack) > 0 {
		if time.Now().After(deadline) {
			return 0, 0, 0, -1
		}
		d := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		ents, err := winapi.ReadDir(d)
		if err != nil {
			denied++
			continue
		}
		for _, e := range ents {
			p := filepath.Join(d, e.Name)
			switch {
			case e.Link:
			case e.IsDir:
				stack = append(stack, p)
			default:
				if e.ID != 0 {
					if seen[e.ID] {
						continue
					}
					seen[e.ID] = true
				}
				files++
				lSize += e.Alloc
				al, _, err := winapi.FileAlloc(p)
				if err != nil {
					al = e.Alloc
				}
				size += al
				cmp.add(p, e, al)
			}
		}
	}
	return
}

func pctDiff(a, b int64) float64 {
	if b == 0 {
		if a == 0 {
			return 0
		}
		return 100
	}
	d := float64(a-b) / float64(b) * 100
	if d < 0 {
		d = -d
	}
	return d
}

// mftFileDiffs compares every test file's size on disk as the folder listing
// reports it with the file's own MFT record.
func mftFileDiffs(dir string) string {
	r, closeVol, err := winapi.OpenVolume(filepath.VolumeName(dir) + `\`)
	if err != nil {
		return err.Error()
	}
	defer closeVol()
	v, err := ntfs.Open(r)
	if err != nil {
		return err.Error()
	}
	var out []string
	var walk func(d string)
	walk = func(d string) {
		ents, _ := winapi.ReadDir(d)
		for _, e := range ents {
			p := filepath.Join(d, e.Name)
			if e.Link {
				continue
			}
			if e.IsDir {
				walk(p)
				continue
			}
			rec, err := v.Record(e.ID & 0xFFFFFFFFFFFF)
			mark := ""
			if err != nil || rec.Alloc != e.Alloc {
				mark = " ≠"
			}
			out = append(out, fmt.Sprintf("%s %d/%d (размер %d/%d)%s", e.Name, e.Alloc, rec.Alloc, e.Size, rec.Size, mark))
		}
	}
	walk(dir)
	return strings.Join(out, "; ")
}

// wofChecks looks at files Windows itself compressed (CompactOS): how big
// they are by the folder listing, by the MFT record, by the open file and by
// GetCompressedFileSize (what Explorer shows as size on disk).
func (t *selfTest) wofChecks(mft bool) {
	t.check("Анализ диска", "Чтение MFT: файлы, сжатые Windows", func() (string, string) {
		if !winapi.IsWindows || !mft {
			return skipf("нужны Windows, права администратора и NTFS")
		}
		dir := filepath.Join(os.Getenv("SystemRoot"), "System32")
		r, closeVol, err := winapi.OpenVolume(filepath.VolumeName(dir) + `\`)
		if err != nil {
			return failf("%v", err)
		}
		defer closeVol()
		v, err := ntfs.Open(r)
		if err != nil {
			return failf("%v", err)
		}
		ents, err := winapi.ReadDir(dir)
		if err != nil {
			return failf("%v", err)
		}
		var n, wof int
		var lst, rec, hnd, cmp [2]int64 // [all, WOF]
		var sample []string
		for _, e := range ents {
			if e.IsDir || e.Link || e.ID == 0 {
				continue
			}
			rr, err := v.Record(e.ID & 0xFFFFFFFFFFFF)
			if err != nil {
				continue
			}
			ha, ca, err := winapi.FileAlloc(filepath.Join(dir, e.Name))
			if err != nil {
				continue
			}
			n++
			k := 0
			if rr.WOF {
				k = 1
				wof++
				if len(sample) < 4 {
					sample = append(sample, fmt.Sprintf("%s: список %d, MFT %d, файл %d, сжатый %d, размер %d", e.Name, e.Alloc, rr.Alloc, ha, ca, e.Size))
				}
			}
			lst[k] += e.Alloc
			rec[k] += rr.Alloc
			hnd[k] += ha
			cmp[k] += ca
		}
		return okf("System32: %d файлов, сжатых Windows %d. Все файлы — список папки %s, MFT %s, сам файл %s, сжатый размер %s. Сжатые — список %s, MFT %s, файл %s, сжатый %s. Примеры: %s",
			n, wof, human(lst[0]+lst[1]), human(rec[0]+rec[1]), human(hnd[0]+hnd[1]), human(cmp[0]+cmp[1]),
			human(lst[1]), human(rec[1]), human(hnd[1]), human(cmp[1]), strings.Join(sample, "; "))
	})
}
