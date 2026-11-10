<#
  inspect-msi.ps1 - print and check what a Pick-It MSI installs (Windows only).
  Adapted from splash-it installer/inspect-msi.ps1 (splash-it commit 3f26fba), which is
  adapted from gantt-it installer/inspect-msi.ps1 (gantt-it commit aa83f35); the
  checks are the same, only the identity and the file set are Pick-It's.

    powershell -File installer/inspect-msi.ps1 -Msi <path.msi> -Version 1.0.0-rc.1
    powershell -File installer/inspect-msi.ps1 -Msi <path.msi> -Version 0.0.0-dev -AllowBuildMetadata
    powershell -File installer/inspect-msi.ps1 -Msi <path.msi> -Version 1.0.0 -Stage installer/stage

  Reads the MSI database through the Windows Installer COM API and prints
  the package identity (ProductVersion, UpgradeCode, ProductCode, install
  scope), every file with its install path and the folders removed on
  uninstall, and the Revit detection wiring (AppSearch, component
  conditions, launch condition). Then extracts the package with an administrative install
  (msiexec /a: unpacks files only, installs nothing) and reads the version
  resource of PickIt.Revit.dll for each Revit year.

  -Version is the version the build was asked to stamp (the release tag
  without its v, or the dev version). Fails unless:
    - UpgradeCode, name and manufacturer are Pick-It's;
    - the package is per-user and needs no elevation;
    - the MSI is named PickIt-<Version>.msi and its ProductVersion is the
      numeric a.b.c of -Version;
    - it holds exactly the expected add-in files for each Revit year
      (2025, 2026, 2027), and no satellite language folders; with -Stage, the staged folders (installer/stage/<year>/)
      hold exactly those files too, so a new file in the add-in output cannot
      be left out of the MSI unnoticed;
    - it removes PickIt\ and every folder under it that holds a file
      (per year) on uninstall, and never Addins\<year>\;
    - every component installs under Addins\<year>\ of one Revit year and
      carries Condition 'REVIT<year>FOUND OR REMOVE~="ALL"' and the
      Transitive attribute (so a repair re-evaluates it, and an uninstall
      removes it even when that Revit is gone);
    - REVIT<year>FOUND is set by AppSearch from exactly two Revit.exe
      searches: in the InstallLocation of the Revit <year> uninstall entry
      (64-bit HKLM) and in [ProgramFiles64Folder]Autodesk\Revit <year>;
    - the LaunchCondition table holds the "none was found" check, which
      lets an installed product (repair, uninstall) through, and nothing
      else but MajorUpgrade's downgrade check;
    - the package is x64 (64-bit registry search; ICE80);
    - the Upgrade table holds exactly the two MajorUpgrade rows for
      Pick-It's UpgradeCode: detect older versions up to and including this
      ProductVersion (WIX_UPGRADE_DETECTED), and detect-only newer versions
      (WIX_DOWNGRADE_DETECTED), and RemoveExistingProducts runs between
      InstallValidate and InstallInitialize (the old version is removed
      before anything new is installed);
    - each year's PickIt.Revit.dll has ProductVersion (the assembly's
      InformationalVersion) equal to -Version; with -AllowBuildMetadata a "+<commit>" suffix is accepted
      (dev builds).
  CI runs it on every MSI it builds.
#>
param(
  [Parameter(Mandatory = $true)] [string] $Msi,
  [Parameter(Mandatory = $true)] [string] $Version,
  [switch] $AllowBuildMetadata,
  [string] $Stage
)

$ErrorActionPreference = 'Stop'
# Pick-It's UpgradeCode (installer/Product.wxs). NEVER changes.
$UpgradeCode = '{9AED4E11-5334-46F6-971D-5344A97134B7}'
# Supported Revit years. Each has its own folder tree, components and
# detection property in Product.wxs.
$Years = '2025', '2026', '2027'
$LaunchCondition = 'Installed OR REVIT2025FOUND OR REVIT2026FOUND OR REVIT2027FOUND'
$LaunchMessage = 'Pick-It needs Revit 2025, 2026 or 2027, and none was found on this computer.'
$Transitive = 64   # msidbComponentAttributesTransitive
$LocatorTypeDirectory64 = 16   # msidbLocatorTypeDirectory (0) + msidbLocatorType64bit (16)

