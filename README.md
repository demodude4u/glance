# Glance

A small, very fast 3D model viewer for Windows 11. Double-click an STL, OBJ, STEP or IGES file and it is on
screen before you have let go of the mouse.

- **Fast.** Native code, no runtime to start. The window opens with the model already in it, about a tenth of
  a second after launch for STL and OBJ (a 1,000,000-triangle STL included).
- **STEP without the wait.** STEP and IGES are read by a native OpenCascade helper on all cores, then cached,
  so a file you have opened before comes back as fast as an STL. While you look at one model, Glance quietly
  prepares the other STEP files in the same folder.
- **Built for parts.** Click a hole to read its diameter, two faces for the distance or angle between them,
  an edge for its length. Section cut, bounding-box dimensions, mm / inch, area, volume and a watertight check.
- **Simple.** One window, four buttons. Press **F1** for the handful of keys.

## Using it

| | |
|---|---|
| Drag | Orbit |
| Right-drag (or Shift-drag) | Pan |
| Scroll | Zoom to the cursor |
| Double-click | Focus on a point (double-click empty space to fit) |
| F | Fit to window |
| 1 – 6, 0 | Front, back, left, right, top, bottom, isometric |
| P | Perspective / orthographic (also the **Ortho** button) |
| V | Shading: Shaded, Smooth, Lines, X-ray, Wire, Overhangs (also the toolbar button; Shift+V goes back) |
| E, W, S, G | Edges, wireframe, smooth / flat shading, grid |
| C, T | Color, light / dark |
| Z | Flip the up axis |
| M | Measure (see below) |
| X | Section cut along X, then Y, Z, off. Drag the slider or Ctrl+scroll to move it; Shift+X or **Flip** swaps sides |
| B | Bounding-box dimensions on the model |
| L | Lay flat: click a face and the model turns to rest on it (Z puts it back) |
| U | Show millimeters / inches |
| Shift+U | STL / OBJ only: Fix units, choose what unit the file's numbers are in |
| I | Details: size, triangles, surface area, volume, watertight |
| ← → | Previous / next model in the folder |
| R | Turntable |
| O, drag-and-drop | Open a file |
| Ctrl+C | Copy a picture of the model |
| Esc | Close |

### Measuring

Press **M** (or the Measure button) and click what you care about. Glance works out what it is:

| You click | You get |
|---|---|
| A round hole, boss or circular edge | Diameter (radius for a fillet or arc) |
| A straight edge | Length |
| A flat face | Area |
| A corner | Its X, Y, Z position |
| Two holes | Center-to-center distance |
| Two parallel faces | The distance between them (thickness, gap, step height) |
| Two faces or edges at an angle | The angle |
| A hole and a face or edge | Distance from the hole's center |
| Any two corners | Straight-line distance, with the X, Y, Z parts |

Click a third feature to start again, **Esc** to clear. Dragging still orbits while you measure.
STEP faces are the exact CAD faces;
on STL files faces are found from creases, so a hole needs to be reasonably round (12 or more sides) to be
recognised as a circle.

### Units

STEP and IGES files state their units. STL and OBJ are bare numbers, so Glance decides in this order:
your earlier choice for that file, a unit written in the file's header (for example `Units=Inch`), or a
guess: a part whose largest dimension is under 6 is read as inches, anything else as millimeters. A guess that
changed the size rings the **Fix units** button on the toolbar. Click it (or press **Shift+U**) to see how big the
part would be in millimeters, inches, centimeters, meters and feet, and pick the one that looks right; the choice is
remembered for that file.

## Install

Give people one file: `dist\Glance Setup.exe` (about 20 MB). Running it shows two boxes, both ticked:

- **Open model files with Glance**: double-clicking an STL, OBJ, STEP or IGES file opens it in Glance.
- **Add "Open in Glance" to the right-click menu**: works whichever app is set to open the file. On Windows 11
  it is under **Show more options** (or Shift+right-click); the short menu only takes signed, packaged apps.

