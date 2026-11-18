-- Chaos migration 033 (PLAN2 P14): joining an account. SyncState already holds the account binding
-- and the pull cursor; this adds the one marker joining needs. After a device joins, it first pulls the
-- account's state, then uploads whatever the account does not have yet. BackfillPending says that second
-- step is still owed, so a restart between the two never strands local data on this computer.
-- The device list in Settings comes from the service (it knows every device's name); a synced Devices
-- table is not needed for that and is not added.
ALTER TABLE SyncState ADD COLUMN BackfillPending INTEGER NOT NULL DEFAULT 0 CHECK (BackfillPending IN (0,1));
