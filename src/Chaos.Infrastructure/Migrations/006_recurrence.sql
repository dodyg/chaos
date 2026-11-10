-- A rule and its template are versioned payloads; occurrence keys are local nominal slots.
CREATE TABLE RecurrenceSeries (
    Id TEXT NOT NULL PRIMARY KEY,
    SpaceId TEXT NOT NULL REFERENCES Spaces(Id),
    ContainerId TEXT NOT NULL REFERENCES Containers(Id),
    RuleJson TEXT NOT NULL,
    TemplateJson TEXT NOT NULL,
    Revision INTEGER NOT NULL CHECK (Revision >= 1),
    ActionableCount INTEGER NOT NULL DEFAULT 0 CHECK (ActionableCount >= 0),
    EndedAtUtc TEXT NULL,
    Version INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL
);

CREATE TABLE RecurrenceOccurrences (
    Id TEXT NOT NULL PRIMARY KEY,
    SeriesId TEXT NOT NULL REFERENCES RecurrenceSeries(Id),
    NominalKey TEXT NOT NULL,
    ScheduleKey TEXT NOT NULL,
    TaskId TEXT NULL REFERENCES Tasks(Id),
    EffectiveLocal TEXT NOT NULL,
    EditSnapshotJson TEXT NULL,
    State INTEGER NOT NULL CHECK (State IN (0, 1, 2)),
    CompletedAtUtc TEXT NULL,
    SeriesRevision INTEGER NOT NULL CHECK (SeriesRevision >= 1),
    Version INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL,
    CHECK ((State = 0 AND CompletedAtUtc IS NULL) OR (State IN (1, 2) AND CompletedAtUtc IS NOT NULL)),
    UNIQUE (SeriesId, NominalKey)
);
CREATE UNIQUE INDEX UX_RecurrenceOccurrences_Active ON RecurrenceOccurrences(SeriesId)
    WHERE State = 0 AND DeletedAtUtc IS NULL;
CREATE UNIQUE INDEX UX_RecurrenceOccurrences_Task ON RecurrenceOccurrences(TaskId) WHERE TaskId IS NOT NULL;

CREATE TABLE RecurrenceSeriesRevisions (
    SeriesId TEXT NOT NULL REFERENCES RecurrenceSeries(Id),
    Revision INTEGER NOT NULL CHECK (Revision >= 1),
    RuleJson TEXT NOT NULL,
    TemplateJson TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (SeriesId, Revision)
);
CREATE TRIGGER TR_RecurrenceSeries_RevisionInsert AFTER INSERT ON RecurrenceSeries BEGIN INSERT INTO RecurrenceSeriesRevisions (SeriesId, Revision, RuleJson, TemplateJson, CreatedAtUtc) VALUES (NEW.Id, NEW.Revision, NEW.RuleJson, NEW.TemplateJson, NEW.UpdatedAtUtc); END;
CREATE TRIGGER TR_RecurrenceSeries_RevisionUpdate AFTER UPDATE OF Revision ON RecurrenceSeries WHEN NEW.Revision <> OLD.Revision BEGIN INSERT OR IGNORE INTO RecurrenceSeriesRevisions (SeriesId, Revision, RuleJson, TemplateJson, CreatedAtUtc) VALUES (NEW.Id, NEW.Revision, NEW.RuleJson, NEW.TemplateJson, NEW.UpdatedAtUtc); END;

CREATE TABLE RecurrenceExceptions (
    SeriesId TEXT NOT NULL REFERENCES RecurrenceSeries(Id),
    NominalKey TEXT NOT NULL,
    Kind INTEGER NOT NULL CHECK (Kind IN (0, 1, 2)),
    PayloadJson TEXT NULL,
    PRIMARY KEY (SeriesId, NominalKey)
);

-- Keep the exception for a skipped or moved nominal slot synchronized with undo.
CREATE TRIGGER TR_RecurrenceOccurrences_ExceptionUpdate AFTER UPDATE ON RecurrenceOccurrences
BEGIN
    DELETE FROM RecurrenceExceptions WHERE SeriesId = NEW.SeriesId AND NominalKey = NEW.NominalKey; INSERT INTO RecurrenceExceptions (SeriesId, NominalKey, Kind, PayloadJson)
        SELECT NEW.SeriesId, NEW.NominalKey, CASE WHEN NEW.State = 2 THEN 0 WHEN NEW.EditSnapshotJson IS NOT NULL THEN 2 ELSE 1 END,
            json_object('effectiveLocal', NEW.EffectiveLocal, 'edit', json(NEW.EditSnapshotJson))
        WHERE NEW.DeletedAtUtc IS NULL AND (NEW.State = 2 OR NEW.EffectiveLocal <> NEW.ScheduleKey OR NEW.EditSnapshotJson IS NOT NULL); END;