It installs to `%LOCALAPPDATA%\Programs\Glance` for the current user only, with no administrator rights, adds a
Start menu entry, and lists Glance under Settings > Apps > Installed apps, where it can be uninstalled.
The file is not code-signed, so Windows SmartScreen may ask for confirmation the first time (More info > Run anyway).

Windows does not let any installer override a default the person has already chosen for a type. If the picker
appears for a type, choose Glance there once and click **Always**.

Unattended: `"Glance Setup.exe" --quiet [--no-associate] [--no-menu]`, and `Glance.exe --uninstall --quiet`.
From this folder, `.\build.ps1` builds everything and `.\install.ps1` / `.\uninstall.ps1` run those two commands.

## Releasing

1. Set the new number in `src\Directory.Build.props` (the only place it lives) and add a section to `CHANGELOG.md`.
2. Commit, then run `.\build.ps1` so the build carries that commit's id, and run both test suites
   (`tools\hidden_test.py`, `tools\hidden_setup.py`).
3. Tag the commit `vX.Y.Z`. Hand out `dist\Glance Setup.exe`; running it over an older Glance replaces it.

The version shows in the help card (F1) as "Version 1.1.0 (commit)", in the installer's title bar, in the file
properties of both programs and under Settings > Apps > Installed apps. Use the third number for fixes, the second
for new features.

## Building

Needs the .NET 9 SDK, Visual Studio 2022 with the C++ workload, and Python 3 with Pillow (icon and tests).
The STEP reader links against the official OpenCascade 8.0.1 Windows release, which is not kept in this folder:

1. Download `opencascade-release-no-pch.zip` and `3rdparty-vc14-64.zip` from
   <https://github.com/Open-Cascade-SAS/OCCT/releases/tag/V8.0.1>.
2. Unpack the first so that `build\deps\occt\opencascade-8.0.1-vc14-64\inc` exists.
3. From the second, copy `jemalloc.dll` and `tbb12.dll` (the release ones) into `build\deps\runtime`.

### What ships

Everything Glance needs is inside the installer, so nothing else has to be on the PC:

- **Typeface.** Roboto (Regular, Medium, Light) is built into `Glance.exe` and `Glance Setup.exe` and loaded for
  that process only; nothing is installed into Windows. The files in `src\Glance\fonts` are Roboto 2.137 with the
  family renamed to "Glance Roboto" (`tools\make_fonts.py`), so they never clash with an installed Roboto.
- **Runtime libraries.** The Microsoft C++ runtime, OpenCascade, TBB and jemalloc sit next to the STEP reader.
  `tools\check_deps.py` runs in every build and fails it if any shipped file needs a DLL that is neither shipped
  nor part of Windows.
- **Licence notices.** `licenses\` is copied into the install folder (`THIRD-PARTY-NOTICES.txt`, `Apache-2.0.txt`),
  beside the LGPL texts for OpenCascade in `step\`.

Not shipped: a software OpenGL fallback. Glance needs a graphics driver with OpenGL 3.3.

## How it is put together

| | |
|---|---|
| `src\Glance` | The viewer: C# compiled ahead-of-time to a 2 MB native exe. Hand-written Win32 window, OpenGL 3.3 renderer, STL/OBJ parsers, HUD. The graphics driver starts loading on its own thread the moment the process starts. |
| `src\StepNative` | `glance-step.exe`: C++ on OpenCascade. Reads STEP/IGES, tessellates in parallel, writes a packed mesh. Runs as a separate process so a bad CAD file can never take the viewer down. |
| `tools\build_step.py` | Builds the reader and ships only the 25 OpenCascade DLLs it needs. The codec, font and VR libraries the official build imports but the reader never calls are replaced with tiny stand-in DLLs. |
| `tools\hidden_test.py` | End-to-end test on a hidden desktop: opens every format, then drives one window with real mouse and keyboard messages and checks the result. |

Settings and the STEP cache live in `%LOCALAPPDATA%\Glance` (cache capped at 1 GB, oldest first).
Colours and type: Cobalt, Golden Amber, Slate, Charcoal and Ivory, set in Roboto.

OpenCascade is LGPL 2.1 with the Open CASCADE exception; its licence texts ship in `step\`.
