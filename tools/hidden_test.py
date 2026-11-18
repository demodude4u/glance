"""End-to-end test of Glance on a private, never-shown Win32 desktop.

  python tools/hidden_test.py <Glance.exe> <samples_dir> <out_dir>

Nothing appears on the real screen. Every process runs inside a kill-on-close job (ctcore.hidden_desktop from
a separate helper package), with settings and cache redirected to a scratch folder via GLANCE_DATA_DIR.

Part 1 opens each sample format and records how long the first picture took.
Part 2 keeps one window open and drives it with real window messages (mouse drags, wheel, double-click, keys),
asking the app for a picture and its state after each step and checking the state changed the way it should.
Later parts cover measuring, units, pictures, mode options and the update indicator; the last opens the 3MF, PLY
and glTF samples from tools/make_samples.py and checks their triangle counts, sizes and units, then a broken file
of each kind.
"""
import ctypes
import os
import shutil
import sys
import threading
import time

sys.path.insert(0, __import__("os").environ.get("CTCORE_PATH", "."))   # where the ctcore package lives, if not installed
from ctcore.hidden_desktop import HiddenDesktop  # noqa: E402
from PIL import Image  # noqa: E402

u32 = ctypes.WinDLL("user32", use_last_error=True)
# Per-monitor DPI aware, like Glance itself: otherwise Windows rescales the mouse coordinates we post.
u32.SetProcessDpiAwarenessContext.argtypes = [ctypes.c_void_p]
u32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
u32.PostMessageW.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_size_t, ctypes.c_ssize_t]
u32.SetThreadDesktop.argtypes = [ctypes.c_void_p]

WM_CLOSE, WM_KEYDOWN, WM_MOUSEMOVE = 0x10, 0x100, 0x200
WM_LBUTTONDOWN, WM_LBUTTONUP, WM_LBUTTONDBLCLK, WM_RBUTTONDOWN, WM_RBUTTONUP, WM_MOUSEWHEEL = 0x201, 0x202, 0x203, 0x204, 0x205, 0x20A
WM_APP_SHOT = 0x8000 + 2

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print("%s  %s  %s" % ("PASS" if ok else "FAIL", name, detail))


def to_png(bmp):
    png = os.path.splitext(bmp)[0] + ".png"
    Image.open(bmp).convert("RGB").save(png)
    os.remove(bmp)
    return png


def snapshot(exe, out_png, env, model=None, press="", timeout=120):
    bmp = os.path.splitext(out_png)[0] + ".bmp"
    for f in (bmp, bmp + ".txt"):
        if os.path.exists(f):
            os.remove(f)
    args = [exe] + ([model] if model else []) + ["--snapshot", bmp] + (["--press", press] if press else [])
    t0 = time.perf_counter()
    with HiddenDesktop("glance_test") as desk:
        _, h = desk.spawn(args, env=env)
        code = desk.wait(h, timeout)
    dt = time.perf_counter() - t0
    report = open(bmp + ".txt", encoding="utf-8").read() if os.path.exists(bmp + ".txt") else ""
    if os.path.exists(bmp):
        to_png(bmp)
    return code, dt, report


def field(report, key):
    for part in report.replace("\n", " ").split():
        if part.startswith(key + "="):
            return part[len(key) + 1:]
    return None


class Session:
    """One Glance window on a hidden desktop, driven by posted window messages."""

    def __init__(self, exe, model, env, out_dir):
        self.out = out_dir
        self.desk = HiddenDesktop("glance_drive")
        self.env = dict(env, GLANCE_TEST_DIR=out_dir)
        self.pid, self.hproc = self.desk.spawn([exe, model], env=self.env)
        self.hwnd = None
        self.n = 0
        deadline = time.time() + 20
        while time.time() < deadline and self.hwnd is None:
            for h, pid, cls, title, visible in self.desk.windows():
                if cls == "GlanceWindow" and visible:
                    self.hwnd, self.title = h, title
            time.sleep(0.05)
        if self.hwnd is None:
            raise RuntimeError("Glance window never appeared: %r" % (self.desk.windows(),))

    def _on_desktop(self, fn):
        """Window messages can only be posted from a thread attached to the same desktop."""
        box = {}

        def run():
            if not u32.SetThreadDesktop(self.desk.handle):
                box["err"] = ctypes.get_last_error()
                return
            box["value"] = fn()
        t = threading.Thread(target=run)
        t.start(); t.join()
        if "err" in box:
            raise OSError("SetThreadDesktop failed: %d" % box["err"])
        return box.get("value")

    def post(self, msg, wp=0, lp=0):
        return self._on_desktop(lambda: u32.PostMessageW(self.hwnd, msg, wp, lp))

    def post_many(self, items, gap=0.012):
        def run():
            for msg, wp, lp in items:
                u32.PostMessageW(self.hwnd, msg, wp, lp)
                time.sleep(gap)
        self._on_desktop(run)

    @staticmethod
    def xy(x, y):
        return (int(y) << 16) | (int(x) & 0xFFFF)

    def drag(self, x0, y0, x1, y1, right=False, steps=12, hold=0.15):
        down, up, flag = (WM_RBUTTONDOWN, WM_RBUTTONUP, 2) if right else (WM_LBUTTONDOWN, WM_LBUTTONUP, 1)
        items = [(down, flag, self.xy(x0, y0))]
        for i in range(1, steps + 1):
            items.append((WM_MOUSEMOVE, flag, self.xy(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps)))
        self.post_many(items)
        time.sleep(hold)          # pause before releasing, so the orbit does not coast
        self.post(up, 0, self.xy(x1, y1))

    def key(self, vk):
        self.post(WM_KEYDOWN, vk if isinstance(vk, int) else ord(vk), 0)

    def title_now(self):
        for h, pid, cls, title, visible in self.desk.windows():
            if h == self.hwnd:
                return title
        return None

    def shot(self, name, timeout=60):
        self.n += 1
        txt = os.path.join(self.out, "shot%d.txt" % self.n)
        self.post(WM_APP_SHOT, self.n, 0)
        deadline = time.time() + timeout
        while time.time() < deadline and not os.path.exists(txt):
            if HiddenDesktop.wait(self.hproc, 0) is not None:
                raise RuntimeError("Glance exited unexpectedly during '%s'" % name)
            time.sleep(0.03)
        if not os.path.exists(txt):
            raise RuntimeError("no response to snapshot request '%s'" % name)
        time.sleep(0.05)
        state = dict(line.split("=", 1) for line in open(txt, encoding="utf-8").read().split("\n") if "=" in line)
        os.remove(txt)
        bmp = os.path.join(self.out, "shot%d.bmp" % self.n)
        png = os.path.join(self.out, "drive-%02d-%s.png" % (self.n, name))
        if os.path.exists(png):
            os.remove(png)
        os.replace(to_png(bmp), png)
        state["png"] = png
        return state

    def close(self):
        self.post(WM_CLOSE)
        code = HiddenDesktop.wait(self.hproc, 10)
        self.desk.close()
        return code


def f(state, key):
    return float(state[key])


def screen(state, x, y, z):
    """Where a model-space point lands on screen (orthographic view, Z-up model)."""
    import math
    yaw, pitch = f(state, "yaw"), f(state, "pitch")
    w, h = (int(v) for v in state["size"].split("x"))
    tx, ty, tz = (float(v) for v in state["target"].split(","))
    cp, sp, cy, sy = math.cos(pitch), math.sin(pitch), math.cos(yaw), math.sin(yaw)
    d = (cp * cy, cp * sy, sp); r = (-sy, cy, 0.0)
    u = (d[1] * r[2] - d[2] * r[1], d[2] * r[0] - d[0] * r[2], d[0] * r[1] - d[1] * r[0])
    rel = (x - tx, y - ty, z - tz)
    ppu = f(state, "ppu")
    return (w / 2 + sum(a * b for a, b in zip(rel, r)) * ppu, h / 2 - sum(a * b for a, b in zip(rel, u)) * ppu)


def click(s, state, x, y, z):
    px, py = screen(state, x, y, z)
    s.post_many([(WM_LBUTTONDOWN, 1, s.xy(px, py)), (WM_LBUTTONUP, 0, s.xy(px, py))], gap=0.03)


def split(result):
    title, _, value = result.partition("|")
    try:
        return title, float(value)
    except ValueError:
        return title, float("nan")


