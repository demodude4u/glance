"""End-to-end test of "Glance Setup.exe" on a private, never-shown Win32 desktop.

  python tools/hidden_setup.py "<dist>\\Glance Setup.exe" <sample.stl> <out_dir>

Installs into <out_dir>\\inst\\Glance (not the real Programs folder), drives the setup window with posted
window messages, and checks the files, the registry entries, the Start menu shortcut and the uninstall.

The Explorer thumbnail handler is checked as far as it can be without Explorer: its registry entries, then a
stand-in for Explorer's thumbnail process (tools/thumb_host.py) that asks Windows for the handler of a file the
way the shell does and gets a picture from it, and installing over and removing a copy whose handler DLL is loaded.
"""
import ctypes
import glob
import json
import os
import shutil
import sys
import threading
import time
import winreg

sys.path.insert(0, __import__("os").environ.get("CTCORE_PATH", "."))   # where the ctcore package lives, if not installed
from ctcore.hidden_desktop import HiddenDesktop  # noqa: E402
from PIL import Image  # noqa: E402

u32 = ctypes.WinDLL("user32", use_last_error=True)
g32 = ctypes.WinDLL("gdi32", use_last_error=True)
u32.SetProcessDpiAwarenessContext.argtypes = [ctypes.c_void_p]
u32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
V = ctypes.c_void_p
u32.PostMessageW.argtypes = [V, ctypes.c_uint, ctypes.c_size_t, ctypes.c_ssize_t]
u32.SendMessageTimeoutW.argtypes = [V, ctypes.c_uint, ctypes.c_size_t, ctypes.c_ssize_t, ctypes.c_uint, ctypes.c_uint, V]
u32.SetThreadDesktop.argtypes = [V]
u32.GetDlgItem.argtypes = [V, ctypes.c_int]; u32.GetDlgItem.restype = V
u32.GetWindowTextW.argtypes = [V, ctypes.c_wchar_p, ctypes.c_int]
u32.GetClientRect.argtypes = [V, V]
u32.GetDC.argtypes = [V]; u32.GetDC.restype = V
u32.ReleaseDC.argtypes = [V, V]
u32.PrintWindow.argtypes = [V, V, ctypes.c_uint]
g32.CreateCompatibleDC.argtypes = [V]; g32.CreateCompatibleDC.restype = V
g32.CreateCompatibleBitmap.argtypes = [V, ctypes.c_int, ctypes.c_int]; g32.CreateCompatibleBitmap.restype = V
g32.SelectObject.argtypes = [V, V]; g32.SelectObject.restype = V
g32.GetDIBits.argtypes = [V, V, ctypes.c_uint, ctypes.c_uint, V, V, ctypes.c_uint]
g32.DeleteObject.argtypes = [V]; g32.DeleteDC.argtypes = [V]

ID_ASSOCIATE, ID_MENU, ID_GO, ID_THUMBNAILS = 101, 102, 103, 104
HANDLER = "\\ShellEx\\{E357FCCD-A995-4576-B01F-234630154E96}"        # where a file type names its thumbnail handler
THUMB_FORMATS = (("stl", (".stl",)), ("obj", (".obj",)), ("3mf", (".3mf",)), ("ply", (".ply",)), ("glb", (".glb",)), ("gltf", (".gltf",)),
                 ("step", (".step", ".stp")), ("iges", (".iges", ".igs")))
BM_CLICK, BM_GETCHECK = 0xF5, 0xF0
results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok)))
    print("%s  %s  %s" % ("PASS" if ok else "FAIL", name, detail))


def reg(path, value=""):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as k:
            return winreg.QueryValueEx(k, value)[0]
    except OSError:
        return None


def has_value(path, value):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as k:
            winreg.QueryValueEx(k, value)
            return True
    except OSError:
        return False


def key_exists(path):
    try:
        winreg.CloseKey(winreg.OpenKey(winreg.HKEY_CURRENT_USER, path))
        return True
    except OSError:
        return False


def set_value(path, value, name=""):
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, path) as k:
        winreg.SetValueEx(k, name, 0, winreg.REG_SZ, value)


def delete_key(path):
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except OSError:
        pass


def delete_value(path, name):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_SET_VALUE) as k:
            winreg.DeleteValue(k, name)
    except OSError:
        pass


def clsid(i):
    return "{4614499B-2F56-4003-83C3-D6115D33A5%02X}" % i


def thumb_places(ext):
    C = "Software\\Classes\\"
    return [C + "Glance.Model" + ext + HANDLER, C + ext + HANDLER, C + "SystemFileAssociations\\" + ext + HANDLER]


