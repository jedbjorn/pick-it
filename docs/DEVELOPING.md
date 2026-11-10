# Developing Pick-It

## Status

The repository is a buildable solution scaffold, not a deployable Revit add-in yet. There are no C# or XAML implementation files, tests, or installer projects.

`src/PickIt.Revit/PickIt.addin` reserves the permanent Revit application ClientId and names `PickIt.Revit.App` as the planned entry point. That class will be added with the implementation.

## Layout

```text
src/
  PickIt.Revit/          Revit/WPF add-in project
    PickIt.addin         Revit application manifest; VendorId DSOS
    Resources/icons/     Planned light/dark 16px and 32px ribbon icons
docs/
  DESIGN.md            Product behaviour and Designs/OS family design language
  DEVELOPING.md        This file
.github/workflows/
  build.yml            Builds every Revit configuration
Directory.Build.props  Shared build settings and Revit configuration mapping
Directory.Packages.props
                        Central Revit API reference package versions
```

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

The **Build** workflow runs for pull requests and pushes to `main`, and can be started manually. It builds all six Debug/Release configurations on Ubuntu with .NET 8 and .NET 10.

CI currently checks compilation only. Packaging, deployment, and Revit integration checks belong with the later implementation.