def units_and_layflat_tests(exe, S, env, out):
    print("== Part 4: file units and lay flat ==")
    WM_APP_UNIT = 0x8000 + 3
    for name, source, what in (("plate-inch-declared.stl", "0", "stated in the file"), ("plate-inch-undeclared.stl", "1", "assumed")):
        s = Session(exe, S(name), env, out)
        try:
            a = s.shot("units-" + what.split()[0])
            dims = [float(v) for v in a["dims"].split(",")]
            check("inch STL (%s) is shown at true size" % what, a["fileUnit"] == "in" and a["unitSource"] == source
                  and all(abs(d - e) < 0.01 for d, e in zip(dims, (80, 50, 10))), "read as %s, %s mm" % (a["fileUnit"], a["dims"]))
            if source == "1":
                s.post(WM_APP_UNIT)
                b = s.shot("units-cycled")
                check("the file's unit can be changed and is remembered", b["fileUnit"] == "cm" and b["unitSource"] == "2", "now %s, %s mm" % (b["fileUnit"], b["dims"]))
                for _ in range(4):
                    s.post(WM_APP_UNIT); s.shot("units-next")
                c = s.shot("units-back")
                check("cycling comes back round to inches", c["fileUnit"] == "in" and c["unitSource"] == "2")
        finally:
            s.close()
    s = Session(exe, S("plate-inch-undeclared.stl"), env, out)
    try:
        a = s.shot("units-reopened")
        check("the chosen unit is remembered when the file is reopened", a["fileUnit"] == "in" and a["unitSource"] == "2")
    finally:
        s.close()
    s = Session(exe, S("plate-inch-declared.stl"), env, out)
    try:
        s.post(0x8000 + 4)
        a = s.shot("fix-units-open")
        import re
        check("the build carries a version number", re.fullmatch(r"\d+\.\d+\.\d+(_\([0-9a-f]{7}\))?", a["version"]) is not None, a["version"])
        check("text is drawn with the built-in typeface", a["fonts"] == "Glance_Roboto/Glance_Roboto_Medium/Glance_Roboto_Light" and a["fontsLoaded"] == "3",
              "%s, %s loaded" % (a["fonts"], a["fontsLoaded"]))
        rows = [tuple(float(v) for v in r.split(",")) for r in a["unitRows"].split(";") if r]
        check("Fix units opens a chooser with five units", a["fixUnits"] == "True" and len(rows) == 5, a["unitRows"])
        s.post_many([(WM_LBUTTONDOWN, 1, s.xy(*rows[0])), (WM_LBUTTONUP, 0, s.xy(*rows[0]))], gap=0.03)
        b = s.shot("fix-units-picked-mm")
        dims = [float(v) for v in b["dims"].split(",")]
        check("picking a row applies that unit and closes the chooser", b["fixUnits"] == "False" and b["fileUnit"] == "mm" and b["unitSource"] == "2"
              and abs(dims[0] - 80 / 25.4) < 0.01, "now %s, %s mm" % (b["fileUnit"], b["dims"]))
        s.post(0x8000 + 4)
        c = s.shot("fix-units-again")
        s.post_many([(WM_LBUTTONDOWN, 1, s.xy(8, 300)), (WM_LBUTTONUP, 0, s.xy(8, 300))], gap=0.03)
        d = s.shot("fix-units-dismissed")
        check("clicking outside the chooser closes it and changes nothing", c["fixUnits"] == "True" and d["fixUnits"] == "False" and d["fileUnit"] == "mm")
        s.post(0x8000 + 4); s.key(0x1B)
        e = s.shot("fix-units-esc")
        check("Esc closes the chooser without closing Glance", e["fixUnits"] == "False")
        s.post(0x8000 + 4)
        f_ = s.shot("fix-units-back")
        rows = [tuple(float(v) for v in r.split(",")) for r in f_["unitRows"].split(";") if r]
        s.post_many([(WM_LBUTTONDOWN, 1, s.xy(*rows[1])), (WM_LBUTTONUP, 0, s.xy(*rows[1]))], gap=0.03)
        g = s.shot("fix-units-picked-in")
        check("picking inches restores the true size", g["fileUnit"] == "in" and g["dims"].startswith("80.000,50.000,10.000"), g["dims"])
    finally:
        s.close()

    s = Session(exe, S("plate-holes.stl"), env, out)
    try:
        a = s.shot("ortho-before")
        spot = dict(b.split(":") for b in a["buttons"].split(";") if b).get("7")
        check("the toolbar has an Ortho button", spot is not None, a["buttons"])
        if spot:
            xy = tuple(float(v) for v in spot.split(","))
            s.post_many([(WM_LBUTTONDOWN, 1, s.xy(*xy)), (WM_LBUTTONUP, 0, s.xy(*xy))], gap=0.03)
            b = s.shot("ortho-toggled")
            s.post_many([(WM_LBUTTONDOWN, 1, s.xy(*xy)), (WM_LBUTTONUP, 0, s.xy(*xy))], gap=0.03)
            c = s.shot("ortho-back")
            check("the Ortho button switches orthographic on and off", b["ortho"] != a["ortho"] and c["ortho"] == a["ortho"],
                  "%s, %s, %s" % (a["ortho"], b["ortho"], c["ortho"]))
        names = [s.shot("shading-start")["shading"]]
        spot = dict(b.split(":") for b in s.shot("shading-buttons")["buttons"].split(";") if b).get("8")
        check("the toolbar has a Shading button", spot is not None)
        s.key("5"); s.key("0")
        if spot:
            xy = tuple(float(v) for v in spot.split(","))
            for _ in range(8):
                s.post_many([(WM_LBUTTONDOWN, 1, s.xy(*xy)), (WM_LBUTTONUP, 0, s.xy(*xy))], gap=0.03)
                names.append(s.shot("shading-%d" % len(names))["shading"])
        check("the Shading button steps through eight looks and comes back round",
              names == ["Shaded", "Smooth", "Studio", "Metal", "Lines", "X-ray", "Wire", "Overhangs", "Shaded"], " > ".join(names))

        # pictures
        def press(spot):
            s.post_many([(WM_LBUTTONDOWN, 1, s.xy(*spot)), (WM_LBUTTONUP, 0, s.xy(*spot))], gap=0.03)

        def spots(state, key):
            return {b.split(":")[0]: tuple(float(v) for v in b.split(":")[1].split(",")) for b in state[key].split(";") if b}
        export = os.path.join(out, "export.png")
        s.key("V"); s.key("V")
        st = s.shot("studio")
        check("Studio look is on for the picture tests", st["shading"] == "Studio")
        pic = spots(st, "buttons").get("9")
        check("the toolbar has a Picture button", pic is not None, st["buttons"])
        press(pic)
        card = s.shot("picture-card")
        ctl = spots(card, "picButtons")
        check("Picture opens a card with copy, save, six sizes and background", card["picture"] == "True" and sorted(ctl) == [str(n) for n in range(20, 30)], card["picButtons"])
        win = tuple(int(v) for v in card["size"].split("x"))

        def nums(state, key):
            return [float(v) for v in state[key].split(",")]

        def save(name):
            if os.path.exists(export): os.remove(export)
            s.post(0x8000 + 5)
            s.shot(name)
            return Image.open(export) if os.path.exists(export) else None

        def solid(im):
            """Bounding box of the part itself in a transparent picture (its soft shadow is only partly opaque)."""
            return im.getchannel("A").point(lambda v: 255 if v >= 250 else 0).getbbox()

        fx, fy, fw, fh = nums(card, "pictureFrame")
        check("a new install makes Full HD pictures", card["pictureChoice"] == "fullhd" and card["pictureSize"] == "1920x1080", "%s %s" % (card["pictureChoice"], card["pictureSize"]))
        check("the window shows a 16:9 frame that fills its width or its height", abs(fw / fh - 16 / 9) < 0.01 and fx >= -0.5 and fy >= -0.5
              and (abs(fw - win[0]) < 1 or abs(fh - win[1]) < 1), "frame %s in a %s window" % (card["pictureFrame"], card["size"]))
        bx, by, bw, bh = nums(card, "pictureCard")
        check("the card stays inside the window", bx >= 0 and bx + bw <= win[0], card["pictureCard"])

        press(ctl["22"]); press(ctl["29"])                       # Window, transparent
        asis = s.shot("picture-window-transparent")
        check("Window makes the picture exactly the window's size", asis["pictureChoice"] == "window" and asis["pictureSize"] == card["size"]
              and nums(asis, "pictureFrame") == [0, 0, win[0], win[1]], "%s, frame %s" % (asis["pictureSize"], asis["pictureFrame"]))
        im = save("picture-saved-window")
        ok = im is not None
        check("a transparent picture is saved as a PNG at the chosen size", ok and im.size == win and im.mode == "RGBA", "%s %s" % ((im.size, im.mode) if ok else ("missing", "")))
        ref = None
        if ok:
            a = im.getchannel("A")
            lo, hi = a.getextrema()
            check("its background is see-through and the part is solid", a.getpixel((2, 2)) == 0 and hi == 255, "corner alpha %d, range %d-%d" % (a.getpixel((2, 2)), lo, hi))
            ref = solid(im)
            im.close()

        press(ctl["26"])                                         # 4K
        big = s.shot("picture-4k-transparent")
        check("4K is 3840 x 2160 whatever the window's shape", big["pictureChoice"] == "4k" and big["pictureSize"] == "3840x2160" and big["pictureLimited"] == "False", big["pictureSize"])
        im = save("picture-saved-4k")
        ok = im is not None
        check("the 4K picture is saved at 3840 x 2160", ok and im.size == (3840, 2160) and im.mode == "RGBA", "%s %s" % ((im.size, im.mode) if ok else ("missing", "")))
        if ok and ref:
            # What is inside the frame on screen is what is in the picture: same framing, just more pixels.
            fx, fy, fw, fh = nums(big, "pictureFrame")
            k = 2160 / fh
            got = solid(im)
            want = [(ref[0] - fx) * k, (ref[1] - fy) * k, (ref[2] - fx) * k, (ref[3] - fy) * k]
            check("the picture holds exactly what the frame shows", all(abs(g - w_) <= 3 + k for g, w_ in zip(got, want)),
                  "part at %s, expected about %s" % (got, [round(v) for v in want]))
            im.close(); shutil.copy(export, os.path.join(out, "export-studio-transparent.png"))

        s.post(0x8000 + 6, 2048)                                 # a graphics card that stops at 2048 pixels
        lim = s.shot("picture-4k-limited")
        check("a size the graphics card cannot draw is made as large as it can, and the card says so",
              lim["pictureSize"] == "2048x1152" and lim["pictureLimited"] == "True", "%s limited=%s" % (lim["pictureSize"], lim["pictureLimited"]))
        s.post(0x8000 + 6, 0)
        check("without that limit 4K is whole again", s.shot("picture-4k-unlimited")["pictureSize"] == "3840x2160")

        press(ctl["27"])                                         # Square
        sq = s.shot("picture-square")
        fx, fy, fw, fh = nums(sq, "pictureFrame")
        check("Square is 2048 x 2048 with a square frame", sq["pictureSize"] == "2048x2048" and abs(fw - fh) < 1 and abs(fh - min(win)) < 1, "%s, frame %s" % (sq["pictureSize"], sq["pictureFrame"]))
        press(ctl["23"])
        hd = s.shot("picture-hd")
        check("HD is 1280 x 720", hd["pictureSize"] == "1280x720", hd["pictureSize"])
        press(ctl["25"])
        check("QHD is 2560 x 1440", s.shot("picture-qhd")["pictureSize"] == "2560x1440")

        press(ctl["24"]); press(ctl["28"])                       # Full HD, background as shown
        before = s.shot("picture-fullhd")
        s.drag(win[0] * 0.25, win[1] * 0.5, win[0] * 0.25 + 120, win[1] * 0.5 + 30)
        turned = s.shot("picture-orbit")
        check("dragging the view turns the model while the card stays open", turned["picture"] == "True" and abs(f(turned, "yaw") - f(before, "yaw")) > 0.2,
              "yaw %.2f -> %.2f" % (f(before, "yaw"), f(turned, "yaw")))
        press((bx + bw / 2, by + bh - 6))                        # a blank part of the card
        check("a click on the card itself leaves it open", s.shot("picture-card-click")["picture"] == "True")
        press((win[0] * 0.25, win[1] * 0.6))
        check("a click on the view closes the card", s.shot("picture-click-away")["picture"] == "False")
        s.key("0")
        press(pic); s.key(0x1B)
        closed = s.shot("picture-closed")
        check("Esc closes the Picture card without closing Glance", closed["picture"] == "False")
        im = save("picture-saved-2")
        check("an 'as shown' picture is opaque, Full HD, with no toolbar", im is not None and im.mode == "RGB" and im.size == (1920, 1080), "%s %s" % ((im.size, im.mode) if im else ("missing", "")))
        im.close(); shutil.copy(export, os.path.join(out, "export-studio.png"))
        s.key("V"); s.post(0x8000 + 5); s.shot("picture-saved-3")
        shutil.copy(export, os.path.join(out, "export-metal.png"))
        for _ in range(5):
            s.key("V")
        check("back to Shaded after the picture tests", s.shot("shading-reset")["shading"] == "Shaded")
        s.key("V")
        check("V does the same from the keyboard", s.shot("shading-key")["shading"] == "Smooth")
        s.key("E")
        check("E still switches edges on its own", s.shot("shading-edges")["shading"] == "Shaded")
        a = s.shot("mm-plate")
        check("a millimeter-sized STL is left alone", a["fileUnit"] == "mm" and a["dims"].startswith("80.000,50.000,10.000"), a["dims"])
    finally:
        s.close()

    s = Session(exe, S("bracket-ascii.stl"), env, out)
    try:
        s.key("P"); s.key("0"); s.key("L")
        a = s.shot("layflat-armed")
        check("L arms lay flat", a["layFlat"] == "True")
        click(s, a, 6, 30, 40)                       # the wall's +X face
        b = s.shot("layflat-done")
        wd = [float(v) for v in b["worldDims"].split(",")]
        check("clicking a face lays the model on it", b["custom"] == "True" and b["layFlat"] == "False" and abs(wd[2] - 60) < 0.01 and abs(wd[1] - 40) < 0.01,
              "now %s (was 60 x 40 x 46)" % b["worldDims"])
        s.key("Z")
        c = s.shot("layflat-undone")
        check("Z puts the model back", c["custom"] == "False" and c["worldDims"].startswith("60.000,40.000,46.000"), c["worldDims"])
        s.key("L"); s.key(0x1B)
        d = s.shot("layflat-cancelled")
        check("Esc cancels lay flat without closing", d["layFlat"] == "False")
        s.key("L")
        e = s.shot("layflat-armed-2")
        click(s, e, 50, 30, 6)                       # the base's top face: the model ends up upside down
        g = s.shot("layflat-top")
        s.key("M")
        g = s.shot("measure-after-layflat")
        w, h = (int(v) for v in g["size"].split("x"))
        s.post_many([(WM_LBUTTONDOWN, 1, s.xy(w / 2, h / 2)), (WM_LBUTTONUP, 0, s.xy(w / 2, h / 2))], gap=0.03)
        h = s.shot("layflat-then-measure")
        check("measuring still works on a laid-flat model", h["selA"] not in ("", "None"), "%s: %s" % (h["selA"], h["result"]))
    finally:
        code = s.close()
    check("lay-flat session exits cleanly", code == 0)


