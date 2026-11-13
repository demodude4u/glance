"""Checks the folder look-ahead on a hidden desktop: open one STEP file, wait, and confirm the neighbouring
files were converted in the background, then that stepping to one of them is served from the cache.

  python tools/hidden_lookahead.py <Glance.exe> <folder_with_step_files> <out_dir>
"""
import glob
import os
import shutil
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hidden_test as T  # noqa: E402

exe, folder, out = (os.path.abspath(a) for a in sys.argv[1:4])
os.makedirs(out, exist_ok=True)
data = os.path.join(out, "data")
shutil.rmtree(data, ignore_errors=True)
env = dict(os.environ, GLANCE_QUIET="1", GLANCE_DATA_DIR=data)
env.pop("GLANCE_NO_LOOKAHEAD", None)
files = sorted(f for f in os.listdir(folder) if f.lower().endswith((".stp", ".step", ".igs")))
s = T.Session(exe, os.path.join(folder, files[0]), env, out)
try:
    a = s.shot("first")
    deadline = time.time() + 60
    while time.time() < deadline and len(glob.glob(os.path.join(data, "cache", "*.gmesh"))) < len(files):
        time.sleep(0.25)
    cached = len(glob.glob(os.path.join(data, "cache", "*.gmesh")))
    T.check("neighbouring files were converted in the background", cached == len(files), "%d of %d cached" % (cached, len(files)))
    s.key(0x27)
    b = s.shot("next")
    T.check("stepping to the next file is served from the cache", b["cache"] == "True" and b["file"] == files[1], "%s in %s ms" % (b["file"], b["loadMs"]))
finally:
    code = s.close()
T.check("exits cleanly with no conversion left running", code == 0)
sys.exit(1 if any(not r[1] for r in T.results) else 0)
