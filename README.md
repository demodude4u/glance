# Glance

A small, very fast 3D model viewer for Windows 11. Double-click an STL, OBJ, 3MF, PLY, glTF, STEP or IGES file
and it is on screen before you have let go of the mouse.

- **Fast.** Native code, no runtime to start. The window opens with the model already in it, about a tenth of
  a second after launch for STL, OBJ, 3MF, PLY and glTF (a 1,000,000-triangle STL included).
- **STEP without the wait.** STEP and IGES are read by a native OpenCascade helper on all cores, then cached,
  so a file you have opened before comes back as fast as an STL. While you look at one model, Glance quietly
  prepares the other STEP files in the same folder.
- **Built for parts.** Click a hole to read its diameter, two faces for the distance or angle between them,
  an edge for its length. Section cut, bounding-box dimensions, mm / inch / inch fractions, area, volume and a
  watertight check.
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
| V | Shading: Shaded, Smooth, Studio, Metal, Lines, X-ray, Wire, Overhangs (also the toolbar button; Shift+V goes back). A mode with options shows them in a second row under the toolbar |
| Ctrl+S | Save a picture as PNG (the **Picture** button sets size and background) |
| E, W, S, G | Edges, wireframe, smooth / flat shading, grid |
| C, T | Color, light / dark |
| Z | Flip the up axis |
| M | Measure (see below) |
| X | Section cut along X, then Y, Z, off. Drag the slider or Ctrl+scroll to move it; Shift+X or **Flip** swaps sides |
| B | Bounding-box dimensions on the model |
| L | Lay flat: click a face and the model turns to rest on it (Z puts it back) |
| U | Show millimeters, inches, or inches as fractions (also the toolbar button) |
| Shift+U | STL, OBJ and PLY, and a glTF that looks too large: Fix units, choose what unit the file's numbers are in |
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

### Pictures

The **Picture** button (top right) copies or saves the current view without the toolbar, readouts or cards.
Choose a size by name: **Window** (exactly what the window shows), **HD** 1280 × 720, **Full HD** 1920 × 1080,
**QHD** 2560 × 1440, **4K** 3840 × 2160 or **Square** 2048 × 2048. The named sizes have their own shape, so while
the card is open the window dims everything outside a frame: what is inside the frame is the picture. Scroll,
drag and right-drag still move the model, to place it in the frame; a click on the view closes the card. If the
graphics card cannot draw a size, the picture is made as large as it can in the same shape and the card says so.
The background is either as shown or transparent, for dropping onto a slide or a document. **Ctrl+C** copies and
**Ctrl+S** saves with the last settings, showing the frame for a moment. For presentation pictures, choose the **Studio** or **Metal** look first:
both replace the grid and working background with a plain backdrop and add soft shadows. The clipboard has no
transparency, so a copied picture always has a background.

### Mode options

Each shading mode has options of its own. While a mode is on, they appear as a second row of smaller buttons
under the toolbar; each button shows its current choice and steps to the next one when clicked. The choices are
remembered for that mode.

