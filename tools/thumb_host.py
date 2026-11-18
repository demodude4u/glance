"""A stand-in for the process Windows loads a thumbnail handler into.

  python tools/thumb_host.py <jobs.json> <results.json>

Run it through tools/hidden_thumbs.py, which starts it on a hidden desktop. It does to GlanceThumbs.dll what
Explorer's helper process does: load the DLL, ask DllGetClassObject for a class factory, create a handler object,
hand it a file (as a stream, the only way Explorer's helper does it; or as a path or a shell item, which other
programs may use), call IThumbnailProvider::GetThumbnail and take the bitmap that comes back.

Each job is {"name", "file", "format" (index into ThumbTypes.Formats), "px", "how", "png"}; "how" is one of
  stream    DllGetClassObject -> IClassFactory -> IInitializeWithStream on the shell's own file stream
  file      the same, through IInitializeWithFile
  item      the same, through IInitializeWithItem
  cocreate  CoCreateInstance by class id, which goes through the registry (needs the handler registered)
  shell     IShellItem::BindToHandler(BHID_ThumbnailHandler): the shell looks up the handler registered for the
            file's type and initializes it itself, in this process (needs the handler registered)
"movable": true checks afterwards that the file can be renamed (nothing holds it), "again": N asks the same
object for a second picture of N pixels. A job with "threads": N runs N copies at once. "hold": seconds keeps the process (and the loaded DLL) alive after
the jobs (or until the file named by "until" appears), "touch" names a file written when the jobs are done, and
"after" is one more job run once the hold is over.
"""
import ctypes
import json
import os
import sys
import threading
import time
from ctypes import POINTER, byref, c_int, c_long, c_uint, c_ulong, c_void_p, c_wchar_p

from PIL import Image

ole32 = ctypes.WinDLL("ole32")
shlwapi = ctypes.WinDLL("shlwapi")
shell32 = ctypes.WinDLL("shell32")
gdi32 = ctypes.WinDLL("gdi32")


class GUID(ctypes.Structure):
    _fields_ = [("a", c_ulong), ("b", ctypes.c_ushort), ("c", ctypes.c_ushort), ("d", ctypes.c_ubyte * 8)]

    def __init__(self, text):
        super().__init__()
        ole32.CLSIDFromString(c_wchar_p(text), byref(self))


IClassFactory = GUID("{00000001-0000-0000-C000-000000000046}")
IThumbnailProvider = GUID("{E357FCCD-A995-4576-B01F-234630154E96}")
IInitializeWithStream = GUID("{B824B49D-22AC-4161-AC8A-9916E8FA3F7F}")
IInitializeWithFile = GUID("{B7D14566-0509-4CCE-A71F-0A554233BD9B}")
IInitializeWithItem = GUID("{7F73BE3F-FB79-493C-A6C7-7EE14E245841}")
IShellItem = GUID("{43826D1E-E718-42EE-BC55-A1E261C37BFE}")
IPersist = GUID("{0000010C-0000-0000-C000-000000000046}")
BHID_ThumbnailHandler = GUID("{7B2E650A-8E20-4F4A-B09E-6597AFC72FB0}")
CLSID_BASE = "{4614499B-2F56-4003-83C3-D6115D33A5%02X}"

shlwapi.SHCreateStreamOnFileEx.argtypes = [c_wchar_p, c_ulong, c_ulong, c_int, c_void_p, POINTER(c_void_p)]
shell32.SHCreateItemFromParsingName.argtypes = [c_wchar_p, c_void_p, POINTER(GUID), POINTER(c_void_p)]
ole32.CoCreateInstance.argtypes = [POINTER(GUID), c_void_p, c_ulong, POINTER(GUID), POINTER(c_void_p)]
gdi32.GetObjectW.argtypes = [c_void_p, c_int, c_void_p]
gdi32.DeleteObject.argtypes = [c_void_p]


def method(obj, index, *argtypes):
    """Calls entry `index` of a COM object's function table."""
    table = ctypes.cast(obj, POINTER(POINTER(c_void_p))).contents
    fn = ctypes.WINFUNCTYPE(c_long, c_void_p, *argtypes)(table[index])
    return lambda *args: fn(obj, *args)


def release(obj):
    if obj:
        return method(obj, 2)() & 0xFFFFFFFF
    return 0


def query(obj, iid):
    out = c_void_p()
    hr = method(obj, 0, POINTER(GUID), POINTER(c_void_p))(byref(iid), byref(out))
    return hr, out


