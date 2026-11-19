package ntfs

import (
	"bytes"
	"encoding/binary"
	"testing"
	"unicode/utf16"
)

// ---------------------------------------------------------------- image builder

type img struct {
	b       []byte
	cluster int
	recSize int
	runs    []run // where $MFT lives
}

type fname struct {
	parent uint64
	name   string
	ns     byte
}

type fileSpec struct {
	num      uint64
	inUse    bool
	dir      bool
	base     uint64 // extension record of this base
	names    []fname
	mtime    int64 // unix
	attr     uint32
	resident []byte    // resident data
	nonRes   *[3]int64 // size, alloc, total(compressed; 0 = same as alloc)
	reparse  uint32
	corrupt  bool
	wof      int64 // allocation of a WofCompressedData stream
}

func u16s(s string) []byte {
	u := utf16.Encode([]rune(s))
	b := make([]byte, 2*len(u))
	for i, c := range u {
		binary.LittleEndian.PutUint16(b[2*i:], c)
	}
	return b
}

func resident(typ uint32, value []byte) []byte {
	ln := (0x18 + len(value) + 7) &^ 7
	h := make([]byte, ln)
	binary.LittleEndian.PutUint32(h, typ)
	binary.LittleEndian.PutUint32(h[4:], uint32(ln))
	binary.LittleEndian.PutUint32(h[0x10:], uint32(len(value)))
	binary.LittleEndian.PutUint16(h[0x14:], 0x18)
	copy(h[0x18:], value)
	return h
}

func nonResident(typ uint32, runs []byte, size, alloc, total int64, flags uint16) []byte {
	hl := 0x40
	if total > 0 {
		hl = 0x48
	}
	ln := (hl + len(runs) + 1 + 7) &^ 7
	h := make([]byte, ln)
	binary.LittleEndian.PutUint32(h, typ)
	binary.LittleEndian.PutUint32(h[4:], uint32(ln))
	h[8] = 1
	binary.LittleEndian.PutUint16(h[0x0C:], flags)
	binary.LittleEndian.PutUint16(h[0x20:], uint16(hl))
	binary.LittleEndian.PutUint64(h[0x28:], uint64(alloc))
	binary.LittleEndian.PutUint64(h[0x30:], uint64(size))
	binary.LittleEndian.PutUint64(h[0x38:], uint64(size))
	if total > 0 {
		binary.LittleEndian.PutUint64(h[0x40:], uint64(total))
	}
	copy(h[hl:], runs)
	return h
}

// encodeRuns writes mapping pairs (absolute lcns in, deltas out).
func encodeRuns(rs []run) []byte {
	var out []byte
	prev := int64(0)
	for _, r := range rs {
		n := le(r.n, false)
		d := le(r.lcn-prev, true)
		prev = r.lcn
		out = append(out, byte(len(n))|byte(len(d))<<4)
		out = append(out, n...)
		out = append(out, d...)
	}
	return append(out, 0)
}

func le(v int64, signed bool) []byte {
	var b []byte
	for {
		b = append(b, byte(v))
		v >>= 8
		if (!signed && v == 0) || (signed && ((v == 0 && b[len(b)-1]&0x80 == 0) || (v == -1 && b[len(b)-1]&0x80 != 0))) {
			return b
		}
	}
}