def update_indicator_tests(exe, S, env, out):
    """The update indicator, bottom right, in each of its stages (GLANCE_UPDATE_FAKE: no network, no install)."""
    import re
    print("== Part 5: the update indicator ==")

    def at(state):
        return tuple(int(v) for v in state["updateAt"].split(","))

    s = Session(exe, S("bracket-ascii.stl"), dict(env, GLANCE_UPDATE_FAKE="checking"), out)
    try:
        a = s.shot("update-checking")
        check("the title bar carries the version", re.search(r" Glance \d+\.\d+\.\d+$", s.title_now() or "") is not None, s.title_now() or "")
        w, h = (int(v) for v in a["size"].split("x"))
        check("a busy bar shows while Glance looks for a newer version", a["update"] == "Checking" and a["updateAt"] != "", a["updateAt"])
        x, y = at(a)
        check("it sits in the bottom right corner", x > w * 0.6 and y > h * 0.8, "%d,%d in %dx%d" % (x, y, w, h))
        s.post_many([(WM_LBUTTONDOWN, 1, s.xy(x, y)), (WM_LBUTTONUP, 0, s.xy(x, y))], gap=0.03)
        check("the bar is not a button", s.shot("update-checking-click")["update"] == "Checking")
        s.key("T")
        s.shot("update-checking-light")
    finally:
        s.close()

    s = Session(exe, S("bracket-ascii.stl"), dict(env, GLANCE_UPDATE_FAKE="downloading:0.42"), out)
    try:
        time.sleep(1.2)
        b = s.shot("update-downloading")
        check("the bar fills as the update is downloaded", b["update"] == "Downloading" and abs(float(b["updateFill"]) - 0.42) < 0.02, b["updateFill"])
        check("it stays where the busy bar was", at(b) == (x, y), b["updateAt"])
    finally:
        s.close()

    s = Session(exe, S("bracket-ascii.stl"), dict(env, GLANCE_UPDATE_FAKE="ready:9.9.9"), out)
    try:
        time.sleep(1.2)
        c = s.shot("update-ready")
        check("an Update button takes the bar's place when the update is ready", c["update"] == "Ready" and at(c) == (x, y), c["updateAt"])
        s.key("T")
        s.shot("update-ready-light")
        s.key("M")
        m = s.shot("update-ready-measure")
        check("the measure card keeps working beside it", m["measure"] == "True" and m["update"] == "Ready")
        s.post_many([(WM_LBUTTONDOWN, 1, s.xy(x, y)), (WM_LBUTTONUP, 0, s.xy(x, y))], gap=0.03)
        check("pressing Update closes Glance so it can be replaced", HiddenDesktop.wait(s.hproc, 10) == 0)
    finally:
        s.desk.close()

    s = Session(exe, S("bracket-ascii.stl"), env, out)
    try:
        time.sleep(1.0)
        d = s.shot("update-none")
        check("a copy that was not installed shows no indicator", d["update"] == "None" and d["updateAt"] == "", d["updateAt"])
    finally:
        s.close()


