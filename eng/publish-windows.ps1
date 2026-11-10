# Self-contained win-x64 publish for Chaos (PLAN §16 CI: publish + MSIX artifact generation).
# Directory.Build.props restores the win-x64 RID for every project, so the committed lock files
# already describe this publish and it runs in locked mode like every other restore.
#
# Usage:  pwsh -File eng/publish-windows.ps1 [-o <folder>] [extra dotnet publish arguments]

$ErrorActionPreference = "Stop"
$env:DOTNET_NOLOGO = "1"

dotnet publish src/Chaos.Desktop `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -m:1 `
    -p:RestoreLockedMode=true `
    @args
exit $LASTEXITCODE
