"""Open every model in a folder with Glance on a hidden desktop and report what happened.

  python tools/hidden_batch.py <Glance.exe> <models_dir> <out_dir> [--timeout S]

Writes results.csv (one row per file: outcome, triangles, time to model, conversion time) and a picture of each
model. The cache is redirected to <out_dir>\\data, so the first pass measures real first-open times.
"""
import csv
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hidden_test as T  # noqa: E402


def main():
    exe, models, out = (os.path.abspath(a) for a in sys.argv[1:4])
    timeout = float(sys.argv[sys.argv.index("--timeout") + 1]) if "--timeout" in sys.argv else 240
    os.makedirs(os.path.join(out, "pictures"), exist_ok=True)
    env = dict(os.environ, GLANCE_QUIET="1", GLANCE_DATA_DIR=os.path.join(out, "data"), GLANCE_NO_LOOKAHEAD="1")
    files = sorted(f for f in os.listdir(models) if f.lower().endswith((".stl", ".obj", ".step", ".stp", ".igs", ".iges")))
    rows, bad = [], 0
    t_all = time.time()
    for i, name in enumerate(files):
        path = os.path.join(models, name)
        png = os.path.join(out, "pictures", os.path.splitext(name)[0] + os.path.splitext(name)[1].replace(".", "_") + ".png")
        code, dt, rep = T.snapshot(exe, png, env, path, timeout=timeout)
        ok = code == 0 and rep.startswith("ok")
        outcome = "ok" if ok else "timeout" if code is None else "error" if code == 3 else "crash(%s)" % code
        if not ok:
            bad += 1
        rows.append({
            "file": name, "kb": os.path.getsize(path) // 1024, "outcome": outcome,
            "triangles": T.field(rep, "tris") or "", "model_ms": T.field(rep, "loadMs") or "", "convert_ms": T.field(rep, "parse") or "",
            "edges": T.field(rep, "edges") or "", "watertight": T.field(rep, "closed") or "", "wall_s": "%.2f" % dt,
            "message": rep[:120] if not ok else "",
        })
        print("%3d/%d  %-8s %7s KB  %8s tris  %7s ms  %s" % (i + 1, len(files), outcome, rows[-1]["kb"], rows[-1]["triangles"], rows[-1]["model_ms"], name[:60]), flush=True)
    with open(os.path.join(out, "results.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader(); w.writerows(rows)
    print("\n%d files, %d not ok, %.0f s total" % (len(rows), bad, time.time() - t_all))


if __name__ == "__main__":
    main()
