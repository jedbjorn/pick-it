<#
  verify-signatures.ps1 - check the Authenticode signatures of a Pick-It MSI
  and its manual-install zips (Windows only).

    powershell -File installer/verify-signatures.ps1 -Msi <PickIt-x.msi> -Zips <a.zip>,<b.zip>,<c.zip>
    powershell -File installer/verify-signatures.ps1 -Msi <PickIt-x.msi> -Zips ... -Unsigned

  Checks the MSI itself, then every DLL and EXE inside it (extracted with an
  administrative install, msiexec /a: unpacks files only, installs nothing)
  and inside each zip. Every DLL and EXE must be Pick-It's own (PickIt.*):
  a third-party binary fails the check, so none can ship without a decision
  about signing it.

  Default (release.yml on a v* tag): fails unless the MSI and each of those
  binaries has a Valid signature (chain, file hash) whose signer is
  CN=AnJ Arch-Tech LLC and which carries an RFC 3161 timestamp.

  -Unsigned (every other build: PR, push to main, release dry runs): fails if
  any of them carries a signature, so a build that is not a tag release can
  never pass off signed bytes. It also exercises the extraction on every build.

  CI runs it in _build.yml's msi job, after the zips are made and before
  anything is uploaded.
#>
param(
  [Parameter(Mandatory = $true)] [string] $Msi,
  [Parameter(Mandatory = $true)] [string[]] $Zips,
  [switch] $Unsigned
)

$ErrorActionPreference = 'Stop'
$Signer = 'AnJ Arch-Tech LLC'
$FirstParty = 'PickIt.*'

$problems = New-Object System.Collections.Generic.List[string]
$checked = 0

function Test-File([string] $path, [string] $label) {
  $sig = Get-AuthenticodeSignature -LiteralPath $path
  $cn = if ($sig.SignerCertificate) {
    $sig.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
  } else { '' }
  $ts = if ($sig.TimeStamperCertificate) {
    $sig.TimeStamperCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
  } else { '' }
  Write-Host ("  {0,-10} signer '{1}'  timestamp '{2}'  {3}" -f $sig.Status, $cn, $ts, $label)
  $script:checked++
  if ($Unsigned) {
    if ($sig.Status -ne 'NotSigned') { $script:problems.Add("$label is signed (status $($sig.Status), signer '$cn'); this build must be unsigned") }
    return
  }
  if ($sig.Status -ne 'Valid') { $script:problems.Add("$label signature status is $($sig.Status): $($sig.StatusMessage)") }
  if ($cn -ne $Signer) { $script:problems.Add("$label is signed by '$cn', expected '$Signer'") }
  if (-not $ts) { $script:problems.Add("$label has no timestamp") }
}

# Every DLL/EXE under $root: each must be first-party, each is checked.
function Test-Tree([string] $root, [string] $label) {
  $rootFull = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\') + '\'
  $pe = @(Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { $_.Extension -in '.dll', '.exe' } | Sort-Object FullName)
  if ($pe.Count -eq 0) { $script:problems.Add("$label holds no DLL or EXE"); return }
  foreach ($f in $pe) {
    $rel = $f.FullName.Substring($rootFull.Length)
    if ($f.Name -notlike $FirstParty) {
      $script:problems.Add("$label\$rel is not a Pick-It binary ($FirstParty); decide whether it ships and is signed")
      continue
    }
    Test-File $f.FullName "$label\$rel"
  }
}

$mode = if ($Unsigned) { 'unsigned (not a tag release)' } else { "signed by CN=$Signer, timestamped" }
Write-Host "Expecting: $mode"

$msiFull = (Resolve-Path -LiteralPath $Msi).Path
$msiName = Split-Path -Leaf $msiFull
Write-Host "MSI:"
Test-File $msiFull $msiName

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("pickit-sig-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
  $extract = Join-Path $work 'msi'
  $log = Join-Path $work 'msi-extract.log'
  $p = Start-Process -FilePath 'msiexec.exe' -Wait -PassThru -ArgumentList @(
    '/a', "`"$msiFull`"", '/qn', "TARGETDIR=`"$extract`"", '/l*v', "`"$log`"")
  if ($p.ExitCode -ne 0) {
    $problems.Add("administrative extract (msiexec /a) of $msiName failed with exit code $($p.ExitCode)")
    if (Test-Path $log) { Get-Content $log -Tail 40 | Write-Host }
  } else {
    Write-Host "Binaries in ${msiName}:"
    Test-Tree $extract $msiName
  }

  foreach ($zip in $Zips) {
    $zipFull = (Resolve-Path -LiteralPath $zip).Path
    $zipName = Split-Path -Leaf $zipFull
    $dest = Join-Path $work ([System.IO.Path]::GetFileNameWithoutExtension($zipName))
    Expand-Archive -LiteralPath $zipFull -DestinationPath $dest
    Write-Host "Binaries in ${zipName}:"
    Test-Tree $dest $zipName
  }
} finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Zips.Count -eq 0) { $problems.Add('no zips given') }
if ($problems.Count) {
  $problems | ForEach-Object { Write-Host "::error::$_" }
  exit 1
}
Write-Host "$checked files checked: $mode."