function Invoke-Com($obj, [string] $name, [System.Reflection.BindingFlags] $kind, [object[]] $argList = @()) {
  return $obj.GetType().InvokeMember($name, $kind, $null, $obj, $argList)
}
$Call = [System.Reflection.BindingFlags]::InvokeMethod
$Get = [System.Reflection.BindingFlags]::GetProperty

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = Invoke-Com $installer 'OpenDatabase' $Call @((Resolve-Path $Msi).Path, 0)

function Query([string] $sql, [int] $columns) {
  $view = Invoke-Com $db 'OpenView' $Call @($sql)
  Invoke-Com $view 'Execute' $Call | Out-Null
  $rows = @()
  while ($true) {
    $rec = Invoke-Com $view 'Fetch' $Call
    if ($null -eq $rec) { break }
    $row = @()
    for ($c = 1; $c -le $columns; $c++) { $row += , (Invoke-Com $rec 'StringData' $Get @($c)) }
    $rows += , $row
  }
  Invoke-Com $view 'Close' $Call | Out-Null
  return , $rows
}

$props = @{}
foreach ($r in (Query 'SELECT `Property`, `Value` FROM `Property`' 2)) { $props[$r[0]] = $r[1] }

$summary = Invoke-Com $db 'SummaryInformation' $Get @(0)
$wordCount = [int](Invoke-Com $summary 'Property' $Get @(15))
$template = Invoke-Com $summary 'Property' $Get @(7)

