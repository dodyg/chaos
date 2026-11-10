# Desktop support

## First supported desktop deliverable

Windows 11 x64 (per PLAN §1 platform assumption). Domain, Application and Infrastructure
projects are portable `net11.0`; only `Chaos.Desktop` and `Chaos.Desktop.Windows` target
`net11.0-windows10.0.19041.0`. macOS/Linux desktop targets and mobile are **not** claimed
as supported in this phase.

## What the Windows shell does (work package 13)

| Feature | How | Where |
| --- | --- | --- |
| One instance per data root | Per-user mutex decides before storage opens; a second launch sends its arguments over a current-user named pipe and exits | `SingleInstanceGuard`, `LaunchRouter` |
| Activation | `--capture`, `--pad` and `chaos:` URIs (`capture`, `pad`, `task/<id>`, `reminder/<job>/<generation>/<action>`) become intents; the page takes them when shown | `LaunchIntents`, `ShellActivations`, `shell.take` |
| Notification-area icon | Open Chaos, Quick add, New pad note, Quit; a left click brings the window forward | `DesktopShell` (NeoAstra tray + menu services) |
| Close to tray | Closing the window hides it while "Keep Chaos in the notification area" is on and the tray icon exists; Quit ends the process | `DesktopShell`, Settings |
| Global shortcuts | Ctrl+Alt+N (quick add) and Ctrl+Alt+P (new pad note) by default, editable or off; conflicts and failures are shown in Settings | NeoAstra `RegisterHotKey` service; migration 013 |
| Windows notifications | With package identity: reminders are scheduled with Windows (`AddToSchedule`), arrive after Chaos exits, and carry Open, Done, Snooze 10 min and Snooze 1 hour as `chaos:` protocol buttons. Without identity: the in-app presenter | `WindowsToastAdapter`, `InAppNotificationAdapter` |
| Settings | Name, time zone, numeric date order, shortcuts, close to tray, Quit, version | `Settings.tsx`, `shell` RPC |
| Accessibility basics | Skip link, `dir="auto"` on every text field, plaintext bidi for task titles, reduced motion, system light/dark | `App.tsx`, `style.css`, `components/ui` |
| Package | MSIX with the `chaos` protocol, full trust, no file-system write virtualization, generated logos | `packaging/AppxManifest.xml`, `eng/package-msix.ps1` |

## Data location

`%LOCALAPPDATA%\Chaos` holds `data\chaos.db`, `backups\` and `desktop\` (NeoAstra's private
window and service state). The WebView profile is NeoAstra's own and is not part of backups.
The installed package does not virtualize writes, so the same folder is used whether Chaos runs
from the package or from a publish folder, package updates keep it, and uninstalling the package
does not delete it. "Delete all local data" in Data & backups is the way to remove it.

## Known platform limits

- **Windows decides whether a notification shows.** Do Not Disturb and Focus can hold or hide
  notifications, and a turned-off Chaos notification setting in Windows makes the adapter report
  "Denied"; reminders then appear inside Chaos while it is open. Settings shows which applies.
- **Nothing is delivered while the computer is off.** Missed reminders are coalesced and shown
  once at the next start (package 10 behaviour).
- **A notification button that starts Chaos opens its window.** When Chaos is not running, Windows
  starts it to handle Done or Snooze; the action happens and the window opens normally.
- **Windows can refuse to bring the window forward** (foreground lock rules). The window is shown
  and restored; the taskbar button may flash instead of taking focus.
- **Summary-ready prompts stay in the app.** They are stored on the summary row, not as Windows
  notifications (decisions, package 11).
- **Unpackaged runs** (`dotnet run`, a publish folder) have no package identity: no Windows
  notifications after exit and no `chaos:` protocol. Everything else works.

## Interactive acceptance still required

The automated checks run headlessly. These need a person at a Windows desktop and are listed,
with steps, in `docs/acceptance.md`:

- The window renders the React UI and a typed RPC round trip works through it.
- Installing the signed MSIX, offline first start, capture, restart.
- Tray icon, close to tray, Quit; global shortcuts and a conflict report.
- Scheduled notification after exit; Open, Done and Snooze from the notification reach one
  instance; date change cancels the old notification; notifications turned off in Windows.
- High DPI (100/150/200%), RTL text, keyboard-only use and Narrator.
- Package upgrade keeps data.
