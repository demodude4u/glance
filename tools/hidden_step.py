"""Run the STEP reader alone on a hidden desktop with phase tracing."""
import os, sys, time
sys.path.insert(0, __import__("os").environ.get("CTCORE_PATH", "."))   # where the ctcore package lives, if not installed
from ctcore.hidden_desktop import HiddenDesktop
exe, model, out = sys.argv[1], sys.argv[2], sys.argv[3]
fmt = "iges" if model.lower().endswith((".igs", ".iges")) else "step"
log = out + ".log"
t0 = time.perf_counter()
with open(log, "w") as f, HiddenDesktop("glance_step") as d:
    _, h = d.spawn([exe, model, out, fmt], stdout=f, stderr=f, env=dict(os.environ, GLANCE_STEP_TRACE="1", **({"GLANCE_STEP_REPAIR": "1"} if len(sys.argv) > 4 else {})))
    code = d.wait(h, 300)
print("exit=%s total=%.2fs size=%s" % (code, time.perf_counter() - t0, os.path.getsize(out) if os.path.exists(out) else None))
print(open(log).read())
