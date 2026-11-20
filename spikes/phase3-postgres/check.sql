DO $$
BEGIN
 IF (SELECT count(*) FROM "SyncStreams" WHERE ("Id" = '00000000-0000-0000-0000-000000000001' AND "NextSeq" = 9 AND "ChangesPrunedThroughSeq" = 4) OR ("Id" = '00000000-0000-0000-0000-000000000002' AND "NextSeq" = 3 AND "ChangesPrunedThroughSeq" = 0)) <> 2 THEN RAISE EXCEPTION 'stream counters lost'; END IF;
 IF (SELECT count(*) FROM "SyncEntities" WHERE "Groups" = '{"title":"example"}'::jsonb) <> 2 THEN RAISE EXCEPTION 'entities lost'; END IF;
 IF (SELECT count(*) FROM "SyncChanges" c JOIN "SyncStreams" s ON s."Id" = c."StreamId" WHERE c."Seq" = s."NextSeq" - 1 AND c."Seq" > s."ChangesPrunedThroughSeq") <> 2 THEN RAISE EXCEPTION 'pull cursor broken'; END IF;
 IF (SELECT count(*) FROM "Blobs") <> 2 THEN RAISE EXCEPTION 'blobs lost'; END IF;
END $$;