# Directory table -> full target paths (DefaultDir is "[short|]long[:source]").
$dirs = @{}
foreach ($r in (Query 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' 3)) {
  $name = ($r[2] -split ':')[0]
  $name = ($name -split '\|')[-1]
  $dirs[$r[0]] = @{ Parent = $r[1]; Name = $name }
}
function DirPath([string] $id) {
  $d = $dirs[$id]
  if ($null -eq $d) { return $id }
  # TARGETDIR is the root; its children are standard folders (AppDataFolder,
  # resolved by Windows Installer at install time), so print those by id.
  if ([string]::IsNullOrEmpty($d.Parent) -or $d.Parent -eq $id -or $d.Parent -eq 'TARGETDIR') { return $id }
  $parent = DirPath $d.Parent
  if ($d.Name -eq '.') { return $parent }
  return "$parent\$($d.Name)"
}
$compDir = @{}
$components = @()
foreach ($r in (Query 'SELECT `Component`, `Directory_`, `Attributes`, `Condition` FROM `Component`' 4)) {
  $compDir[$r[0]] = $r[1]
  $components += [pscustomobject]@{ Id = $r[0]; Path = (DirPath $r[1]); Attributes = [int]$r[2]; Condition = $r[3] }
}
$files = @()
foreach ($r in (Query 'SELECT `FileName`, `Component_`, `FileSize` FROM `File`' 3)) {
  $name = ($r[0] -split '\|')[-1]
  $files += [pscustomobject]@{ Path = "$(DirPath $compDir[$r[1]])\$name"; Size = [int64]$r[2] }
}
$files = $files | Sort-Object Path

Write-Host "MSI:              $Msi"
Write-Host "ProductName:      $($props['ProductName'])"
Write-Host "Manufacturer:     $($props['Manufacturer'])"
Write-Host "ProductVersion:   $($props['ProductVersion'])"
Write-Host "UpgradeCode:      $($props['UpgradeCode'])"
Write-Host "ProductCode:      $($props['ProductCode'])"
Write-Host "ALLUSERS:         '$($props['ALLUSERS'])'  MSIINSTALLPERUSER: '$($props['MSIINSTALLPERUSER'])'"
Write-Host "Template:         $template   Word Count: $wordCount (8 = no elevation required)"
Write-Host "ARPURLUPDATEINFO: $($props['ARPURLUPDATEINFO'])"
Write-Host "Files ($($files.Count)):"
$files | ForEach-Object { Write-Host ("  {0,9}  {1}" -f $_.Size, $_.Path) }

$problems = @()
if ($props['UpgradeCode'] -ne $UpgradeCode) { $problems += "UpgradeCode is $($props['UpgradeCode']), expected $UpgradeCode" }
if ($props['ProductName'] -ne 'Pick-It') { $problems += "ProductName is '$($props['ProductName'])'" }
if ($props['Manufacturer'] -ne 'Designs/OS') { $problems += "Manufacturer is '$($props['Manufacturer'])'" }
if ($props['ALLUSERS'] -and $props['ALLUSERS'] -ne '2') { $problems += "ALLUSERS is '$($props['ALLUSERS'])' (per-machine)" }
if (($wordCount -band 8) -eq 0) { $problems += "summary Word Count ${wordCount}: package requests elevation" }
if (($template -split ';')[0] -ne 'x64') { $problems += "Template is '$template', expected an x64 package" }
$numeric = [regex]::Match($Version, '^\d+\.\d+\.\d+').Value
if (-not $numeric) { $problems += "-Version '$Version' does not start with a numeric a.b.c" }
if ($props['ProductVersion'] -ne $numeric) {
  $problems += "ProductVersion is $($props['ProductVersion']), expected $numeric (from $Version)"
}
$msiName = Split-Path -Leaf $Msi
if ($msiName -ne "PickIt-$Version.msi") { $problems += "MSI is named $msiName, expected PickIt-$Version.msi" }
# The exact file set, per Revit year (8 files each, 24 in all): PDBs ship,
# XML doc files do not (installer/stage.sh drops them). No satellite
# language folders. A file added to the add-in
# output (as the ribbon icons under PickIt\Resources\icons\ were) must be
# added here and to Product.wxs too, deliberately.
$perYear = @(
  'PickIt.addin',
  'PickIt\PickIt.Revit.deps.json',
  'PickIt\PickIt.Revit.dll',
  'PickIt\PickIt.Revit.pdb',
  'PickIt\Resources\icons\pick_16.png',
  'PickIt\Resources\icons\pick_16_dark.png',
  'PickIt\Resources\icons\pick_32.png',
  'PickIt\Resources\icons\pick_32_dark.png'
)
# The folders Pick-It owns in each Addins\<year>\: PickIt\ and every
# folder under it that holds a listed file (and their parents). Each must be
# removed on uninstall.
$ownedDirs = @('PickIt')
foreach ($rel in $perYear) {
  $dir = Split-Path -Parent $rel
  while ($dir -and $dir -ne 'PickIt') {
    if ($ownedDirs -notcontains $dir) { $ownedDirs += $dir }
    $dir = Split-Path -Parent $dir
  }
}
$expected = foreach ($year in $Years) {
  foreach ($rel in $perYear) { "AppDataFolder\Autodesk\Revit\Addins\$year\$rel" }
}
$actual = @($files | ForEach-Object { $_.Path })
foreach ($want in $expected) {
  if ($actual -notcontains $want) { $problems += "missing $want" }
}
foreach ($have in $actual) {
  if ($expected -notcontains $have) { $problems += "unexpected $have" }
}
if ($actual.Count -ne $expected.Count) {
  $problems += "MSI has $($actual.Count) files, expected $($expected.Count)"
}

# The staged add-in output must match the same list: Product.wxs names each
# file explicitly, so a file new in the build output would otherwise be
# missing from the MSI without anything failing.
if ($Stage) {
  foreach ($year in $Years) {
    $root = Join-Path $Stage $year
    if (-not (Test-Path $root)) { $problems += "no staged folder $root"; continue }
    $rootFull = (Resolve-Path $root).Path.TrimEnd('\') + '\'
    $staged = @(Get-ChildItem -Path $root -Recurse -File | ForEach-Object { $_.FullName.Substring($rootFull.Length) })
    foreach ($rel in $staged) {
      if ($perYear -notcontains $rel) { $problems += "staged $year\$rel is not in the MSI file list (Product.wxs, inspect-msi.ps1)" }
    }
    foreach ($rel in $perYear) {
      if ($staged -notcontains $rel) { $problems += "staged $year is missing $rel" }
    }
  }
}

# Per-year component conditions. Every component must belong to one Revit
# year's Addins\<year>\ tree and install only when that year was detected,
# re-evaluated on repair (Transitive).
Write-Host "Components ($($components.Count)):"
$components | Sort-Object Path, Id | ForEach-Object {
  Write-Host ("  {0,-28} attr {1,4}  [{2}]  {3}" -f $_.Id, $_.Attributes, $_.Condition, $_.Path)
}
foreach ($c in $components) {
  $m = [regex]::Match($c.Path, '^AppDataFolder\\Autodesk\\Revit\\Addins\\(\d{4})(\\|$)')
  if (-not $m.Success -or $Years -notcontains $m.Groups[1].Value) {
    $problems += "component $($c.Id) installs to $($c.Path), outside the Addins\<year>\ folders"
    continue
  }
  $year = $m.Groups[1].Value
  $want = "REVIT${year}FOUND OR REMOVE~=`"ALL`""
  if ($c.Condition -ne $want) {
    $problems += "component $($c.Id) ($($c.Path)) has condition '$($c.Condition)', expected '$want'"
  }
  if (($c.Attributes -band $Transitive) -eq 0) {
    $problems += "component $($c.Id) ($($c.Path)) is not Transitive (attributes $($c.Attributes))"
  }
}
foreach ($year in $Years) {
  if (-not ($components | Where-Object { $_.Condition -eq "REVIT${year}FOUND OR REMOVE~=`"ALL`"" })) {
    $problems += "no component is conditioned on REVIT${year}FOUND"
  }
}

# Revit detection: REVIT<year>FOUND comes from two Revit.exe file searches,
# the uninstall entry's InstallLocation (64-bit registry, a directory) and
# the default install folder.
$appSearch = Query 'SELECT `Property`, `Signature_` FROM `AppSearch`' 2
$sigFile = @{}
foreach ($r in (Query 'SELECT `Signature`, `FileName` FROM `Signature`' 2)) { $sigFile[$r[0]] = ($r[1] -split '\|')[-1] }
$drLoc = @{}
foreach ($r in (Query 'SELECT `Signature_`, `Parent`, `Path`, `Depth` FROM `DrLocator`' 4)) {
  $drLoc[$r[0]] = @{ Parent = $r[1]; Path = $r[2]; Depth = $r[3] }
}
$regLoc = @{}
foreach ($r in (Query 'SELECT `Signature_`, `Root`, `Key`, `Name`, `Type` FROM `RegLocator`' 5)) {
  $regLoc[$r[0]] = @{ Root = [int]$r[1]; Key = $r[2]; Name = $r[3]; Type = [int]$r[4] }
}
Write-Host 'Revit detection (AppSearch):'
foreach ($year in $Years) {
  $prop = "REVIT${year}FOUND"
  $sigs = @($appSearch | Where-Object { $_[0] -eq $prop } | ForEach-Object { $_[1] })
  $uninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7346B4A0-' + $year.Substring(2) + '00-0510-0000-705C0D862004}'
  $defaultPath = "[ProgramFiles64Folder]Autodesk\Revit $year"
  $viaRegistry = $false
  $viaDefault = $false
  foreach ($sig in $sigs) {
    $dr = $drLoc[$sig]
    if ($sigFile[$sig] -ne 'Revit.exe' -or $null -eq $dr -or "$($dr.Depth)" -notin @('', '0')) {
      $problems += "$prop search $sig is not a Revit.exe file search at depth 0"
      continue
    }
    $reg = if ($dr.Parent) { $regLoc[$dr.Parent] } else { $null }
    if ($reg -and -not $dr.Path -and $reg.Root -eq 2 -and $reg.Key -eq $uninstallKey -and
        $reg.Name -eq 'InstallLocation' -and $reg.Type -eq $LocatorTypeDirectory64) {
      $viaRegistry = $true
      Write-Host "  $prop <- Revit.exe in HKLM\$($reg.Key) InstallLocation (64-bit)"
    } elseif (-not $dr.Parent -and $dr.Path -eq $defaultPath) {
      $viaDefault = $true
      Write-Host "  $prop <- Revit.exe in $($dr.Path)"
    } else {
      $problems += "$prop search $sig looks in an unexpected place (parent '$($dr.Parent)', path '$($dr.Path)')"
    }
  }
  if ($sigs.Count -ne 2) { $problems += "$prop has $($sigs.Count) AppSearch rows, expected 2" }
  if (-not $viaRegistry) { $problems += "$prop does not search the InstallLocation of $uninstallKey (64-bit)" }
  if (-not $viaDefault) { $problems += "$prop does not search $defaultPath" }
}

