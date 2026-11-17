-- Chaos migration 029 (PLAN2 P6, Google Calendar). (PLAN2 names this 028; 028 was taken by calendar feeds.)
--
-- GoogleAccounts is synced: the connection follows the person (which accounts, which calendars), but
-- the refresh token never leaves the device. It lives in the OS-protected secret store under
-- chaos/google/{accountId}, so a second device shows the account as "sign in on this device" until it
-- has signed in once. GoogleAccountState is device-local: this device's last problem with the account.
-- The chosen calendars are CalendarSources rows with Provider = 2 (AccountRef = the account id,
-- ExternalId = Google's calendar id), created by migration 028.

CREATE TABLE GoogleAccounts (
    Id           TEXT NOT NULL PRIMARY KEY,   -- googleaccount:{subject}, deterministic
    Email        TEXT NOT NULL CHECK (length(Email) BETWEEN 1 AND 320),
    Subject      TEXT NOT NULL CHECK (length(Subject) BETWEEN 1 AND 255),
    Version      INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL
);

CREATE TABLE GoogleAccountState (
    AccountId      TEXT NOT NULL PRIMARY KEY REFERENCES GoogleAccounts(Id) ON DELETE CASCADE,
    NeedsSignIn    INTEGER NOT NULL DEFAULT 0 CHECK (NeedsSignIn IN (0, 1)),
    LastAttemptUtc TEXT NULL,
    LastSuccessUtc TEXT NULL,
    LastError      TEXT NULL
);
