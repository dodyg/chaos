INSERT INTO "Accounts" ("Id", "CreatedAt", "PrimaryEmail", "NextSeq", "ChangesPrunedThroughSeq") VALUES
 ('00000000-0000-0000-0000-000000000001', now(), 'one@example.invalid', 9, 4),
 ('00000000-0000-0000-0000-000000000002', now(), 'two@example.invalid', 3, 0);
INSERT INTO "SyncEntities" ("AccountId", "Kind", "EntityKey", "Groups", "LastSeq", "UpdatedAt")
 SELECT "Id", 'task', 'task-' || "Id"::text, '{"title":"example"}'::jsonb, "NextSeq" - 1, now() FROM "Accounts";
INSERT INTO "SyncChanges" ("AccountId", "Seq", "ChangeId", "Kind", "EntityKey", "Op", "Groups", "CreatedAt")
 SELECT "Id", "NextSeq" - 1, "Id", 'task', 'task-' || "Id"::text, 'upsert', '{"title":"example"}'::jsonb, now() FROM "Accounts";
INSERT INTO "Blobs" ("AccountId", "Sha256", "ByteSize", "State", "StorageKey", "WrappedKey", "CreatedAt")
 SELECT "Id", repeat('a', 64), 1, 'Clean', 'example', decode('00', 'hex'), now() FROM "Accounts";
