# Run once per checkout to enable the repository's push checks.
$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..")
try {
    $existing = git config --get core.hooksPath
    if ($LASTEXITCODE -notin 0, 1) { throw "Could not read Git hooks configuration." }
    if ($existing -and $existing -ne ".githooks") {
        throw "Git hooks already use '$existing'. Integrate .githooks/pre-push there before changing this setting."
    }
    if (!$IsWindows) {
        chmod +x .githooks/pre-push
        if ($LASTEXITCODE -ne 0) { throw "Could not make the push hook executable." }
    }
    git config --local core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) { throw "Could not enable Git hooks." }
    Write-Host "Push checks enabled: Prettier will check both frontends before each push."
} finally {
    Pop-Location
}
