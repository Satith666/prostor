// Package ntfs reads the master file table (MFT) of an NTFS volume directly:
// one sequential pass over $MFT gives every file's name, parent folder, size
// on disk and modification time, which is many times faster than listing
// folders one by one. It needs read access to the raw volume
// (administrator rights on Windows).
//
// The reader works on any io.ReaderAt, so it is tested on images built in
// memory; on Windows it reads \\.\C: (see OpenVolume in package winapi).
package ntfs

import (
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"runtime"
	"sort"
	"sync"
	"sync/atomic"
	"unicode/utf16"
)

// Record numbers of NTFS system files.
const (
	RecMFT    = 0
	RecRoot   = 5
	RecExtend = 11
	FirstUser = 16 // records below are NTFS metadata (except the root folder)
)

// Rec is what Prostor needs from one file record. Index in the result slice
// is the record number; Name == "" means the record is unused.
type Rec struct {
	Parent uint64 // record number of the parent folder
	Name   string // long (Win32/POSIX) name
	Size   int64  // logical size of the unnamed data stream
	Alloc  int64  // space on disk (0 for data stored inside the record)
	MTime  int64  // unix seconds, from $STANDARD_INFORMATION
	Attr   uint32 // FILE_ATTRIBUTE_*
	Links  uint16 // hard links (long names)
	Dir    bool
	Link   bool // junction, symlink or mount point (name surrogate reparse point)
	WOF    bool // compressed by Windows (CompactOS, compact /exe)
}

// Volume is an opened NTFS volume.
type Volume struct {
	r           io.ReaderAt
	SectorSize  int
	ClusterSize int
	RecordSize  int
	MFTStart    int64 // byte offset of the first $MFT cluster
	runs        []run // where $MFT lives
	mftSize     int64 // bytes of $MFT holding records
}

type run struct {
	vcn, lcn, n int64 // in clusters; lcn < 0 = sparse
}

// Open reads the boot sector and locates $MFT.
func Open(r io.ReaderAt) (*Volume, error) {
	b := make([]byte, 512)
	if _, err := r.ReadAt(b, 0); err != nil {
		return nil, fmt.Errorf("загрузочный сектор: %w", err)
	}
	if string(b[3:11]) != "NTFS    " {
		return nil, errors.New("не NTFS")
	}
	v := &Volume{r: r}
	v.SectorSize = int(binary.LittleEndian.Uint16(b[0x0B:]))
	spc := int(b[0x0D])
	if spc > 0x80 { // large clusters: 2^(256-n) sectors
		spc = 1 << (256 - spc)
	}
	v.ClusterSize = v.SectorSize * spc
	if v.SectorSize < 256 || v.ClusterSize <= 0 || v.ClusterSize > 2<<20 {
		return nil, fmt.Errorf("неверная геометрия: сектор %d, кластер %d", v.SectorSize, v.ClusterSize)
	}
	cpr := int8(b[0x40])
	if cpr < 0 {
		v.RecordSize = 1 << uint(-cpr)
	} else {
		v.RecordSize = int(cpr) * v.ClusterSize
	}
	if v.RecordSize < 512 || v.RecordSize > 64<<10 {
		return nil, fmt.Errorf("неверный размер записи %d", v.RecordSize)
	}
	v.MFTStart = int64(binary.LittleEndian.Uint64(b[0x30:])) * int64(v.ClusterSize)
	if err := v.locateMFT(); err != nil {
		return nil, err
	}
	return v, nil
}

// readAligned reads whole sectors (raw volumes refuse anything else).
func (v *Volume) readAligned(p []byte, off int64) error {
	ss := int64(v.SectorSize)
	start := off / ss * ss
	end := (off + int64(len(p)) + ss - 1) / ss * ss
	if start == off && end == off+int64(len(p)) {
		_, err := v.r.ReadAt(p, off)
		return err
	}
	tmp := make([]byte, end-start)
	if _, err := v.r.ReadAt(tmp, start); err != nil {
		return err
	}
	copy(p, tmp[off-start:])
	return nil
}

// ---------------------------------------------------------------- records

var errBadRecord = errors.New("повреждённая запись")