func (m *img) record(f fileSpec, mftRuns []run) []byte {
	rec := make([]byte, m.recSize)
	copy(rec, "FILE")
	usaCnt := m.recSize/512 + 1
	binary.LittleEndian.PutUint16(rec[4:], 0x30)
	binary.LittleEndian.PutUint16(rec[6:], uint16(usaCnt))
	first := (0x30 + 2*usaCnt + 7) &^ 7
	binary.LittleEndian.PutUint16(rec[0x14:], uint16(first))
	var flags uint16
	if f.inUse {
		flags |= 1
	}
	if f.dir {
		flags |= 2
	}
	binary.LittleEndian.PutUint16(rec[0x16:], flags)
	binary.LittleEndian.PutUint64(rec[0x20:], f.base)
	binary.LittleEndian.PutUint32(rec[0x2C:], uint32(f.num))
	var body []byte
	if f.base == 0 {
		si := make([]byte, 0x48)
		binary.LittleEndian.PutUint64(si[0x08:], uint64(f.mtime*10_000_000+filetimeEpoch))
		binary.LittleEndian.PutUint32(si[0x20:], f.attr)
		body = append(body, resident(atStdInfo, si)...)
	}
	for _, n := range f.names {
		nm := u16s(n.name)
		v := make([]byte, 0x42+len(nm))
		binary.LittleEndian.PutUint64(v, n.parent|1<<48)
		v[0x40] = byte(len(nm) / 2)
		v[0x41] = n.ns
		copy(v[0x42:], nm)
		body = append(body, resident(atFileName, v)...)
	}
	if f.num == RecMFT {
		var size int64
		for _, r := range mftRuns {
			size += r.n * int64(m.cluster)
		}
		body = append(body, nonResident(atData, encodeRuns(mftRuns), size, size, 0, 0)...)
	} else if f.resident != nil {
		body = append(body, resident(atData, f.resident)...)
	} else if f.nonRes != nil {
		var fl uint16
		if f.nonRes[2] > 0 {
			fl = flagCompressed
		}
		body = append(body, nonResident(atData, encodeRuns([]run{{vcn: 0, n: 1, lcn: 100}}), f.nonRes[0], f.nonRes[1], f.nonRes[2], fl)...)
	}
	if f.wof > 0 {
		h := nonResident(atData, encodeRuns([]run{{vcn: 0, n: 1, lcn: 100}}), f.wof*3, f.wof, 0, 0)
		// name the stream: grow the header to hold "WofCompressedData"
		name := u16s("WofCompressedData")
		hl := int(binary.LittleEndian.Uint16(h[0x20:]))
		runsPart := append([]byte(nil), h[hl:]...)
		nh := make([]byte, 0, len(h)+len(name)+8)
		nh = append(nh, h[:hl]...)
		nh = append(nh, name...)
		nh = append(nh, runsPart...)
		for len(nh)%8 != 0 {
			nh = append(nh, 0)
		}
		binary.LittleEndian.PutUint32(nh[4:], uint32(len(nh)))
		nh[9] = byte(len(name) / 2)
		binary.LittleEndian.PutUint16(nh[0x0A:], uint16(hl))
		binary.LittleEndian.PutUint16(nh[0x20:], uint16(hl+len(name)))
		body = append(body, nh...)
	}
	if f.reparse != 0 {
		v := make([]byte, 8)
		binary.LittleEndian.PutUint32(v, f.reparse)
		body = append(body, resident(atReparse, v)...)
	}
	copy(rec[first:], body)
	end := first + len(body)
	binary.LittleEndian.PutUint32(rec[end:], atEnd)
	binary.LittleEndian.PutUint32(rec[0x18:], uint32(end+8))
	binary.LittleEndian.PutUint32(rec[0x1C:], uint32(m.recSize))
	// update sequence: real last bytes of each 512-byte block go to the array
	usn := []byte{0x07, 0x00}
	copy(rec[0x30:], usn)
	for i := 1; i < usaCnt; i++ {
		e := i*512 - 2
		rec[0x30+2*i], rec[0x30+2*i+1] = rec[e], rec[e+1]
		rec[e], rec[e+1] = usn[0], usn[1]
	}
	if f.corrupt {
		rec[511] ^= 0xFF
	}
	return rec
}

// build lays out the records in $MFT extents and returns the volume image.
func build(cluster int, mftRuns []run, files []fileSpec) []byte {
	m := &img{cluster: cluster, recSize: 1024}
	var clusters int64
	for _, r := range mftRuns {
		if r.lcn+r.n > clusters {
			clusters = r.lcn + r.n
		}
	}
	m.b = make([]byte, (clusters+4)*int64(cluster))
	// boot sector
	copy(m.b[3:], "NTFS    ")
	binary.LittleEndian.PutUint16(m.b[0x0B:], 512)
	m.b[0x0D] = byte(cluster / 512)
	binary.LittleEndian.PutUint64(m.b[0x30:], uint64(mftRuns[0].lcn))
	m.b[0x40] = 0xF6 // 1024-byte records
	// $MFT as a flat byte stream, then scattered over its extents
	var mftLen int64
	for _, r := range mftRuns {
		mftLen += r.n * int64(cluster)
	}
	mft := make([]byte, mftLen)
	for _, f := range files {
		if int64(f.num+1)*1024 <= mftLen { // the small image holds fewer records
			copy(mft[f.num*1024:], m.record(f, mftRuns))
		}
	}
	off := int64(0)
	for _, r := range mftRuns {
		n := r.n * int64(cluster)
		copy(m.b[r.lcn*int64(cluster):], mft[off:off+n])
		off += n
	}
	return m.b
}