def inspection_tests(exe, S, env, out):
    print("== Part 3: measuring and inspection ==")
    s = Session(exe, S("plate-holes.stl"), env, out)
    try:
        s.key("P"); s.key("5")
        a = s.shot("plate-top")
        check("plate analysed for measuring", int(a["chains"]) > 0 and a["watertight"] == "True", "%s edges, watertight=%s" % (a["chains"], a["watertight"]))
        scale = float(a["scale"])
        s.post_many([(WM_LBUTTONDOWN, 1, s.xy(50 * scale, 30 * scale)), (WM_LBUTTONUP, 0, s.xy(50 * scale, 30 * scale))])
        b = s.shot("toolbar-measure")
        check("clicking the Measure button turns measuring on", b["measure"] == "True")

        click(s, b, 26, 25, 10)
        c = s.shot("hole-1")
        check("clicking a hole edge gives its diameter", c["selA"] == "Circle" and c["result"].startswith("Ø 12.00"), "%s: %s" % (c["selA"], c["result"]))
        click(s, c, 64, 25, 10)
        d = s.shot("hole-spacing")
        title, value = split(d["result"])
        check("two holes give the centre distance", title == "Center distance" and abs(value - 40) < 0.01, d["result"])

        s.key(0x1B)
        e = s.shot("cleared")
        check("Esc clears the measurement but stays in measure mode", e["selA"] == "" and e["measure"] == "True")
        click(s, e, 0, 0, 10); click(s, e, 80, 50, 10)
        g = s.shot("corner-to-corner")
        title, value = split(g["result"])
        check("corner to corner gives the diagonal", g["selA"] == "Point" and g["selB"] == "Point" and abs(value - 94.3398) < 0.01, g["result"])
        s.key(0x1B)
        click(s, g, 40, 0, 10)
        g2 = s.shot("edge-length")
        check("a straight edge gives its length", g2["selA"] == "Line" and g2["result"].startswith("80.00"), "%s: %s" % (g2["selA"], g2["result"]))
        s.key(0x1B)
        click(s, g2, 40, 8, 10)
        g3 = s.shot("face-area")
        check("a flat face gives its area", g3["selA"] == "Plane" and g3["result"].startswith("3,836."), "%s: %s" % (g3["selA"], g3["result"]))

        s.key("U")
        u = s.shot("inches")
        check("U switches to inches", u["inch"] == "True" and u["units"] == "in" and "in" in u["result"], u["result"])
        s.key("U")
        u2 = s.shot("inch-fractions")
        check("U again switches to inch fractions", u2["units"] == "frac" and u2["sizeText"].endswith('"'), u2["sizeText"])
        s.key("U")
        u3 = s.shot("back-to-mm")
        check("a third U comes back round to millimeters", u3["units"] == "mm" and u3["inch"] == "False" and "mm" in u3["result"], u3["result"])
        s.key(0x1B); s.key(0x1B)
        m = s.shot("measure-off")
        check("Esc twice leaves measure mode without closing", m["measure"] == "False")

        s.key("0"); s.key("X")
        x1 = s.shot("section-x")
        check("X starts a section cut", x1["section"] == "1")
        s.post(WM_MOUSEWHEEL, ((120 * 5) << 16) | 0x8, s.xy(0, 0))
        x2 = s.shot("section-moved")
        check("Ctrl+scroll moves the section plane", abs(f(x2, "sectionPos") - 0.6) < 0.01 and x2["dist"] == x1["dist"], "position %s" % x2["sectionPos"])
        s.key("X"); s.key("X"); s.key("X")
        x3 = s.shot("section-off")
        check("X cycles through Y, Z and off", x3["section"] == "0")
        s.key("B")
        bx = s.shot("bounding-box")
        check("B shows bounding-box dimensions", bx["box"] == "True")
    finally:
        code = s.close()
    check("inspection session exits cleanly", code == 0)

    s = Session(exe, S("bracket-ascii.stl"), env, out)
    try:
        s.key("P"); s.key("5"); s.key("M")
        a = s.shot("bracket-top")
        click(s, a, 50, 5, 6); click(s, a, 20, 20, 30)
        b = s.shot("face-to-face")
        title, value = split(b["result"])
        check("two parallel faces give the distance between them", b["selA"] == "Plane" and b["selB"] == "Plane" and title == "Distance" and abs(value - 24) < 0.01, b["result"])
        s.key(0x1B); s.key("0")
        c = s.shot("bracket-iso")
        click(s, c, 50, 30, 6); click(s, c, 6, 30, 40)
        d = s.shot("face-angle")
        title, value = split(d["result"])
        check("two faces at right angles give 90 degrees", title == "Angle" and abs(value - 90) < 0.05, d["result"])
    finally:
        s.close()

    s = Session(exe, S("rounded-cube.step"), env, out)
    try:
        s.key("P"); s.key("0"); s.key("M")
        a = s.shot("step-iso")
        w, h = (int(v) for v in a["size"].split("x"))
        s.post_many([(WM_LBUTTONDOWN, 1, s.xy(w / 2, h / 2)), (WM_LBUTTONUP, 0, s.xy(w / 2, h / 2))], gap=0.03)
        b = s.shot("step-face")
        check("STEP faces can be picked", b["selA"] not in ("", "None"), "%s: %s" % (b["selA"], b["result"]))
    finally:
        s.close()