# Major upgrade. MajorUpgrade AllowSameVersionUpgrades="yes" writes two
# Upgrade rows: one that finds and removes any older (or equal) version,
# one that only detects a newer version (the downgrade launch condition).
# RemoveExistingProducts must run after InstallValidate and before
# InstallInitialize, so the old version, every year of it, is gone before
# the new one installs.
$msidbUpgradeAttributesOnlyDetect = 2
$msidbUpgradeAttributesVersionMinInclusive = 256
$msidbUpgradeAttributesVersionMaxInclusive = 512
$upgrades = Query 'SELECT `UpgradeCode`, `VersionMin`, `VersionMax`, `Attributes`, `ActionProperty` FROM `Upgrade`' 5
Write-Host "Upgrade rows ($($upgrades.Count)):"
$upgrades | ForEach-Object { Write-Host "  $($_[0])  min '$($_[1])'  max '$($_[2])'  attr $($_[3])  -> $($_[4])" }
$pv = $props['ProductVersion']
$older = @($upgrades | Where-Object { $_[4] -eq 'WIX_UPGRADE_DETECTED' })
$newer = @($upgrades | Where-Object { $_[4] -eq 'WIX_DOWNGRADE_DETECTED' })
if ($upgrades.Count -ne 2) { $problems += "Upgrade table has $($upgrades.Count) rows, expected 2" }
foreach ($r in $upgrades) {
  if ($r[0] -ne $UpgradeCode) { $problems += "Upgrade row for $($r[4]) has UpgradeCode $($r[0]), expected $UpgradeCode" }
}
if ($older.Count -ne 1) {
  $problems += 'Upgrade table has no WIX_UPGRADE_DETECTED row (removal of older versions)'
} else {
  $a = [int]$older[0][3]
  if ($older[0][1] -or $older[0][2] -ne $pv -or ($a -band $msidbUpgradeAttributesVersionMaxInclusive) -eq 0 -or
      ($a -band $msidbUpgradeAttributesOnlyDetect) -ne 0) {
    $problems += "WIX_UPGRADE_DETECTED row must remove every version up to and including $pv (min '$($older[0][1])', max '$($older[0][2])', attributes $a)"
  }
}
if ($newer.Count -ne 1) {
  $problems += 'Upgrade table has no WIX_DOWNGRADE_DETECTED row (downgrade check)'
} else {
  $a = [int]$newer[0][3]
  if ($newer[0][1] -ne $pv -or $newer[0][2] -or ($a -band $msidbUpgradeAttributesOnlyDetect) -eq 0 -or
      ($a -band $msidbUpgradeAttributesVersionMinInclusive) -ne 0) {
    $problems += "WIX_DOWNGRADE_DETECTED row must only detect versions above $pv (min '$($newer[0][1])', max '$($newer[0][2])', attributes $a)"
  }
}
$execSeq = @{}
foreach ($r in (Query 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`' 2)) { $execSeq[$r[0]] = [int]$r[1] }
$rep = $execSeq['RemoveExistingProducts']
Write-Host ("InstallExecuteSequence: InstallValidate {0}, RemoveExistingProducts {1}, InstallInitialize {2}" -f `
  $execSeq['InstallValidate'], $rep, $execSeq['InstallInitialize'])
if ($null -eq $rep) {
  $problems += 'RemoveExistingProducts is not in InstallExecuteSequence'
} elseif (-not ($execSeq['InstallValidate'] -lt $rep -and $rep -lt $execSeq['InstallInitialize'])) {
  $problems += "RemoveExistingProducts ($rep) is not between InstallValidate and InstallInitialize"
}

# Launch condition: blocks a first install without any supported Revit,
# never a repair or uninstall. MajorUpgrade adds its own downgrade check
# (NOT WIX_DOWNGRADE_DETECTED); nothing else may be there.
$launch = Query 'SELECT `Condition`, `Description` FROM `LaunchCondition`' 2
Write-Host "Launch conditions ($($launch.Count)):"
$launch | ForEach-Object { Write-Host "  [$($_[0])] $($_[1])" }
$revitLaunch = @($launch | Where-Object { $_[0] -eq $LaunchCondition -and $_[1] -eq $LaunchMessage })
if ($revitLaunch.Count -ne 1) {
  $problems += "LaunchCondition table has no [$LaunchCondition] '$LaunchMessage'"
}
foreach ($r in $launch) {
  if ($r[0] -ne $LaunchCondition -and $r[0] -ne 'NOT WIX_DOWNGRADE_DETECTED') {
    $problems += "unexpected launch condition [$($r[0])] '$($r[1])'"
  }
}

# Folder cleanup on uninstall (RemoveFile rows with no file name = RemoveFolder).
$removed = @(
  foreach ($r in (Query 'SELECT `DirProperty`, `FileName`, `InstallMode` FROM `RemoveFile`' 3)) {
    if ([string]::IsNullOrEmpty($r[1]) -and ([int]$r[2] -band 2)) { DirPath $r[0] }
  }
) | Sort-Object
Write-Host "Folders removed on uninstall ($($removed.Count)):"
$removed | ForEach-Object { Write-Host "  $_" }
foreach ($year in $Years) {
  $base = "AppDataFolder\Autodesk\Revit\Addins\$year"
  foreach ($sub in $ownedDirs) {
    if ($removed -notcontains "$base\$sub") { $problems += "uninstall does not remove $base\$sub" }
  }
  if ($removed -contains $base) { $problems += "uninstall removes $base (Revit's folder, shared with other add-ins)" }
}

# Version resource of the packed add-in: extract with an administrative
# install (unpacks the files to a folder; installs and registers nothing).
$msiFull = (Resolve-Path $Msi).Path
$extract = Join-Path ([System.IO.Path]::GetTempPath()) ("pickit-msi-" + [guid]::NewGuid().ToString('N'))
$log = "$extract.log"
$p = Start-Process -FilePath 'msiexec.exe' -Wait -PassThru -ArgumentList @(
  '/a', "`"$msiFull`"", '/qn', "TARGETDIR=`"$extract`"", '/l*v', "`"$log`"")
if ($p.ExitCode -ne 0) {
  $problems += "administrative extract (msiexec /a) failed with exit code $($p.ExitCode); log: $log"
} else {
  foreach ($year in $Years) {
    $dll = Get-ChildItem -Path $extract -Recurse -Filter 'PickIt.Revit.dll' |
      Where-Object { $_.FullName -match "\\Addins\\$year\\PickIt\\PickIt\.Revit\.dll$" } |
      Select-Object -First 1
    if ($null -eq $dll) { $problems += "extract has no Addins\$year\PickIt\PickIt.Revit.dll"; continue }
    $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll.FullName)
    Write-Host ("PickIt.Revit.dll {0}: ProductVersion {1}  FileVersion {2}" -f $year, $vi.ProductVersion, $vi.FileVersion)
    $ok = ($vi.ProductVersion -eq $Version) -or
          ($AllowBuildMetadata -and $vi.ProductVersion.StartsWith("$Version+"))
    if (-not $ok) { $problems += "Addins\$year PickIt.Revit.dll ProductVersion is '$($vi.ProductVersion)', expected '$Version'" }
  }
  Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
}

if ($problems.Count) {
  $problems | ForEach-Object { Write-Host "::error::$_" }
  exit 1
}
Write-Host 'MSI identity, layout, folder cleanup, Revit detection and version stamps OK.'
