"""Writes small sample models used to test Glance: a torus knot (binary STL), a bracket-like
ASCII STL, and an OBJ with materials and no normals."""
import math
import os
import struct
import sys

out = sys.argv[1]
os.makedirs(out, exist_ok=True)


def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a):
    l = math.sqrt(sum(x * x for x in a)) or 1.0
    return (a[0] / l, a[1] / l, a[2] / l)


def knot(nu=420, nv=28, p=2, q=3, R=30.0, r=12.0, tube=5.0):
    def c(t):
        return ((R + r * math.cos(q * t)) * math.cos(p * t), (R + r * math.cos(q * t)) * math.sin(p * t), r * math.sin(q * t))
    rings = []
    for i in range(nu):
        t = 2 * math.pi * i / nu
        o = c(t)
        tan = norm(sub(c(t + 1e-3), c(t - 1e-3)))
        n = norm(cross(tan, (0, 0, 1)))
        b = cross(tan, n)
        ring = []
        for j in range(nv):
            a = 2 * math.pi * j / nv
            ring.append(tuple(o[k] + tube * (math.cos(a) * n[k] + math.sin(a) * b[k]) for k in range(3)))
        rings.append(ring)
    tris = []
    for i in range(nu):
        a, b = rings[i], rings[(i + 1) % nu]
        for j in range(nv):
            j2 = (j + 1) % nv
            tris.append((a[j], b[j], b[j2]))
            tris.append((a[j], b[j2], a[j2]))
    return tris


def box(x0, y0, z0, x1, y1, z1):
    v = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    q = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    t = []
    for a, b, c, d in q:
        t.append((v[a], v[b], v[c])); t.append((v[a], v[c], v[d]))
    return t


def write_binary_stl(path, tris, header=b"Glance sample"):
    with open(path, "wb") as f:
        f.write(header.ljust(80, b"\0"))
        f.write(struct.pack("<I", len(tris)))
        for a, b, c in tris:
            n = norm(cross(sub(b, a), sub(c, a)))
            f.write(struct.pack("<12fH", *n, *a, *b, *c, 0))


def write_ascii_stl(path, tris):
    with open(path, "w") as f:
        f.write("solid bracket\n")
        for a, b, c in tris:
            n = norm(cross(sub(b, a), sub(c, a)))
            f.write(f" facet normal {n[0]:e} {n[1]:e} {n[2]:e}\n  outer loop\n")
            for p in (a, b, c):
                f.write(f"   vertex {p[0]:e} {p[1]:e} {p[2]:e}\n")
            f.write("  endloop\n endfacet\n")
        f.write("endsolid bracket\n")


def write_obj(path):
    # A lathe-turned vase (Y up), quads, two materials, no normals: exercises smoothing and colours.
    seg, rows = 48, 40
    prof = []
    for i in range(rows + 1):
        t = i / rows
        prof.append((8 + 6 * math.sin(t * 3.4) + 3 * t, 40 * t))
    with open(os.path.splitext(path)[0] + ".mtl", "w") as f:
        f.write("newmtl body\nKd 0.0 0.278 0.671\nnewmtl band\nKd 1.0 0.757 0.027\n")
    with open(path, "w") as f:
        f.write("mtllib " + os.path.basename(os.path.splitext(path)[0]) + ".mtl\n")
        for r, y in prof:
            for j in range(seg):
                a = 2 * math.pi * j / seg
                f.write(f"v {r * math.cos(a):.5f} {y:.5f} {r * math.sin(a):.5f}\n")
        f.write(f"v 0 0 0\n")
        for i in range(rows):
            f.write("usemtl band\n" if 16 <= i < 20 else "usemtl body\n")
            for j in range(seg):
                a = i * seg + j + 1; b = i * seg + (j + 1) % seg + 1
                c = (i + 1) * seg + (j + 1) % seg + 1; d = (i + 1) * seg + j + 1
                f.write(f"f {a} {d} {c} {b}\n")
        centre = (rows + 1) * seg + 1
        f.write("usemtl body\n")
        for j in range(seg):
            f.write(f"f {centre} {j + 1} {(j + 1) % seg + 1}\n")


def plate_with_holes():
    """80 x 50 x 10 plate with a 12 mm hole at (20, 25) and an 8 mm hole at (60, 25): known values for the measuring tests."""
    tris, n, h = [], 64, 10.0
    for cx, r, x0, x1 in ((20.0, 6.0, 0.0, 40.0), (60.0, 4.0, 40.0, 80.0)):
        cy, hw, hh = 25.0, 20.0, 25.0
        corner = math.atan2(hh, hw)
        angles = sorted(set([2 * math.pi * i / n for i in range(n)] + [corner, math.pi - corner, math.pi + corner, 2 * math.pi - corner]))
        ring, rim = [], []
        for a in angles:
            c, s_ = math.cos(a), math.sin(a)
            k = min(hw / abs(c) if abs(c) > 1e-12 else 1e30, hh / abs(s_) if abs(s_) > 1e-12 else 1e30)
            ring.append((cx + r * c, cy + r * s_))
            rim.append((min(max(cx + k * c, x0), x1), min(max(cy + k * s_, 0.0), 50.0)))
        m = len(angles)
        for i in range(m):
            j = (i + 1) % m
            a0, a1, b0, b1 = ring[i], ring[j], rim[i], rim[j]
            for z, up in ((h, True), (0.0, False)):                  # top and bottom faces
                q = [(a0[0], a0[1], z), (b0[0], b0[1], z), (b1[0], b1[1], z), (a1[0], a1[1], z)]
                if not up: q.reverse()
                tris.append((q[0], q[1], q[2])); tris.append((q[0], q[2], q[3]))
            q = [(a0[0], a0[1], 0.0), (a0[0], a0[1], h), (a1[0], a1[1], h), (a1[0], a1[1], 0.0)]      # hole wall
            tris.append((q[0], q[1], q[2])); tris.append((q[0], q[2], q[3]))
            if abs(b0[0] - 40.0) < 1e-9 and abs(b1[0] - 40.0) < 1e-9:
                continue                                                 # the seam between the two halves is not a wall
            q = [(b0[0], b0[1], 0.0), (b1[0], b1[1], 0.0), (b1[0], b1[1], h), (b0[0], b0[1], h)]      # outer wall
            tris.append((q[0], q[1], q[2])); tris.append((q[0], q[2], q[3]))
    return tris


write_binary_stl(os.path.join(out, "plate-holes.stl"), plate_with_holes())
inch = [tuple(tuple(c / 25.4 for c in v) for v in t) for t in plate_with_holes()]
write_binary_stl(os.path.join(out, "plate-inch-declared.stl"), inch, b"alibre stl binary : Units=Inch")
write_binary_stl(os.path.join(out, "plate-inch-undeclared.stl"), inch)
write_binary_stl(os.path.join(out, "knot.stl"), knot())
bracket = box(0, 0, 0, 60, 40, 6) + box(0, 0, 6, 6, 40, 46) + box(6, 17, 6, 40, 23, 30)
write_ascii_stl(os.path.join(out, "bracket-ascii.stl"), bracket)
write_obj(os.path.join(out, "vase.obj"))
write_binary_stl(os.path.join(out, "knot-large.stl"), knot(nu=4200, nv=120))
print("ok")