// fixup restores the last two bytes of every 512-byte block of a record.
func fixup(rec []byte) error {
	if len(rec) < 48 || string(rec[:4]) != "FILE" {
		return errBadRecord
	}
	usaOfs := int(binary.LittleEndian.Uint16(rec[4:]))
	usaCnt := int(binary.LittleEndian.Uint16(rec[6:]))
	if usaCnt < 2 || usaOfs+usaCnt*2 > len(rec) || (usaCnt-1)*512 > len(rec) {
		return errBadRecord
	}
	usn := rec[usaOfs : usaOfs+2]
	for i := 1; i < usaCnt; i++ {
		end := i*512 - 2
		if rec[end] != usn[0] || rec[end+1] != usn[1] {
			return errBadRecord
		}
		rec[end], rec[end+1] = rec[usaOfs+2*i], rec[usaOfs+2*i+1]
	}
	return nil
}

const (
	atStdInfo  = 0x10
	atAttrList = 0x20
	atFileName = 0x30
	atData     = 0x80
	atReparse  = 0xC0
	atEnd      = 0xFFFFFFFF

	flagCompressed = 0x0001
	flagSparse     = 0x8000

	filetimeEpoch = 116444736000000000
)

type attr struct {
	typ      uint32
	nonRes   bool
	named    bool
	name     string // data streams only
	flags    uint16
	value    []byte // resident value
	startVCN int64
	runs     []byte // non-resident: mapping pairs
	alloc    int64
	size     int64
	init     int64
	total    int64 // compressed/sparse: clusters really allocated, in bytes
}

// attrs walks the attributes of a fixed-up record.
func attrs(rec []byte, fn func(a *attr) bool) {
	off := int(binary.LittleEndian.Uint16(rec[0x14:]))
	used := int(binary.LittleEndian.Uint32(rec[0x18:]))
	if used > len(rec) {
		used = len(rec)
	}
	for off+16 <= used {
		typ := binary.LittleEndian.Uint32(rec[off:])
		if typ == atEnd {
			return
		}
		ln := int(binary.LittleEndian.Uint32(rec[off+4:]))
		if ln < 16 || off+ln > used {
			return
		}
		h := rec[off : off+ln]
		a := attr{typ: typ, nonRes: h[8] != 0, named: h[9] != 0, flags: binary.LittleEndian.Uint16(h[0x0C:])}
		if a.named && typ == atData {
			nl, no := int(h[9]), int(binary.LittleEndian.Uint16(h[0x0A:]))
			if no+2*nl <= ln {
				u := make([]uint16, nl)
				for i := range u {
					u[i] = binary.LittleEndian.Uint16(h[no+2*i:])
				}
				a.name = string(utf16.Decode(u))
			}
		}
		if !a.nonRes {
			if ln >= 0x18 {
				vl := int(binary.LittleEndian.Uint32(h[0x10:]))
				vo := int(binary.LittleEndian.Uint16(h[0x14:]))
				if vo+vl <= ln {
					a.value = h[vo : vo+vl]
				}
			}
		} else if ln >= 0x40 {
			a.startVCN = int64(binary.LittleEndian.Uint64(h[0x10:]))
			ro := int(binary.LittleEndian.Uint16(h[0x20:]))
			if ro < ln {
				a.runs = h[ro:]
			}
			a.alloc = int64(binary.LittleEndian.Uint64(h[0x28:]))
			a.size = int64(binary.LittleEndian.Uint64(h[0x30:]))
			a.init = int64(binary.LittleEndian.Uint64(h[0x38:]))
			a.total = a.alloc
			if a.flags&(flagCompressed|flagSparse) != 0 && ln >= 0x48 {
				a.total = int64(binary.LittleEndian.Uint64(h[0x40:]))
			}
		}
		if !fn(&a) {
			return
		}
		off += ln
	}
}

