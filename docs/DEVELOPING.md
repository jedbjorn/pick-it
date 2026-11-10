# Developing Pick-It

## Status

The add-in is complete: the ribbon button, the screen eyedropper, the picked-colour icon and the state store; and the Shift-click menu (recent colours, Clear, Clear history, Add to palette…) with the writer for Revit's custom colours. It was built and run in Revit 2026 and 2027 (Windows 10, one monitor, 100 % to 200 % scaling); Revit 2025 is compile-checked. Unit tests cover the eyedropper's geometry and pixel lookup (`tests/PickIt.Tests`).

## Layout

```text
src/
  PickIt.Revit/          Revit/WPF add-in project
    App.cs               IExternalApplication: panel, button, icon and theme switching; owns the state
    Commands/            PickCommand (the button) and its availability class
    Model/               Rgb
    Services/            PickStore (state.json under %APPDATA%\Designs-OS\PickIt), PickLog (Debug only, but for
                         the eyedropper's diagnostic line),
                         RevitPalette (the custom colours as read from Revit.ini, plus our writes),
                         PaletteWriter + NativeDialog (drive Revit's Color dialog, unseen),
                         DebugCommands (Debug only: the command file)
    UI/ColorIcon.cs      Draws the picked-colour ribbon images
    UI/Picking/          The eyedropper: overlays, screen capture, magnifier (ported from Gantt-It)
    UI/MenuPopup/        The Shift-click menu: one popup window, menu view and custom-colour grid
    UI/Theme.xaml        The styles the menu needs, copied from Gantt-It's Theme.xaml
    UI/Theming/          Revit theme detection, the family palette, and its brushes as XAML resources
    PickIt.addin         Revit application manifest; VendorId DSOS
    Resources/icons/     Idle ribbon icons, light/dark, 16px and 32px (generated)
tests/
  PickIt.Tests/          xunit tests (net8.0, any OS) of the pure code: links UI/Picking/ScreenSampling.cs
                         and Model/Rgb.cs as source; does not reference the add-in project
installer/
  PickIt.Installer.wixproj  WiX project: PickIt-<version>.msi (Windows only, not in the solution)
  Product.wxs            The per-user MSI: Revit detection, per-year components
  stage.sh               Lays a Release build out as Addins\<year>\ for the MSI and zips
  inspect-msi.ps1        CI check of the MSI's identity, file set and detection wiring
  test-detection.ps1     CI check: silent installs against fake Revit installs
  branding/              Setup artwork (generated)
tools/
  make_icons.py          Generates Resources/icons (needs Pillow)
  make_installer_branding.py  Generates installer/branding (needs Pillow)
docs/
  DESIGN.md            Product behaviour and Designs/OS family design language
  DEVELOPING.md        This file
.github/workflows/
  build.yml            PR/main entry point; calls _build.yml
  _build.yml           Reusable pipeline: unit tests, six configurations, the MSI, the detection test
  release.yml          Tag-driven GitHub Release
Directory.Build.props  Shared build settings and Revit configuration mapping
Directory.Packages.props
                        Central package versions: the Revit API references and the test stack
```

