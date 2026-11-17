"""Writes small sample models used to test Glance: a torus knot (binary STL), a bracket-like
ASCII STL, an OBJ with materials and no normals, and 3MF, PLY and glTF files of known size."""
import base64
import json
import math
import os
import struct
import sys
import zipfile

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
# The same plate drawn in sixteenths of an inch: 5 x 3 1/8 x 5/8 in, holes of 3/4 and 1/2 in, 2 1/2 in apart.
# Every one of those is a fraction; the corner-to-corner diagonal (5.8962 in) is not. For the fraction tests.
sixteenths = [tuple(tuple(c / 16.0 for c in v) for v in t) for t in plate_with_holes()]
write_binary_stl(os.path.join(out, "plate-fraction.stl"), sixteenths, b"alibre stl binary : Units=Inch")
write_binary_stl(os.path.join(out, "knot.stl"), knot())
bracket = box(0, 0, 0, 60, 40, 6) + box(0, 0, 6, 6, 40, 46) + box(6, 17, 6, 40, 23, 30)
write_ascii_stl(os.path.join(out, "bracket-ascii.stl"), bracket)
write_obj(os.path.join(out, "vase.obj"))
write_binary_stl(os.path.join(out, "knot-large.stl"), knot(nu=4200, nv=120))


# ───────────────────────── 3MF, PLY and glTF ─────────────────────────
# Every size below is checked by tools/hidden_test.py: change one and change the other.

def box_mesh(x0, y0, z0, x1, y1, z1):
    """Eight corners and six outward-facing quads."""
    v = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    return v, [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]


def faceted_box(x0, y0, z0, x1, y1, z1):
    """The same box with four vertices of its own per face, so each face carries its own normal."""
    v, quads = box_mesh(x0, y0, z0, x1, y1, z1)
    pos, nrm, tri = [], [], []
    for q in quads:
        n = norm(cross(sub(v[q[1]], v[q[0]]), sub(v[q[2]], v[q[0]])))
        b = len(pos)
        pos += [v[i] for i in q]; nrm += [n] * 4
        tri += [(b, b + 1, b + 2), (b, b + 2, b + 3)]
    return pos, nrm, tri


CORE = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02"


def mesh_xml(v, quads, pre="", extra=""):
    out = ["<%smesh><%svertices>" % (pre, pre)]
    out += ['<%svertex x="%g" y="%g" z="%g"/>' % ((pre,) + p) for p in v]
    out.append("</%svertices><%striangles>" % (pre, pre))
    for a, b, c, d in quads:
        out.append('<%striangle v1="%d" v2="%d" v3="%d"%s/>' % (pre, a, b, c, extra))
        out.append('<%striangle v1="%d" v2="%d" v3="%d"%s/>' % (pre, a, c, d, extra))
    out.append("</%striangles></%smesh>" % (pre, pre))
    return "\n".join(out)