def thumbs_registered(inst):
    """True when every class and every file type points at the handler in the installed folder, and nothing asks for it to run inside Explorer."""
    dll = os.path.join(inst, "GlanceThumbs.dll")
    for i, (_, exts) in enumerate(THUMB_FORMATS):
        key = "Software\\Classes\\CLSID\\" + clsid(i)
        if reg(key + "\\InprocServer32") != dll or reg(key + "\\InprocServer32", "ThreadingModel") != "Apartment" or has_value(key, "DisableProcessIsolation"):
            return False
        if reg("Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved", clsid(i)) is None:
            return False
        for e in exts:
            if any(reg(place) != clsid(i) for place in thumb_places(e)):
                return False
            if reg("Software\\Classes\\Glance.Model" + e, "Treatment") != 0 or reg("Software\\Classes\\Glance.Model" + e, "TypeOverlay") != "":
                return False
    return True


def thumbs_gone():
    """True when nothing of the handler is left in the registry."""
    for i, (_, exts) in enumerate(THUMB_FORMATS):
        if key_exists("Software\\Classes\\CLSID\\" + clsid(i)) or has_value("Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved", clsid(i)):
            return False
        for e in exts:
            if any((reg(place) or "").upper().startswith("{4614499B") for place in thumb_places(e)) or has_value("Software\\Classes\\Glance.Model" + e, "Treatment"):
                return False
    return True


class ThumbHost:
    """tools/thumb_host.py on a hidden desktop: a process that loads the thumbnail handler the way Explorer's own does."""

    def __init__(self, out, env, name, jobs, **options):
        self.results = os.path.join(out, name + "-results.json")
        spec = os.path.join(out, name + "-jobs.json")
        json.dump(dict(options, jobs=jobs), open(spec, "w", encoding="utf-8"))
        if os.path.exists(self.results):
            os.remove(self.results)
        self.desk = HiddenDesktop("glance_thumb_host")
        _, self.h = self.desk.spawn([sys.executable, os.path.join(os.path.dirname(os.path.abspath(__file__)), "thumb_host.py"), spec, self.results], env=env)

    def finish(self, timeout=120):
        code = self.desk.wait(self.h, timeout)
        self.desk.close()
        got = json.load(open(self.results, encoding="utf-8"))["results"] if os.path.exists(self.results) else []
        return code, {r["name"]: r for r in got}


class SetupWindow:
    def __init__(self, exe, env):
        self.desk = HiddenDesktop("glance_setup")
        self.pid, self.hproc = self.desk.spawn([exe], env=env)
        self.hwnd = None
        deadline = time.time() + 20
        while time.time() < deadline and self.hwnd is None:
            for h, pid, cls, title, visible in self.desk.windows():
                if cls == "GlanceSetup" and visible:
                    self.hwnd = h
            time.sleep(0.05)
        if self.hwnd is None:
            raise RuntimeError("setup window never appeared: %r" % (self.desk.windows(),))

    def on_desktop(self, fn):
        box = {}

        def run():
            if not u32.SetThreadDesktop(self.desk.handle):
                box["err"] = ctypes.get_last_error(); return
            box["v"] = fn()
        t = threading.Thread(target=run); t.start(); t.join()
        if "err" in box:
            raise OSError("SetThreadDesktop failed: %d" % box["err"])
        return box.get("v")

    def click(self, ctl):
        self.on_desktop(lambda: u32.PostMessageW(u32.GetDlgItem(self.hwnd, ctl), BM_CLICK, 0, 0))
        time.sleep(0.15)

    def checked(self, ctl):
        def f():
            out = ctypes.c_size_t()
            u32.SendMessageTimeoutW(u32.GetDlgItem(self.hwnd, ctl), BM_GETCHECK, 0, 0, 2, 2000, ctypes.byref(out))
            return out.value == 1
        return self.on_desktop(f)

    def text(self, ctl):
        def f():
            b = ctypes.create_unicode_buffer(128)
            u32.GetWindowTextW(u32.GetDlgItem(self.hwnd, ctl), b, 128)
            return b.value
        return self.on_desktop(f)

    def shot(self, png):
        def f():
            r = (ctypes.c_int * 4)(); u32.GetClientRect(self.hwnd, r)
            w, h = r[2], r[3]
            sdc = u32.GetDC(None); mdc = g32.CreateCompatibleDC(sdc); bmp = g32.CreateCompatibleBitmap(sdc, w, h)
            old = g32.SelectObject(mdc, bmp)
            u32.PrintWindow(self.hwnd, mdc, 1 | 2)                 # client area, rendered content
            g32.SelectObject(mdc, old)
            hdr = (ctypes.c_uint32 * 10)(40, w, (-h) & 0xFFFFFFFF, 1 | (32 << 16), 0, 0, 0, 0, 0, 0)
            buf = ctypes.create_string_buffer(w * h * 4)
            g32.GetDIBits(mdc, bmp, 0, h, buf, hdr, 0)
            g32.DeleteObject(bmp); g32.DeleteDC(mdc); u32.ReleaseDC(None, sdc)
            return w, h, buf.raw
        w, h, raw = self.on_desktop(f)
        Image.frombuffer("RGBA", (w, h), raw, "raw", "BGRA", 0, 1).convert("RGB").save(png)

    def wait_text(self, ctl, wanted, timeout=90):
        deadline = time.time() + timeout
        while time.time() < deadline:
            t = self.text(ctl)
            if t in wanted:
                return t
            time.sleep(0.1)
        return self.text(ctl)

    def finish(self):
        code = self.desk.wait(self.hproc, 10)
        self.desk.close()
        return code


