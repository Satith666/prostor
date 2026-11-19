"""Builds a COFF .syso with Windows resources (icon, manifest, version info)
that the Go linker embeds into the .exe. No external tools needed."""
import argparse
import struct

RT_ICON, RT_GROUP_ICON, RT_VERSION, RT_MANIFEST = 3, 14, 16, 24
LANG = 0x0409


def read_ico(path):
    data = open(path, "rb").read()
    _, typ, count = struct.unpack_from("<HHH", data, 0)
    assert typ == 1
    entries = []
    for i in range(count):
        w, h, cc, _, planes, bpp, size, off = struct.unpack_from("<BBBBHHII", data, 6 + 16 * i)
        entries.append((w, h, cc, planes, bpp, data[off:off + size]))
    return entries


def utf16z(s):
    return (s + "\0").encode("utf-16-le")


def pad4(b):
    return b + b"\0" * ((4 - len(b) % 4) % 4)


def ver_block(key, value=b"", value_len=0, typ=1, children=()):
    body = struct.pack("<HHH", 0, value_len, typ) + utf16z(key)
    body = pad4(body)
    body += value
    for c in children:
        body = pad4(body) + c
    return struct.pack("<H", len(body)) + body[2:]


def version_info(ver, strings):
    parts = [int(x) for x in ver.split(".")] + [0, 0, 0, 0]
    ms, ls = (parts[0] << 16) | parts[1], (parts[2] << 16) | parts[3]
    fixed = struct.pack("<13I", 0xFEEF04BD, 0x00010000, ms, ls, ms, ls, 0x3F, 0, 0x40004, 1, 0, 0, 0)
    str_children = []
    for k, v in strings.items():
        val = utf16z(v)
        str_children.append(ver_block(k, val, len(val) // 2, 1))
    table = ver_block("040904B0", b"", 0, 1, str_children)
    sfi = ver_block("StringFileInfo", b"", 0, 1, [table])
    var = ver_block("Translation", struct.pack("<HH", 0x0409, 0x04B0), 4, 0)
    vfi = ver_block("VarFileInfo", b"", 0, 1, [var])
    return ver_block("VS_VERSION_INFO", fixed, len(fixed), 0, [sfi, vfi])


def build_rsrc(resources):
    """resources: {type: {id: bytes}} -> (section bytes, reloc offsets)"""
    types = sorted(resources)
    # layout: root dir, type dirs, name dirs, data entries, data
    dir_size = lambda n: 16 + 8 * n
    off = dir_size(len(types))
    type_dir_off = {}
    for t in types:
        type_dir_off[t] = off
        off += dir_size(len(resources[t]))
    name_dir_off = {}
    for t in types:
        for rid in sorted(resources[t]):
            name_dir_off[(t, rid)] = off
            off += dir_size(1)
    entry_off = {}
    for t in types:
        for rid in sorted(resources[t]):
            entry_off[(t, rid)] = off
            off += 16
    data_off = {}
    for t in types:
        for rid in sorted(resources[t]):
            off = (off + 7) & ~7
            data_off[(t, rid)] = off
            off += len(resources[t][rid])
    buf = bytearray(off)

    def put_dir(at, entries):
        struct.pack_into("<IIHHHH", buf, at, 0, 0, 0, 0, 0, len(entries))
        for i, (ident, target, is_dir) in enumerate(entries):
            struct.pack_into("<II", buf, at + 16 + 8 * i, ident, target | (0x80000000 if is_dir else 0))

    put_dir(0, [(t, type_dir_off[t], True) for t in types])
    relocs = []
    for t in types:
        put_dir(type_dir_off[t], [(rid, name_dir_off[(t, rid)], True) for rid in sorted(resources[t])])
        for rid in sorted(resources[t]):
            put_dir(name_dir_off[(t, rid)], [(LANG, entry_off[(t, rid)], False)])
            data = resources[t][rid]
            struct.pack_into("<IIII", buf, entry_off[(t, rid)], data_off[(t, rid)], len(data), 0, 0)
            relocs.append(entry_off[(t, rid)])
            buf[data_off[(t, rid)]:data_off[(t, rid)] + len(data)] = data
    return bytes(buf), relocs


def write_coff(path, section, relocs):
    n_sec = 1
    hdr_size = 20 + 40 * n_sec
    raw_ptr = hdr_size
    reloc_ptr = raw_ptr + len(section)
    sym_ptr = reloc_ptr + 10 * len(relocs)
    out = bytearray()
    out += struct.pack("<HHIIIHH", 0x8664, n_sec, 0, sym_ptr, 1, 0, 0)
    out += struct.pack("<8sIIIIIIHHI", b".rsrc", 0, 0, len(section), raw_ptr, reloc_ptr, 0, len(relocs), 0, 0x40000040)
    out += section
    for r in relocs:
        out += struct.pack("<IIH", r, 0, 3)  # IMAGE_REL_AMD64_ADDR32NB against symbol 0
    out += struct.pack("<8sIhHBB", b".rsrc", 0, 1, 0, 3, 0)
    out += struct.pack("<I", 4)  # empty string table
    open(path, "wb").write(out)


MANIFEST = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity type="win32" name="{name}" version="{ver}" processorArchitecture="amd64"/>
  <description>{desc}</description>
  <dependency>
    <dependentAssembly>
      <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*"/>
    </dependentAssembly>
  </dependency>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="{level}" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}}"/>
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
{dpi}
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
</assembly>
"""


DPI = {
    "pm": """      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>""",
    "system": """      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware>""",
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ico", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--version", default="1.0.0.0")
    ap.add_argument("--name", required=True)
    ap.add_argument("--desc", required=True)
    ap.add_argument("--product", default="Prostor")
    ap.add_argument("--orig", required=True)
    ap.add_argument("--level", default="asInvoker")
    ap.add_argument("--dpi", default="pm", choices=["pm", "system"])
    a = ap.parse_args()

    icons = read_ico(a.ico)
    res = {RT_ICON: {}, RT_GROUP_ICON: {}, RT_VERSION: {}, RT_MANIFEST: {}}
    grp = struct.pack("<HHH", 0, 1, len(icons))
    for i, (w, h, cc, planes, bpp, data) in enumerate(icons, start=1):
        res[RT_ICON][i] = data
        grp += struct.pack("<BBBBHHIH", w, h, cc, 0, planes or 1, bpp or 32, len(data), i)
    res[RT_GROUP_ICON][1] = grp
    short = ".".join(a.version.split(".")[:3])
    res[RT_VERSION][1] = version_info(a.version, {
        "CompanyName": "Prostor",
        "FileDescription": a.desc,
        "FileVersion": a.version,
        "InternalName": a.orig.rsplit(".", 1)[0],
        "LegalCopyright": "© 2026 Prostor",
        "OriginalFilename": a.orig,
        "ProductName": a.product,
        "ProductVersion": short,
    })
    res[RT_MANIFEST][1] = MANIFEST.format(name=a.name, ver=a.version, desc=a.desc, level=a.level, dpi=DPI[a.dpi]).encode("utf-8")
    section, relocs = build_rsrc(res)
    write_coff(a.out, section, relocs)


if __name__ == "__main__":
    main()
