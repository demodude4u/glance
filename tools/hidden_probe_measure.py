"""Exploratory check of the measuring tool on a real part: clicks a grid of points in the top view (and an
isometric view) on a hidden desktop and prints what each click was recognised as.

  python tools/hidden_probe_measure.py <Glance.exe> <model> <out_dir>
"""
import collections
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hidden_test as T  # noqa: E402

exe, model, out = (os.path.abspath(a) for a in sys.argv[1:4])
os.makedirs(out, exist_ok=True)
env = dict(os.environ, GLANCE_QUIET="1", GLANCE_DATA_DIR=os.path.join(out, "data"), GLANCE_NO_LOOKAHEAD="1")
s = T.Session(exe, model, env, out)
try:
    for view in ("5", "0"):
        s.key("P") if view == "5" else None
        s.key(view); s.key("M") if view == "5" else None
        a = s.shot("view" + view)
        w, h = (int(v) for v in a["size"].split("x"))
        seen = collections.Counter(); examples = {}; selected = False
        for gy in range(1, 8):
            for gx in range(1, 12):
                x, y = w * gx / 12, h * gy / 8
                if selected:
                    s.key(0x1B)          # clear the previous pick (Esc with nothing selected would leave measure mode)
                s.post_many([(T.WM_LBUTTONDOWN, 1, s.xy(x, y)), (T.WM_LBUTTONUP, 0, s.xy(x, y))], gap=0.02)
                st = s.shot("p")
                kind = st["selA"] or "-"
                selected = kind != "-"
                seen[kind] += 1
                if kind != "-":
                    examples.setdefault(kind, set()).add(st["result"])
        print("view %s: %s" % (view, dict(seen)))
        for k, v in examples.items():
            print("   %-9s %s" % (k, "  |  ".join(sorted(v)[:8])))
    if selected:
        s.key(0x1B)
    w2, h2 = w, h
    s.post_many([(T.WM_LBUTTONDOWN, 1, s.xy(w2 * 0.5, h2 * 0.5)), (T.WM_LBUTTONUP, 0, s.xy(w2 * 0.5, h2 * 0.5))], gap=0.02)
    s.post_many([(T.WM_LBUTTONDOWN, 1, s.xy(w2 * 0.42, h2 * 0.55)), (T.WM_LBUTTONUP, 0, s.xy(w2 * 0.42, h2 * 0.55))], gap=0.02)
    final = s.shot("final")
    print("pair:", final["selA"], final["selB"], final["result"], "| regions", final["regions"], "chains", final["chains"])
    print("picture:", final["png"])
finally:
    s.close()
