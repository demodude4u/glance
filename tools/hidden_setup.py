"""End-to-end test of "Glance Setup.exe" on a private, never-shown Win32 desktop.

  python tools/hidden_setup.py "<dist>\\Glance Setup.exe" <sample.stl> <out_dir>

Installs into <out_dir>\\inst\\Glance (not the real Programs folder), drives the setup window with posted
window messages, and checks the files, the registry entries, the Start menu shortcut and the uninstall.
"""
import ctypes
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

ID_ASSOCIATE, ID_MENU, ID_GO = 101, 102, 103
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
    env = dict(os.environ, GLANCE_SETUP_DIR=inst, GLANCE_SETUP_LNK=lnk, GLANCE_DATA_DIR=os.path.join(out, "data"), GLANCE_QUIET="1")
    exe = os.path.join(inst, "Glance.exe")
    C = "Software\\Classes\\"
    menu_key = C + "SystemFileAssociations\\%s\\shell\\Glance\\command"
    exts = (".stl", ".obj", ".step", ".stp", ".iges", ".igs")

    print("== 1: both boxes ticked (the default) ==")
    report = os.path.join(out, "fonts.txt")
    w = SetupWindow(setup, dict(env, GLANCE_SETUP_REPORT=report))
    w.shot(os.path.join(out, "1-ready.png"))
    fonts = open(report).read() if os.path.exists(report) else ""
    check("setup uses the built-in typeface", fonts == "loaded=3 regular=True medium=True light=True", fonts)
    check("both boxes are ticked to begin with", w.checked(ID_ASSOCIATE) and w.checked(ID_MENU))
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
    check("Glance is listed in Installed apps", reg("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Glance", "DisplayName") == "Glance"
          and (reg("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Glance", "UninstallString") or "").endswith('--uninstall'))

    print("== 2: the installed copy works ==")
    bmp = os.path.join(out, "3-installed.bmp")
    code = run(exe, [sample, "--snapshot", bmp], env)
    report = open(bmp + ".txt", encoding="utf-8").read() if os.path.exists(bmp + ".txt") else ""
    tris = [p[5:] for p in report.split() if p.startswith("tris=")]
    check("the installed Glance opens a model", code == 0 and tris and int(tris[0]) > 0, "tris=%s" % (tris[0] if tris else "?"))
    if os.path.exists(bmp):
        Image.open(bmp).convert("RGB").save(bmp[:-4] + ".png"); os.remove(bmp)

    print("== 3: both boxes cleared, installed over the top ==")
    w = SetupWindow(setup, env)
    w.click(ID_ASSOCIATE); w.click(ID_MENU)
    check("the boxes can be cleared", not w.checked(ID_ASSOCIATE) and not w.checked(ID_MENU))
    w.shot(os.path.join(out, "4-cleared.png"))
    w.click(ID_GO)
    check("install over an existing copy finishes", w.wait_text(ID_GO, ("Done", "Close")) == "Done")
    w.click(ID_GO); w.finish()
    check("no type is taken over", all(not (reg(C + e) or "").startswith("Glance.") for e in exts), str(reg(C + ".stl")))
    check("the right-click entry is gone", all(reg(menu_key % e) is None for e in exts))
    check("Glance is still offered under Open with", all(has_value(C + e + "\\OpenWithProgids", "Glance.Model" + e) for e in exts))

    print("== 4: unattended install, then uninstall ==")
    check("--quiet installs without a window", run(setup, ["--quiet", "--no-menu"], env, 120) == 0)
    check("--quiet honours its switches", reg(C + ".stl") == "Glance.Model.stl" and reg(menu_key % ".stl") is None)
    check("uninstall runs", run(exe, ["--uninstall", "--quiet"], env, linger=lambda: not os.path.exists(inst)) == 0)
    check("program folder is removed", not os.path.exists(inst))
    check("shortcut is removed", not os.path.exists(lnk))
    check("registry entries are removed", reg(C + "Glance.Model.stl") is None and reg("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Glance", "DisplayName") is None
          and not (reg(C + ".stl") or "").startswith("Glance.") and not has_value("Software\\RegisteredApplications", "Glance"))

    failed = [n for n, ok in results if not ok]
    print("\n%d checks, %d failed" % (len(results), len(failed)))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
