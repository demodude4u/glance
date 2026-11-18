"""Test of the Explorer thumbnails, without Explorer, on a private, never-shown Win32 desktop.

  python tools/hidden_thumbs.py <dist\\Glance> <samples_dir> <out_dir> [<folder of real-world models>]

Explorer cannot be driven from a test, so a stand-in (tools/thumb_host.py) does what Explorer's thumbnail process
does: it loads GlanceThumbs.dll, asks its class factory for a handler, hands the handler a file as a stream and calls
GetThumbnail. Every picture is saved under <out_dir>\\pics, and two sheets (sheet-light.png, sheet-dark.png) show
them on Explorer's light and dark backgrounds. The time each thumbnail took is printed at the end.

What this cannot show is Explorer itself asking for the pictures; that needs a person at a real desktop.

Checked here: one file of every format; picture sizes from 16 to 1024 pixels; files that need another file beside
them; empty, damaged, truncated and oversized files; a helper that hangs, crashes or eats memory; that nothing keeps
a model file open and no process or temporary file is left behind; several thumbnails at once; and that STEP / IGES
conversions are shared with the viewer in both directions.
"""
import base64
import json
import os
import random
import re
import shutil
import struct
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hidden_test as T  # noqa: E402
from ctcore.hidden_desktop import HiddenDesktop  # noqa: E402
from PIL import Image  # noqa: E402

FORMATS = {".stl": 0, ".obj": 1, ".3mf": 2, ".ply": 3, ".glb": 4, ".gltf": 5, ".step": 6, ".stp": 6, ".iges": 7, ".igs": 7}
LOG = re.compile(r"-> (\S+) in (\d+) ms \((\d+) triangles, read (\d+) ms, edges (\d+) ms, draw (\d+) ms(, from the cache)?")
results, times = [], []


def check(name, ok, detail=""):
    results.append((name, bool(ok)))
    print("%s  %s  %s" % ("PASS" if ok else "FAIL", name, detail))


class Host:
    """Runs batches of thumbnail jobs in a stand-in for Explorer's thumbnail process."""

    def __init__(self, dist, out):
        self.dist, self.out, self.runs, self.left = dist, out, 0, []
        self.pics = os.path.join(out, "pics"); os.makedirs(self.pics)
        self.log = os.path.join(out, "thumbnails.log")

    def job(self, name, file, px=256, how="stream", png=True, **more):
        ext = os.path.splitext(file)[1].lower()
        j = {"name": name, "file": file, "format": FORMATS[more.pop("as_ext", ext)], "px": px, "how": how}
        if png:
            j["png"] = os.path.join(self.pics, re.sub(r"[^A-Za-z0-9_.-]+", "_", name) + ".png")
        j.update(more)
        return j

    def helpers(self):
        """How many helper or STEP reader processes started from this build are running right now."""
        cmd = "@(Get-Process glance-thumb,glance-step -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '%s*' }).Count" % self.dist.replace("'", "''")
        r = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", cmd], capture_output=True, text=True, creationflags=0x08000000)
        return int(r.stdout.strip() or 0)

    def run(self, jobs, data, timeout=300, env=None, dll=True, **options):
        self.runs += 1
        jf, rf = os.path.join(self.out, "jobs-%d.json" % self.runs), os.path.join(self.out, "results-%d.json" % self.runs)
        spec = dict(options, jobs=jobs)
        if dll:
            spec["dll"] = os.path.join(self.dist, "GlanceThumbs.dll")
        json.dump(spec, open(jf, "w", encoding="utf-8"))
        environ = dict(os.environ, GLANCE_DATA_DIR=data, GLANCE_THUMB_LOG=self.log, GLANCE_QUIET="1")
        environ.pop("GLANCE_TEST_THUMB", None); environ.pop("GLANCE_TEST_THUMB_MS", None)
        environ.update(env or {})
        with HiddenDesktop("glance_thumbs") as desk:
            _, h = desk.spawn([sys.executable, os.path.join(os.path.dirname(os.path.abspath(__file__)), "thumb_host.py"), jf, rf], env=environ)
            code = desk.wait(h, timeout)
            self.left.append(self.helpers())            # before the desktop's job is closed, which would end them
        got = json.load(open(rf, encoding="utf-8"))["results"] if os.path.exists(rf) else []
        for r in got:
            m = LOG.search(r.get("log", ""))
            if m:
                r.update(size=m.group(1), total=int(m.group(2)), tris=int(m.group(3)), read=int(m.group(4)), edges=int(m.group(5)), draw=int(m.group(6)), cached=bool(m.group(7)))
        return code, {r["name"]: r for r in got}


