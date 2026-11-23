-- Q14: standups (PLAN3 9.12). A standup is a member's Markdown post in a shared space, shown on the
-- space Overview newest first. The author alone edits it; an author or an owner deletes it. A deleted
-- standup stays as a tombstone so every member's device drops it from the list as well.
CREATE TABLE SpaceUpdates (
    Id             TEXT NOT NULL PRIMARY KEY,
    SpaceId        TEXT NOT NULL REFERENCES Spaces(Id),
    AuthorMemberId TEXT NOT NULL,
    ForDate        TEXT NOT NULL,
    RawMarkdown    TEXT NOT NULL CHECK (length(RawMarkdown) <= 20000),
    EditedAtUtc    TEXT NULL,
    Version        INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc   TEXT NOT NULL,
    UpdatedAtUtc   TEXT NOT NULL,
    DeletedAtUtc   TEXT NULL
);
CREATE INDEX IX_SpaceUpdates_Space ON SpaceUpdates (SpaceId, CreatedAtUtc DESC) WHERE DeletedAtUtc IS NULL;
