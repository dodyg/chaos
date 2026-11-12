-- A pending issue PATCH survives a lost response and database restart. The payload contains
-- hashes of attempted values, not issue text. Keep it in archives with the linked baseline.
CREATE TABLE GitHubIssueWrites (
    IssueLinkId TEXT PRIMARY KEY REFERENCES GitHubIssueLinks(Id) ON DELETE CASCADE,
    PayloadJson TEXT NOT NULL CHECK (json_valid(PayloadJson)),
    CreatedAtUtc TEXT NOT NULL
);
