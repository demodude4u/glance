# Changelog

The version number lives in `src\Directory.Build.props`. Each release is tagged `vX.Y.Z` in git.

## Unreleased

- **Reload when the file changes.** Save the file again from your CAD program and the open window shows the new
  version, keeping the view, the section cut and the shading. A file caught half-written leaves the model on
  screen until it can be read. `autoReload=0` in `settings.ini` switches it off.
- **Weight.** In Details, click **Material** and pick what the part is made of (twelve common metals and
  plastics, or type a density) to see what it weighs, in grams and kilograms or in pounds. The choice is
  remembered.
- **Hole list.** Details lists every round hole grouped by diameter, such as `4 ×  Ø 6.60 mm`, with the depth
  where all of that size are equally deep. Point at a row and those holes are ringed on the model. A hole saved
  as two half-cylinders counts as one.
- **Stock size.** Details shows the smallest box the part fits in when it may be turned, so a part saved at an
  angle still gets the size of the stock to cut it from. Point at the row to see the box.
- **Copy the numbers.** Click any value in Details to copy it as text; click the file name to copy the whole
  card, or **Holes** for the hole list. The size at the bottom left and the measurement card copy the same way,
  and **Ctrl+Shift+C** copies the measurement on screen or the size.
- Details names the upright dimension of a Y-up file (OBJ, glTF) Height, where it used to say Depth.
- Stock size and holes are worked out the first time Details is opened; opening a model is as fast as before.

## 1.6.0 (2026-11-18)

- **3MF files.** Glance opens `.3mf`: the mesh objects, placed by components and by the build's items with their
  transforms, so a file of several parts comes in with each part where it belongs. Objects kept in other model
  parts of the package, the way slicers save projects, are found too. The size is right without asking: the unit
  comes from the file (millimeters when it states none). Materials, colours, textures and slices are not read.
- **PLY files.** Text and binary `.ply` in either byte order, with normals and colours per vertex where the file
  has them, and faces as triangles, polygons or triangle strips. PLY has no units, so it works like STL: Glance
  guesses, and **Fix units** changes it. A point cloud says that it has no surfaces to show.
- **glTF files.** `.glb` and `.gltf` (glTF 2.0): the scene's nodes with their transforms, meshes with or without
  indices, normals, the material's base colour and vertex colours; the data may be in the file, in `.bin` files
  beside it or embedded in the text. glTF is in meters with Y up, so the model comes in at its true size in
  millimeters and standing the right way up. Textures, skins and animation are not read. A file packed with Draco
  or meshopt compression is refused with a message saying so. Some CAD exporters write millimeters into a glTF
  unchanged, which makes the part a thousand times too large: a glTF model more than 10 m across gets the
  **Fix units** button, ringed, with a short note, and the unit picked is remembered for that file.
- The three formats are everywhere the others are: double-click (the installer registers `.3mf`, `.ply`, `.glb`
  and `.gltf`), right-click **Open in Glance**, drag and drop, **O**, and the arrow keys through a folder. A file
  that is damaged or cut short shows a message instead of a model.
- Opening STL, OBJ, STEP and IGES is unchanged: the new readers are only touched when a file of their type is
  opened.

## 1.5.2 (2026-11-17)

- No changes to how Glance works. Released to check the update path end to end on a real install.

## 1.5.1 (2026-11-17)

- **Cleaner Studio and Metal shading.** The soft shadows in corners and holes are now smooth instead of speckled.
  The part itself is no longer darkened where it stands on the ground, which used to darken the whole edge of a
  thin flat part; the soft shadow on the ground is unchanged.

## 1.5.0 (2026-11-17)

- **Picture sizes by name.** The Picture card offers Window, HD (1280 × 720), Full HD (1920 × 1080), QHD
  (2560 × 1440), 4K (3840 × 2160) and Square (2048 × 2048) in place of 1×, 2× and 4×. While the card is open the
  window dims everything outside a frame, and what is inside the frame is the picture. The model can still be
  turned, moved and zoomed with the card open; a click on the view closes it. **Ctrl+C** and **Ctrl+S** show the
  frame for a moment. A size the graphics card cannot draw is made as large as it can, and the card says so. An
  earlier choice of 1× becomes Window and 4× becomes 4K; everyone else starts on Full HD.
