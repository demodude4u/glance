"""Time first-open of chosen models on a hidden desktop, with a fresh cache each run.

  python tools/hidden_time.py <Glance.exe> <out_dir> <model> [<model> ...] [--runs N]
"""
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hidden_test as T  # noqa: E402

args = [a for a in sys.argv[1:] if not a.startswith("--")]
runs = int(sys.argv[sys.argv.index("--runs") + 1]) if "--runs" in sys.argv else 3
if "--runs" in sys.argv:
    args.remove(str(runs))
exe, out, models = os.path.abspath(args[0]), os.path.abspath(args[1]), args[2:]
os.makedirs(out, exist_ok=True)
for model in models:
    for i in range(runs):
        data = os.path.join(out, "data")
        shutil.rmtree(data, ignore_errors=True)
        env = dict(os.environ, GLANCE_QUIET="1", GLANCE_DATA_DIR=data, GLANCE_NO_LOOKAHEAD="1")
        code, dt, rep = T.snapshot(exe, os.path.join(out, "t.png"), env, model)
        keys = ("loadMs", "parse", "firstFrame", "warm", "ctx", "tris")
        print("%-44s " % os.path.basename(model)[:44] + "  ".join("%s=%s" % (k, T.field(rep, k)) for k in keys), flush=True)
