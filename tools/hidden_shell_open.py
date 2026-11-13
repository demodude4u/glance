"""Checks the real double-click path on a hidden desktop: asks the Windows shell to open a model file
(the same thing Explorer does) and reports which window appeared.

  python tools/hidden_shell_open.py <model> [<model> ...]
"""
import os
import sys
import time

sys.path.insert(0, __import__("os").environ.get("CTCORE_PATH", "."))   # where the ctcore package lives, if not installed
from ctcore.hidden_desktop import HiddenDesktop  # noqa: E402

ok = True
for model in sys.argv[1:]:
    model = os.path.abspath(model)
    with HiddenDesktop("glance_shell") as desk:
        t0 = time.perf_counter()
        desk.spawn(["cmd.exe", "/c", "start", "", model], env=dict(os.environ, GLANCE_NO_LOOKAHEAD="1"))
        found, deadline = None, time.time() + 20
        while time.time() < deadline and found is None:
            for h, pid, cls, title, visible in desk.windows():
                if visible and cls == "GlanceWindow":
                    found = (title, time.perf_counter() - t0)
            time.sleep(0.02)
        others = [(c, t) for _, _, c, t, v in desk.windows() if v and c != "GlanceWindow" and t]
    name = os.path.basename(model)
    if found and name in found[0]:
        print("PASS  %-28s opened in Glance: window '%s' after %.2f s (includes cmd.exe start-up)" % (name, found[0], found[1]))
    else:
        ok = False
        print("FAIL  %-28s Glance window not found; other windows: %r" % (name, others[:4]))
sys.exit(0 if ok else 1)
