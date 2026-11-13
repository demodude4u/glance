"""Prepares the Roboto files that are built into Glance.exe and Glance Setup.exe.

  python tools/make_fonts.py <folder with Roboto-Regular/Medium/Light.ttf> src/Glance/fonts

The glyphs are untouched. Only the family name is changed, to "Glance Roboto ...", so the built-in copy
never collides with (or is mistaken for) whatever Roboto version a PC may have installed, and the tests can
tell that the built-in copy is the one in use. Roboto is licensed under Apache 2.0, which allows this;
the change is recorded in THIRD-PARTY-NOTICES.txt as the licence requires.
"""
import os
import sys

from fontTools.ttLib import TTFont

src, dst = sys.argv[1], sys.argv[2]
os.makedirs(dst, exist_ok=True)
for style, family in (("Regular", "Glance Roboto"), ("Medium", "Glance Roboto Medium"), ("Light", "Glance Roboto Light")):
    f = TTFont(os.path.join(src, "Roboto-%s.ttf" % style))
    name = f["name"]
    name.names = [n for n in name.names if n.nameID not in (16, 17, 21, 22)]      # no second route to the old family name
    for n in name.names:
        if n.nameID == 1: n.string = family
        elif n.nameID == 2: n.string = "Regular"
        elif n.nameID == 3: n.string = "Glance:" + family.replace(" ", "") + ":2.137"
        elif n.nameID == 4: n.string = family
        elif n.nameID == 6: n.string = family.replace(" ", "")
    out = os.path.join(dst, "GlanceRoboto-%s.ttf" % style)
    f.save(out)
    print("%s  %s  %d bytes" % (out, family, os.path.getsize(out)))
