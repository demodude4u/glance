"""Checks that everything a shipped Glance folder loads is either in the folder or part of Windows itself.

  python tools/check_deps.py <dist\\Glance> ["<dist>\\Glance Setup.exe"]

A DLL that only resolves because of something installed on the build PC (Visual Studio, a C++ redistributable,
a debug runtime) would make Glance fail on a clean office PC, so anything that is not a known Windows component
and not sitting next to the program is reported.
"""
import os
import struct
import sys

# Shipped with every 64-bit Windows 10 / 11.
WINDOWS = {
    "kernel32.dll", "user32.dll", "gdi32.dll", "advapi32.dll", "shell32.dll", "ole32.dll", "oleaut32.dll", "comdlg32.dll",
    "opengl32.dll", "glu32.dll", "ws2_32.dll", "bcrypt.dll", "crypt32.dll", "ntdll.dll", "shlwapi.dll", "version.dll",
    "dwmapi.dll", "uxtheme.dll", "imm32.dll", "winmm.dll", "psapi.dll", "dbghelp.dll", "iphlpapi.dll", "secur32.dll",
    "userenv.dll", "comctl32.dll", "wsock32.dll", "netapi32.dll", "msvcrt.dll", "ucrtbase.dll", "powrprof.dll", "setupapi.dll",
    "ncrypt.dll", "mswsock.dll", "winhttp.dll", "normaliz.dll", "kernelbase.dll", "gdiplus.dll", "msimg32.dll", "d3d11.dll", "dxgi.dll",
}
# Present on a developer's PC, not on a clean one: these must be next to the program.
REDIST = ("msvcp", "vcruntime", "concrt", "vcomp", "vccorlib", "mfc", "atl", "ucrtbased", "msvcr")


def imports(path):
    with open(path, "rb") as f:
        d = f.read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    if d[pe:pe + 4] != b"PE\0\0":
        return []
    nsec, optsize = struct.unpack_from("<H", d, pe + 6)[0], struct.unpack_from("<H", d, pe + 20)[0]
    opt = pe + 24
    if struct.unpack_from("<H", d, opt)[0] != 0x20B:
        return []
    dirs = opt + 112
    secs = []
    for i in range(nsec):
        o = opt + optsize + i * 40
        vsize, va, rsize, raw = struct.unpack_from("<IIII", d, o + 8)
        secs.append((va, max(vsize, rsize), raw))

    def off(rva):
        for va, size, raw in secs:
            if va <= rva < va + size:
                return rva - va + raw
        return None

    def name(rva):
        o = off(rva)
        return d[o:d.index(b"\0", o)].decode("ascii", "replace").lower() if o is not None else "?"

    out = []
    imp = struct.unpack_from("<I", d, dirs + 8)[0]                # import table
    o = off(imp) if imp else None
    while o is not None:
        row = struct.unpack_from("<IIIII", d, o)
        if row[3] == 0:
            break
        out.append(name(row[3])); o += 20
    dly = struct.unpack_from("<I", d, dirs + 13 * 8)[0]           # delay-load table
    o = off(dly) if dly else None
    while o is not None:
        row = struct.unpack_from("<IIIIIIII", d, o)
        if row[1] == 0:
            break
        out.append(name(row[1])); o += 32
    return out


def main():
    root = sys.argv[1]
    files = []
    for dp, _, fs in os.walk(root):
        files += [os.path.join(dp, f) for f in fs if f.lower().endswith((".exe", ".dll"))]
    files += sys.argv[2:]
    problems = 0
    for path in sorted(files):
        here = {f.lower() for f in os.listdir(os.path.dirname(path))}
        for dep in sorted(set(imports(path))):
            if dep in here or dep.startswith(("api-ms-win-", "ext-ms-")):
                continue
            if dep in WINDOWS and not dep.startswith(REDIST):
                continue
            problems += 1
            print("MISSING  %s needs %s" % (os.path.relpath(path, root) if path.startswith(root) else os.path.basename(path), dep))
    print("%d programs and libraries checked, %d unresolved dependencies" % (len(files), problems))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
