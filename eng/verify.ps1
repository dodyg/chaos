# Full verification. -NoServer skips the PostgreSQL suite and must be reported.
# Supply CHAOS_TEST_PG or use the configured wslc container runtime; Docker is not required.
param([switch]$NoServer)

$ErrorActionPreference = "Stop"
$env:DOTNET_NOLOGO = "1"
$timings = [System.Collections.Generic.List[object]]::new()
$total = [System.Diagnostics.Stopwatch]::StartNew()

function Invoke-VerifyStep([string]$Name, [scriptblock]$Command) {
    Write-Host "== $Name =="
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Command
        $exitCode = $LASTEXITCODE
    } finally {
        $watch.Stop()
        $timings.Add([pscustomobject]@{ Step = $Name; Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) })
        Write-Host "== $Name took $([math]::Round($watch.Elapsed.TotalSeconds, 1))s =="
    }
    if ($exitCode -ne 0) { throw "$Name failed with exit code $exitCode" }
}

try {
    Invoke-VerifyStep "tool restore" { dotnet tool restore }
    Invoke-VerifyStep "locked restore" { dotnet restore Chaos.slnx --locked-mode -m:1 }
    Invoke-VerifyStep "build (Release)" { dotnet build Chaos.slnx -c Release --no-restore -m:1 }

    $testProjects = @("Domain", "Application", "Infrastructure", "Sync", "Server", "Desktop")
    if ($NoServer) {
        Write-Warning "server tests not run (-NoServer)"
        $testProjects = $testProjects | Where-Object { $_ -ne "Server" }
    }
    foreach ($project in $testProjects) {
        Invoke-VerifyStep "tests: $project" {
            if ($project -eq "Server") {
                dotnet test "tests/Chaos.$project.Tests" -c Release --no-build --output Minimal -- --minimum-expected-tests 1 --maximum-parallel-tests 1
            } else {
                dotnet test "tests/Chaos.$project.Tests" -c Release --no-build --output Minimal -- --minimum-expected-tests 1
            }
        }
    }
    Invoke-VerifyStep "slopwatch" { dotnet tool run slopwatch -- analyze -d . --fail-on warning }
    if ($NoServer) { Write-Host "== verify OK (server tests not run) ==" } else { Write-Host "== verify OK ==" }
} finally {
    $total.Stop()
    Write-Host "== timings (seconds) =="
    $timings | Format-Table -AutoSize | Out-Host
    Write-Host "Total: $([math]::Round($total.Elapsed.TotalSeconds, 1))s"
}
