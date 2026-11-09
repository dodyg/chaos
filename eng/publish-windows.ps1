# Self-contained win-x64 publish for Chaos (PLAN §16 CI: publish + MSIX artifact generation).
# Publishes MUST NOT update the committed NuGet lock files: a -r win-x64 restore writes a
# RID-specific group into packages.lock.json and later locked-mode restores then fail with
# NU1004. RestorePackagesWithLockFile=false keeps the committed lock files for the normal
# locked workflow while the publish restores freely.
#
# Usage:  powershell -File eng/publish-windows.ps1

$ErrorActionPreference = "Stop"
$env:DOTNET_NOLOGO = "1"

dotnet publish src/Chaos.Desktop `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -m:1 `
    -p:RestorePackagesWithLockFile=false `
    @args
exit $LASTEXITCODE
