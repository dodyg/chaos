# Desktop support

## First supported desktop deliverable

Windows 11 x64 (per PLAN §1 platform assumption). Domain, Application and Infrastructure
projects are portable `net11.0`; only `Chaos.Desktop` and `Chaos.Desktop.Windows` target
`net11.0-windows10.0.19041.0`. macOS/Linux desktop targets and mobile are **not** claimed
as supported in this phase.

## NeoAstra boundary (PLAN §11)

NeoAstra is the UI framework. Its current Windows notification presenter cannot satisfy
scheduled actionable reminders, so reminders go through a dedicated Windows notification
adapter behind the `INotificationAdapter` application port (implemented in
`Chaos.Desktop.Windows/Notifications` in work package 10). NeoAstra's own balloons are not
treated as scheduling infrastructure.

## Interactive acceptance items (require a real Windows desktop session)

The development sandbox used for automated verification is headless and cannot install or
interact with packaged applications. The following are therefore implemented in code and
unit-tested headlessly, but require recorded interactive acceptance on a real desktop
before PLAN.md release-gate items may be checked:

- Visual confirmation of the NeoAstra window rendering the React UI, and a typed RPC
  round-trip performed through the running window.
- Global shortcut registration (Ctrl+Alt+N capture / Ctrl+Alt+P pad) and conflict reporting.
- Tray presence, foreground activation and focus recovery of the single running instance.
- Installed-MSIX scheduled actionable notification spike: schedule while online/offline,
  deliver after exit, Open/Done/Snooze activation routing to one instance, cancellation
  after date change, permission-denied behavior.
- High-DPI/RTL/screen-reader checks on real hardware.

`SingleInstanceGuard` (per-instance lock, secondary-launch detection) is unit-tested
headlessly in `Chaos.Desktop.Tests`; wiring secondary launches to route capture/activation
to the primary is completed with the host integration in work package 13.

## MSIX identity/signing requirements (established in work package 10/13)

- Package identity (Name/Publisher) is required for OS-scheduled toast notifications.
- Signing: a test certificate for development; production certificate decided at release.
- Data must live under `%LOCALAPPDATA%/Chaos` (never under the install directory) so
  package upgrades preserve the database.
