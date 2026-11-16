# Changelog

The version number lives in `src\Directory.Build.props`. Each release is tagged `vX.Y.Z` in git.

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