def hx(hr):
    return "0x%08X" % (hr & 0xFFFFFFFF)


class BITMAP(ctypes.Structure):
    _fields_ = [("type", c_long), ("width", c_long), ("height", c_long), ("widthBytes", c_long),
                ("planes", ctypes.c_ushort), ("bitsPixel", ctypes.c_ushort), ("bits", c_void_p)]


class DIBSECTION(ctypes.Structure):
    _fields_ = [("bm", BITMAP), ("header", ctypes.c_byte * 40), ("fields", c_ulong * 3), ("section", c_void_p), ("offset", c_ulong)]


def provider_for(dll, job, steps):
    """Returns (hr, IThumbnailProvider) for the job, initialized with its file."""
    how, path = job["how"], job["file"]
    clsid = GUID(CLSID_BASE % job.get("format", 0))
    provider = c_void_p()

    if how == "shell":
        item = c_void_p()
        hr = shell32.SHCreateItemFromParsingName(path, None, byref(IShellItem), byref(item))
        if hr < 0:
            return hr, None
        hr = method(item, 3, c_void_p, POINTER(GUID), POINTER(GUID), POINTER(c_void_p))(None, byref(BHID_ThumbnailHandler), byref(IThumbnailProvider), byref(provider))
        release(item)
        steps.append("BindToHandler %s" % hx(hr))
        return hr, provider if hr >= 0 else None

    if how == "cocreate":
        hr = ole32.CoCreateInstance(byref(clsid), None, 1, byref(IThumbnailProvider), byref(provider))       # in-process server
        steps.append("CoCreateInstance %s" % hx(hr))
        if hr < 0:
            return hr, None
        init = "stream"
    else:
        factory = c_void_p()
        hr = dll.DllGetClassObject(byref(clsid), byref(IClassFactory), byref(factory))
        steps.append("DllGetClassObject %s" % hx(hr))
        if hr < 0:
            return hr, None
        hr = method(factory, 3, c_void_p, POINTER(GUID), POINTER(c_void_p))(None, byref(IThumbnailProvider), byref(provider))
        release(factory)
        steps.append("CreateInstance %s" % hx(hr))
        if hr < 0:
            return hr, None
        init = how

    if init == "stream":
        hr, with_stream = query(provider, IInitializeWithStream)
        if hr >= 0:
            stream = c_void_p()
            hr = shlwapi.SHCreateStreamOnFileEx(path, 0x40, 0, 0, None, byref(stream))       # read, deny nobody
            if hr >= 0:
                hr = method(with_stream, 3, c_void_p, c_ulong)(stream, 0)
                release(stream)                 # the handler holds its own reference for as long as it needs the file
            release(with_stream)
    elif init == "file":
        hr, with_file = query(provider, IInitializeWithFile)
        if hr >= 0:
            hr = method(with_file, 3, c_wchar_p, c_ulong)(path, 0)
            release(with_file)
    else:
        hr, with_item = query(provider, IInitializeWithItem)
        if hr >= 0:
            item = c_void_p()
            hr = shell32.SHCreateItemFromParsingName(path, None, byref(IShellItem), byref(item))
            if hr >= 0:
                hr = method(with_item, 3, c_void_p, c_ulong)(item, 0)
                release(item)
            release(with_item)
    steps.append("Initialize(%s) %s" % (init, hx(hr)))
    if hr < 0:
        release(provider)
        return hr, None
    return hr, provider


def log_size():
    try:
        return os.path.getsize(os.environ.get("GLANCE_THUMB_LOG", ""))
    except OSError:
        return 0