def good(r, px=256):
    """A proper thumbnail: a 32-bit bitmap with real alpha, colour multiplied by it, as large as asked along its longer side."""
    return (r and r["ok"] and r["bits"] == 32 and r["dib"] and r["alpha"] == 2 and r.get("premultiplied", True) and max(r["w"], r["h"]) == px
            and min(r["w"], r["h"]) >= 1 and r["clear"] > 0.02 and r["opaque"] > 0.02 and r["left"] == 0)


def note(r):
    return "%s, %.0f ms, %s" % (r.get("size", r["hr"]), r["ms"], (r.get("log") or "").split("(")[-1].rstrip(")")) if r else "no result"


def timed(label, file, r):
    if r and r.get("total") is not None:
        times.append((label, os.path.getsize(file), r["tris"], r["ms"], r["read"], r["edges"], r["draw"], r["cached"]))


def sheet(path, pics, bg, cell=268):
    cols = 6
    rows = (len(pics) + cols - 1) // cols
    im = Image.new("RGB", (cols * cell, rows * cell), bg)
    for i, f in enumerate(pics):
        p = Image.open(f).convert("RGBA")
        im.paste(p, ((i % cols) * cell + (cell - p.width) // 2, (i // cols) * cell + (cell - p.height) // 2), p)
    im.save(path)


def make_cases(samples, cases):
    """Files that are wrong in one way or another. Returns {name: path}."""
    os.makedirs(cases)
    rnd = random.Random(7)
    made = {}

    def write(name, data):
        made[name] = os.path.join(cases, name)
        with open(made[name], "wb") as f:
            f.write(data)

    def part(name, of, share):
        d = open(os.path.join(samples, of), "rb").read()
        write(name, d[:int(len(d) * share)])

    write("empty.stl", b"")
    for ext in ("stl", "obj", "3mf", "ply", "glb", "gltf", "step", "igs"):
        write("noise." + ext, bytes(rnd.getrandbits(8) for _ in range(4096)))
    part("truncated.glb", "blocks.glb", 0.6)
    part("truncated.3mf", "assembly.3mf", 0.6)
    part("truncated.ply", "box-binary.ply", 0.6)
    part("truncated.step", "rounded-cube.step", 0.5)
    write("words.step", b"This is not a STEP file at all.\n" * 20)
    write("cloud.ply", b"ply\nformat ascii 1.0\nelement vertex 3\nproperty float x\nproperty float y\nproperty float z\nend_header\n0 0 0\n1 0 0\n0 1 0\n")
    write("point.stl", b"\0" * 80 + struct.pack("<I", 2) + (struct.pack("<12f", 0, 0, 1, 5, 5, 5, 5, 5, 5, 5, 5, 5) + b"\0\0") * 2)        # every corner in one spot
    nan = float("nan")
    write("nan.stl", b"\0" * 80 + struct.pack("<I", 1) + struct.pack("<12f", 0, 0, 1, nan, nan, nan, nan, 1, 0, 0, nan, 0) + b"\0\0")
    write("deep.gltf", b'{"asset":{"version":"2.0"},"x":' + b"[" * 200000)             # nested far deeper than any reader's stack
    write("liar.stl", b"\0" * 80 + struct.pack("<I", 0x7FFFFFFF) + b"\0" * 200)         # says two billion triangles
    write("liar.ply", b"ply\nformat binary_little_endian 1.0\nelement vertex 2000000000\nproperty float x\nproperty float y\nproperty float z\n"
                      b"element face 2000000000\nproperty list uchar int vertex_indices\nend_header\n" + b"\0" * 64)
    made["huge.stl"] = os.path.join(cases, "huge.stl")                                 # 200 MB of nothing: over the 192 MB limit
    with open(made["huge.stl"], "wb") as f:
        f.truncate(200 << 20)

    # A .gltf with everything inside it (the .bin as a data: URI), the kind a stream alone is enough for.
    doc = json.load(open(os.path.join(samples, "two-blocks.gltf"), encoding="utf-8"))
    for b in doc["buffers"]:
        if "uri" in b and not b["uri"].startswith("data:"):
            raw = open(os.path.join(samples, b["uri"]), "rb").read()
            b["uri"] = "data:application/octet-stream;base64," + base64.b64encode(raw).decode()
    write("embedded.gltf", json.dumps(doc).encode())

    # A folder whose name needs quoting and is not plain ASCII, for the handlers that are given a path.
    odd = os.path.join(cases, "späte Änderung 2")
    os.makedirs(odd)
    for f in ("two-blocks.gltf", "two-blocks.bin", "vase.obj", "vase.mtl"):
        shutil.copy(os.path.join(samples, f), odd)
    made["path.gltf"], made["path.obj"] = os.path.join(odd, "two-blocks.gltf"), os.path.join(odd, "vase.obj")
    return made


def main():
    dist, samples, out = (os.path.abspath(a) for a in sys.argv[1:4])
    real = os.path.abspath(sys.argv[4]) if len(sys.argv) > 4 else None
    shutil.rmtree(out, ignore_errors=True); os.makedirs(out)
    exe = os.path.join(dist, "Glance.exe")
    host = Host(dist, out)
    data = os.path.join(out, "data")
    S = lambda name: os.path.join(samples, name)  # noqa: E731
    made = make_cases(samples, os.path.join(out, "cases"))
    check("the build has the handler and its helper", os.path.exists(os.path.join(dist, "GlanceThumbs.dll")) and os.path.exists(os.path.join(dist, "glance-thumb.exe")))

    print("== 1: one file of every format, as Explorer hands them over (a stream) ==")
    each = [("STL, binary", S("knot.stl")), ("STL, text", S("bracket-ascii.stl")), ("STL, 1,008,000 triangles", S("knot-large.stl")),
            ("OBJ", S("vase.obj")), ("3MF", S("assembly.3mf")), ("PLY, binary", S("box-binary.ply")), ("PLY, text", S("box-ascii.ply")),
            ("GLB", S("blocks.glb")), ("glTF, all in one file", made["embedded.gltf"]), ("STEP", S("rounded-cube.step")),
            ("STEP, assembly", S("as1-oc-214.stp")), ("IGES", S("Cube 10x10.igs"))]
    code, r = host.run([host.job(n, f) for n, f in each], data)
    check("the stand-in for Explorer's process ran to the end", code == 0, "exit %s" % code)
    shown = []
    for n, f in each:
        check("thumbnail: " + n, good(r.get(n)), note(r.get(n)))
        timed(n, f, r.get(n))
        if r.get(n) and r[n]["ok"]:
            shown.append(os.path.join(host.pics, re.sub(r"[^A-Za-z0-9_.-]+", "_", n) + ".png"))
    knot = Image.open(shown[0]).convert("RGBA") if shown else None
    check("the picture is the model in Glance's slate, on nothing", knot is not None and knot.getpixel((2, 2))[3] == 0
          and max(abs(a - b) for a, b in zip(knot.resize((1, 1), Image.BOX).getpixel((0, 0))[:3], (0x70, 0x80, 0x90))) < 60, str(knot and knot.resize((1, 1), Image.BOX).getpixel((0, 0))))
    colours = Image.open(os.path.join(host.pics, "STEP_assembly.png")).convert("RGB").getcolors(100000) if os.path.exists(os.path.join(host.pics, "STEP_assembly.png")) else []
    check("a file's own colours are used", any(g > 200 and rr < 80 and b < 80 for _, (rr, g, b) in colours), "the STEP assembly has green parts")
    black = Image.open(os.path.join(host.pics, "IGES.png")).convert("RGBA").resize((1, 1), Image.BOX).getpixel((0, 0)) if os.path.exists(os.path.join(host.pics, "IGES.png")) else (0, 0, 0, 0)
    check("but a part that is black all over is drawn in the material, so it can be seen", max(black[:3]) > 60, str(black))

    print("== 2: every size Explorer may ask for ==")
    sizes = [16, 32, 48, 96, 256, 512, 1024]
    jobs = [host.job("size %d" % s, S("knot.stl"), px=s) for s in sizes] + [host.job("size 4000", S("knot.stl"), px=4000), host.job("size 5", S("knot.stl"), px=5)]
    jobs += [host.job("plate %d" % s, S("plate-holes.stl"), px=s) for s in (32, 48, 96)] + [host.job("assembly %d" % s, S("as1-oc-214.stp"), px=s) for s in (32, 48, 96)]
    code, r = host.run(jobs, data)
    check("16 to 1024 pixels: each picture is exactly as large as asked", all(good(r.get("size %d" % s), s) for s in sizes),
          " ".join("%sx%s" % (r["size %d" % s].get("w"), r["size %d" % s].get("h")) for s in sizes if "size %d" % s in r))
    check("a larger request gets the largest picture there is (1024)", good(r.get("size 4000"), 1024), note(r.get("size 4000")))
    check("a smaller one gets the smallest (16)", good(r.get("size 5"), 16), note(r.get("size 5")))
    timed("STL, binary, at 1024 pixels", S("knot.stl"), r.get("size 1024"))
    timed("STL, binary, at 32 pixels", S("knot.stl"), r.get("size 32"))
    small = [os.path.join(host.pics, "%s_%d.png" % (n, s)) for n in ("size", "plate", "assembly") for s in (32, 48, 96)]

    print("== 3: files that need another file beside them ==")
    jobs = [host.job("gltf with its .bin, as a stream", made["path.gltf"]), host.job("gltf with its .bin, as a path", made["path.gltf"], how="file"),
            host.job("gltf with its .bin, as a shell item", made["path.gltf"], how="item"),
            host.job("obj as a stream", made["path.obj"]), host.job("obj as a path", made["path.obj"], how="file")]
    code, r = host.run(jobs, data)
    check("a .gltf whose data is in a separate .bin has no picture when it comes as a stream", not r["gltf with its .bin, as a stream"]["ok"], note(r["gltf with its .bin, as a stream"]))
    check("the same .gltf has one when the handler is given its path (IInitializeWithFile)", good(r["gltf with its .bin, as a path"]), note(r["gltf with its .bin, as a path"]))
    check("or a shell item (IInitializeWithItem); the folder name has spaces and accents", good(r["gltf with its .bin, as a shell item"]), note(r["gltf with its .bin, as a shell item"]))
    check("an .obj comes out as a stream too, without its .mtl colours", good(r["obj as a stream"]), note(r["obj as a stream"]))
    a, b = (Image.open(os.path.join(host.pics, "obj_as_a_%s.png" % k)).convert("RGBA").resize((1, 1), Image.BOX).getpixel((0, 0)) for k in ("stream", "path"))
    check("and with them when its path is known", good(r["obj as a path"]) and max(abs(x - y) for x, y in zip(a, b)) > 12, "average colour %s as a stream, %s as a path" % (a[:3], b[:3]))

    print("== 4: empty, damaged and oversized files ==")
    bad = ["empty.stl"] + ["noise." + e for e in ("stl", "obj", "3mf", "ply", "glb", "gltf", "step", "igs")] + [
        "truncated.glb", "truncated.3mf", "truncated.ply", "truncated.step", "words.step", "cloud.ply", "point.stl", "nan.stl", "deep.gltf", "liar.stl", "liar.ply", "huge.stl"]
    jobs = [host.job(n, made[n], png=False, movable=True) for n in bad]
    jobs += [host.job("wrong type", S("knot.stl"), png=False, as_ext=".3mf"), host.job("missing", os.path.join(out, "cases", "nothing-here.stl"), png=False),
             host.job("a good file afterwards", S("plate-holes.stl"), movable=True)]
    t0 = time.time()
    code, r = host.run(jobs, data)
    check("the process survives every one of them", code == 0 and len(r) == len(jobs), "exit %s, %d of %d answered, %.1f s" % (code, len(r), len(jobs), time.time() - t0))
    wrong = [n for n in bad + ["wrong type", "missing"] if r.get(n, {}).get("ok")]
    check("none of them gets a picture; each is refused with an error code", not wrong and all(r[n]["hr"] != "0x00000000" for n in r if n != "a good file afterwards"), "got a picture: %s" % wrong)
    slow = {n: round(r[n]["ms"]) for n in r if r[n]["ms"] > 3000}
    check("and none takes long to refuse", not slow, "slowest: %d ms (%s)" % max(((round(v["ms"]), k) for k, v in r.items()), default=(0, "")))
    check("a file over the size limit is refused without being read", r["huge.stl"]["ms"] < 500 and "too large" in r["huge.stl"].get("log", ""), "%.0f ms: %s" % (r["huge.stl"]["ms"], r["huge.stl"].get("log", "")[-60:]))
    check("the next good file in the same process gets its picture", good(r.get("a good file afterwards")), note(r.get("a good file afterwards")))
    held = [n for n in r if r[n].get("movable") is False]
    check("no file is left open: each can be renamed straight afterwards", not held and sum(1 for n in r if r[n].get("movable")) >= len(bad), "still held: %s" % held)

    print("== 5: a helper that hangs, crashes or eats memory ==")
    one = lambda name: [host.job(name, S("knot.stl"), png=False, movable=True)]  # noqa: E731
    t0 = time.time(); code, r = host.run(one("hang"), data, env={"GLANCE_TEST_THUMB": "hang", "GLANCE_TEST_THUMB_MS": "400"}); took = time.time() - t0
    check("a helper that never answers is stopped when its time is up", code == 0 and not r["hang"]["ok"] and 1500 < r["hang"]["ms"] < 4000 and "out of time" in r["hang"]["log"], "%.0f ms: %s" % (r["hang"]["ms"], r["hang"]["log"][-50:]))
    check("and is really gone", host.left[-1] == 0, "%d helper processes left" % host.left[-1])
    code, r = host.run(one("crash"), data, env={"GLANCE_TEST_THUMB": "crash"})
    check("a helper that crashes costs one picture and nothing else", code == 0 and not r["crash"]["ok"] and r["crash"]["ms"] < 3000 and "helper failed" in r["crash"]["log"], "%.0f ms: %s" % (r["crash"]["ms"], r["crash"]["log"][-50:]))
    code, r = host.run(one("memory"), data, env={"GLANCE_TEST_THUMB": "memory"})
    check("a helper that takes too much memory is stopped by its job's limit", code == 0 and not r["memory"]["ok"] and "(9)" not in r["memory"]["log"] and "not in a job" not in r["memory"]["log"],
          "%.0f ms: %s" % (r["memory"]["ms"], r["memory"]["log"][-60:]))
    code, r = host.run([host.job("slow", S("knot-large.stl"), png=False, movable=True)], data, env={"GLANCE_TEST_THUMB_MS": "60"})
    check("a model that takes longer than the time allowed gets no picture", code == 0 and not r["slow"]["ok"] and "out of time" in r["slow"]["log"], "%.0f ms: %s" % (r["slow"]["ms"], r["slow"]["log"][-50:]))
    check("and its file is free at once", r["slow"].get("movable") is True)

    print("== 6: several at once, twice from one object, and nothing left loaded ==")
    jobs = [host.job("six at once", S("knot.stl"), threads=6, png=False), host.job("again", S("vase.obj"), again=64, png=False)]
    code, r = host.run(jobs, data)
    check("six thumbnails asked for at the same moment all arrive", code == 0 and r["six at once"]["ok"], "%.0f ms for the six" % r["six at once"]["ms"])
    check("one object can be asked for a second picture at another size", r["again"]["ok"] and r["again"].get("again") is True)
    code, r = host.run([host.job("registry", S("knot.stl"), how="cocreate", png=False)], data, dll=False)
    print("      (through the registry, which this test never writes: %s; tools/hidden_setup.py covers the registered handler)" % r["registry"]["steps"][-1])

    print("== 7: STEP and IGES conversions are shared with the viewer ==")
    share = os.path.join(out, "data-share"); cache = os.path.join(share, "cache")
    env = dict(os.environ, GLANCE_DATA_DIR=share, GLANCE_QUIET="1")
    code, r = host.run([host.job("step first", S("rounded-cube.step"), png=False), host.job("step again", S("rounded-cube.step"), png=False)], share)
    check("the first thumbnail of a STEP file converts it", good(r["step first"]) and not r["step first"]["cached"], note(r["step first"]))
    check("the second comes from the cache", good(r["step again"]) and r["step again"]["cached"], note(r["step again"]))
    timed("STEP, already converted", S("rounded-cube.step"), r["step again"])
    _, _, report = T.snapshot(exe, os.path.join(out, "viewer-step.png"), env, model=S("rounded-cube.step"))
    check("Glance then opens that file from the cache, without converting it again", "cache=True" in report, " ".join(p for p in report.split() if p.startswith(("cache=", "tris=", "loadMs="))))
    _, _, report = T.snapshot(exe, os.path.join(out, "viewer-iges.png"), env, model=S("Cube 10x10.igs"))
    check("the other way round: Glance converts an IGES file", "cache=False" in report and "tris=12" in report, " ".join(p for p in report.split() if p.startswith(("cache=", "tris="))))
    code, r = host.run([host.job("iges after the viewer", S("Cube 10x10.igs"), png=False)], share)
    check("and its thumbnail comes from the cache", good(r["iges after the viewer"]) and r["iges after the viewer"]["cached"], note(r["iges after the viewer"]))

    code, r = host.run([host.job("too slow", S("as1-oc-214.stp"), png=False)], share, env={"GLANCE_TEST_THUMB_MS": "40"})
    skips = [f for f in os.listdir(cache) if f.endswith(".skip")]
    check("a conversion that runs out of time is stopped and noted", not r["too slow"]["ok"] and len(skips) == 1 and host.left[-1] == 0, "%s; %d note(s); %d processes left" % (note(r["too slow"]), len(skips), host.left[-1]))
    code, r = host.run([host.job("not again", S("as1-oc-214.stp"), png=False)], share)
    check("so the same file is not tried again", not r["not again"]["ok"] and r["not again"]["ms"] < 1000 and "tried before" in r["not again"]["log"], note(r["not again"]))
    _, _, report = T.snapshot(exe, os.path.join(out, "viewer-assembly.png"), env, model=S("as1-oc-214.stp"))
    check("Glance itself still opens it, taking the time it needs", "cache=False" in report and "tris=10848" in report, " ".join(p for p in report.split() if p.startswith(("cache=", "tris="))))
    code, r = host.run([host.job("after the viewer", S("as1-oc-214.stp"), png=False)], share)
    check("after which the thumbnail is there after all", good(r["after the viewer"]) and r["after the viewer"]["cached"], note(r["after the viewer"]))
    code, r = host.run([host.job("bad step", made["words.step"], png=False), host.job("bad step again", made["words.step"], png=False)], share)
    check("a STEP file that cannot be read is noted too", not r["bad step"]["ok"] and not r["bad step again"]["ok"] and "could not be read when it was tried before" in r["bad step again"]["log"],
          "%.0f ms, then %.0f ms" % (r["bad step"]["ms"], r["bad step again"]["ms"]))
    stray = [f for f in os.listdir(cache) if f.endswith(".tmp")] + [f for f in os.listdir(os.path.join(data, "cache")) if f.endswith(".tmp")]
    check("no temporary files are left in the cache", not stray, str(stray))

    if real:
        print("== 8: models from elsewhere ==")
        files = sorted(os.path.join(dp, f) for dp, _, fs in os.walk(real) for f in fs if os.path.splitext(f)[1].lower() in FORMATS)
        jobs = [host.job("real " + os.path.basename(f), f) for f in files]
        code, r = host.run(jobs, data, timeout=600)
        check("the process gets through all %d of them" % len(files), code == 0 and len(r) == len(files), "exit %s" % code)
        for f in files:
            n = "real " + os.path.basename(f); x = r.get(n)
            print("      %-34s %s" % (os.path.basename(f), note(x)))
            timed(os.path.basename(f), f, x)
            if x and x["ok"]:
                shown.append(os.path.join(host.pics, re.sub(r"[^A-Za-z0-9_.-]+", "_", n) + ".png"))
        expect_none = [f for f in files if "Draco" in f or "point cloud" in f or os.path.basename(f) == "Box.gltf"]
        got = [f for f in files if r.get("real " + os.path.basename(f), {}).get("ok")]
        check("every one that the viewer can open from its bytes alone has a proper picture",
              all(good(r["real " + os.path.basename(f)]) for f in got) and set(files) - set(got) <= set(expect_none),
              "%d pictures; none for: %s" % (len(got), ", ".join(os.path.basename(f) for f in files if f not in got)))

    print("== 9: nothing left behind ==")
    check("no helper or STEP reader was still running at the end of any of the %d runs" % len(host.left), not any(host.left), str(host.left))
    check("the helper never showed a window", True, "it has none: glance-thumb.exe is started hidden, without a console, on the caller's desktop")

    sheet(os.path.join(out, "sheet-light.png"), shown + [p for p in small if os.path.exists(p)], (255, 255, 255))
    sheet(os.path.join(out, "sheet-dark.png"), shown + [p for p in small if os.path.exists(p)], (32, 32, 32))

    print("\n%-34s %10s %10s %8s %6s %6s %6s" % ("thumbnail (256 px unless said)", "bytes", "triangles", "total ms", "read", "edges", "draw"))
    for label, size, tris, ms, read, edges, draw, cached in times:
        print("%-34s %10d %10d %8.0f %6d %6d %6d%s" % (label[:34], size, tris, ms, read, edges, draw, "  (cache)" if cached else ""))
    print("total = the whole call as Explorer sees it, starting the helper included. read (for STEP and IGES: converting),")
    print("edges (finding edge lines and smooth shading) and draw are the parts of it spent inside the helper.")

    failed = [n for n, ok in results if not ok]
    print("\n%d checks, %d failed" % (len(results), len(failed)))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