- **Inch fractions.** The units button (and **U**) now steps through mm, inches and inch fractions. With fractions
  on, a length that is within 0.0005 in of a 64th is written as a reduced fraction (1 3/8", 5/16", 2"); anything
  else stays decimal, so a fraction on screen means the part is that size. It applies to the size read-out,
  bounding-box dimensions, measurements and the details card.
- **Mode options.** Each shading mode has options of its own, shown as a second row of smaller buttons under the
  toolbar while that mode is on and remembered per mode:
  - Shaded and Smooth: Color, Edges (the same as **C** and **E**).
  - Studio: Light (Soft, Left, Right, Top), Backdrop (White, Grey, Dark), Material (Clay, White, Charcoal,
    Part color), Shadow (on, off).
  - Metal: Finish (Aluminum, Steel, Brass, Copper, Black), Surface (Polished, Satin, Matte), Backdrop, Shadow.
  - Lines: Hidden lines (Off, Faint), Weight (Fine, Bold), Paper (White, Blueprint, Dark).
  - X-ray: Opacity (Light, Medium, Heavy), Edges (on, off).
  - Wire: Lines (All triangles, Feature edges only).
  - Overhangs: Angle (30°, 45°, 60°).

  Nothing changes until a choice is made: every mode starts out looking as it did. On a backdrop or paper that
  does not suit the theme (Dark in the light theme, say) the buttons turn solid so they stay readable.
- **Narrow windows.** The toolbar wraps onto more lines instead of running off the right edge, so every button
  (and the Picture button) stays in reach down to the smallest window. The section slider now sits under the
  toolbar instead of over its right-hand buttons, and a long message at the top is shortened to fit.

## 1.4.0 (2026-11-16)

- **Updates you can see.** Glance now looks for a newer version every time it opens (it was once a day). A slim
  bar in the bottom right corner slides while it looks, fills while an update is downloaded, and becomes an
  **Update** button when the update is ready. Pressing it installs the update and reopens Glance on the same file.
  An update that is not pressed still goes in when the last Glance window is closed.
- **Version in the title bar**, after the file name.

## 1.3.0 (2026-11-16)

- **Automatic updates.** An installed Glance looks for a newer release on GitHub a few seconds after it opens
  (at most once a day), downloads it in the background, checks it against the published SHA-256, and installs it
  when the last Glance window is closed. It never interrupts work, never delays start-up, and keeps the choices
  made at install. The next start says "Glance was updated to version X". `autoUpdate=0` in settings.ini turns it off.
- The installer says that Glance keeps itself up to date.

## 1.2.0 (2026-11-13)

- **Studio and Metal looks.** Two more stops on the Shading button, made for pictures: Studio is matte clay with soft
  shadows in corners and on the ground; Metal is machined metal reflecting a photo studio. Both swap the working
  background and grid for a plain backdrop. They are set up the first time they are chosen, so start-up is unchanged.
- **Picture button** (top right). Copy or save a picture of the current view, at 1, 2 or 4 times the window's
  size, with the background as shown or transparent. Pictures never include the toolbar or readouts.
  **Ctrl+S** saves a PNG; **Ctrl+C** copies, now at the chosen size.

## 1.1.0 (2026-11-13)

- **Installer.** `Glance Setup.exe`: one file, two ticked options (open model files with Glance; right-click
  "Open in Glance"), no administrator rights. Glance now appears under Installed apps and can be uninstalled there.
- **Fix units.** For STL and OBJ files, a chooser shows how big the part would be in each unit; pick the one that
  looks right. Replaces the "File is in ..." button.
- **Shading button.** Steps through Shaded, Smooth, Lines, X-ray, Wire and Overhangs (also **V**, Shift+V to go back).
  Overhangs marks faces leaning more than 45° past vertical as the part stands, so it works with Lay flat.
- **Ortho button.** Orthographic view on the toolbar as well as **P**.
- **Built-in typeface.** Roboto travels inside the program, so Glance looks the same on every PC.
- **Version number.** Shown in the help card (F1), the installer's title bar, the file's properties and Installed apps.
- Licence notices for bundled components are installed with the program.
- Every build checks that nothing shipped depends on software missing from a clean PC.

## 1.0.0 (2026-11-13)

- First version: STL, OBJ, STEP and IGES viewer with measuring, section cut, bounding box, lay flat,
  inch/mm display, and unit detection for STL and OBJ files.