| Mode | Options |
|---|---|
| Shaded, Smooth | **Color**: the same colors as C. **Edges**: on or off, the same as E (edges are what tell Shaded from Smooth) |
| Studio | **Light**: Soft, Left, Right, Top. **Backdrop**: White, Grey, Dark. **Material**: Clay, White, Charcoal, Part color (the file's colors, or the color chosen with C). **Shadow**: the shadow on the ground, on or off |
| Metal | **Finish**: Aluminum, Steel, Brass, Copper, Black. **Surface**: Polished, Satin, Matte. **Backdrop**: White, Grey, Dark. **Shadow**: on or off |
| Lines | **Hidden lines**: Off, or Faint to show the edges behind the part. **Weight**: Fine, Bold. **Paper**: White, Blueprint, Dark |
| X-ray | **Opacity**: Light, Medium, Heavy. **Edges**: on or off |
| Wire | **Lines**: All triangles on the solid, or Feature edges only with nothing filled in |
| Overhangs | **Angle**: 30°, 45° or 60° past vertical before a face is marked |

Until a backdrop or paper is chosen it follows the light / dark theme, as before. On one that does not suit the
theme (a dark backdrop in the light theme, say) the buttons turn solid so they stay readable.

### File formats

| | What Glance reads | Units |
|---|---|---|
| STL (`.stl`) | Binary and text | None in the file: see Units |
| OBJ (`.obj`) | Faces of any number of sides, normals, material colours from the `.mtl` beside it | None in the file: see Units |
| 3MF (`.3mf`) | Mesh objects, placed by components and by the build's items with their transforms, including objects kept in other model parts of the package (as slicers write them) | The unit the model states, millimeters if it states none |
| PLY (`.ply`) | Text and binary in either byte order; positions, normals and colours per vertex; faces as triangles, polygons or triangle strips | None in the file: see Units |
| glTF (`.glb`, `.gltf`) | glTF 2.0: the scene's nodes with matrix or translation / rotation / scale transforms, triangle meshes with or without indices, normals, base colour and vertex colours; data in the file, in `.bin` files beside it or embedded in the text | Meters, and Y up: shown in millimeters, standing the right way up |
| STEP (`.step`, `.stp`), IGES (`.iges`, `.igs`) | Solids and surfaces with their colours, through OpenCascade | The unit the file states |

Not read: materials, colours, textures and slices in 3MF; textures, skins, animation and morph targets in glTF
(the model is shown as it rests); glTF 1.0; point clouds. A glTF packed with Draco or meshopt compression is
refused with a message saying so: export it without compression. A PLY that holds only points says it is a point
cloud. A file that is damaged or cut short shows a message in the window, never a crash.

### Units

STEP, IGES and 3MF files state their units, and glTF is always in meters. STL, OBJ and PLY are bare numbers, so
Glance decides in this order:
your earlier choice for that file, a unit written in the file's header (for example `Units=Inch`), or a
guess: a part whose largest dimension is under 6 is read as inches, anything else as millimeters. A guess that
changed the size rings the **Fix units** button on the toolbar. Click it (or press **Shift+U**) to see how big the
part would be in millimeters, inches, centimeters, meters and feet, and pick the one that looks right; the choice is
remembered for that file.

glTF has one exception. Some CAD exporters write millimeters into a glTF file unchanged, which makes the part a
thousand times too large. A glTF model more than 10 m across gets the **Fix units** button too, ringed, with a
short note; pick the right unit and it is remembered for that file like any other.

Lengths are shown in millimeters, decimal inches, or inch fractions: the units button (or **U**) steps through
the three. With fractions on, a length is written as a fraction only when it really is one: within 0.0005 in of
a 64th, reduced, such as 1 3/8", 5/16" or 2". Anything else stays decimal (5.8962"), so a fraction on screen
means the part is that size. This applies to the size read-out, bounding-box dimensions, measurements and the
details card; areas, volumes and angles are unchanged.

## Install