def write_3mf_assembly(path):
    """Millimeters. A 20 x 10 x 5 block is placed three times and a post from a second model part once, through
    components and build items with transforms: 48 triangles, 40 x 50 x 35 mm over all. Materials, a colour group
    and a slice stack (whose 2D vertices must not be read as mesh vertices) are there to be stepped over."""
    block = mesh_xml(*box_mesh(0, 0, 0, 20, 10, 5), extra=' pid="10" p1="0"')
    post = mesh_xml(*box_mesh(0, 0, 0, 4, 4, 30))
    main = """<?xml version="1.0" encoding="UTF-8"?>
<!-- a comment with a <vertex x="900" y="900" z="900"/> in it -->
<model unit="millimeter" xml:lang="en-US" requiredextensions="p" xmlns="%s"
 xmlns:m="http://schemas.microsoft.com/3dmanufacturing/material/2015/02"
 xmlns:p="http://schemas.microsoft.com/3dmanufacturing/production/2015/06"
 xmlns:s="http://schemas.microsoft.com/3dmanufacturing/slice/2015/07">
 <metadata name="Title">Glance sample: 5 > 4 &amp; "quotes"</metadata>
 <resources>
  <basematerials id="10"><base name="Cobalt" displaycolor="#0047AB"/></basematerials>
  <m:colorgroup id="11"><m:color color="#FFC107"/></m:colorgroup>
  <s:slicestack id="12" zbottom="0"><s:slice ztop="1"><s:vertices><s:vertex x="0" y="0"/><s:vertex x="500" y="500"/></s:vertices>
   <s:polygon startv="0"><s:segment v2="1"/></s:polygon></s:slice></s:slicestack>
  <object id="1" type="model" pid="10" pindex="0" name="block, a > b">
%s
  </object>
  <object id="3" type="model">
   <components>
    <component objectid="1"/>
    <component objectid="1" transform="0 1 0 -1 0 0 0 0 1 40 0 0"/>
    <component objectid="2" p:path="/3D/Objects/post.model" transform="1 0 0 0 1 0 0 0 1 0 0 5"/>
   </components>
  </object>
 </resources>
 <build>
  <item objectid="3" transform="1 0 0 0 1 0 0 0 1 100 50 0"/>
  <item objectid="1" transform="2 0 0 0 2 0 0 0 2 100 80 0"/>
 </build>
</model>
""" % (CORE, block)
    part = ('<?xml version="1.0" encoding="UTF-8"?>\n<model unit="millimeter" xmlns="%s"><resources><object id="2" type="model">\n%s\n'
            '</object></resources><build/></model>\n' % (CORE, post))
    rels = ('<?xml version="1.0" encoding="UTF-8"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Target="/3D/main.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>')
    types = ('<?xml version="1.0" encoding="UTF-8"?>\n<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
             '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
             '<Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>')
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", types)
        z.writestr("_rels/.rels", rels)
        z.writestr("3D/main.model", main)
        z.writestr("3D/Objects/post.model", part)


def write_3mf_inch(path):
    """Inches, stored without compression, no relationships part, and the core elements under a namespace prefix:
    a 2 x 1 x 0.5 in block, which is 50.8 x 25.4 x 12.7 mm, 12 triangles."""
    body = mesh_xml(*box_mesh(0, 0, 0, 2, 1, 0.5), pre="c:")
    xml = ('<?xml version="1.0" encoding="UTF-8"?>\n<c:model unit="inch" xmlns:c="%s"><c:resources><c:object id="1" type="model">\n%s\n'
           '</c:object></c:resources><c:build><c:item objectid="1"/></c:build></c:model>\n' % (CORE, body))
    with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as z:
        z.writestr("3D/3dmodel.model", xml)


def write_ply_ascii(path):
    """Text: a 30 x 20 x 10 box as six quads (12 triangles once fanned), a colour per vertex, no normals."""
    v, quads = box_mesh(0, 0, 0, 30, 20, 10)
    with open(path, "w", newline="\n") as f:
        f.write("ply\nformat ascii 1.0\ncomment Glance sample\nelement vertex 8\nproperty float x\nproperty float y\nproperty float z\n"
                "property uchar red\nproperty uchar green\nproperty uchar blue\nelement face 6\nproperty list uchar int vertex_indices\nend_header\n")
        for i, p in enumerate(v):
            f.write("%g %g %g %d %d %d\n" % (p + ((0, 71, 171) if i < 4 else (255, 193, 7))))
        for q in quads:
            f.write("4 %d %d %d %d\n" % q)


