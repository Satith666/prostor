"""Sanity-checks a PE file: subsystem, resource tree, icon group, version strings, manifest."""
import struct
import sys

data = open(sys.argv[1], "rb").read()
pe = struct.unpack_from("<I", data, 0x3C)[0]
assert data[pe:pe + 4] == b"PE\0\0", "not PE"
machine, nsec, _, _, _, optsize, chars = struct.unpack_from("<HHIIIHH", data, pe + 4)
opt = pe + 24
magic = struct.unpack_from("<H", data, opt)[0]
assert magic == 0x20B, "not PE32+"
subsystem = struct.unpack_from("<H", data, opt + 68)[0]
ndirs = struct.unpack_from("<I", data, opt + 108)[0]
rva, size = struct.unpack_from("<II", data, opt + 112 + 8 * 2)
print(f"machine=0x{machine:x} subsystem={subsystem} ({'GUI' if subsystem == 2 else 'console'}) rsrcRVA=0x{rva:x} size={size}")
secs = []
for i in range(nsec):
    o = opt + optsize + 40 * i
    name, vsize, va, rawsize, rawptr = struct.unpack_from("<8sIIII", data, o)
    secs.append((name.rstrip(b"\0").decode(), va, vsize, rawptr, rawsize))
print("sections:", [s[0] for s in secs])


def rva2off(r):
    for n, va, vs, rp, rs in secs:
        if va <= r < va + max(vs, rs):
            return rp + (r - va)
    raise ValueError(hex(r))


base = rva2off(rva)
found = {}


def walk(off, level, path):
    _, _, _, _, nnamed, nid = struct.unpack_from("<IIHHHH", data, base + off)
    for i in range(nnamed + nid):
        ident, target = struct.unpack_from("<II", data, base + off + 16 + 8 * i)
        if target & 0x80000000:
            walk(target & 0x7FFFFFFF, level + 1, path + [ident])
        else:
            drva, dsize, _, _ = struct.unpack_from("<IIII", data, base + target)
            found[tuple(path + [ident])] = data[rva2off(drva):rva2off(drva) + dsize]


walk(0, 0, [])
names = {3: "ICON", 14: "GROUP_ICON", 16: "VERSION", 24: "MANIFEST"}
for k, v in sorted(found.items()):
    print(f"  {names.get(k[0], k[0])}/{k[1]}/lang {k[2]:#06x}: {len(v)} bytes")

grp = [v for k, v in found.items() if k[0] == 14][0]
_, typ, cnt = struct.unpack_from("<HHH", grp, 0)
print("icon group entries:", cnt)
for i in range(cnt):
    w, h, cc, _, planes, bpp, sz, nid = struct.unpack_from("<BBBBHHIH", grp, 6 + 14 * i)
    img = found[(3, nid, 0x409)]
    kind = "PNG" if img[:4] == b"\x89PNG" else ("DIB" if struct.unpack_from("<I", img)[0] == 40 else "??")
    assert len(img) == sz
    print(f"   {w or 256}x{h or 256} {bpp}bpp id={nid} {kind}")

ver = [v for k, v in found.items() if k[0] == 16][0]
txt = ver.decode("utf-16-le", errors="replace")
for key in ["FileDescription", "ProductName", "FileVersion", "OriginalFilename"]:
    i = txt.find(key)
    val = txt[i + len(key):].lstrip("\0").split("\0")[0]
    print(f"   {key} = {val}")
sig = struct.unpack_from("<I", ver, ver.find(struct.pack("<I", 0xFEEF04BD)))[0]
print("   VS_FIXEDFILEINFO signature ok:", sig == 0xFEEF04BD, "wLength ok:", struct.unpack_from("<H", ver)[0] == len(ver))
man = [v for k, v in found.items() if k[0] == 24][0].decode()
print("manifest level:", man[man.find("level=") : man.find("level=") + 20])