Debug builds append one line per pick, cancel, menu action, palette write or failure to `%LOCALAPPDATA%\Temp\PickIt\pick.log`, and honour `PICKIT_THEME=dark|light` to force a theme. A palette write logs `palette slot=5 rgb=250,250,10 ok|fail <reason> hide=hook|hook-offscreen|timer|none ms=… ticks=… step=… show=…`: slots are 0 to 15 in the log (the menu's tooltips count from 1), and `hide` says how Revit's dialog was kept out of sight (`hook`: transparent before it was first shown; `timer`: the fallback, moved off-screen on the first timer tick, so it was on screen for a moment).

### Eyedropper diagnostics

Every pick writes one `pickdiag` line to the same log about how the screen looked to the add-in:

```text
pickdiag anomaly=none scope=v2 thread=permonitor-v2 sysdpi=96 horzres=1920 desktophorzres=1920 capture_ms=14 bytes=8294400 monitors=1 | m0 rect=0,0,1920x1080 client=1920x1080 dpi=96 m11=1 hdr=off
```

| Field | Means |
|---|---|
| `scope` | The per-monitor DPI scope the pick ran in: `v2`, `v1` (Windows refused v2), or `off` (it did not take effect) |
| `thread` | The thread's DPI awareness inside that scope, as Windows reports it |
| `sysdpi` | `GetDpiForSystem()` |
| `horzres` / `desktophorzres` | The primary monitor's width as GDI gives it to this thread, and its real width. Unequal: coordinates are being scaled (virtualised) |
| `capture_ms`, `bytes` | Time to copy every monitor, and the size of the captures |
| `m<i> rect` | Monitor i in physical pixels (`left,top,widthxheight`), in overlay order |
| `m<i> client` | The overlay's client size from `GetClientRect` after it covered the monitor |
| `m<i> dpi`, `m11` | `GetDpiForWindow` for the overlay, and WPF's device pixels per DIP for it (`dpi / 96` when all is well) |
| `m<i> hdr` | `on` when the monitor has HDR (advanced colour) on, in which case the magnifier says so; `unknown` when Windows did not say |
| `anomaly` | `none`, or any of `scope-inactive` (`scope=off`), `virtualised-dc` (`horzres` ≠ `desktophorzres`), `overlay-size` (an overlay's `client` is not its monitor's size), joined with `+` |

Debug builds always write the line. **Release builds** write it when `anomaly` is not `none`, or when a file named `diagnostics.on` exists in the log folder (`%LOCALAPPDATA%\Temp\PickIt\diagnostics.on`; its content does not matter, and it is read at every pick, so Revit needn't restart). To get a report from a user whose picks are off: have them create that empty file, pick once, and send `pick.log`. Release builds write nothing else to the log, and the diagnostic path starts the log afresh once it passes 4 MB.

### Debug commands

Debug builds also take commands from outside Revit, so a tester needn't synthesise input. Write `%LOCALAPPDATA%\Temp\PickIt\commands.txt` (one command per line, `#` for comments); Revit's Idling event picks it up, deletes it, runs the lines in order, logs each as `> line` and writes `commands.done` beside it. A file modified in the last quarter second, or still locked, waits for the next tick.

| Command | Does |
|---|---|
| `set rgb=r,g,b` | Makes a colour current; recent colours untouched |
| `pick rgb=r,g,b` | As if the eyedropper had picked it: current, and added to the recent colours |
| `pick at=x,y` | Runs a real eyedropper session (overlays, capture, the `pickdiag` line) and picks physical screen pixel x,y by itself, without moving the pointer; coordinates are as in `pickdiag` (negative left of or above the primary, `at=-100,250`). Logs the usual `pick rgb=… hex=…`, or `cancel` when no monitor has that pixel or the session lost the foreground |
| `overlaycheck` | Opens the overlays as a pick would, hides the magnifiers, copies the screen again and compares it with the frozen captures, then cancels. Logs `overlaycheck monitor=i rect=… diff=N` per monitor: `diff=0` means the overlay draws its frozen image one to one; otherwise N pixels differ and `first=x,y was=#… now=#…` gives the first. Anything that changes on screen during the half second the check takes counts as a difference: keep the screen still. With HDR on, expect differences |
| `clear` / `clearhistory` | The menu's Clear / Clear history |
| `palette slot=N rgb=r,g,b` | Writes a colour into custom-colour slot N (0 to 15) |
| `menu [view=palette] [close=ms]` | Opens the Shift-click menu at the centre of Revit's screen; `close=ms` closes it by itself, otherwise Esc or a click does (it also closes if it never gets activation) |

The polling keeps Revit's Idling event firing without delay, which keeps one core busy: Debug builds only.

## Building

Configurations select the Revit major version:

| Configuration | Revit | Target framework |
|---|---|---|
| `Debug R25`, `Release R25` | 2025 | `net8.0-windows` |
| `Debug R26`, `Release R26` | 2026 | `net8.0-windows` |
| `Debug R27`, `Release R27` | 2027 | `net10.0-windows` |

```sh
dotnet build PickIt.sln -c "Debug R26"
```

Building all three Revit targets requires both the .NET 8 and .NET 10 SDKs. `EnableWindowsTargeting` allows compilation on non-Windows hosts. Revit itself is not needed to compile because the `Nice3point.Revit.Api.*` packages provide reference-only API assemblies.

The package versions match Gantt-It's API floors: 2025.0.2, 2026.0.4, and 2027.0.0.

## CI

The **Build** workflow runs for pull requests and pushes to `main`, and can be started manually. It calls `_build.yml`, the one pipeline that `release.yml` also uses:

- **test:** `tests/PickIt.Tests` on Ubuntu (`dotnet test tests/PickIt.Tests/PickIt.Tests.csproj -c "Release R26"`; the tests ignore the Revit suffix, the configuration only has to be one `Directory.Build.props` knows). The build legs wait for it.
- **build:** all six Debug/Release configurations on Ubuntu with .NET 8 and .NET 10. The Debug legs upload `PickIt-Debug-R25/R26/R27`, the add-in laid out like `Addins\<year>\`.
- **msi:** on Windows, builds Release R25, R26 and R27, stages them with `installer/stage.sh`, builds `installer/PickIt.Installer.wixproj`, and checks the result with `installer/inspect-msi.ps1`. Uploads `PickIt-msi` and the per-year manual-install zips `PickIt-zips`.
- **detect:** on Windows, `installer/test-detection.ps1` installs the MSI silently against fake Revit installs and checks which years it fills, repair, upgrade, uninstall, and that `state.json` survives.

The unit tests cover only code with no WPF, Win32 or Revit dependency: the test project compiles `UI/Picking/ScreenSampling.cs` and `Model/Rgb.cs` as linked source instead of referencing the add-in (which is Windows-only), so pure helpers that should be tested belong in a file it links. There are no Revit integration checks yet; the Debug commands above are what a VM tester drives.

## Installer

`PickIt-<version>.msi` is a per-user WiX MSI for Revit 2025, 2026 and 2027, copied from Splash-It's (itself Gantt-It's): same Revit detection (a year counts when `Revit.exe` is found), same per-year conditional components, same `MajorUpgrade` that blocks downgrade, same folder cleanup. Only the identity and the file set differ. It installs the add-in into `%AppData%\Autodesk\Revit\Addins\<year>\` (`PickIt.addin` and a `PickIt\` folder) for each year found, with no custom actions and no elevation, and a silent `msiexec /i PickIt-<version>.msi /qn` installs the same set with no UI.

The MSI owns only those add-in folders. `%AppData%\Designs-OS\PickIt\state.json` (the recent colours) is written by the add-in, so it survives upgrades and uninstall.

```sh
dotnet build src/PickIt.Revit/PickIt.Revit.csproj -c "Release R25"   # and R26, R27
bash installer/stage.sh 2025                                        # and 2026, 2027
dotnet build installer/PickIt.Installer.wixproj -c Release
```

**The file set is deliberate.** Product.wxs names every file of the add-in output (the add-in assembly, its PDB and `deps.json`, and the four icons) and `inspect-msi.ps1` checks the staged folders against the same list. Adding a project reference (a `PickIt.Core.dll`), a satellite-language folder or another resource to the add-in output makes CI fail until the file is added to the component list in `Product.wxs` (one component per file per year) and to `$perYear` in `inspect-msi.ps1`.

**The UpgradeCode never changes.** It is `9AED4E11-5334-46F6-971D-5344A97134B7` in `Product.wxs`, and likewise the ClientId in `PickIt.addin`.

### RST compatibility

rst-c (the Revit Standardization Tool, `jedbjorn/rst-c`) offers Gantt-It and Splash-It in its setup bundle and tray as "companions". A companion has to meet seven requirements, written up in rst-c's `companions/README.md` ("The companion contract"). This is how Pick-It meets each one:

| # | Requirement | Pick-It |
|---|---|---|
| C1 | One per-user MSI for every supported Revit year | `Scope="perUser"` in `Product.wxs`; `inspect-msi.ps1` checks no elevation is needed |
| C2 | A stable UpgradeCode | Above; `inspect-msi.ps1` pins it |
| C3 | `MajorUpgrade` that upgrades in place and blocks downgrade | `Product.wxs`; `inspect-msi.ps1` checks both `Upgrade` rows |
| C4 | Clean silent install and uninstall, exit 0 | `test-detection.ps1`. The MSI has a launch condition: with no Revit 2025, 2026 or 2027 installed, a silent first install fails with a message. rst-c's bundle treats companions as non-vital, so that does not roll rst-c back |
| C5 | A GitHub Release asset named `PickIt-<version>.msi`, with `SHA256SUMS.txt` | `release.yml` publishes both (full releases only count; the tray ignores pre-releases) |
| C6 | Own `.addin` per year with its own ClientId and VendorId `DSOS`; nothing under `%AppData%\RST\` | `PickIt.addin`: ClientId `4ba74feb-9790-415b-943c-d1a5a95e4acc`, VendorId `DSOS` |
| C7 | No dependency on rst-c, and none on Pick-It | The add-in does not reference or look for RST |

rst-c learns about Pick-It from its own registry (`companions/companions.json`, and the tray's product catalog in `RST.Core/Updates`), which is edited in the rst-c repo once a Pick-It release exists. Those entries need the values above: id `pick-it`, repo `jedbjorn/pick-it`, asset name `PickIt-<version>.msi`, UpgradeCode, ClientId `4ba74feb-9790-415b-943c-d1a5a95e4acc`, VendorId `DSOS`. Nothing in this repo changes for that.

### Releasing

A release is a git tag on a commit that is on `main`: `git tag -a v1.0.0 origin/main -m "Pick-It 1.0.0"` then `git push origin v1.0.0`. `release.yml` resolves the version from the tag (the MSI ProductVersion is its numeric `a.b.c`), runs `_build.yml` with `-p:PickItVersion=<version>`, and publishes the MSI, the three per-year zips and `SHA256SUMS.txt` as a GitHub Release. A tag with a hyphen (`v1.0.0-rc.1`) is a pre-release. Run the workflow by hand from the Actions tab for a dry run that publishes nothing.