def write_ply_binary(path, big=False):
    """Binary. Little-endian: a 60 x 40 x 20 box, float positions with normals and colours, a property and a whole
    element Glance has no use for, faces as triangles. Big-endian: a 3 x 2 x 1 box (small enough to be read as
    inches), double positions, faces as quads with 16-bit indices under the older name vertex_index."""
    e = ">" if big else "<"
    with open(path, "wb") as f:
        if big:
            v, quads = box_mesh(0, 0, 0, 3, 2, 1)
            f.write(b"ply\r\nformat binary_big_endian 1.0\r\nelement vertex 8\r\nproperty double x\r\nproperty double y\r\nproperty double z\r\n"
                    b"element face 6\r\nproperty uchar flags\r\nproperty list uchar short vertex_index\r\nend_header\r\n")
            for p in v:
                f.write(struct.pack(e + "3d", *p))
            for q in quads:
                f.write(struct.pack(e + "BB4h", 7, 4, *q))
        else:
            pos, nrm, tri = faceted_box(0, 0, 0, 60, 40, 20)
            f.write(b"ply\nformat binary_little_endian 1.0\nelement vertex 24\nproperty float x\nproperty float y\nproperty float z\n"
                    b"property float quality\nproperty float nx\nproperty float ny\nproperty float nz\n"
                    b"property uchar red\nproperty uchar green\nproperty uchar blue\nproperty uchar alpha\n"
                    b"element edge 2\nproperty int vertex1\nproperty int vertex2\nproperty list uchar float weights\n"
                    b"element face 12\nproperty list uchar uint vertex_indices\nend_header\n")
            for p, n in zip(pos, nrm):
                f.write(struct.pack(e + "3f f 3f 4B", *p, 0.5, *n, 0, 71, 171, 255))
            f.write(struct.pack(e + "iiB2f", 0, 1, 2, 0.25, 0.75)); f.write(struct.pack(e + "iiB", 1, 2, 0))
            for t in tri:
                f.write(struct.pack(e + "B3I", 3, *t))


def write_ply_strips(path):
    """Text, faces as triangle strips: a flat 20 x 10 plate in two strips of five quads each, 20 triangles."""
    with open(path, "w", newline="\n") as f:
        f.write("ply\nformat ascii 1.0\nelement vertex 18\nproperty float x\nproperty float y\nproperty float z\n"
                "element tristrips 1\nproperty list int int vertex_indices\nend_header\n")
        for j in range(3):
            for i in range(6):
                f.write("%g %g 0\n" % (i * 4, j * 5))
        rows = [[k for i in range(6) for k in (j * 6 + i, (j + 1) * 6 + i)] for j in range(2)]
        f.write("25 %s -1 %s\n" % (" ".join(map(str, rows[0])), " ".join(map(str, rows[1]))))


def gltf_views(parts):
    """Packs byte strings into one buffer, each on a 4-byte boundary; returns the buffer and the bufferViews."""
    blob, views = b"", []
    for data in parts:
        blob += b"\0" * (-len(blob) % 4)
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)})
        blob += data
    return blob, views


def write_glb(path):
    """One file, meters, Y up. A 20 x 30 x 10 mm block (normals, 16-bit indices) is used by two nodes: once under a
    parent that scales by 2, turns a quarter turn about Y and moves, once moved by a matrix. 24 triangles;
    120 x 160 x 150 mm in the file's own axes, so 120 wide, 150 deep and 160 tall once it stands up."""
    pos, nrm, tri = faceted_box(0, 0, 0, 0.02, 0.03, 0.01)
    blob, views = gltf_views([b"".join(struct.pack("<3f", *p) for p in pos), b"".join(struct.pack("<3f", *n) for n in nrm),
                              b"".join(struct.pack("<3H", *t) for t in tri)])
    h = math.sqrt(0.5)
    doc = {
        "asset": {"version": "2.0", "generator": "Glance make_samples"},
        "scene": 0, "scenes": [{"nodes": [0, 2]}],
        "nodes": [
            {"name": "turned", "translation": [0.1, 0, 0], "rotation": [0, h, 0, h], "scale": [2, 2, 2], "children": [1]},
            {"name": "raised", "translation": [0, 0.05, 0], "mesh": 0},
            {"name": "moved", "matrix": [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0.1, 1], "mesh": 1},
        ],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1}, "indices": 2, "material": 0}]},
                   {"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1}, "indices": 2, "material": 1}]}],
        "materials": [{"pbrMetallicRoughness": {"baseColorFactor": [0.0, 0.063, 0.407, 1.0], "metallicFactor": 0.1}},
                      {"pbrMetallicRoughness": {"baseColorFactor": [1.0, 0.533, 0.002, 1.0]}}],
        "buffers": [{"byteLength": len(blob)}], "bufferViews": views,
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": 24, "type": "VEC3", "min": [0, 0, 0], "max": [0.02, 0.03, 0.01]},
            {"bufferView": 1, "componentType": 5126, "count": 24, "type": "VEC3"},
            {"bufferView": 2, "componentType": 5123, "count": 36, "type": "SCALAR"},
        ],
    }
    text = json.dumps(doc).encode()
    text += b" " * (-len(text) % 4)
    blob += b"\0" * (-len(blob) % 4)
    with open(path, "wb") as f:
        f.write(struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(text) + 8 + len(blob)))
        f.write(struct.pack("<I4s", len(text), b"JSON")); f.write(text)
        f.write(struct.pack("<I4s", len(blob), b"BIN\0")); f.write(blob)


