"""Builds the native STEP reader (glance-step.exe) and assembles its runtime folder.

  python tools/build_step.py <occt_root> <thirdparty_root> <out_dir>

OpenCascade's official Windows build links its data-exchange libraries against the visualisation stack,
which in turn imports FreeImage, FFmpeg, FreeType and OpenVR. The STEP reader never calls any of them, so
instead of shipping ~60 MB of unused codecs we satisfy those imports with tiny stub DLLs that export the same
names. TBB and jemalloc are genuinely used by the kernel and are copied as they are.
"""
import glob
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REAL_THIRD_PARTY = {"tbb12.dll", "jemalloc.dll"}
SYSTEM = ("api-ms-", "kernel32", "vcruntime", "msvcp", "advapi32", "user32", "winmm", "wsock32", "gdi32",
          "shell32", "ole32", "ws2_32", "opengl32", "comdlg32", "winspool", "oleaut32", "shlwapi", "imm32",
          "version", "bcrypt", "ntdll", "dbghelp", "psapi", "glu32", "d3d", "dxgi", "dwmapi")
ROOT_DLLS = ["TKDESTEP.dll", "TKDEIGES.dll", "TKXCAF.dll", "TKMesh.dll", "TKBRep.dll"]
LIBS = ["TKernel", "TKMath", "TKG2d", "TKG3d", "TKGeomBase", "TKBRep", "TKTopAlgo", "TKMesh", "TKXSBase",
        "TKDE", "TKDESTEP", "TKDEIGES", "TKXCAF", "TKLCAF", "TKCAF", "TKCDF"]


def vs_tools():
    vswhere = os.path.expandvars(r"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe")
    vs = subprocess.run([vswhere, "-latest", "-products", "*", "-property", "installationPath"],
                        capture_output=True, text=True).stdout.strip()
    vcvars = os.path.join(vs, r"VC\Auxiliary\Build\vcvars64.bat")
    if not os.path.exists(vcvars):
        sys.exit("Visual Studio C++ tools not found")
    return vcvars


def run_vc(vcvars, command, cwd):
    script = os.path.join(cwd, "_build.cmd")
    with open(script, "w") as f:
        f.write('@echo off\ncall "%s" >nul\n%s\n' % (vcvars, command))
    r = subprocess.run(["cmd", "/c", script], cwd=cwd, capture_output=True, text=True)
    os.remove(script)
    if r.returncode != 0:
        sys.exit("FAILED: %s\n%s\n%s" % (command, r.stdout[-6000:], r.stderr[-3000:]))
    return r.stdout


def imports(vcvars, dll, work):
    """{imported dll name (lower): [symbol names]} for one DLL."""
    out = run_vc(vcvars, 'dumpbin /nologo /imports "%s"' % dll, work)
    result, current = {}, None
    for line in out.splitlines():
        m = re.match(r"^\s{4}(\S+\.dll)\s*$", line, re.I)
        if m:
            current = result.setdefault(m.group(1).lower(), [])
            continue
        m = re.match(r"^\s+[0-9A-Fa-f]+\s+(\S+)\s*$", line)
        if m and current is not None:
            current.append(m.group(1))
    return result


def main():
    occt, third, out = (os.path.abspath(a) for a in sys.argv[1:4])
    bin_dir = os.path.join(occt, "win64", "vc14", "bin")
    lib_dir = os.path.join(occt, "win64", "vc14", "lib")
    inc_dir = os.path.join(occt, "inc")
    work = os.path.join(ROOT, "build", "step-native")
    os.makedirs(work, exist_ok=True)
    vcvars = vs_tools()

    # 1. Compile the reader.
    src = os.path.join(ROOT, "src", "StepNative", "glance-step.cpp")
    libs = " ".join(l + ".lib" for l in LIBS)
    run_vc(vcvars,
           'cl /nologo /O2 /EHsc /MD /std:c++17 /DNDEBUG /DNOMINMAX /DWIN32_LEAN_AND_MEAN /DUNICODE /D_UNICODE /wd4996 /I"%s" "%s" '
           '/Fe:glance-step.exe /link /LIBPATH:"%s" %s /SUBSYSTEM:CONSOLE' % (inc_dir, src, lib_dir, libs), work)

    # 2. Dependency closure of the OpenCascade DLLs, and what they import from elsewhere.
    have = {f.lower(): f for f in os.listdir(bin_dir) if f.lower().endswith(".dll")}
    needed, external, todo = {}, {}, [d.lower() for d in ROOT_DLLS]
    todo += [d for d in imports(vcvars, os.path.join(work, "glance-step.exe"), work) if d in have]
    while todo:
        d = todo.pop()
        if d in needed:
            continue
        needed[d] = have[d]
        for dep, names in imports(vcvars, os.path.join(bin_dir, have[d]), work).items():
            if dep in have:
                todo.append(dep)
            elif not dep.startswith(SYSTEM):
                external.setdefault(dep, set()).update(names)

    # 3. Assemble the runtime folder.
    if os.path.isdir(out):
        shutil.rmtree(out)
    os.makedirs(out)
    shutil.copy2(os.path.join(work, "glance-step.exe"), out)
    for f in needed.values():
        shutil.copy2(os.path.join(bin_dir, f), out)
    for lic in ("LICENSE_LGPL_21.txt", "OCCT_LGPL_EXCEPTION.txt"):
        shutil.copy2(os.path.join(occt, lic), out)

    # The Visual C++ runtime, app-local, so nothing has to be installed on the machine.
    vs = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(vcvars))))
    crt = sorted(glob.glob(os.path.join(vs, "VC", "Redist", "MSVC", "*", "x64", "Microsoft.VC*.CRT")))
    if crt:
        for name in ("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"):
            if os.path.exists(os.path.join(crt[-1], name)):
                shutil.copy2(os.path.join(crt[-1], name), out)

    stubbed = []
    for dep, names in sorted(external.items()):
        if dep in REAL_THIRD_PARTY:
            hits = [p for p in glob.glob(os.path.join(third, "**", dep), recursive=True)
                    if "debug" not in p.lower() and not os.path.basename(os.path.dirname(p)).lower().endswith("d")]
            if not hits:
                sys.exit("missing third-party runtime: " + dep)
            shutil.copy2(hits[0], out)
            continue
        base = os.path.splitext(dep)[0]
        with open(os.path.join(work, base + ".def"), "w") as f:
            f.write('LIBRARY "%s"\nEXPORTS\n' % base)
            for n in sorted(names):
                f.write("  %s=glance_unused\n" % n)
        with open(os.path.join(work, base + ".c"), "w") as f:
            f.write("/* Stand-in for %s: Glance never calls into it. */\nvoid* glance_unused(void) { return 0; }\n" % dep)
        run_vc(vcvars, 'cl /nologo /O1 /GS- /LD "%s.c" /Fe:"%s" /link /DEF:"%s.def" /NOENTRY /NODEFAULTLIB'
               % (base, os.path.join(out, dep), base), work)
        stubbed.append("%s (%d names)" % (dep, len(names)))

    for junk in glob.glob(os.path.join(out, "*.lib")) + glob.glob(os.path.join(out, "*.exp")):
        os.remove(junk)
    total = sum(os.path.getsize(os.path.join(out, f)) for f in os.listdir(out))
    print("glance-step: %d OpenCascade DLLs, %.1f MB total" % (len(needed), total / 1e6))
    print("stubs: " + ", ".join(stubbed))


if __name__ == "__main__":
    main()