// decodeRuns turns mapping pairs into extents.
func decodeRuns(b []byte, vcn int64) []run {
	var out []run
	lcn := int64(0)
	for i := 0; i < len(b) && b[i] != 0; {
		h := b[i]
		ls, os := int(h&0x0F), int(h>>4)
		i++
		if ls == 0 || ls > 8 || os > 8 || i+ls+os > len(b) {
			break
		}
		var n int64
		for k := ls - 1; k >= 0; k-- {
			n = n<<8 | int64(b[i+k])
		}
		i += ls
		r := run{vcn: vcn, n: n, lcn: -1}
		if os > 0 {
			var d int64
			for k := os - 1; k >= 0; k-- {
				d = d<<8 | int64(b[i+k])
			}
			if b[i+os-1]&0x80 != 0 { // sign-extend
				d -= 1 << (8 * uint(os))
			}
			lcn += d
			r.lcn = lcn
			i += os
		}
		out = append(out, r)
		vcn += n
	}
	return out
}

// ---------------------------------------------------------------- $MFT layout

func (v *Volume) readRecordAt(off int64) ([]byte, error) {
	rec := make([]byte, v.RecordSize)
	if err := v.readAligned(rec, off); err != nil {
		return nil, err
	}
	if err := fixup(rec); err != nil {
		return nil, err
	}
	return rec, nil
}

// mftOffset maps a byte offset inside $MFT to the volume.
func (v *Volume) mftOffset(off int64) (int64, bool) {
	cs := int64(v.ClusterSize)
	vcn := off / cs
	for _, r := range v.runs {
		if vcn >= r.vcn && vcn < r.vcn+r.n {
			if r.lcn < 0 {
				return 0, false
			}
			return (r.lcn+vcn-r.vcn)*cs + off%cs, true
		}
	}
	return 0, false
}

func (v *Volume) locateMFT() error {
	rec, err := v.readRecordAt(v.MFTStart)
	if err != nil {
		return fmt.Errorf("запись $MFT: %w", err)
	}
	var list []byte
	var listNonRes *attr
	attrs(rec, func(a *attr) bool {
		switch {
		case a.typ == atData && !a.named && a.nonRes:
			v.runs = append(v.runs, decodeRuns(a.runs, a.startVCN)...)
			if a.startVCN == 0 {
				v.mftSize = a.init
				if v.mftSize <= 0 || v.mftSize > a.size {
					v.mftSize = a.size
				}
			}
		case a.typ == atAttrList && !a.nonRes:
			list = a.value
		case a.typ == atAttrList && a.nonRes:
			c := *a
			listNonRes = &c
		}
		return true
	})
	if len(v.runs) == 0 || v.mftSize <= 0 {
		return errors.New("у $MFT нет данных")
	}
	// a very fragmented $MFT keeps the rest of its map in extension records
	if listNonRes != nil {
		list = v.readNonResident(listNonRes)
	}
	if list != nil {
		seen := map[uint64]bool{}
		for off := 0; off+0x1A <= len(list); {
			typ := binary.LittleEndian.Uint32(list[off:])
			ln := int(binary.LittleEndian.Uint16(list[off+4:]))
			if ln < 0x1A || off+ln > len(list) {
				break
			}
			ref := binary.LittleEndian.Uint64(list[off+0x10:]) & 0xFFFFFFFFFFFF
			if typ == atData && list[off+6] == 0 && ref != RecMFT && !seen[ref] {
				seen[ref] = true
				if o, ok := v.mftOffset(int64(ref) * int64(v.RecordSize)); ok {
					if ext, err := v.readRecordAt(o); err == nil {
						attrs(ext, func(a *attr) bool {
							if a.typ == atData && !a.named && a.nonRes && a.startVCN > 0 {
								v.runs = append(v.runs, decodeRuns(a.runs, a.startVCN)...)
							}
							return true
						})
					}
				}
			}
			off += ln
		}
		sort.Slice(v.runs, func(i, j int) bool { return v.runs[i].vcn < v.runs[j].vcn })
	}
	return nil
}

func (v *Volume) readNonResident(a *attr) []byte {
	if a.size <= 0 || a.size > 64<<20 {
		return nil
	}
	cs := int64(v.ClusterSize)
	out := make([]byte, 0, a.size)
	for _, r := range decodeRuns(a.runs, 0) {
		buf := make([]byte, r.n*cs)
		if r.lcn >= 0 {
			if err := v.readAligned(buf, r.lcn*cs); err != nil {
				return nil
			}
		}
		out = append(out, buf...)
	}
	if int64(len(out)) > a.size {
		out = out[:a.size]
	}
	return out
}

