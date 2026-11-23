-- The text last agreed with the server for each shared text (pad text, page text, task notes), so a remote
-- replacement can be merged three ways with an unsent local edit (PLAN3 9.9). Device-local, never synced.
CREATE TABLE TextBase (
    Kind TEXT NOT NULL,
    EntityKey TEXT NOT NULL,
    Hlc TEXT NOT NULL,
    Text TEXT NOT NULL,
    PRIMARY KEY (Kind, EntityKey)
);
