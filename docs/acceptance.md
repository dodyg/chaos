# Acceptance evidence (PLAN §17)

This file records what was run, on what, and with what result. A line is marked **passed** only
when it was actually run; everything that needs a person at a Windows desktop is listed under
"Interactive acceptance" with its steps and stays **not yet run** until someone records a result.

## Environment of the recorded run (2026-11-10)

| Item | Value |
| --- | --- |
| OS | Windows 11 Pro, 10.0.26300, x64 |
| .NET SDK / runtime | 11.0.100 / 11.0.0 (`global.json`, roll-forward disabled) |
| C# | 15.0 in every project (`Directory.Build.props`) |
| NeoAstra | 1.12.0 (package, tool, templates) |
| SQLite | Microsoft.Data.Sqlite 10.0.14 (bundled native library with FTS5) |
| TUnit | 1.81.16 under Microsoft.Testing.Platform |
| Node / npm | 26.12.1 / 11.20.0 |
| Windows SDK packaging tools | 10.0.26100.0 |
| WebView2 Runtime | 157.0.4341.42 |

## Automated checks (passed)

| Check | Command | Result |
| --- | --- | --- |
| Locked restore, Release build, solution tests, Slopwatch | `pwsh -File eng/verify.ps1` | Passed. 0 warnings, 0 errors. 486 tests: Domain 159, Application 49, Infrastructure 217, Desktop 61. Slopwatch 0 issues |
| Frontend type check and bundle | part of the Release build (`tsrs --noEmit && vite build`) | Passed |
| Frontend component tests | `npm test` in `src/Chaos.Desktop/frontend` | Passed, 70 tests |
| Self-contained publish (locked) | `pwsh -File eng/publish-windows.ps1 -o artifacts/msix/publish` | Passed |
| MSIX package | `pwsh -File eng/package-msix.ps1` | Passed (unsigned). `Chaos_0.1.0_x64.msix`, 48 MB; 121 MB installed layout |
| Offline start from the publish folder | published `Chaos.Desktop.exe --data-dir <new folder>` | Passed. Window titled "Chaos"; migrations 1–13 and seed on a new data folder; process stays up |
| Second launch routed | same exe `--data-dir <same folder> --capture` while the first runs | Passed. Second process exits 0 after 308 ms; first keeps running |
| Process start to window handle | 3 runs on new data folders | 1.06 s, 2.13 s, 2.19 s. This is window creation, not "first usable window"; the §16 target (≤ 3 s to usable) still needs the interactive measurement |

The tests cover, among others: migration 013 (schema 13) and reopening; profile settings
validation (names, IANA and Windows zone IDs, date order, shortcut syntax and duplicates);
settings persistence across restart; launch-argument and `chaos:` URI parsing including hostile
input; pipe routing (delivery, refusal, no primary, frame limits); notification buttons (Done,
Snooze, stale, open subtasks, repeated clicks reuse one OperationId); toast XML escaping and
protocol actions; the Settings page; and an activation opening a task in the window.

## Interactive acceptance (not yet run)

Run these on a Windows 11 x64 desktop with a fresh Windows user if possible. Record each result
here with the date, the build (commit) and anything unexpected.

### Installing the package

Windows installs only signed packages whose certificate it trusts. For a test machine, create
your own certificate and trust it yourself (this changes your certificate store, so the scripts
never do it):

```powershell
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=Chaos Dev" -KeyUsage DigitalSignature `
  -FriendlyName "Chaos Dev" -CertStoreLocation Cert:\CurrentUser\My `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
$password = Read-Host -AsSecureString "PFX password"
Export-PfxCertificate -Cert $cert -FilePath chaos-dev.pfx -Password $password
# Trust it for package installs (elevated PowerShell):
Import-PfxCertificate -FilePath chaos-dev.pfx -CertStoreLocation Cert:\LocalMachine\TrustedPeople -Password $password
pwsh -File eng/package-msix.ps1 -Publisher "CN=Chaos Dev" -CertificatePath chaos-dev.pfx -CertificatePassword $password
Add-AppxPackage artifacts\msix\Chaos_0.1.0_x64.msix
```

### Checklist

| # | Step | Expected | Result |
| --- | --- | --- | --- |
| 1 | Disconnect the network. Start Chaos from Start. | My Day opens with an Add task affordance; no sign-in, no error | not yet run |
| 2 | Ctrl+N, type `Call plumber tomorrow 3pm #home`, Enter. Quit from the tray, start again. | The task is there with its planned date, time and tag | not yet run |
| 3 | Measure start to a usable My Day three times (stopwatch or screen recording). | ≤ 3 s on the reference machine (§16 target) | not yet run |
| 4 | Close the window with the title-bar X. | Chaos stays in the notification area; the tray menu opens it again | not yet run |
| 5 | From another app press Ctrl+Alt+N, then Ctrl+Alt+P. | Chaos comes forward with quick add focused, then a new pad note | not yet run |
| 6 | Set the capture shortcut to one another app already uses (e.g. one registered by a running utility). | Settings says it is in use elsewhere; Ctrl+N still works in Chaos | not yet run |
| 7 | Settings shows "Installed version". Add a reminder 3 minutes ahead, Quit Chaos. | A Windows notification arrives on time with Open, Done, Snooze 10 min, Snooze 1 hour | not yet run |
| 8 | Click Done on that notification (Chaos closed), then on a second one click Snooze 10 min with Chaos running. | Task completed once; snoozed one returns 10 minutes later; only one Chaos process | not yet run |
| 9 | Schedule a reminder, then change the task's due date before it fires. | Only the new time notifies | not yet run |
| 10 | Turn Chaos notifications off in Windows Settings. | Chaos Settings/Notifications say they are off; reminders appear in the app | not yet run |
| 11 | Display scale 100%, 150%, 200%; narrow the window to about 800 px. | Text sharp, nothing clipped, sidebar usable | not yet run |
| 12 | Add `اتصل بأحمد غدًا 3pm #عمل` and `Review مشروع plan`. | Titles align by their own direction in lists and the editor; quick add highlights `غدًا` | not yet run |
| 13 | Use Chaos with the keyboard only, then with Narrator: skip link, sidebar, quick add, task editor, Settings. | Every control reachable with visible focus and a spoken name | not yet run |
| 14 | Windows dark mode and "Animation effects" off. | Dark theme; no slide-in animations | not yet run |
| 15 | Build and install version 0.1.1 over 0.1.0. | Data, settings and backups are unchanged | not yet run |
| 16 | Watch network activity (Resource Monitor) for a few minutes of use. | No connections from Chaos or its WebView | not yet run |

### Performance baseline

The §16 representative dataset (10,000 tasks, 20,000 subtasks, 20,000 diary entries, 1,000
tags, a year of plans, 1,000 series) has **not** been generated or measured yet. Smallest next
step: a seeding tool that writes that dataset through the import path into a scratch data root,
then record quick-add preview and task save p95, first search page and backup time here.