// Records is the number of file records $MFT holds.
func (v *Volume) Records() int64 { return v.mftSize / int64(v.RecordSize) }

// ---------------------------------------------------------------- scanning

type extPart struct {
	base          uint64
	hasData       bool
	size, alloc   int64
	wofAlloc      int64 // WofCompressedData stream (files compressed by Windows, CompactOS)
	name          string
	ns            byte
	parent        uint64
	links         uint16
	reparseTag    uint32
	hasReparseTag bool
}

// parseRecord fills r from a base record, or returns the part an extension
// record contributes to its base record.
func parseRecord(rec []byte, r *Rec) (ext *extPart, inUse bool) {
	flags := binary.LittleEndian.Uint16(rec[0x16:])
	if flags&1 == 0 {
		return nil, false
	}
	base := binary.LittleEndian.Uint64(rec[0x20:]) & 0xFFFFFFFFFFFF
	var p extPart
	bestNS := byte(255)
	attrs(rec, func(a *attr) bool {
		switch a.typ {
		case atStdInfo:
			if len(a.value) >= 0x24 {
				if ft := int64(binary.LittleEndian.Uint64(a.value[0x08:])); ft > filetimeEpoch {
					r.MTime = (ft - filetimeEpoch) / 10_000_000
				}
				r.Attr = binary.LittleEndian.Uint32(a.value[0x20:])
			}
		case atFileName:
			if len(a.value) < 0x42 {
				break
			}
			ns := a.value[0x41]
			if ns == 2 { // DOS 8.3 alias of a long name
				break
			}
			p.links++
			nl := int(a.value[0x40])
			if 0x42+nl*2 > len(a.value) {
				break
			}
			// prefer Win32 names (1, 3) over POSIX (0); keep the first one otherwise
			rank := byte(1)
			if ns == 0 {
				rank = 2
			}
			if rank < bestNS {
				bestNS = rank
				u := make([]uint16, nl)
				for i := range u {
					u[i] = binary.LittleEndian.Uint16(a.value[0x42+2*i:])
				}
				p.name = string(utf16.Decode(u))
				p.ns = ns
				p.parent = binary.LittleEndian.Uint64(a.value[0:]) & 0xFFFFFFFFFFFF
			}
		case atData:
			if a.named {
				if a.name == "WofCompressedData" {
					if !a.nonRes {
						p.wofAlloc = 0
					} else if a.startVCN == 0 {
						p.wofAlloc = a.total
					}
				}
				break
			}
			if !a.nonRes {
				// data kept inside the record: Windows reports it rounded up to 8 bytes
				p.hasData, p.size, p.alloc = true, int64(len(a.value)), int64(len(a.value)+7)&^7
			} else if a.startVCN == 0 {
				p.hasData, p.size, p.alloc = true, a.size, a.total
			}
		case atReparse:
			if len(a.value) >= 4 {
				p.reparseTag = binary.LittleEndian.Uint32(a.value)
				p.hasReparseTag = true
			}
		}
		return true
	})
	if base != 0 {
		p.base = base
		return &p, false
	}
	r.Dir = flags&2 != 0
	r.Name, r.Parent, r.Links = p.name, p.parent, p.links
	if p.hasData {
		r.Size, r.Alloc = p.size, p.alloc
	}
	if p.hasReparseTag {
		r.Link = p.reparseTag&0x20000000 != 0
		if p.reparseTag == tagWOF {
			r.WOF = true
			if p.wofAlloc > 0 {
				r.Alloc = p.wofAlloc // the real data lives in the compressed stream
			}
		}
	}
	if r.Dir {
		r.Size, r.Alloc = 0, 0
	}
	return nil, true
}

// tagWOF marks files compressed by the Windows Overlay Filter (compact /exe, CompactOS).
const tagWOF = 0x80000017

// Progress is called from time to time with records read so far.
type Progress func(done, total int64)