def run(exe, args, env, timeout=60, linger=None):
    """linger: keep the desktop (and its kill-on-close job) alive until this returns true, for helpers the program leaves behind."""
    with HiddenDesktop("glance_setup_run") as desk:
        _, h = desk.spawn([exe] + args, env=env)
        code = desk.wait(h, timeout)
        deadline = time.time() + 15
        while linger and not linger() and time.time() < deadline:
            time.sleep(0.25)
        return code


def main():
    setup, sample, out = sys.argv[1], sys.argv[2], os.path.abspath(sys.argv[3])
    shutil.rmtree(out, ignore_errors=True); os.makedirs(out)
    inst = os.path.join(out, "inst", "Glance")
    lnk = os.path.join(out, "inst", "Glance.lnk")
    tmp = os.path.join(out, "tmp"); os.makedirs(tmp)                    # where a handler DLL that is in use gets moved to
    env = dict(os.environ, GLANCE_SETUP_DIR=inst, GLANCE_SETUP_LNK=lnk, GLANCE_DATA_DIR=os.path.join(out, "data"), GLANCE_QUIET="1", TMP=tmp, TEMP=tmp)
    samples = os.path.dirname(os.path.abspath(sample))
    exe = os.path.join(inst, "Glance.exe")
    C = "Software\\Classes\\"
    menu_key = C + "SystemFileAssociations\\%s\\shell\\Glance\\command"
    exts = (".stl", ".obj", ".3mf", ".ply", ".glb", ".gltf", ".step", ".stp", ".iges", ".igs")

    print("== 1: every box ticked (the default) ==")
    report = os.path.join(out, "fonts.txt")
    w = SetupWindow(setup, dict(env, GLANCE_SETUP_REPORT=report))
    time.sleep(0.4)                                                     # until it has drawn itself, or the picture is black
    w.shot(os.path.join(out, "1-ready.png"))
    fonts = open(report).read() if os.path.exists(report) else ""
    props = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Directory.Build.props"), encoding="utf-8").read()
    version = props.split("<Version>")[1].split("</Version>")[0]
    check("setup uses the built-in typeface", fonts.endswith("loaded=3 regular=True medium=True light=True"), fonts)
    check("setup carries the version from Directory.Build.props", fonts.startswith("version=%s " % version), "%s vs %s" % (fonts.split(" ")[0], version))
    check("all three boxes are ticked to begin with", w.checked(ID_ASSOCIATE) and w.checked(ID_MENU) and w.checked(ID_THUMBNAILS))
    check("the button says Install", w.text(ID_GO) == "Install", w.text(ID_GO))
    t0 = time.time(); w.click(ID_GO)
    end = w.wait_text(ID_GO, ("Done", "Close"))
    check("install finishes", end == "Done", "button says %r after %.1f s" % (end, time.time() - t0))
    time.sleep(0.3); w.shot(os.path.join(out, "2-done.png"))
    w.click(ID_GO)
    check("Done closes setup with exit code 0", w.finish() == 0)
    n = sum(len(f) for _, _, f in os.walk(inst))
    check("program files are in place", os.path.exists(exe) and os.path.exists(os.path.join(inst, "step", "glance-step.exe")), "%d files" % n)
    check("licence notices are installed", os.path.exists(os.path.join(inst, "THIRD-PARTY-NOTICES.txt")) and os.path.exists(os.path.join(inst, "Apache-2.0.txt")))
    check("Start menu shortcut is written", os.path.exists(lnk) and os.path.getsize(lnk) > 100)
    want = '"%s" "%%1"' % exe
    check("every type opens with Glance", all(reg(C + e) == "Glance.Model" + e and reg(C + "Glance.Model%s\\shell\\open\\command" % e) == want for e in exts))
    check("every type has the right-click entry", all(reg(menu_key % e) == want for e in exts), str(reg(menu_key % ".stl")))
    check("Installed apps shows the same version", reg("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Glance", "DisplayVersion") == version,
          str(reg("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Glance", "DisplayVersion")))
    check("Glance is listed in Installed apps", reg("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Glance", "DisplayName") == "Glance"
          and (reg("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Glance", "UninstallString") or "").endswith('--uninstall'))
    check("the thumbnail handler and its helper are installed", os.path.exists(os.path.join(inst, "GlanceThumbs.dll")) and os.path.exists(os.path.join(inst, "glance-thumb.exe")))
    check("every type has the thumbnail handler, registered for this user only and to run outside Explorer", thumbs_registered(inst),
          "%s -> %s" % (reg(C + ".stl" + HANDLER), reg(C + "CLSID\\" + clsid(0) + "\\InprocServer32")))
    check("the choice is recorded", reg("Software\\Glance", "Thumbnails") == 1)

    print("== 1b: Windows finds the handler and gets a picture from it ==")
    # In this process, not in Explorer: the shell is asked for the thumbnail handler of each file, which makes it look
    # the file type up in the registry, create the class from the installed DLL and hand it the file itself.
    kinds = [f for f in ("knot.stl", "vase.obj", "assembly.3mf", "box-binary.ply", "blocks.glb", "rounded-cube.step", "Cube 10x10.igs") if os.path.exists(os.path.join(samples, f))]
    pics = os.path.join(out, "thumbnails"); os.makedirs(pics)
    jobs = [{"name": f, "file": os.path.join(samples, f), "px": 256, "how": "shell", "png": os.path.join(pics, f + ".png")} for f in kinds]
    jobs.append({"name": "by class id", "file": os.path.abspath(sample), "format": 0, "px": 96, "how": "cocreate", "png": os.path.join(pics, "by-class-id.png")})
    code, got = ThumbHost(out, env, "shell", jobs).finish()
    bad = [f for f in kinds if not got.get(f, {}).get("ok")]
    check("the shell's own lookup leads to Glance's handler for every type, and each gives a picture", code == 0 and kinds and not bad,
          "; ".join("%s %sx%s" % (f, got[f].get("w"), got[f].get("h")) for f in kinds if f in got) + (" | failed: %s %s" % (bad, [got.get(f, {}).get("steps") for f in bad]) if bad else ""))
    check("creating the class by its id works too (COM finds the per-user registration)", got.get("by class id", {}).get("ok"), str(got.get("by class id", {}).get("steps")))

    print("== 2: the installed copy works ==")
    bmp = os.path.join(out, "3-installed.bmp")
    code = run(exe, [sample, "--snapshot", bmp], env)
    report = open(bmp + ".txt", encoding="utf-8").read() if os.path.exists(bmp + ".txt") else ""
    tris = [p[5:] for p in report.split() if p.startswith("tris=")]
    check("the installed Glance opens a model", code == 0 and tris and int(tris[0]) > 0, "tris=%s" % (tris[0] if tris else "?"))
    if os.path.exists(bmp):
        Image.open(bmp).convert("RGB").save(bmp[:-4] + ".png"); os.remove(bmp)

    print("== 3: every box cleared, installed over the top ==")
    w = SetupWindow(setup, env)
    w.click(ID_ASSOCIATE); w.click(ID_MENU); w.click(ID_THUMBNAILS)
    check("the boxes can be cleared", not w.checked(ID_ASSOCIATE) and not w.checked(ID_MENU) and not w.checked(ID_THUMBNAILS))
    w.shot(os.path.join(out, "4-cleared.png"))
    w.click(ID_GO)
    check("install over an existing copy finishes", w.wait_text(ID_GO, ("Done", "Close")) == "Done")
    w.click(ID_GO); w.finish()
    check("no type is taken over", all(not (reg(C + e) or "").startswith("Glance.") for e in exts), str(reg(C + ".stl")))
    check("the right-click entry is gone", all(reg(menu_key % e) is None for e in exts))
    check("Glance is still offered under Open with", all(has_value(C + e + "\\OpenWithProgids", "Glance.Model" + e) for e in exts))
    check("the thumbnail handler is not registered, and that choice is recorded", thumbs_gone() and reg("Software\\Glance", "Thumbnails") == 0, str(reg(C + ".stl" + HANDLER)))
    check("its files are still installed, for a later change of mind", os.path.exists(os.path.join(inst, "GlanceThumbs.dll")))

    print("== 4: unattended installs ==")
    check("--quiet --no-thumbnails installs without a window", run(setup, ["--quiet", "--no-thumbnails"], env, 120) == 0)
    check("and leaves the thumbnail handler out, nothing else", thumbs_gone() and reg(C + ".stl") == "Glance.Model.stl" and reg(menu_key % ".stl") == want)
    other = "{11111111-2222-3333-4444-555555555555}"                    # another app's thumbnail handler for STL files, already there
    set_value(C + ".stl" + HANDLER, other); set_value(C + "SystemFileAssociations\\.stl" + HANDLER, other)
    check("--quiet installs without a window", run(setup, ["--quiet", "--no-menu"], env, 120) == 0)
    check("--quiet honours its switches", reg(C + ".stl") == "Glance.Model.stl" and reg(menu_key % ".stl") is None and thumbs_registered(inst))

    print("== 5: installing over a copy whose thumbnail handler Windows has loaded ==")

    def holder(name):
        """A process that has made a thumbnail (so the DLL is loaded and has run) and stays alive until told to go on."""
        ready, go = os.path.join(out, name + "-ready"), os.path.join(out, name + "-go")
        job = {"name": "before", "file": os.path.abspath(sample), "format": 0, "px": 96, "how": "stream"}
        h = ThumbHost(out, env, name, [job], dll=os.path.join(inst, "GlanceThumbs.dll"), touch=ready, hold=120, until=go, after=dict(job, name="after"))
        deadline = time.time() + 60
        while not os.path.exists(ready) and time.time() < deadline:
            time.sleep(0.1)
        return h, go

    h, go = holder("held-install")
    was = os.path.getmtime(os.path.join(inst, "GlanceThumbs.dll"))
    t0 = time.time()
    code = run(setup, ["--quiet"], env, 120)
    aside = glob.glob(os.path.join(tmp, "glance-thumbs-old-*.dll"))
    check("the install goes through all the same", code == 0 and os.path.exists(exe) and os.path.exists(os.path.join(inst, "GlanceThumbs.dll")), "exit %s after %.1f s" % (code, time.time() - t0))
    check("the DLL that was in use was moved out of the way, not deleted under the process using it", len(aside) == 1 and os.path.getmtime(aside[0]) == was, str([os.path.basename(a) for a in aside]))
    check("the new copy is registered", thumbs_registered(inst))
    open(go, "w").close()
    code, got = h.finish()
    check("the process that had the old DLL loaded carried on and still gets thumbnails", code == 0 and got.get("before", {}).get("ok") and got.get("after", {}).get("ok"),
          "before: %s, after: %s" % (got.get("before", {}).get("hr"), got.get("after", {}).get("steps")))
    check("the next install clears the moved file away", run(setup, ["--quiet", "--no-menu"], env, 120) == 0 and not glob.glob(os.path.join(tmp, "glance-thumbs-old-*.dll")))

    print("== 6: uninstall, again with the handler loaded ==")
    h, go = holder("held-uninstall")
    check("uninstall runs", run(exe, ["--uninstall", "--quiet"], env, linger=lambda: not os.path.exists(inst)) == 0)
    check("program folder is removed", not os.path.exists(inst), str(os.listdir(inst)) if os.path.exists(inst) else "")
    check("shortcut is removed", not os.path.exists(lnk))
    check("registry entries are removed", reg(C + "Glance.Model.stl") is None and reg("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Glance", "DisplayName") is None
          and not (reg(C + ".stl") or "").startswith("Glance.") and not has_value("Software\\RegisteredApplications", "Glance"))
    check("the thumbnail handler is unregistered", thumbs_gone() and not key_exists("Software\\Glance"))
    check("and the other app's handler it had replaced is back", reg(C + ".stl" + HANDLER) == other and reg(C + "SystemFileAssociations\\.stl" + HANDLER) == other,
          "%s / %s" % (reg(C + ".stl" + HANDLER), reg(C + "SystemFileAssociations\\.stl" + HANDLER)))
    open(go, "w").close()
    code, got = h.finish()
    check("the process that had the DLL loaded is unharmed (its next thumbnail simply fails)", code == 0 and got.get("before", {}).get("ok") and "after" in got and not got["after"]["ok"],
          "exit %s, after: %s" % (code, got.get("after", {}).get("hr")))
    for place in (C + ".stl" + HANDLER, C + "SystemFileAssociations\\.stl" + HANDLER):     # the test's own stand-in for another app
        delete_key(place); delete_key(place[:place.rindex("\\")])

    failed = [n for n, ok in results if not ok]
    print("\n%d checks, %d failed" % (len(results), len(failed)))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
