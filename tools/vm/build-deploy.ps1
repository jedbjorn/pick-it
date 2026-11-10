<#
  build-deploy.ps1 — guest side (W10C-Testing). Build Pick-It from a pushed source zip and, for Debug
  configurations, install it as a dev add-in under %APPDATA%\Autodesk\Revit\Addins\<year>\.

  Guest:
    powershell -NoProfile -ExecutionPolicy Bypass -File C:\PickIt\stage\build-deploy.ps1 -Zip C:\PickIt\stage\pick-it-src.zip -Years 2026,2027
    ... -Years 2026 -Config Release   # build only, no deploy
    ... -Years 2025                   # Debug R25: compile only (no Revit 2025 in the guest)

  Stop Revit first (it locks loaded DLLs). Build logs: C:\PickIt\logs\build-<config>.log
#>
param(
  [Parameter(Mandatory)][string]$Zip,
  [string]$Years = '2026',
  [ValidateSet('Debug', 'Release')][string]$Config = 'Debug',
  [switch]$NoExtract
)
$ErrorActionPreference = 'Stop'
$src = 'C:\PickIt\src\pick-it'
$logs = 'C:\PickIt\logs'
New-Item -ItemType Directory -Force $logs | Out-Null
if (Get-Process Revit -ErrorAction SilentlyContinue) { Write-Output 'Revit is running; stop it first'; exit 2 }
if (-not $NoExtract) {
  if (Test-Path $src) { Remove-Item $src -Recurse -Force }
  Expand-Archive -Path $Zip -DestinationPath $src -Force
}
$proj = Join-Path $src 'src\PickIt.Revit\PickIt.Revit.csproj'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

foreach ($year in ($Years -split ',' | ForEach-Object { [int]$_.Trim() })) {
  $tag, $tfm = switch ($year) { 2025 { 'R25', 'net8.0-windows' } 2026 { 'R26', 'net8.0-windows' } 2027 { 'R27', 'net10.0-windows' } default { throw "unsupported year $year" } }
  $cfg = "$Config $tag"
  $log = Join-Path $logs ("build-{0}.log" -f ($cfg -replace ' ', '-'))
  & dotnet build $proj -c $cfg -nologo -v:minimal *> $log
  $code = $LASTEXITCODE
  Write-Output ("build [{0}] exit={1} (log {2})" -f $cfg, $code, $log)
  Get-Content $log | Select-String -Pattern 'error|warning' | Select-Object -First 30 | ForEach-Object { Write-Output ("  " + $_.Line.Substring(0, [Math]::Min(400, $_.Line.Length))) }
  if ($code -ne 0) { exit $code }
  if ($Config -ne 'Debug' -or $year -eq 2025) { continue }

  $out = Join-Path $src "src\PickIt.Revit\bin\$cfg\$tfm"
  $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
  $dest = Join-Path $addins 'PickIt'
  New-Item -ItemType Directory -Force $addins | Out-Null
  if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
  New-Item -ItemType Directory -Force $dest | Out-Null
  Copy-Item (Join-Path $out '*') $dest -Recurse -Force
  Move-Item (Join-Path $dest 'PickIt.addin') (Join-Path $addins 'PickIt.addin') -Force
  Write-Output ("deployed {0} -> {1}" -f $cfg, $dest)
}
