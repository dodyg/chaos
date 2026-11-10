# Builds the Chaos MSIX (PLAN §13/§16: self-contained win-x64 publish + MSIX artifact).
#
#   pwsh -File eng/package-msix.ps1                       # unsigned package (inspect or sign later)
#   pwsh -File eng/package-msix.ps1 -CertificatePath dev.pfx -CertificatePassword (Read-Host -AsSecureString) `
#        -Publisher "CN=Chaos Dev"                        # signed; Publisher must equal the certificate subject
#
# Windows installs only signed packages whose certificate it trusts. For a development machine,
# create a test certificate yourself (docs/acceptance.md, "Installing the package") — this script
# never creates certificates or changes the certificate store.
# Output: artifacts/msix/Chaos_<version>_x64.msix

param(
    [string]$Version = "0.1.0",
    [string]$Publisher = "CN=Chaos Dev",
    [string]$CertificatePath,
    [SecureString]$CertificatePassword
)

$ErrorActionPreference = "Stop"
$env:DOTNET_NOLOGO = "1"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts/msix"
$publish = Join-Path $out "publish"
$layout = Join-Path $out "layout"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3." }

$kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
$makeappx = Get-ChildItem $kits -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq "x64" } | Sort-Object FullName | Select-Object -Last 1
if ($null -eq $makeappx) { throw "makeappx.exe not found: install the Windows 10/11 SDK (App packaging tools)." }

Remove-Item -Recurse -Force $publish, $layout -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "== publish (self-contained win-x64) =="
& (Join-Path $PSScriptRoot "publish-windows.ps1") -o $publish "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== layout =="
Copy-Item -Recurse $publish $layout
New-Item -ItemType Directory -Force (Join-Path $layout "Images") | Out-Null
Copy-Item -Force (Join-Path $root "packaging/Images/*") (Join-Path $layout "Images")
$manifest = (Get-Content (Join-Path $root "packaging/AppxManifest.xml") -Raw).
    Replace('$VERSION$', "$Version.0").
    Replace('$PUBLISHER$', [System.Security.SecurityElement]::Escape($Publisher))
Set-Content (Join-Path $layout "AppxManifest.xml") $manifest -Encoding utf8NoBOM
# A loose debug database or log from a local run must never ship.
Get-ChildItem $layout -Recurse -Include *.pdb, *.db, *.db-wal, *.db-shm | Remove-Item -Force

Write-Host "== pack =="
$package = Join-Path $out "Chaos_${Version}_x64.msix"
& $makeappx.FullName pack /d $layout /p $package /o /h SHA256
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($CertificatePath) {
    $signtool = Join-Path $makeappx.Directory.FullName "signtool.exe"
    $plain = if ($CertificatePassword) { [System.Net.NetworkCredential]::new("", $CertificatePassword).Password } else { "" }
    Write-Host "== sign =="
    & $signtool sign /fd SHA256 /f $CertificatePath /p $plain $package
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} else {
    Write-Host "Package is unsigned. Sign it before installing (see docs/acceptance.md)."
}

Write-Host "== MSIX: $package =="
