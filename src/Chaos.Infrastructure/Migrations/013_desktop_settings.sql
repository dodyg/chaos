-- Desktop shell settings (PLAN §13, work package 13).

-- Global shortcuts are stored as normalized accelerators such as Ctrl+Alt+N. NULL turns a
-- shortcut off; the in-app shortcuts (Ctrl+N, Ctrl+Shift+P) keep working either way.
-- KeepRunningInTray: closing the window leaves Chaos in the notification area so reminders
-- and summaries keep running; Quit from the tray menu ends it.
ALTER TABLE Profiles ADD COLUMN CaptureShortcut TEXT NULL DEFAULT 'Ctrl+Alt+N' CHECK (CaptureShortcut IS NULL OR length(CaptureShortcut) BETWEEN 1 AND 64);
ALTER TABLE Profiles ADD COLUMN PadShortcut TEXT NULL DEFAULT 'Ctrl+Alt+P' CHECK (PadShortcut IS NULL OR length(PadShortcut) BETWEEN 1 AND 64);
ALTER TABLE Profiles ADD COLUMN KeepRunningInTray INTEGER NOT NULL DEFAULT 1 CHECK (KeepRunningInTray IN (0,1));