def thumbnail(dll, job):
    steps, result = [], {"name": job["name"], "ok": False}
    logged = log_size()
    t0 = time.perf_counter()
    hr, provider = provider_for(dll, job, steps)
    if provider is not None:
        hbmp, alpha = c_void_p(), c_uint(99)
        hr = method(provider, 3, c_uint, POINTER(c_void_p), POINTER(c_uint))(job["px"], byref(hbmp), byref(alpha))
        result["ms"] = (time.perf_counter() - t0) * 1000
        steps.append("GetThumbnail %s" % hx(hr))
        if job.get("again") and hr >= 0:        # a second picture from the same object, at another size
            h2, a2 = c_void_p(), c_uint()
            hr2 = method(provider, 3, c_uint, POINTER(c_void_p), POINTER(c_uint))(job["again"], byref(h2), byref(a2))
            result["again"] = hr2 >= 0 and bool(h2.value)
            if h2.value:
                gdi32.DeleteObject(h2)
        result["left"] = release(provider)      # 0: the object is gone, and with it everything it held
        if hr >= 0 and hbmp.value:
            dib = DIBSECTION()
            got = gdi32.GetObjectW(hbmp, ctypes.sizeof(DIBSECTION), byref(dib))
            w, h = dib.bm.width, abs(dib.bm.height)
            result.update(w=w, h=h, bits=dib.bm.bitsPixel, dib=got == ctypes.sizeof(DIBSECTION), alpha=alpha.value)
            if got and dib.bm.bits and dib.bm.bitsPixel == 32:
                raw = ctypes.string_at(dib.bm.bits, w * h * 4)
                # Colour already multiplied by alpha is what Explorer wants: no channel may exceed alpha.
                px = Image.frombuffer("RGBA", (w, h), raw, "raw", "BGRA", 0, 1)
                r, g, b, a = px.split()
                over = sum(1 for c in (r, g, b) for v, av in zip(c.getdata(), a.getdata()) if v > av + 1) if w * h <= 300 * 300 else 0
                hist = a.histogram()
                result.update(premultiplied=over == 0, opaque=hist[255] / (w * h), clear=hist[0] / (w * h))
                if job.get("png"):
                    Image.frombuffer("RGBA", (w, h), raw, "raw", "BGRa", 0, 1).save(job["png"])      # PNG wants colour not multiplied
                result["ok"] = True
            gdi32.DeleteObject(hbmp)
    result.setdefault("ms", (time.perf_counter() - t0) * 1000)
    result["hr"] = hx(hr)
    result["steps"] = steps
    if job.get("movable"):                      # nothing may still hold the file: renaming it must work
        try:
            os.rename(job["file"], job["file"] + ".moved"); os.rename(job["file"] + ".moved", job["file"])
            result["movable"] = True
        except OSError as e:
            result["movable"] = False; result["moveError"] = str(e)
    if os.environ.get("GLANCE_THUMB_LOG") and job.get("threads", 1) == 1:
        try:
            with open(os.environ["GLANCE_THUMB_LOG"], encoding="utf-8") as f:
                f.seek(logged)
                result["log"] = f.read().strip()
        except OSError:
            result["log"] = ""
    return result


def main():
    jobs = json.load(open(sys.argv[1], encoding="utf-8"))
    out = {"results": [], "pid": os.getpid()}
    ole32.CoInitializeEx(None, 2)               # an apartment thread, as Explorer's helper uses for handlers
    dll = None
    if jobs.get("dll"):
        dll = ctypes.WinDLL(jobs["dll"])
        dll.DllGetClassObject.argtypes = [POINTER(GUID), POINTER(GUID), POINTER(c_void_p)]
        dll.DllGetClassObject.restype = c_long
        dll.DllCanUnloadNow.restype = c_long
        out["canUnload"] = dll.DllCanUnloadNow()

    def save():
        with open(sys.argv[2] + ".tmp", "w", encoding="utf-8") as f:
            json.dump(out, f, indent=1)
        os.replace(sys.argv[2] + ".tmp", sys.argv[2])

    for job in jobs["jobs"]:
        n = job.get("threads", 1)
        if n == 1:
            out["results"].append(thumbnail(dll, job))
        else:
            got = [None] * n

            def run(i):
                ole32.CoInitializeEx(None, 2)
                got[i] = thumbnail(dll, dict(job, name="%s#%d" % (job["name"], i), png=None))
            t0 = time.perf_counter()
            threads = [threading.Thread(target=run, args=(i,)) for i in range(n)]
            [t.start() for t in threads]
            [t.join() for t in threads]
            out["results"].append({"name": job["name"], "ok": all(g and g["ok"] for g in got), "ms": (time.perf_counter() - t0) * 1000, "each": got})
        save()                                   # after every job, so a crash still leaves what was done
    if jobs.get("touch"):
        open(jobs["touch"], "w").close()
    end = time.time() + jobs.get("hold", 0)
    while time.time() < end and not (jobs.get("until") and os.path.exists(jobs["until"])):
        time.sleep(0.1)
    if jobs.get("after"):                        # one more job after the hold: the DLL has been loaded all this time
        out["results"].append(thumbnail(dll, jobs["after"]))
        save()
    return 0


if __name__ == "__main__":
    sys.exit(main())
