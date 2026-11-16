-- Chaos migration 027 (PLAN2 P3, custom view alerts): the alerts a view's evaluator has raised on
-- this device. Device-local: each device evaluates its own matches (SavedViewMatches is the
-- baseline) and decides its own delivery, so nothing here syncs.
--
-- An alert is one grouped notice per view ("3 new tasks in My open bugs"). It has no task
-- foreign key: it names a view, and the task ids it carries are only for the tasks it counted.
-- State: 0 Pending (waiting for its delivery time, after quiet hours), 1 Delivered (shown, waiting
-- to be dismissed), 2 Acknowledged, 4 Cancelled (the view was deleted or its alerts switched off).
-- Presentation: 0 not yet, 1 shown by the system, 2 shown in the app only, 3 suppressed.

CREATE TABLE ViewAlerts (
    Id                TEXT NOT NULL PRIMARY KEY,
    ViewId            TEXT NOT NULL REFERENCES SavedViews(Id),
    TaskCount         INTEGER NOT NULL CHECK (TaskCount >= 1),
    TaskIdsJson       TEXT NOT NULL CHECK (json_valid(TaskIdsJson)),
    FirstTaskTitle    TEXT NOT NULL,
    DeliverAtUtc      TEXT NOT NULL,
    State             INTEGER NOT NULL CHECK (State IN (0, 1, 2, 4)),
    DeliveredAtUtc    TEXT NULL,
    AcknowledgedAtUtc TEXT NULL,
    Presentation      INTEGER NOT NULL DEFAULT 0 CHECK (Presentation BETWEEN 0 AND 3),
    CreatedAtUtc      TEXT NOT NULL,
    UpdatedAtUtc      TEXT NOT NULL,
    CHECK ((State IN (1, 2)) = (DeliveredAtUtc IS NOT NULL)),
    CHECK ((State = 2) = (AcknowledgedAtUtc IS NOT NULL))
);
-- At most one pending alert per view: new matches found before delivery join it.
CREATE UNIQUE INDEX UX_ViewAlerts_PendingView ON ViewAlerts (ViewId) WHERE State = 0;
CREATE INDEX IX_ViewAlerts_StateDeliver ON ViewAlerts (State, DeliverAtUtc);
