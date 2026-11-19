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
		j := newTestJob()
		timer := time.AfterFunc(10*time.Minute, j.Cancel)
		defer timer.Stop()
		w := newAnalysis(sr, j)
		w.run()
		if j.Cancelled() {
			return warnf("обход папки Windows не закончился за 10 минут")
		}
		df := pctDiff(mFiles, w.root.files)
		ds := pctDiff(mSize, w.root.size)
		if df > 1 || ds > 1 {
			// where the difference is: the subfolders that differ most
			a.treeMu.RLock()
			type d struct {
				name string
				m, w int64
			}
			var diffs []d
			wk := map[string]int64{}
			for _, c := range w.root.children {
				wk[lower(c.name)] = c.size
			}
			wk["(файлы в корне)"] = w.root.own
			for _, c := range n.children {
				diffs = append(diffs, d{c.name, c.size, wk[lower(c.name)]})
			}
			diffs = append(diffs, d{"(файлы в корне)", n.own, w.root.own})
			a.treeMu.RUnlock()
			sort.Slice(diffs, func(i, j int) bool { return abs64(diffs[i].m-diffs[i].w) > abs64(diffs[j].m-diffs[j].w) })
			var parts []string
			for _, x := range diffs[:min(6, len(diffs))] {
				parts = append(parts, fmt.Sprintf("%s: MFT %s, обход %s", x.name, human(x.m), human(x.w)))
			}
			return failf("MFT: %d файлов, %s; обход: %d файлов, %s — расхождение %.2f%% по числу и %.2f%% по размеру. Больше всего отличаются: %s",
				mFiles, human(mSize), w.root.files, human(w.root.size), df, ds, strings.Join(parts, "; "))
		}
		msg := fmt.Sprintf("MFT: %d файлов, %s; обход: %d файлов, %s (без доступа папок: %d) — расхождение %.2f%% по числу и %.2f%% по размеру",
			mFiles, human(mSize), w.root.files, human(w.root.size), w.denied.Load(), df, ds)
		if df > 1 || ds > 1 {
			return failf("%s, допустимо до 1%%", msg)
		}
		return okf("%s", msg)
	})
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

func abs64(x int64) int64 {
	if x < 0 {
		return -x
	}
	return x
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
