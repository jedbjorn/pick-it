# Pick-It

[![Build](https://github.com/jedbjorn/pick-it/actions/workflows/build.yml/badge.svg)](https://github.com/jedbjorn/pick-it/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Pick-It** is a Revit add-in by Designs/OS for picking a colour and showing its RGB values directly on the ribbon button.

## What it does

1. Click **Pick-It** on Revit's **Add-Ins** tab.
2. Click any pixel on any monitor. A magnifier shows exactly which pixel you are about to pick; Esc or right-click cancels.
3. The Pick-It icon turns into that colour and shows its R, G and B values. It stays that way, across Revit sessions, until you pick again or clear it.

**Shift-click** the button for the menu:

- **Recent colours**: your last 10 picks. Click one to make it the current colour.
- **Clear** and **Clear history**.
- **Add to palette…**: puts the current colour into one of the 16 custom-colour slots of Revit's own Color dialog, so it is there the next time you set a colour anywhere in Revit.

## Install

Download `PickIt-<version>.msi` from the [latest release](https://github.com/jedbjorn/pick-it/releases/latest) and run it. It installs for the current user, needs no administrator rights, and adds Pick-It to every supported Revit version it finds. Restart Revit afterwards.

## Compatibility

Revit 2025, 2026 and 2027 on Windows, matching [Gantt-It](https://github.com/jedbjorn/gantt-it). Older versions of Revit are not planned.

## Learn more

The intended interaction and the shared Designs/OS design language are in the [product and design brief](docs/DESIGN.md).

## License

Pick-It is released under the [MIT License](LICENSE). Copyright (c) 2026 Designs/OS.

---

For developers: the repository layout and build configurations are in [docs/DEVELOPING.md](docs/DEVELOPING.md).
