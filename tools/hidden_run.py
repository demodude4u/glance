"""Run Glance on a private, never-shown Win32 desktop and save what it rendered.

Uses the ctcore.hidden_desktop helper (CreateDesktopW + a kill-on-close job),
so nothing appears on the real screen and every process started here is cleaned up.

  python tools/hidden_run.py <Glance.exe> <out.png> [model] [--press KEYS] [--timeout S]
"""
import argparse
import os
import sys
import time

sys.path.insert(0, __import__("os").environ.get("CTCORE_PATH", "."))   # where the ctcore package lives, if not installed
from ctcore.hidden_desktop import HiddenDesktop  # noqa: E402
from PIL import Image  # noqa: E402


def snapshot(exe, out_png, model=None, press="", timeout=120, env_extra=None):
    """Returns (exit_code, seconds, report_text). exit_code None = timed out."""
    bmp = os.path.splitext(out_png)[0] + ".bmp"
    for f in (bmp, bmp + ".txt", out_png):
        if os.path.exists(f):
            os.remove(f)
    args = [exe]
    if model:
        args.append(model)
    args += ["--snapshot", bmp]
    if press:
        args += ["--press", press]
    env = dict(os.environ, GLANCE_QUIET="1")
    env.update(env_extra or {})
    t0 = time.perf_counter()
    with HiddenDesktop("glance_test") as desk:
        _, h = desk.spawn(args, env=env)
        code = desk.wait(h, timeout)
        dt = time.perf_counter() - t0
        wins = desk.windows() if code is None else []
    report = ""
    if os.path.exists(bmp + ".txt"):
        report = open(bmp + ".txt", encoding="utf-8").read()
    if os.path.exists(bmp):
        Image.open(bmp).convert("RGB").save(out_png)
        os.remove(bmp)
    if code is None:
        report += " TIMEOUT windows=%r" % (wins,)
    return code, dt, report


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("exe"); ap.add_argument("out"); ap.add_argument("model", nargs="?")
    ap.add_argument("--press", default=""); ap.add_argument("--timeout", type=float, default=120)
    a = ap.parse_args()
    code, dt, report = snapshot(a.exe, a.out, a.model, a.press, a.timeout)
    print("exit=%s  %.2fs  %s" % (code, dt, report))