// Scan reads every file record. Cancel may stop it early (ErrCancelled).
func (v *Volume) Scan(progress Progress, cancel func() bool) ([]Rec, error) {
	total := v.Records()
	if total <= 0 || total > 1<<31 {
		return nil, fmt.Errorf("неверное число записей: %d", total)
	}
	recs := make([]Rec, total)
	rs := int64(v.RecordSize)
	cs := int64(v.ClusterSize)
	// chunks of whole records, inside one extent each
	const chunkTarget = 4 << 20
	type chunk struct {
		first int64 // record number
		buf   []byte
	}
	type span struct{ first, n, vol int64 }
	var spans []span
	for _, r := range v.runs {
		if r.lcn < 0 {
			continue
		}
		start, end := r.vcn*cs, (r.vcn+r.n)*cs
		if end > v.mftSize {
			end = v.mftSize
		}
		// whole records inside this extent
		first := (start + rs - 1) / rs
		last := end / rs // exclusive
		for rn := first; rn < last; {
			n := int64(chunkTarget) / rs
			if n < 1 {
				n = 1
			}
			if rn+n > last {
				n = last - rn
			}
			spans = append(spans, span{rn, n, r.lcn*cs + (rn*rs - start)})
			rn += n
		}
		// records split between two extents (cluster smaller than a record)
		if end%rs != 0 && last*rs < v.mftSize {
			spans = append(spans, span{last, 1, -1})
		}
	}

	workers := runtime.NumCPU()
	if workers > 8 {
		workers = 8
	}
	if workers < 2 {
		workers = 2
	}
	chunks := make(chan chunk, workers*2)
	var done atomic.Int64
	var exts []*extPart
	var extMu sync.Mutex
	var wg sync.WaitGroup
	for w := 0; w < workers; w++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			var local []*extPart
			for c := range chunks {
				n := int64(len(c.buf)) / rs
				for i := int64(0); i < n; i++ {
					rn := c.first + i
					rec := c.buf[i*rs : (i+1)*rs]
					if fixup(rec) != nil {
						continue
					}
					if e, _ := parseRecord(rec, &recs[rn]); e != nil {
						local = append(local, e)
					}
				}
				d := done.Add(n)
				if progress != nil {
					progress(d, total)
				}
			}
			extMu.Lock()
			exts = append(exts, local...)
			extMu.Unlock()
		}()
	}
	var readErr error
	for _, s := range spans {
		if cancel != nil && cancel() {
			readErr = ErrCancelled
			break
		}
		buf := make([]byte, s.n*rs)
		if s.vol >= 0 {
			if err := v.readAligned(buf, s.vol); err != nil {
				readErr = fmt.Errorf("чтение $MFT: %w", err)
				break
			}
		} else { // stitch a record split across extents
			for k := int64(0); k < rs; k += cs {
				o, ok := v.mftOffset(s.first*rs + k)
				if !ok {
					break
				}
				part := buf[k:min(k+cs, rs)]
				if err := v.readAligned(part, o); err != nil {
					break
				}
			}
		}
		chunks <- chunk{s.first, buf}
	}
	close(chunks)
	wg.Wait()
	if readErr != nil {
		return nil, readErr
	}
	// extension records: data streams and names that did not fit in the base record
	for _, e := range exts {
		if e.base >= uint64(len(recs)) {
			continue
		}
		r := &recs[e.base]
		if e.hasData {
			r.Size, r.Alloc = e.size, e.alloc
			if r.Dir {
				r.Size, r.Alloc = 0, 0
			}
		}
		r.Links += e.links
		if e.wofAlloc > 0 && r.Alloc == 0 && !r.Dir {
			r.Alloc = e.wofAlloc
		}
		if e.name != "" && r.Name == "" {
			r.Name, r.Parent = e.name, e.parent
		}
		if e.hasReparseTag {
			r.Link = e.reparseTag&0x20000000 != 0
		}
	}
	return recs, nil
}

// ErrCancelled is returned when Scan was stopped.
var ErrCancelled = errors.New("остановлено")

// Record reads one base file record by number (a file id's low 48 bits).
// Data kept in extension records is not followed.
func (v *Volume) Record(num uint64) (Rec, error) {
	var r Rec
	o, ok := v.mftOffset(int64(num) * int64(v.RecordSize))
	if !ok {
		return r, errors.New("запись за пределами $MFT")
	}
	rec, err := v.readRecordAt(o)
	if err != nil {
		return r, err
	}
	if ext, inUse := parseRecord(rec, &r); ext != nil || !inUse {
		return r, errors.New("не основная или свободная запись")
	}
	return r, nil
}