Give people one file, `Glance.Setup.exe` from the [latest release](https://github.com/demodude4u/glance/releases/latest) (about 21 MB; built here as `dist\Glance Setup.exe`). Running it shows two boxes, both ticked:

- **Open model files with Glance**: double-clicking an STL, OBJ, 3MF, PLY, glTF (`.glb`, `.gltf`), STEP or IGES
  file opens it in Glance.
- **Add "Open in Glance" to the right-click menu**: works whichever app is set to open the file. On Windows 11
  it is under **Show more options** (or Shift+right-click); the short menu only takes signed, packaged apps.

It installs to `%LOCALAPPDATA%\Programs\Glance` for the current user only, with no administrator rights, adds a
Start menu entry, and lists Glance under Settings > Apps > Installed apps, where it can be uninstalled.
The file is not code-signed, so Windows SmartScreen may ask for confirmation the first time (More info > Run anyway).

Windows does not let any installer override a default the person has already chosen for a type. If the picker
appears for a type, choose Glance there once and click **Always**.

Unattended: `"Glance Setup.exe" --quiet [--no-associate] [--no-menu]`, and `Glance.exe --uninstall --quiet`.
From this folder, `.\build.ps1` builds everything and `.\install.ps1` / `.\uninstall.ps1` run those two commands.

## Updates

An installed Glance keeps itself up to date from <https://github.com/demodude4u/glance/releases>:

1. Every time it opens, as soon as the model is on screen, it reads `version.txt` from the latest release. A slim
   bar slides in the bottom right corner while it looks, and fades away if there is nothing new.
   Nothing else is sent: it is a plain download from GitHub, with no account, identifier or usage data.
2. If that version is newer, it downloads `Glance.Setup.exe` in the background and checks its SHA-256 against the
   one in `version.txt`; the bar fills as the download goes. A file that does not match is thrown away.
3. When the update is ready the bar becomes an **Update** button. Pressing it closes Glance, installs the update
   and opens Glance again on the same file. If it is not pressed, the update goes in silently when the last
   Glance window is closed. Either way the choices made at install time are kept, and the next start shows
   "Glance was updated to version X".

It never runs before the first picture is on screen, never closes a window on its own, and only applies to a copy put there by
the installer (a build run from a folder leaves itself alone). To switch it off, add `autoUpdate=0` to
`%LOCALAPPDATA%\Glance\settings.ini`. What happened is logged in `%LOCALAPPDATA%\Glance\update\update.log`.

The SHA-256 check guards against a damaged or partial download. It does not replace code signing: anyone who can
publish a release in the repository can publish an update.

## Releasing

1. Set the new number in `src\Directory.Build.props` (the only place it lives) and add a section to `CHANGELOG.md`.
2. Commit, then run `.\build.ps1` so the build carries that commit's id, and run the three test suites
   (`tools\hidden_test.py`, `tools\hidden_setup.py`, `tools\hidden_update.py`).
3. Tag the commit `vX.Y.Z` and push the branch and the tag.
4. Create a GitHub release for that tag and attach the two files in `dist\release`: `Glance.Setup.exe` and
   `version.txt`. Both names matter: installed copies fetch exactly those from the latest release, so an update
   goes out to everyone the moment the release is published. Mark a release as a pre-release to hold it back.

The version shows in Glance's title bar, in the help card (F1) as "Version 1.5.0 (commit)", in the installer's title bar, in the file
properties of both programs and under Settings > Apps > Installed apps. Use the third number for fixes, the second
for new features.

The tests drive the real programs on a hidden desktop through a small helper package, `ctcore`, which is not part of
this repository; set `CTCORE_PATH` to its folder if it is not installed.

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
| `src\Glance` | The viewer: C# compiled ahead-of-time to a 2 MB native exe. Hand-written Win32 window, OpenGL 3.3 renderer, HUD, and its own small parsers for STL, OBJ, 3MF, PLY and glTF (`Loaders*.cs`; each is only touched when a file of its type is opened). The graphics driver starts loading on its own thread the moment the process starts. |
| `src\StepNative` | `glance-step.exe`: C++ on OpenCascade. Reads STEP/IGES, tessellates in parallel, writes a packed mesh. Runs as a separate process so a bad CAD file can never take the viewer down. |
| `tools\build_step.py` | Builds the reader and ships only the 25 OpenCascade DLLs it needs. The codec, font and VR libraries the official build imports but the reader never calls are replaced with tiny stand-in DLLs. |
| `tools\hidden_test.py` | End-to-end test on a hidden desktop: opens every format, then drives one window with real mouse and keyboard messages and checks the result. |
| `tools\make_samples.py` | Writes the small sample models the tests open: STL, OBJ, 3MF, PLY and glTF files of known size. |

Settings and the STEP cache live in `%LOCALAPPDATA%\Glance` (cache capped at 1 GB, oldest first).
Colours and type: Cobalt, Golden Amber, Slate, Charcoal and Ivory, set in Roboto.

OpenCascade is LGPL 2.1 with the Open CASCADE exception; its licence texts ship in `step\`.
