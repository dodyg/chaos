-- Alert preferences belong to the account, even for a shared view.
CREATE TABLE ViewAlertSettings (
    Id TEXT NOT NULL PRIMARY KEY,
    ProfileId TEXT NOT NULL REFERENCES Profiles(Id),
    ViewId TEXT NOT NULL,
    Enabled INTEGER NOT NULL CHECK (Enabled IN (0,1)),
    Version INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
INSERT INTO ViewAlertSettings (Id, ProfileId, ViewId, Enabled, CreatedAtUtc, UpdatedAtUtc)
SELECT 'viewalert:' || s.OwnerProfileId || ':' || v.Id, s.OwnerProfileId, v.Id, v.AlertsEnabled, v.CreatedAtUtc, v.UpdatedAtUtc
FROM SavedViews v JOIN Spaces s ON s.Id = v.SpaceId WHERE s.Kind = 1;

INSERT INTO SyncPending (Kind, EntityKey, GroupName)
SELECT 'view_alert_setting', Id, 'create' FROM ViewAlertSettings;
INSERT INTO SyncPending (Kind, EntityKey, GroupName)
SELECT 'view_alert_setting', Id, 'settings' FROM ViewAlertSettings;

-- Whether this device has initialized its own silent baseline for an enabled view.
CREATE TABLE ViewAlertBaselineState (ViewId TEXT NOT NULL PRIMARY KEY REFERENCES SavedViews(Id));
INSERT INTO ViewAlertBaselineState (ViewId)
SELECT v.Id FROM SavedViews v JOIN Spaces s ON s.Id=v.SpaceId WHERE s.Kind=1 AND v.AlertsEnabled=1;
