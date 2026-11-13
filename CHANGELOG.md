# Changelog

The version number lives in `src\Directory.Build.props`. Each release is tagged `vX.Y.Z` in git.

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
