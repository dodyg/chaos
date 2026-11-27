-- Tags can be pinned to the sidebar and archived out of it. Both travel with the tag, so every device agrees.
ALTER TABLE Tags ADD COLUMN Pinned INTEGER NOT NULL DEFAULT 0 CHECK (Pinned IN (0,1));
ALTER TABLE Tags ADD COLUMN Archived INTEGER NOT NULL DEFAULT 0 CHECK (Archived IN (0,1));
