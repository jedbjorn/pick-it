# Pick-It

[![Build](https://github.com/jedbjorn/pick-it/actions/workflows/build.yml/badge.svg)](https://github.com/jedbjorn/pick-it/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Pick-It** is a Revit add-in by Designs/OS for picking a colour and showing its RGB values directly on the ribbon button.

> **Status: scaffold only.** This repository currently contains project scaffolding. The Revit entry point, colour picker, dynamic ribbon icon, and installer are not implemented yet, and there is nothing to install.

## What it will do

1. Click **Pick-It** on Revit's **Add-Ins** tab.
2. Pick a colour.
3. The Pick-It icon updates to show the picked colour and its RGB values.

## Compatibility

Revit 2025, 2026 and 2027 on Windows, matching [Gantt-It](https://github.com/jedbjorn/gantt-it). Older versions of Revit are not planned.

## Learn more

The intended interaction and the shared Designs/OS design language are in the [product and design brief](docs/DESIGN.md).

## License

Pick-It is released under the [MIT License](LICENSE). Copyright (c) 2026 Designs/OS.

---

For developers: the repository layout and build configurations are in [docs/DEVELOPING.md](docs/DEVELOPING.md).
