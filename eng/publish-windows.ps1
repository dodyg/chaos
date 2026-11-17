# Self-contained win-x64 publish for Chaos (PLAN §16 CI: publish + MSIX artifact generation).
# Directory.Build.props restores the win-x64 RID for every project, so the committed lock files
# already describe this publish and it runs in locked mode like every other restore.
#
# -Arch arm64 publishes win-arm64 instead; that RID is not in the lock files yet, so it restores unlocked.
#
# Usage:  pwsh -File eng/publish-windows.ps1 [-Arch x64|arm64] [-o <folder>] [extra dotnet publish arguments]

param(
    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64",
    [Parameter(ValueFromRemainingArguments = $true)]
    $Rest
)

$ErrorActionPreference = "Stop"
$env:DOTNET_NOLOGO = "1"

dotnet publish src/Chaos.Desktop `
    -c Release `
    -r "win-$Arch" `
    --self-contained true `
    -m:1 `
    -p:RestoreLockedMode=$($Arch -eq "x64") `
    @Rest
exit $LASTEXITCODE
