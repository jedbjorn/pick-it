#!/usr/bin/env bash
# Stage a built add-in for the MSI (and the manual-install zips).
# Adapted from splash-it installer/stage.sh (splash-it commit 3f26fba), which is
# adapted from gantt-it installer/stage.sh (gantt-it commit aa83f35).
#
#   installer/stage.sh <year> [configuration]     e.g. installer/stage.sh 2026
#
# Copies the PickIt.Revit build output of "Release R25" / "Release R26" /
# "Release R27" (or the configuration given) into installer/stage/<year>/,
# laid out exactly like
# %AppData%\Autodesk\Revit\Addins\<year>\ — the same layout as build.yml's
# "Stage add-in" step for the Debug artifacts, minus the XML doc files:
#
#   installer/stage/<year>/PickIt.addin
#   installer/stage/<year>/PickIt/PickIt.Revit.dll, PickIt.Revit.pdb,
#                                   PickIt.Revit.deps.json,
#                                   Resources/icons/pick_{16,32}[_dark].png
#
# No satellite language folders are expected, and inspect-msi.ps1 -Stage fails
# if one appears.
#
# Product.wxs packs installer/stage/2025/, 2026/ and 2027/, naming each file
# explicitly (inspect-msi.ps1 -Stage checks the staged set against that list).
# Run `dotnet build src/PickIt.Revit/PickIt.Revit.csproj -c "Release R26"`
# (and R25, R27) first. Works in Git Bash on Windows and on Linux. Re-running
# replaces the year's stage folder.

set -euo pipefail

year="${1:?usage: installer/stage.sh <2025|2026|2027> [configuration]}"
case "$year" in
  2025) tfm=net8.0-windows;  suffix=R25 ;;
  2026) tfm=net8.0-windows;  suffix=R26 ;;
  2027) tfm=net10.0-windows; suffix=R27 ;;
  *) echo "stage.sh: unsupported Revit year '$year'" >&2; exit 2 ;;
esac
config="${2:-Release $suffix}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/src/PickIt.Revit/bin/$config/$tfm"
dest="$root/installer/stage/$year"

if [[ ! -f "$out/PickIt.Revit.dll" || ! -f "$out/PickIt.addin" ]]; then
  echo "stage.sh: no build output in '$out' — build \"$config\" first" >&2
  exit 1
fi

rm -rf "$dest"
mkdir -p "$dest/PickIt"
cp "$out/PickIt.addin" "$dest/"
cp -r "$out/." "$dest/PickIt/"
rm "$dest/PickIt/PickIt.addin"
# Ship the PDBs, not the XML doc files (PickIt.Revit.xml): an <name>.xml next
# to a <name>.dll is IntelliSense documentation, of no use to Revit.
for dll in "$dest"/PickIt/*.dll; do
  rm -f "${dll%.dll}.xml"
done

echo "staged $config -> installer/stage/$year"
(cd "$dest" && find . -type f | sort)