def fraction_and_option_tests(exe, S, env, out):
    print("== Part 6: inch fractions, mode options, narrow windows, settings from older versions ==")
    from PIL import ImageChops
    k = 25.4 / 16                    # plate-fraction.stl is the 80 x 50 x 10 plate drawn in sixteenths of an inch

    def press(s, spot):
        s.post_many([(WM_LBUTTONDOWN, 1, s.xy(*spot)), (WM_LBUTTONUP, 0, s.xy(*spot))], gap=0.03)

    def rects(state, key="optionRects"):
        return [tuple(float(v) for v in r.split(",")) for r in state[key].split(";") if r]

    def spot(state, action):
        return {b.split(":")[0]: tuple(float(v) for v in b.split(":")[1].split(",")) for b in state["buttons"].split(";") if b}.get(str(action))

    def opt(state):
        return dict(p.split(":") for p in state["options"].split(";") if p)

    def to_shading(s, name, shot):
        st = s.shot(shot)
        for _ in range(8):
            if st["shading"] == name:
                break
            s.key("V"); st = s.shot(shot)
        return st

    def to_units(s, name, shot):
        st = s.shot(shot)
        for _ in range(3):
            if st["units"] == name:
                break
            s.key("U"); st = s.shot(shot)
        return st

    # ── inch fractions ──
    s = Session(exe, S("plate-fraction.stl"), env, out)
    try:
        a = s.shot("fraction-start")
        if a["ortho"] != "True":
            s.key("P")
        s.key("5")
        check("lengths start in millimeters", a["units"] == "mm" and a["sizeText"] == "127.00 × 79.375 × 15.875 mm", a["sizeText"])
        st = to_units(s, "frac", "fraction-units")
        check("a part made to inch fractions reads as fractions", st["units"] == "frac" and st["sizeText"] == '5" × 3 1/8" × 5/8"', st["sizeText"])
        check("the units button says fractions are on", spot(st, 3) is not None)
        s.key("M")
        b = s.shot("fraction-measure")
        click(s, b, 26 * k, 25 * k, 10 * k)
        c = s.shot("fraction-hole")
        check("a 3/4 inch hole reads as a fraction", c["selA"] == "Circle" and c["result"] == 'Ø 3/4"', c["result"])
        click(s, c, 64 * k, 25 * k, 10 * k)
        d = s.shot("fraction-spacing")
        check("hole spacing of 2 1/2 inches reads as a fraction", d["result"].startswith("Center distance") and d["resultText"] == '2 1/2"', d["resultText"])
        s.key(0x1B)
        click(s, d, 0, 0, 10 * k); click(s, d, 80 * k, 50 * k, 10 * k)
        e = s.shot("fraction-diagonal")
        check("a length that is not a fraction stays decimal", e["selB"] == "Point" and e["resultText"] == '5.8962"', e["resultText"])
        s.key(0x1B)
        click(s, e, 40 * k, 0, 10 * k)
        g = s.shot("fraction-edge")
        check("a whole number of inches has no fraction part", g["selA"] == "Line" and g["result"] == '5"', g["result"])
        s.key(0x1B); s.key(0x1B); s.key("0"); s.key("B"); s.key("I")
        s.shot("fraction-box-details")
        s.key("T")
        s.shot("fraction-box-details-other-theme")
        s.key("T"); s.key("B"); s.key("I")
        st = to_units(s, "in", "fraction-decimal-inch")
        check("decimal inches are still there, one step before fractions", st["sizeText"] == "5.000 × 3.125 × 0.625 in", st["sizeText"])
        to_units(s, "frac", "fraction-again")
    finally:
        s.close()

    s = Session(exe, S("plate-holes.stl"), env, out)
    try:
        a = s.shot("fraction-reopened")
        parts = a["sizeText"].split(" × ")
        check("the fraction choice is remembered", a["units"] == "frac", a["units"])
        check("a metric part stays decimal where it is not on a fraction", len(parts) == 3 and parts[0] == '3.1496"' and parts[2] == '0.3937"', a["sizeText"])
        st = to_units(s, "mm", "fraction-off")
        check("and reads in millimeters again afterwards", st["sizeText"] == "80.00 × 50.00 × 10.00 mm", st["sizeText"])

        # ── mode options: the second row ──
        st = to_shading(s, "Shaded", "options-shaded")
        scale = float(st["scale"])
        w, h = (int(v) for v in st["size"].split("x"))
        check("Shaded offers Color and Edges in a second row", list(opt(st)) == ["Color", "Edges"] and opt(st)["Edges"] == "On" and len(rects(st)) == 2, st["options"])
        s.key("X")
        x1 = s.shot("section-under-toolbar")
        sx, sy, sw, sh = (float(v) for v in x1["sliderBox"].split(","))
        check("the section slider does not cover the toolbar", sy >= 48 * scale or sx >= float(x1["toolbarRight"]), "slider %s, toolbar ends at %s" % (x1["sliderBox"], x1["toolbarRight"]))
        check("the toast sits under the section slider", x1["toast"] != "" and float(x1["toastY"]) >= sy + sh, "toast at %s" % x1["toastY"])
        s.key("X"); s.key("X"); s.key("X")

        s.key("0")
        st = to_shading(s, "Studio", "options-studio")
        dark = st["dark"] == "True"
        want = "Light:Soft;Backdrop:%s;Material:Clay;Shadow:On" % ("Dark" if dark else "White")
        rr = rects(st)
        check("Studio shows its options in a second row", st["options"] == want and len(rr) == 4, st["options"])
        check("the row sits under the toolbar, inside the window", all(r[1] >= 48 * scale and r[0] >= 0 and r[0] + r[2] <= w for r in rr), st["optionRects"])

        seen = [opt(st)["Light"]]
        for i in range(4):
            press(s, spot(st, 100)); st = s.shot("studio-light-%d" % (i + 1)); seen.append(opt(st)["Light"])
            if i == 0:
                rr = rects(st)
                check("the toast names the option and sits under the row", st["toast"] == "Light  ·  Left" and float(st["toastY"]) >= max(r[1] + r[3] for r in rr),
                      "%s at %s" % (st["toast"], st["toastY"]))
        check("the Light button steps through four lights and comes back round", seen == ["Soft", "Left", "Right", "Top", "Soft"], " > ".join(seen))

        seen, edge, chips = [opt(st)["Backdrop"]], {}, {}
        for i in range(3):
            press(s, spot(st, 101)); st = s.shot("studio-backdrop-%d" % (i + 1)); seen.append(opt(st)["Backdrop"])
            edge[seen[-1]] = Image.open(st["png"]).getpixel((w - 12, h // 2))
            chips[seen[-1]] = st["solidPills"] == "True"
        fits = "Dark" if dark else "White"                           # the backdrop this theme's see-through buttons read well on
        check("buttons get a solid chip on a backdrop that does not suit the theme", all(chips[n] == (n != fits) for n in chips),
              ", ".join("%s %s" % (n, "chip" if chips[n] else "plain") for n in chips))
        order = ["White", "Grey", "Dark"]
        start = order.index(seen[0])
        check("the Backdrop button steps through three backdrops", seen == [order[(start + i) % 3] for i in range(4)], " > ".join(seen))
        lum = {n: sum(p[:3]) / 3 for n, p in edge.items()}
        check("each backdrop looks the part", 200 <= lum["White"] < 250 and 80 <= lum["Grey"] <= 190 and lum["Dark"] <= 60,
              ", ".join("%s %d" % (n, lum[n]) for n in order))

        seen, face = [opt(st)["Material"]], {}
        for i in range(4):
            press(s, spot(st, 102)); st = s.shot("studio-material-%d" % (i + 1)); seen.append(opt(st)["Material"])
            face[seen[-1]] = sum(Image.open(st["png"]).getpixel((w // 2, h // 2))[:3]) / 3
        check("the Material button steps through four materials", seen == ["Clay", "White", "Charcoal", "Part color", "Clay"], " > ".join(seen))
        check("charcoal is dark and white is lighter than clay", face["Charcoal"] < 90 and face["White"] > face["Clay"] > 150,
              ", ".join("%s %d" % (n, face[n]) for n in face))

        lower = (int(w * 0.05), int(h * 0.35), int(w * 0.95), int(h * 0.9))
        on = Image.open(st["png"]).crop(lower)
        press(s, spot(st, 103)); st = s.shot("studio-shadow-off")
        off = Image.open(st["png"]).crop(lower)
        check("Shadow off takes the shadow off the ground", opt(st)["Shadow"] == "Off" and ImageChops.difference(on, off).getbbox() is not None)
        press(s, spot(st, 103)); st = s.shot("studio-shadow-on")
        back = Image.open(st["png"]).crop(lower)
        check("Shadow on puts it back exactly", opt(st)["Shadow"] == "On" and ImageChops.difference(on, back).getbbox() is None)

        press(s, spot(st, 100)); st = s.shot("studio-left")                    # leave Light on Left for the next session
        kept = st["options"]
        s.key("X")
        x2 = s.shot("studio-section")
        sx, sy, sw, sh = (float(v) for v in x2["sliderBox"].split(","))
        rr = rects(x2)
        check("the section slider moves under the options row", sy >= max(r[1] + r[3] for r in rr), "slider %s, row %s" % (x2["sliderBox"], x2["optionRects"]))
        check("and the toast under the slider", float(x2["toastY"]) >= sy + sh, x2["toastY"])
        s.key("X"); s.key("X"); s.key("X")
        s.key("I")
        s.shot("studio-details")
        s.key("I"); s.key("T")
        s.shot("studio-other-theme")
        s.key("T")

        # ── the other modes ──
        def cycle(action, label, shot, n):
            """Presses one option button n times; returns the names it showed and the state after each press."""
            nonlocal st
            names, states = [opt(st)[label]], {}
            for i in range(n):
                press(s, spot(st, action)); st = s.shot("%s-%d" % (shot, i + 1)); names.append(opt(st)[label])
                states[names[-1]] = st
            return names, states

        def centre(state):
            return Image.open(state["png"]).getpixel((w // 2, h // 2))[:3]

        def differ(a, b):
            """Whether two shots show the model differently (the toolbar, toast and read-outs are left out)."""
            return ImageChops.difference(Image.open(a["png"]).crop(lower), Image.open(b["png"]).crop(lower)).getbbox() is not None

        def ring(names, order):
            return names == [order[(order.index(names[0]) + i) % len(order)] for i in range(len(order) + 1)]

        st = to_shading(s, "Shaded", "solid-shaded")
        names, states = cycle(100, "Color", "solid-color", 5)
        check("the Color button steps through the colors, as C does", len(set(names[:5])) == 5 and names[5] == names[0]
              and all(states[n]["material"] == n for n in states), " > ".join(names))
        press(s, spot(st, 101)); st = s.shot("solid-edges-off")
        check("Edges off turns Shaded into Smooth", st["shading"] == "Smooth" and opt(st)["Edges"] == "Off" and st["edges"] == "False", "%s, %s" % (st["shading"], st["options"]))
        press(s, spot(st, 101)); st = s.shot("solid-edges-on")
        check("and Edges on turns it back", st["shading"] == "Shaded" and opt(st)["Edges"] == "On", "%s, %s" % (st["shading"], st["options"]))

        st = to_shading(s, "Metal", "metal")
        check("Metal shows finish, surface, backdrop and shadow", st["options"] == "Finish:Aluminum;Surface:Satin;Backdrop:%s;Shadow:On" % ("Dark" if dark else "White"), st["options"])
        names, states = cycle(100, "Finish", "metal-finish", 5)
        px = {n: centre(states[n]) for n in states}
        check("the Finish button steps through five metals", names == ["Aluminum", "Steel", "Brass", "Copper", "Black", "Aluminum"], " > ".join(names))
        check("brass and copper are warm, steel is darker than aluminum, black darkest", px["Brass"][0] > px["Brass"][2] + 40 and px["Copper"][0] > px["Copper"][2] + 40
              and sum(px["Black"]) < sum(px["Steel"]) < sum(px["Aluminum"]), ", ".join("%s %s" % (n, px[n]) for n in px))
        names, states = cycle(101, "Surface", "metal-surface", 3)
        check("the Surface button steps through polished, satin and matte", ring(names, ["Polished", "Satin", "Matte"]) and names[0] == "Satin", " > ".join(names))
        check("each surface looks different", differ(states["Polished"], states["Satin"]) and differ(states["Satin"], states["Matte"]) and differ(states["Polished"], states["Matte"]))
        names, states = cycle(102, "Backdrop", "metal-backdrop", 3)
        check("Metal has the same three backdrops", ring(names, ["White", "Grey", "Dark"]), " > ".join(names))
        on = st
        press(s, spot(st, 103)); st = s.shot("metal-shadow-off")
        check("Metal's shadow can be switched off", opt(st)["Shadow"] == "Off" and differ(on, st))
        press(s, spot(st, 103)); st = s.shot("metal-shadow-on")

        st = to_shading(s, "Lines", "lines")
        check("Lines shows hidden lines, weight and paper", st["options"] == "Hidden lines:Off;Weight:Fine;Paper:%s" % ("Dark" if dark else "White"), st["options"])
        plain = st
        press(s, spot(st, 100)); st = s.shot("lines-hidden-faint")
        check("Hidden lines: Faint draws the edges behind the part", opt(st)["Hidden lines"] == "Faint" and differ(plain, st))
        faint = st
        press(s, spot(st, 100)); st = s.shot("lines-hidden-off")
        check("and Off takes them away again", opt(st)["Hidden lines"] == "Off" and not differ(plain, st) and differ(faint, st))
        press(s, spot(st, 101)); st = s.shot("lines-bold")
        check("Weight: Bold draws heavier lines", opt(st)["Weight"] == "Bold" and differ(plain, st))
        press(s, spot(st, 101)); st = s.shot("lines-fine")
        names, states = cycle(102, "Paper", "lines-paper", 3)
        blue = Image.open(states["Blueprint"]["png"]).getpixel((w - 12, h // 2))
        check("the Paper button steps through white, blueprint and dark", ring(names, ["White", "Blueprint", "Dark"]), " > ".join(names))
        check("blueprint paper is blue, with solid buttons in the light theme only", blue[2] > blue[0] + 60 and (states["Blueprint"]["solidPills"] == "True") == (not dark)
              and (states["White"]["solidPills"] == "True") == dark, "%s, chips %s" % (blue, states["Blueprint"]["solidPills"]))

        st = to_shading(s, "X-ray", "xray")
        check("X-ray shows opacity and edges", st["options"] == "Opacity:Medium;Edges:On", st["options"])
        names, states = cycle(100, "Opacity", "xray-opacity", 3)
        check("the Opacity button steps through light, medium and heavy", ring(names, ["Light", "Medium", "Heavy"]) and names[0] == "Medium", " > ".join(names))
        check("each opacity looks different", differ(states["Light"], states["Medium"]) and differ(states["Medium"], states["Heavy"]))
        press(s, spot(st, 101)); st = s.shot("xray-edges-off")
        check("X-ray's edges can be switched off without leaving X-ray", st["shading"] == "X-ray" and opt(st)["Edges"] == "Off" and st["edges"] == "False", "%s, %s" % (st["shading"], st["options"]))
        st = to_shading(s, "Shaded", "xray-left")
        st = to_shading(s, "X-ray", "xray-again")
        check("and X-ray remembers that", opt(st)["Edges"] == "Off", st["options"])
        press(s, spot(st, 101)); st = s.shot("xray-edges-on")

        st = to_shading(s, "Wire", "wire")
        check("Wire shows which lines it draws", st["options"] == "Lines:All triangles", st["options"])
        solid_px = centre(st)
        press(s, spot(st, 100)); st = s.shot("wire-feature-edges")
        open_px = centre(st)
        check("Feature edges only leaves the part see-through", st["shading"] == "Wire" and opt(st)["Lines"] == "Feature edges only"
              and sum(abs(a_ - b_) for a_, b_ in zip(solid_px, open_px)) > 40, "centre %s -> %s" % (solid_px, open_px))
        press(s, spot(st, 100)); st = s.shot("wire-all-triangles")

        st = to_shading(s, "Overhangs", "overhangs")
        names, states = cycle(100, "Angle", "overhangs-angle", 3)
        check("the Angle button steps through 30, 45 and 60 degrees", ring(names, ["30°", "45°", "60°"]) and names[0] == "45°", " > ".join(names))
        to_shading(s, "Shaded", "options-leave")
    finally:
        s.close()

    s = Session(exe, S("knot.stl"), env, out)
    try:
        st = to_shading(s, "Wire", "wire-knot")
        press(s, spot(st, 100)); st = s.shot("wire-knot-feature-edges")
        check("a model with no sharp edges keeps its triangles, and says why", opt(st)["Lines"] == "Feature edges only" and "no sharp edges" in st["toast"],
              "%s (%s feature edge lines)" % (st["toast"], st["edgeLines"]))
        press(s, spot(st, 100)); st = s.shot("wire-knot-all")
        st = to_shading(s, "Overhangs", "overhangs-knot")
        w, h = (int(v) for v in st["size"].split("x"))
        marked = {}
        for i in range(3):
            im = Image.open(st["png"]).convert("RGB").crop((0, int(h * 0.2), w, int(h * 0.9)))
            marked[opt(st)["Angle"]] = sum(1 for r, g, b in im.getdata() if r > 120 and r - b > 90)
            press(s, spot(st, 100)); st = s.shot("overhangs-knot-%d" % (i + 1))
        check("a smaller angle marks more of the part, a larger one less", marked.get("30°", 0) > marked.get("45°", 0) > marked.get("60°", 0) > 0,
              ", ".join("%s %d px" % (n, marked[n]) for n in marked))
        to_shading(s, "Shaded", "overhangs-done")
    finally:
        s.close()

    s = Session(exe, S("plate-holes.stl"), env, out)
    try:
        st = to_shading(s, "Studio", "options-reopened")
        check("each mode's choices are remembered", opt(st)["Light"] == "Left" and st["options"] == kept, "%s (left as %s)" % (st["options"], kept))
        press(s, spot(st, 100)); press(s, spot(st, 100)); press(s, spot(st, 100))
        st = s.shot("options-light-soft")
        check("Light is back on Soft for the rest of the run", opt(st)["Light"] == "Soft", st["options"])
        to_shading(s, "Shaded", "options-done")
    finally:
        code = s.close()
    check("options session exits cleanly", code == 0)

    # ── a narrow window, opened with settings written by an older version ──
    old = os.path.join(out, "data-old")
    shutil.rmtree(old, ignore_errors=True)
    os.makedirs(old)
    with open(os.path.join(old, "settings.ini"), "w") as fh_:
        fh_.write("width=420\nheight=460\ntheme=light\ninch=1\npictureScale=4\npictureAlpha=0\nseenHint=1\n")
    s = Session(exe, S("plate-holes.stl"), dict(env, GLANCE_DATA_DIR=old), out)
    try:
        a = s.shot("narrow-start")
        w, h = (int(v) for v in a["size"].split("x"))
        scale = float(a["scale"])
        check("the window is as narrow as Glance allows", w <= 420 * scale, a["size"])

        def boxes(state):
            return {b.split(":")[0]: tuple(float(v) for v in b.split(":")[1].split(",")) for b in state["buttonRects"].split(";") if b}

        def tidy(state):
            """Every button (toolbar, Picture, options) is wholly inside the window and no two overlap."""
            bb = list(boxes(state).values())
            inside = all(b[0] >= 0 and b[1] >= 0 and b[0] + b[2] <= w and b[1] + b[3] <= h for b in bb)
            clash = any(p[0] < q[0] + q[2] and q[0] < p[0] + p[2] and p[1] < q[1] + q[3] and q[1] < p[1] + p[3] for i, p in enumerate(bb) for q in bb[i + 1:])
            return inside and not clash

        need = ["0", "1", "2", "6", "7", "8", "3", "5", "9"]         # Measure ... units, Fix units, Picture
        check("in a narrow window the toolbar wraps and every button is still there", int(a["toolbarLines"]) >= 2 and all(n in boxes(a) for n in need) and tidy(a),
              "%s lines: %s" % (a["toolbarLines"], a["buttonRects"]))
        press(s, spot(a, 8))
        nb = s.shot("narrow-shading-button")
        check("the Shading button on a wrapped line still works", nb["shading"] == "Smooth" and tidy(nb), nb["shading"])
        press(s, spot(nb, 9))
        pc = s.shot("narrow-picture-card")
        cx, cy, cw, ch = (float(v) for v in pc["pictureCard"].split(","))
        pb = boxes(pc)["9"]
        check("the whole Picture card fits in the window, under the Picture button", pc["picture"] == "True" and cx >= 0 and cx + cw <= w
              and cy >= pb[1] + pb[3] and cy + ch <= h, "card %s in %s" % (pc["pictureCard"], pc["size"]))
        s.key(0x1B)
        check("inch=1 from an older version still means inches", a["units"] == "in", a["units"])
        check("4x pictures from an older version become 4K", a["pictureChoice"] == "4k", a["pictureChoice"])
        st = to_shading(s, "Studio", "narrow-studio")
        rr = rects(st)
        overlap = any(a_[0] < b_[0] + b_[2] and b_[0] < a_[0] + a_[2] and a_[1] < b_[1] + b_[3] and b_[1] < a_[1] + a_[3]
                      for i, a_ in enumerate(rr) for b_ in rr[i + 1:])
        check("in a narrow window the options wrap onto more lines", int(st["optionLines"]) >= 2 and len(rr) == 4, "%s lines" % st["optionLines"])
        check("every option stays inside the window and none overlap", not overlap and all(r[0] >= 0 and r[0] + r[2] <= w for r in rr) and tidy(st), st["optionRects"])
        check("the toast sits under the wrapped rows", st["toast"] != "" and float(st["toastY"]) >= max(r[1] + r[3] for r in rr), st["toastY"])
        s.key("X")
        x3 = s.shot("narrow-section")
        sx, sy, sw, sh = (float(v) for v in x3["sliderBox"].split(","))
        check("the section slider fits under them", sy >= max(r[1] + r[3] for r in rects(x3)) and sx >= 0 and sx + sw <= w, x3["sliderBox"])
        check("and the toast under the slider", float(x3["toastY"]) >= sy + sh, x3["toastY"])
        s.key("X"); s.key("X"); s.key("X"); s.key("U")
        s.shot("narrow-fractions")
        s.key("I")
        s.shot("narrow-details")
    finally:
        s.close()
    ini = open(os.path.join(old, "settings.ini")).read()
    check("settings are written back in the new form", "units=frac" in ini and "pictureSize=4k" in ini and "inch=" not in ini and "pictureScale" not in ini, ini.replace("\n", " "))


def mesh_format_tests(exe, S, env, out):
    """3MF, PLY and glTF: the samples tools/make_samples.py writes have known sizes, triangle counts and units."""
    import json
    import struct
    import zipfile
    print("== Part 7: 3MF, PLY and glTF ==")

    def themed(theme):
        data = os.path.join(out, "data-formats-" + theme)
        shutil.rmtree(data, ignore_errors=True)
        os.makedirs(data)
        with open(os.path.join(data, "settings.ini"), "w") as fh_:
            fh_.write("theme=%s\nseenHint=1\n" % theme)
        return dict(env, GLANCE_DATA_DIR=data)

    dark, light = themed("dark"), themed("light")

    #  file, Format row, triangles, size in mm in the file's axes, file unit, how the unit is known, colours, own normals, Y up
    cases = [
        ("assembly.3mf", "3MF", 48, (40, 50, 35), "mm", "0", False, False, False),
        ("block-inch.3mf", "3MF", 12, (50.8, 25.4, 12.7), "in", "0", False, False, False),
        ("box-ascii.ply", "PLY", 12, (30, 20, 10), "mm", "1", True, False, False),
        ("box-binary.ply", "PLY", 12, (60, 40, 20), "mm", "1", True, True, False),
        ("box-big-endian.ply", "PLY", 12, (76.2, 50.8, 25.4), "in", "1", False, False, False),
        ("plate-strips.ply", "PLY", 20, (20, 10, 0), "mm", "1", False, False, False),
        ("blocks.glb", "GLB", 24, (120, 160, 150), "m", "0", True, True, True),
        ("two-blocks.gltf", "glTF", 24, (110, 20, 30), "m", "0", True, False, True),
    ]
    what = {"assembly.3mf": "a 3MF of several parts placed by components and build items",
            "block-inch.3mf": "a 3MF in inches, uncompressed, with prefixed elements",
            "box-ascii.ply": "a text PLY with quads and vertex colours",
            "box-binary.ply": "a binary PLY with normals, colours and data to skip",
            "box-big-endian.ply": "a big-endian PLY with doubles, small enough to be inches",
            "plate-strips.ply": "a PLY made of triangle strips",
            "blocks.glb": "a GLB with nodes moved by TRS and by a matrix",
            "two-blocks.gltf": "a glTF with a .bin beside it and a buffer inside it"}
    for name, fmt, tris, size, unit, source, colors, normals, yup in cases:
        stem = name.replace(".", "-")
        code, dt, rep = snapshot(exe, os.path.join(out, "format-%s-dark.png" % stem), dark, S(name))
        dims = [float(v) for v in (field(rep, "dims") or "0,0,0").split(",")]
        ok = code == 0 and rep.startswith("ok")
        check("open " + what[name], ok and int(field(rep, "tris") or 0) == tris and all(abs(d - e) < 0.01 for d, e in zip(dims, size)),
              "%s triangles, %s mm (expected %d, %s), parse %s ms" % (field(rep, "tris"), field(rep, "dims"), tris, " x ".join("%g" % v for v in size), field(rep, "parse")) if ok else rep[:120])
        check("  its units: %s, %s" % (unit, "stated by the file" if source == "0" else "assumed"), ok and field(rep, "fileUnit") == unit and field(rep, "unitSource") == source,
              "%s, source %s" % (field(rep, "fileUnit"), field(rep, "unitSource")))
        check("  its format, colours, normals and up axis", ok and field(rep, "format") == fmt and field(rep, "colors") == str(colors)
              and field(rep, "smooth") == str(normals) and field(rep, "yUp") == str(yup),
              "format %s, colours %s, own normals %s, Y up %s" % (field(rep, "format"), field(rep, "colors"), field(rep, "smooth"), field(rep, "yUp")))
        code, dt, rep = snapshot(exe, os.path.join(out, "format-%s-light-details.png" % stem), light, S(name), press="I")
        check("  and it draws in the light theme with the details card", code == 0 and rep.startswith("ok"), rep[:60])

    # ── files that cannot be shown: a plain message, never a crash ──
    bad = os.path.join(out, "bad-formats")
    shutil.rmtree(bad, ignore_errors=True)
    os.makedirs(bad)

    def write(name, data):
        with open(os.path.join(bad, name), "wb") as fh_:
            fh_.write(data if isinstance(data, bytes) else data.encode())
        return os.path.join(bad, name)

    whole = open(S("assembly.3mf"), "rb").read()
    model = whole.find(b"3D/main.model") + len(b"3D/main.model")        # where the model's packed data starts
    with zipfile.ZipFile(os.path.join(bad, "no-model.3mf"), "w") as z:
        z.writestr("readme.txt", "nothing to see")
    with zipfile.ZipFile(os.path.join(bad, "bad-xml.3mf"), "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("3D/3dmodel.model", "<html><body>not a model</body></html>")
    box = open(S("box-binary.ply"), "rb").read()
    gltf = json.load(open(S("two-blocks.gltf")))
    lone = dict(gltf, buffers=[dict(gltf["buffers"][0], uri="gone.bin"), gltf["buffers"][1]])
    away = dict(gltf, buffers=[dict(gltf["buffers"][0], uri="//server/share/two-blocks.bin"), gltf["buffers"][1]])
    outside = dict(gltf, accessors=[dict(gltf["accessors"][0], count=5000)] + gltf["accessors"][1:])
    glb = open(S("blocks.glb"), "rb").read()
    broken = [
        ("a 3MF that is not a zip", write("broken.3mf", "this is not a zip package\n"), "3MF"),
        ("a 3MF cut short", write("truncated.3mf", whole[:len(whole) * 3 // 5]), "3MF"),
        ("a 3MF whose packed data is damaged", write("damaged.3mf", whole[:model] + b"\xff" * 40 + whole[model + 40:]), "3MF"),
        ("a 3MF package with no model in it", os.path.join(bad, "no-model.3mf"), "no 3D model"),
        ("a 3MF whose model is some other XML", os.path.join(bad, "bad-xml.3mf"), "3MF"),
        ("a PLY point cloud", write("cloud.ply", "ply\nformat ascii 1.0\nelement vertex 3\nproperty float x\nproperty float y\nproperty float z\nend_header\n0 0 0\n1 0 0\n0 1 0\n"), "point cloud"),
        ("a binary PLY cut short", write("truncated.ply", box[:-40]), "stops before"),
        ("a PLY that promises far more than it holds", write("greedy.ply", box.replace(b"element vertex 24", b"element vertex 2000000000")), "stops before"),
        ("a PLY with no header end", write("broken.ply", "ply\nformat ascii 1.0\nelement vertex 3\nproperty float x\n"), "PLY"),
        ("a PLY with letters where numbers belong", write("letters.ply", "ply\nformat ascii 1.0\nelement vertex 3\nproperty float x\nproperty float y\nproperty float z\nelement face 1\nproperty list uchar int vertex_indices\nend_header\n0 0 0\none two three\n"), "PLY"),
        ("a GLB with nonsense inside", write("broken.glb", b"glTF" + struct.pack("<III", 2, 40, 20) + b"JSON{not json at all!!"), "glTF"),
        ("a GLB cut short", write("truncated.glb", glb[:len(glb) // 2]), "glTF"),
        ("a glTF 1.0 binary", write("old.glb", b"glTF" + struct.pack("<II", 1, 20) + bytes(8)), "glTF 1.0"),
        ("a glTF whose text stops half way", write("broken.gltf", '{"asset": {"version": "2.0"}, "meshes": ['), "glTF"),
        ("a glTF that needs Draco", write("draco.gltf", json.dumps(dict(gltf, extensionsRequired=["KHR_draco_mesh_compression"]))), "Draco"),
        ("a glTF that needs meshopt", write("meshopt.gltf", json.dumps(dict(gltf, extensionsRequired=["EXT_meshopt_compression"]))), "meshopt"),
        ("a glTF whose .bin is missing", write("missing-bin.gltf", json.dumps(lone)), "gone.bin is missing"),
        ("a glTF pointing at a server for its data", write("elsewhere.gltf", json.dumps(away)), "doesn't fetch"),
        ("a glTF pointing outside its own data", write("outside.gltf", json.dumps(outside)), "glTF"),
        ("a glTF with no meshes", write("empty.gltf", '{"asset": {"version": "2.0"}, "scenes": [{"nodes": []}]}'), "no surfaces"),
    ]
    shutil.copy(S("two-blocks.bin"), os.path.join(bad, "two-blocks.bin"))         # so only the named fault is at work
    for label, path, phrase in broken:
        code, dt, rep = snapshot(exe, os.path.join(out, "format-error-%s.png" % os.path.basename(path).replace(".", "-")), dark, path)
        check("%s shows an error" % label, code == 3 and rep.startswith("error") and phrase in rep, rep[:110])

    # ── in a window: units, details, stepping through the folder ──
    s = Session(exe, S("blocks.glb"), dark, out)
    try:
        a = s.shot("glb")
        wd = [float(v) for v in a["worldDims"].split(",")]
        check("a glTF model stands the right way up, in millimeters", a["yUp"] == "True" and a["format"] == "GLB"
              and all(abs(d - e) < 0.01 for d, e in zip(wd, (120, 150, 160))), "%s mm as it stands, colours %s" % (a["worldDims"], a["fileColors"]))
        check("its colours come from the file", a["fileColors"] == "True")
        check("there is no Fix units button for a file that states its units", "5" not in dict(b.split(":") for b in a["buttons"].split(";") if b), a["buttons"])
        s.post(0x8000 + 3)
        b = s.shot("glb-units")
        check("asking to change a glTF file's units says why not", "state their own units" in b["toast"] and b["fileUnit"] == "m", b["toast"])
        s.key("I")
        s.shot("glb-details")
    finally:
        s.close()

    # A glTF saved in millimeters by a CAD exporter: read as meters it is 110 m long, so Fix units is offered.
    odd = os.path.join(out, "gltf-units")
    shutil.rmtree(odd, ignore_errors=True)
    os.makedirs(odd)
    shutil.copy(S("two-blocks.bin"), os.path.join(odd, "two-blocks.bin"))
    huge = dict(gltf, nodes=gltf["nodes"] + [{"scale": [1000, 1000, 1000], "children": [0, 1]}], scenes=[{"nodes": [len(gltf["nodes"])]}])
    with open(os.path.join(odd, "saved-in-mm.gltf"), "w") as fh_:
        json.dump(huge, fh_)
    has_fix = lambda st: "5" in dict(b.split(":") for b in st["buttons"].split(";") if b)
    near = lambda st, want, tol: all(abs(float(d) - e) < tol for d, e in zip(st["dims"].split(","), want))
    s = Session(exe, os.path.join(odd, "saved-in-mm.gltf"), dark, out)
    try:
        a = s.shot("gltf-too-large")
        check("a glTF over 10 m across is read as meters but offers Fix units", has_fix(a) and a["unitDoubt"] == "True" and a["fileUnit"] == "m"
              and a["unitSource"] == "0" and near(a, (110000, 20000, 30000), 0.5), "%s, %s mm, buttons %s" % (a["fileUnit"], a["dims"], a["buttons"]))
        check("and a note says why", "too large" in a["toast"], a["toast"])
        s.post(0x8000 + 4)
        b = s.shot("gltf-fix-units")
        rows = [tuple(float(v) for v in r.split(",")) for r in b["unitRows"].split(";") if r]
        check("its chooser opens with five units", b["fixUnits"] == "True" and len(rows) == 5, b["unitRows"])
        s.post_many([(WM_LBUTTONDOWN, 1, s.xy(*rows[0])), (WM_LBUTTONUP, 0, s.xy(*rows[0]))], gap=0.03)
        c = s.shot("gltf-picked-mm")
        check("picking millimeters brings it to its true size", c["fileUnit"] == "mm" and c["unitSource"] == "2" and c["unitDoubt"] == "False"
              and near(c, (110, 20, 30), 0.01), "now %s, %s mm" % (c["fileUnit"], c["dims"]))
        check("and Fix units stays, to change it back", has_fix(c), c["buttons"])
    finally:
        s.close()
    s = Session(exe, os.path.join(odd, "saved-in-mm.gltf"), dark, out)
    try:
        a = s.shot("gltf-remembered")
        check("the unit chosen for a glTF is remembered", a["fileUnit"] == "mm" and a["unitSource"] == "2" and near(a, (110, 20, 30), 0.01),
              "%s, %s mm" % (a["fileUnit"], a["dims"]))
    finally:
        s.close()

    s = Session(exe, S("assembly.3mf"), dark, out)
    try:
        a = s.shot("3mf")
        check("a 3MF has no Fix units button either", a["format"] == "3MF" and "5" not in dict(b.split(":") for b in a["buttons"].split(";") if b), a["buttons"])
        check("its parts are found for measuring", int(a["chains"]) > 0 and a["watertight"] == "True", "%s edges, watertight=%s" % (a["chains"], a["watertight"]))
        s.key(0x27)
        nxt = s.shot("3mf-next")
        check("Right arrow steps on to the next model, another new format", nxt["file"] == "block-inch.3mf" and int(nxt["tris"]) == 12 and not nxt["error"], nxt["file"])
        s.key(0x25)
        check("and Left arrow comes back", s.shot("3mf-back")["file"] == "assembly.3mf")
    finally:
        s.close()

    s = Session(exe, S("box-ascii.ply"), dark, out)
    try:
        a = s.shot("ply")
        check("a PLY has the Fix units button, like an STL", "5" in dict(b.split(":") for b in a["buttons"].split(";") if b) and a["fileUnit"] == "mm" and a["unitSource"] == "1", a["buttons"])
        s.post(0x8000 + 3)
        b = s.shot("ply-inches")
        dims = [float(v) for v in b["dims"].split(",")]
        check("and its unit can be changed", b["fileUnit"] == "in" and b["unitSource"] == "2" and abs(dims[0] - 762) < 0.01, "now %s, %s mm" % (b["fileUnit"], b["dims"]))
        for _ in range(4):
            s.post(0x8000 + 3); s.shot("ply-next-unit")
        c = s.shot("ply-mm")
        check("cycling comes back round to millimeters", c["fileUnit"] == "mm" and c["unitSource"] == "2" and c["dims"].startswith("30.000,20.000,10.000"), c["dims"])
    finally:
        code = s.close()
    check("format sessions exit cleanly", code == 0)


def main():
    exe, samples, out = (os.path.abspath(a) for a in sys.argv[1:4])
    os.makedirs(out, exist_ok=True)
    data = os.path.join(out, "data")
    shutil.rmtree(data, ignore_errors=True)
    env = dict(os.environ, GLANCE_QUIET="1", GLANCE_DATA_DIR=data, GLANCE_NO_LOOKAHEAD="1")
    S = lambda name: os.path.join(samples, name)

    print("== Part 1: every format opens, with time to first picture ==")
    cases = [("empty", None), ("stl-binary", "knot.stl"), ("stl-ascii", "bracket-ascii.stl"), ("obj", "vase.obj"),
             ("stl-1M-triangles", "knot-large.stl"), ("step", "rounded-cube.step"), ("step-cached", "rounded-cube.step"),
             ("step-assembly", "as1-oc-214.stp"), ("iges", "Cube 10x10.igs")]
    for name, model in cases:
        code, dt, rep = snapshot(exe, os.path.join(out, "open-%s.png" % name), env, S(model) if model else None)
        if model is None:
            check("open " + name, code == 0 and rep.startswith("empty"), rep[:40])
            continue
        ok = code == 0 and rep.startswith("ok") and int(field(rep, "tris") or 0) > 0
        check("open " + name, ok, "first picture %s ms (model ready %s ms, parse %s ms), %s triangles, cache=%s"
              % (field(rep, "firstFrame"), field(rep, "loadMs"), field(rep, "parse"), field(rep, "tris"), field(rep, "cache")))
        if name == "step-cached":
            check("step second open comes from the cache", field(rep, "cache") == "True")

    bad = os.path.join(out, "broken.step")
    open(bad, "w").write("this is not a STEP file\n")
    code, dt, rep = snapshot(exe, os.path.join(out, "open-broken-step.png"), env, bad)
    check("broken STEP shows an error instead of crashing", code == 3 and rep.startswith("error"), rep[:90])
    code, dt, rep = snapshot(exe, os.path.join(out, "open-missing.png"), env, S("does-not-exist.stl"))
    check("missing file shows an error", code == 3 and rep.startswith("error"), rep[:90])
    code, dt, rep = snapshot(exe, os.path.join(out, "view-help.png"), env, S("as1-oc-214.stp"), press="?")
    check("help overlay renders", code == 0)
    code, dt, rep = snapshot(exe, os.path.join(out, "view-details-light.png"), env, S("as1-oc-214.stp"), press="TI")
    check("details card + light theme render", code == 0)

    print("== Part 2: drive one window with real mouse and keyboard messages ==")
    s = Session(exe, S("as1-oc-214.stp"), env, out)
    try:
        a = s.shot("start")
        w, h = (int(v) for v in a["size"].split("x"))
        cx, cy = w // 2, h // 2
        check("window title shows the file", "as1-oc-214.stp" in (s.title_now() or ""), s.title_now() or "")
        check("model loaded with CAD edges", int(a["tris"]) > 0 and int(a["edgeLines"]) > 0, "%s triangles, %s edge lines" % (a["tris"], a["edgeLines"]))

        s.drag(cx, cy, cx + 260, cy + 90)
        b = s.shot("orbit")
        check("left-drag orbits", abs(f(b, "yaw") - f(a, "yaw")) > 0.5 and abs(f(b, "pitch") - f(a, "pitch")) > 0.1,
              "yaw %.2f -> %.2f, pitch %.2f -> %.2f" % (f(a, "yaw"), f(b, "yaw"), f(a, "pitch"), f(b, "pitch")))

        s.post(WM_MOUSEWHEEL, (360 << 16), s.xy(0, 0))
        c = s.shot("zoom-in")
        check("wheel zooms in", f(c, "dist") < f(b, "dist") * 0.75, "distance %.1f -> %.1f" % (f(b, "dist"), f(c, "dist")))

        s.drag(cx, cy, cx - 180, cy + 60, right=True)
        d = s.shot("pan")
        check("right-drag pans", d["target"] != c["target"] and abs(f(d, "yaw") - f(c, "yaw")) < 1e-3, "%s -> %s" % (c["target"], d["target"]))

        s.key("F")
        e = s.shot("fit")
        check("F fits the model", e["target"] == a["target"] and abs(f(e, "dist") - f(a, "dist")) / f(a, "dist") < 0.35,
              "distance %.1f, target %s" % (f(e, "dist"), e["target"]))

        s.post(WM_LBUTTONDBLCLK, 1, s.xy(cx, cy))
        g = s.shot("focus")
        check("double-click focuses on the surface", g["target"] != e["target"], "target %s" % g["target"])

        for vk, key, name, want in [("5", "pitch", "top view", 1.5708), ("1", "pitch", "front view", 0.0), ("0", "pitch", "isometric", 0.49)]:
            s.key(vk)
            st = s.shot(name.replace(" ", "-"))
            check("key %s gives the %s" % (vk, name), abs(f(st, key) - want) < 0.01, "pitch %.3f" % f(st, key))

        toggles = [("E", "edges", "False", "edges off"), ("E", "edges", "True", "edges on"), ("W", "wire", "True", "wireframe"),
                   ("W", "wire", "False", "wireframe off"), ("G", "grid", "False", "grid off"), ("G", "grid", "True", "grid on"),
                   ("P", "ortho", "True", "orthographic"), ("P", "ortho", "False", "perspective"),
                   ("T", "dark", None, "theme toggle"), ("Z", "yUp", "True", "up axis flipped"), ("Z", "yUp", "False", "up axis restored"),
                   ("I", "info", "True", "details card"), ("I", "info", "False", "details hidden"),
                   (0x70, "help", "True", "help"), (0x1B, "help", "False", "Esc closes help, not the app")]
        prev = s.shot("before-toggles")
        for vk, key, want, name in toggles:
            s.key(vk)
            st = s.shot(name.replace(" ", "-").replace(",", ""))
            ok = st[key] != prev[key] if want is None else st[key] == want
            check("%s (%s)" % (name, vk if isinstance(vk, str) else hex(vk)), ok, "%s=%s" % (key, st[key]))
            prev = st

        s.key("C")
        st = s.shot("material")
        check("C switches from file colours to a material", st["fileColors"] == "False", "material=%s" % st["material"])

        s.key("R")
        time.sleep(0.4)
        r1 = s.shot("turntable")
        time.sleep(0.4)
        r2 = s.shot("turntable-later")
        check("R spins the turntable", r1["spin"] == "True" and f(r2, "yaw") != f(r1, "yaw"), "yaw %.2f -> %.2f" % (f(r1, "yaw"), f(r2, "yaw")))
        s.key("R")

        s.key(0x27)
        nxt = s.shot("next-file")
        check("Right arrow opens the next model in the folder", nxt["file"] not in ("", "as1-oc-214.stp") and int(nxt["tris"]) > 0 and not nxt["error"],
              "now showing %s (%s triangles, %s ms)" % (nxt["file"], nxt["tris"], nxt["loadMs"]))
        s.key(0x25)
        back = s.shot("previous-file")
        check("Left arrow goes back", back["file"] == "as1-oc-214.stp", "%s in %s ms, cache=%s" % (back["file"], back["loadMs"], back["cache"]))

        for i in range(9):
            s.key(0x27)
        last = s.shot("after-rapid-browsing")
        check("rapid arrow presses settle on a loaded model", int(last["tris"]) > 0 and not last["error"], "ended on %s" % last["file"])
    finally:
        code = s.close()
    check("closing the window exits cleanly", code == 0, "exit code %s" % code)
    check("settings were saved on exit", os.path.exists(os.path.join(data, "settings.ini")))

    inspection_tests(exe, S, env, out)
    units_and_layflat_tests(exe, S, env, out)
    fraction_and_option_tests(exe, S, env, out)
    mesh_format_tests(exe, S, env, out)
    update_indicator_tests(exe, S, env, out)

    failed = [r for r in results if not r[1]]
    print("\n%d checks, %d failed" % (len(results), len(failed)))
    for name, _, detail in failed:
        print("  FAILED: %s  %s" % (name, detail))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