func sample() []fileSpec {
	return []fileSpec{
		{num: 0, inUse: true, names: []fname{{RecRoot, "$MFT", 3}}},
		{num: 3, inUse: true, names: []fname{{16, "split.dat", 3}}, resident: make([]byte, 7)}, // straddles two extents with 512-byte clusters
		{num: 5, inUse: true, dir: true, names: []fname{{RecRoot, ".", 3}}},
		{num: 11, inUse: true, dir: true, names: []fname{{RecRoot, "$Extend", 3}}},
		{num: 16, inUse: true, dir: true, names: []fname{{RecRoot, "Users", 3}}, mtime: 1700000000},
		{num: 17, inUse: true, names: []fname{{16, "a.txt", 3}}, resident: make([]byte, 100), mtime: 1700000100},
		{num: 18, inUse: true, names: []fname{{16, "big.bin", 3}}, nonRes: &[3]int64{10000, 12288, 0}, mtime: 1700000200, attr: 0x20},
		{num: 19, inUse: true, names: []fname{{16, "LONGFI~1.TXT", 2}, {16, "Long file name.txt", 1}}, resident: []byte("hi")},
		{num: 20, inUse: true, names: []fname{{16, "linked.dll", 1}, {RecRoot, "linked2.dll", 1}}, nonRes: &[3]int64{4096, 4096, 0}},
		{num: 21, inUse: true, dir: true, names: []fname{{16, "Junction", 3}}, reparse: 0xA0000003, attr: 0x410},
		{num: 22, inUse: false, names: []fname{{16, "deleted.tmp", 3}}, resident: []byte("x")},
		{num: 23, inUse: true, names: []fname{{16, "fragmented.vhdx", 3}}},
		{num: 24, inUse: true, base: 23, nonRes: &[3]int64{50000, 53248, 20480}},
		{num: 25, inUse: true, names: []fname{{16, "broken.bin", 3}}, resident: []byte("zz"), corrupt: true},
		{num: 26, inUse: true, names: []fname{{16, "cloud.docx", 3}}, nonRes: &[3]int64{900000, 0, 0}, reparse: 0x9000001A, attr: 0x400420},
		{num: 27, inUse: true, names: []fname{{16, "compact.dll", 3}}, nonRes: &[3]int64{300000, 0, 0}, reparse: tagWOF, wof: 98304},
		// a compressed file whose streams and tag all sit in extension records
		{num: 30, inUse: true, names: []fname{{16, "OSClient.NET.dll", 3}}},
		{num: 31, inUse: true, base: 30, wof: 12812288},
		{num: 32, inUse: true, base: 30, nonRes: &[3]int64{40000000, 0, 0}, reparse: tagWOF},
		{num: 33, inUse: true, base: 30, names: []fname{{RecRoot, "second-link.dll", 1}}},
	}
}

