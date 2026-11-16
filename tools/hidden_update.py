"""End-to-end test of Glance updating itself, on a private, never-shown Win32 desktop.

  python tools/hidden_update.py "<dist>\\Glance Setup.exe" <sample.stl> <out_dir>

Nothing touches the network or the real install: Glance is installed into <out_dir>\\inst\\Glance and told
(GLANCE_UPDATE_URL) to look for releases in a local folder that the test fills with a version.txt and an installer.
"""
import hashlib
import os
import shutil
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hidden_setup as S  # noqa: E402
import hidden_test as T  # noqa: E402
from ctcore.hidden_desktop import HiddenDesktop  # noqa: E402

check = S.check


def wait_for(cond, seconds):
    end = time.time() + seconds
    while time.time() < end:
        if cond():
            return True
        time.sleep(0.2)
    return cond()


def main():
    setup, sample, out = sys.argv[1], sys.argv[2], os.path.abspath(sys.argv[3])
    shutil.rmtree(out, ignore_errors=True); os.makedirs(out)
    inst, data, rel = os.path.join(out, "inst", "Glance"), os.path.join(out, "data"), os.path.join(out, "release")
    upd = os.path.join(data, "update")
    os.makedirs(rel)
    env = dict(os.environ, GLANCE_SETUP_DIR=inst, GLANCE_SETUP_LNK=os.path.join(out, "inst", "Glance.lnk"), GLANCE_DATA_DIR=data,
               GLANCE_QUIET="1", GLANCE_UPDATE_URL="file:///" + rel.replace("\\", "/") + "/", GLANCE_UPDATE_NOW="1")
    exe = os.path.join(inst, "Glance.exe")
    marker = os.path.join(inst, "THIRD-PARTY-NOTICES.txt")           # deleted before each run: its return proves a reinstall
    C = "Software\\Classes\\"
    menu_key = C + "SystemFileAssociations\\.stl\\shell\\Glance\\command"
    props = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Directory.Build.props"), encoding="utf-8").read()
    current = props.split("<Version>")[1].split("</Version>")[0]

    shutil.copy(setup, os.path.join(rel, "Glance.Setup.exe"))
    good = hashlib.sha256(open(setup, "rb").read()).hexdigest().upper()

    def publish(version, digest):
        with open(os.path.join(rel, "version.txt"), "w") as f:
            f.write("%s\n%s\n" % (version, digest))

    def fresh():
        shutil.rmtree(upd, ignore_errors=True)
        if os.path.exists(marker):
            os.remove(marker)

    def session():
        return T.Session(exe, sample, env, out)

    def close(s, linger=None):
        """Close the window; keep the desktop (and its kill-on-close job) alive while the installer finishes."""
        s.post(T.WM_CLOSE)
        code = HiddenDesktop.wait(s.hproc, 10)
        if linger:
            wait_for(linger, 40)
            time.sleep(1.0)
        s.desk.close()
        return code

    print("== install the starting copy (right-click entry switched off) ==")
    check("starting copy installs", S.run(setup, ["--quiet", "--no-menu"], env, 120) == 0 and os.path.exists(exe))
    check("its choices are as given", S.reg(C + ".stl") == "Glance.Model.stl" and S.reg(menu_key) is None)

    print("== 1: the published version is the one already installed ==")
    fresh(); publish(current, good)
    s = session()
    looked = wait_for(lambda: os.path.exists(os.path.join(upd, "checked.txt")), 20)
    time.sleep(1.0)
    check("Glance looks for a newer version after it has opened", looked)
    check("nothing is downloaded when there is none", not os.path.exists(os.path.join(upd, "ready.txt")) and not os.path.exists(os.path.join(upd, "Glance.Setup.exe")))
    check("closing leaves the install alone", close(s) == 0 and not os.path.exists(marker))

    print("== 2: a newer version whose installer does not match its published SHA-256 ==")
    fresh(); publish("9.9.9", "0" * 64)
    s = session()
    wait_for(lambda: os.path.exists(os.path.join(upd, "checked.txt")), 20)
    time.sleep(3.0)
    left = sorted(os.listdir(upd)) if os.path.exists(upd) else []
    check("a download that fails the check is thrown away", "ready.txt" not in left and "Glance.Setup.exe" not in left and "Glance.Setup.exe.part" not in left, str(left))
    check("and is never installed", close(s) == 0 and not os.path.exists(marker))

    print("== 3: a newer version, published properly ==")
    fresh(); publish("9.9.9", good)
    s = session()
    ready = wait_for(lambda: os.path.exists(os.path.join(upd, "ready.txt")), 40)
    check("the new installer is downloaded and verified in the background", ready and os.path.exists(os.path.join(upd, "Glance.Setup.exe")))
    time.sleep(1.0)
    check("nothing is replaced while Glance is open", not os.path.exists(marker) and s.shot("still-open")["tris"] != "0")
    code = close(s, linger=lambda: os.path.exists(marker))
    log = os.path.join(upd, "update.log")
    check("closing Glance installs the update", code == 0 and os.path.exists(marker) and os.path.exists(exe),
          "exit %s; log: %s" % (code, " | ".join(open(log).read().split()) if os.path.exists(log) else "none"))
    check("the update keeps the choices made at install", S.reg(C + ".stl") == "Glance.Model.stl" and S.reg(menu_key) is None, "menu entry: %s" % S.reg(menu_key))
    bmp = os.path.join(out, "after-update.bmp")
    code = S.run(exe, [sample, "--snapshot", bmp], env)
    check("the updated Glance opens a model", code == 0 and os.path.exists(bmp + ".txt") and "tris=" in open(bmp + ".txt").read())

    print("== 4: a second Glance window is still open ==")
    fresh(); publish("9.9.9", good)
    a = session()
    wait_for(lambda: os.path.exists(os.path.join(upd, "ready.txt")), 40)
    b = session()
    a.post(T.WM_CLOSE); HiddenDesktop.wait(a.hproc, 10); time.sleep(6.0)
    check("the update waits rather than closing the other window", not os.path.exists(marker) and b.shot("other-window")["tris"] != "0")
    a.desk.close()
    code = close(b, linger=lambda: os.path.exists(marker))
    check("and goes in once the last window is closed", os.path.exists(marker))

    print("== 5: updates switched off in settings.ini ==")
    fresh()
    ini = os.path.join(data, "settings.ini")
    lines = [l for l in open(ini).read().splitlines() if not l.startswith("autoUpdate=")] + ["autoUpdate=0"]
    open(ini, "w").write("\n".join(lines) + "\n")
    s = session()
    time.sleep(4.0)
    check("autoUpdate=0 stops Glance looking at all", not os.path.exists(os.path.join(upd, "checked.txt")))
    check("and the setting survives closing", close(s) == 0 and "autoUpdate=0" in open(ini).read())

    S.run(exe, ["--uninstall", "--quiet"], env, linger=lambda: not os.path.exists(inst))
    failed = [n for n, ok in S.results if not ok]
    print("\n%d checks, %d failed" % (len(S.results), len(failed)))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
