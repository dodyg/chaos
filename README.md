# chaos
A feeble attempt to organize modern life

Chaos is a personal planner for Windows that works without an account or a network: capture a
task in any of six languages, plan the week, choose today's work, get reminders, and keep your
data in a local SQLite database you can back up, export and restore. `SPEC.md` describes the
product and `PLAN.md` the Phase 1 implementation.

## Run it

Requirements: Windows 10 2004 or later (Windows 11 recommended), the .NET SDK pinned in
`global.json` (11.0.100), Node 26 and the WebView2 Runtime.

```powershell
dotnet tool restore
dotnet restore Chaos.slnx --locked-mode
dotnet run --project src/Chaos.Desktop -c Release -- --data-dir .\local-data
```

Without `--data-dir`, data lives in `%LOCALAPPDATA%\Chaos`. A second start with the same data
folder hands its request to the running window (`--capture` focuses quick add, `--pad` opens a
new pad note).

## Check it

```powershell
pwsh -File eng/verify.ps1
```

Locked restore, Release build (which also type-checks and bundles the frontend), every TUnit
project and Slopwatch. Frontend component tests run with `npm test` in
`src/Chaos.Desktop/frontend` after a build.

## Package it

```powershell
pwsh -File eng/package-msix.ps1
```

Builds `artifacts/msix/Chaos_<version>_x64.msix`. Signing, installing and the desktop acceptance
checklist are in `docs/acceptance.md`. Only the installed package delivers Windows notifications
after Chaos exits; a plain run shows reminders inside the app.

## Documents

- `docs/acceptance.md`: recorded checks and the interactive release checklist
- `docs/desktop-support.md`: what the Windows shell does and its limits
- `docs/data-transfer.md`: backup, export and import formats
- `docs/decisions.md`: implementation decisions
- `docs/dependency-versions.md`: pinned versions