func check(t *testing.T, recs []Rec) {
	t.Helper()
	get := func(n int) Rec { return recs[n] }
	if r := get(3); r.Name != "split.dat" || r.Size != 7 || r.Alloc != 8 {
		t.Errorf("record across extents: %+v", r)
	}
	if r := get(16); !r.Dir || r.Name != "Users" || r.Parent != RecRoot || r.MTime != 1700000000 {
		t.Errorf("Users: %+v", r)
	}
	if r := get(17); r.Name != "a.txt" || r.Parent != 16 || r.Size != 100 || r.Alloc != 104 || r.MTime != 1700000100 {
		t.Errorf("resident file: %+v", r)
	}
	if r := get(18); r.Size != 10000 || r.Alloc != 12288 || r.Attr != 0x20 {
		t.Errorf("non-resident file: %+v", r)
	}
	if r := get(19); r.Name != "Long file name.txt" || r.Links != 1 {
		t.Errorf("long name over DOS alias: %+v", r)
	}
	if r := get(20); r.Name != "linked.dll" || r.Parent != 16 || r.Links != 2 {
		t.Errorf("hard links: %+v", r)
	}
	if r := get(21); !r.Link || !r.Dir {
		t.Errorf("junction: %+v", r)
	}
	if r := get(22); r.Name != "" {
		t.Errorf("deleted record is read: %+v", r)
	}
	if r := get(23); r.Name != "fragmented.vhdx" || r.Size != 50000 || r.Alloc != 20480 {
		t.Errorf("data stream from an extension record: %+v", r)
	}
	if r := get(24); r.Name != "" {
		t.Errorf("extension record looks like a file: %+v", r)
	}
	if r := get(25); r.Name != "" {
		t.Errorf("record with broken fixup is read: %+v", r)
	}
	if r := get(26); r.Link || r.Size != 900000 || r.Alloc != 0 {
		t.Errorf("cloud placeholder: %+v", r)
	}
	if r := get(27); r.Link || !r.WOF || r.Size != 300000 || r.Alloc != 98304 {
		t.Errorf("file compressed by Windows (WOF): %+v", r)
	}
	if len(recs) > 33 {
		if r := get(30); !r.WOF || r.Size != 40000000 || r.Alloc != 12812288 || r.Links != 2 || r.Name != "OSClient.NET.dll" {
			t.Errorf("compressed file spread over extension records: %+v", r)
		}
	}
}

func TestScan(t *testing.T) {
	// $MFT in two extents, the second one before the first on disk
	runs := []run{{vcn: 0, n: 4, lcn: 40}, {vcn: 4, n: 8, lcn: 8}}
	b := build(4096, runs, sample())
	v, err := Open(bytes.NewReader(b))
	if err != nil {
		t.Fatal(err)
	}
	if v.Records() != 48 || v.RecordSize != 1024 || v.ClusterSize != 4096 {
		t.Fatalf("volume: records %d record %d cluster %d", v.Records(), v.RecordSize, v.ClusterSize)
	}
	var last int64
	recs, err := v.Scan(func(d, total int64) { last = total }, nil)
	if err != nil {
		t.Fatal(err)
	}
	if last != 48 || len(recs) != 48 {
		t.Fatalf("progress total %d, records %d", last, len(recs))
	}
	check(t, recs)
}

// Clusters smaller than a record: records are split between extents.
func TestScanSmallClusters(t *testing.T) {
	runs := []run{{vcn: 0, n: 7, lcn: 100}, {vcn: 7, n: 9, lcn: 20}, {vcn: 16, n: 41, lcn: 160}} // 57 clusters of 512 bytes
	b := build(512, runs, sample())
	v, err := Open(bytes.NewReader(b))
	if err != nil {
		t.Fatal(err)
	}
	recs, err := v.Scan(nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(recs) != 28 {
		t.Fatalf("records %d", len(recs))
	}
	check(t, recs)
}

func TestDecodeRuns(t *testing.T) {
	in := []run{{vcn: 0, n: 10, lcn: 1000}, {vcn: 10, n: 300, lcn: 50}, {vcn: 310, n: 70000, lcn: 9000000}}
	out := decodeRuns(encodeRuns(in), 0)
	if len(out) != 3 {
		t.Fatalf("%+v", out)
	}
	for i := range in {
		if out[i] != in[i] {
			t.Errorf("run %d: %+v != %+v", i, out[i], in[i])
		}
	}
}

func TestNotNTFS(t *testing.T) {
	if _, err := Open(bytes.NewReader(make([]byte, 4096))); err == nil {
		t.Fatal("an empty image opened as NTFS")
	}
}

func TestRecord(t *testing.T) {
	runs := []run{{vcn: 0, n: 4, lcn: 40}, {vcn: 4, n: 8, lcn: 8}}
	v, err := Open(bytes.NewReader(build(4096, runs, sample())))
	if err != nil {
		t.Fatal(err)
	}
	r, err := v.Record(18)
	if err != nil || r.Name != "big.bin" || r.Alloc != 12288 {
		t.Fatalf("record 18: %+v %v", r, err)
	}
	if _, err := v.Record(24); err == nil {
		t.Fatal("an extension record read as a file")
	}
}