def write_gltf(path):
    """Text beside a .bin, plus a second buffer inside the text. A 50 x 20 x 30 mm block (positions and normals
    interleaved, 8-bit indices) from the .bin, and a 10 mm cube 100 mm along X with no indices or normals and a colour
    per vertex from the embedded buffer: 24 triangles, 110 x 20 x 30 mm. A node outside the scene, a line primitive
    and an extension Glance does not know are there to be ignored."""
    pos, nrm, tri = faceted_box(0, 0, 0, 0.05, 0.02, 0.03)
    inter = b"".join(struct.pack("<3f3f", *p, *n) for p, n in zip(pos, nrm))
    blob, views = gltf_views([inter, b"".join(struct.pack("<3B", *t) for t in tri), struct.pack("<2H", 0, 1)])
    views[0]["byteStride"] = 24
    with open(os.path.splitext(path)[0] + ".bin", "wb") as f:
        f.write(blob)

    cpos, _, ctri = faceted_box(0, 0, 0, 0.01, 0.01, 0.01)
    soup = [cpos[i] for t in ctri for i in t]
    data = b"".join(struct.pack("<3f", *p) for p in soup)
    colours = b"".join(struct.pack("<4B", 255, 193, 7, 255) for _ in soup)
    embedded = data + colours
    doc = {
        "asset": {"version": "2.0"}, "extensionsUsed": ["KHR_materials_emissive_strength"],
        "scene": 0, "scenes": [{"nodes": [0, 1]}],
        "nodes": [{"mesh": 0}, {"mesh": 1, "matrix": [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0.1, 0, 0, 1]},
                  {"mesh": 0, "translation": [10, 10, 10], "name": "not in the scene"}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1}, "indices": 2},
                                   {"attributes": {"POSITION": 0}, "indices": 3, "mode": 1}]},
                   {"primitives": [{"attributes": {"POSITION": 4, "COLOR_0": 5}}]}],
        "buffers": [{"uri": os.path.basename(os.path.splitext(path)[0]) + ".bin", "byteLength": len(blob)},
                    {"uri": "data:application/octet-stream;base64," + base64.b64encode(embedded).decode(), "byteLength": len(embedded)}],
        "bufferViews": views + [{"buffer": 1, "byteOffset": 0, "byteLength": len(data)},
                                {"buffer": 1, "byteOffset": len(data), "byteLength": len(colours)}],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": 24, "type": "VEC3"},
            {"bufferView": 0, "byteOffset": 12, "componentType": 5126, "count": 24, "type": "VEC3"},
            {"bufferView": 1, "componentType": 5121, "count": 36, "type": "SCALAR"},
            {"bufferView": 2, "componentType": 5123, "count": 2, "type": "SCALAR"},
            {"bufferView": 3, "componentType": 5126, "count": 36, "type": "VEC3"},
            {"bufferView": 4, "componentType": 5121, "normalized": True, "count": 36, "type": "VEC4"},
        ],
    }
    with open(path, "w", newline="\n") as f:
        json.dump(doc, f, indent=1)


write_3mf_assembly(os.path.join(out, "assembly.3mf"))
write_3mf_inch(os.path.join(out, "block-inch.3mf"))
write_ply_ascii(os.path.join(out, "box-ascii.ply"))
write_ply_binary(os.path.join(out, "box-binary.ply"))
write_ply_binary(os.path.join(out, "box-big-endian.ply"), big=True)
write_ply_strips(os.path.join(out, "plate-strips.ply"))
write_glb(os.path.join(out, "blocks.glb"))
write_gltf(os.path.join(out, "two-blocks.gltf"))
print("ok")
