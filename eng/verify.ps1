# Repeatable verification for Chaos (PLAN §16).
# Usage:  powershell -File eng/verify.ps1
# Sandbox note: -m:1 is used everywhere; MSBuild cannot start worker nodes in restricted
# environments, and it is harmless (single node) on normal machines.

$ErrorActionPreference = "Stop"
$env:DOTNET_NOLOGO = "1"

Write-Host "== tool restore =="
dotnet tool restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== locked restore =="
dotnet restore Chaos.slnx --locked-mode -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== build (Release) =="
dotnet build Chaos.slnx -c Release --no-restore -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== tests: solution (Microsoft.Testing.Platform) =="
dotnet test --solution Chaos.slnx -c Release --no-build --output Minimal -- --minimum-expected-tests 12
if ($LASTEXITCODE -eq 0) {
    Write-Host "solution test run OK"
} else {
    # Documented fallback (PLAN §16): direct TUnit executable runs. Each executable is an
    # MTP runner; a nonzero total is mandatory (a zero count fails the script).
    Write-Host "solution test run unavailable; falling back to direct TUnit executables"
    $fallbackFailed = $false
    foreach ($project in @(
        "tests/Chaos.Domain.Tests",
        "tests/Chaos.Application.Tests",
        "tests/Chaos.Infrastructure.Tests",
        "tests/Chaos.Desktop.Tests"
    )) {
        $dll = Get-ChildItem "$project/bin/Release" -Recurse -Filter "*.Tests.dll" |
            Where-Object { $_.Name -notlike "TUnit*" -and $_.Name -notlike "Microsoft*" } |
            Select-Object -First 1
        if ($null -eq $dll) { Write-Host "MISSING build output: $project"; $fallbackFailed = $true; continue }

        Write-Host "== direct run: $($dll.FullName) =="
        dotnet $dll.FullName --no-banner
        if ($LASTEXITCODE -ne 0) { Write-Host "FAILED: $project"; $fallbackFailed = $true }
    }
    if ($fallbackFailed) { exit 1 }
}

Write-Host "== slopwatch =="
dotnet tool run slopwatch -- analyze -d . --fail-on warning
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== verify OK =="
