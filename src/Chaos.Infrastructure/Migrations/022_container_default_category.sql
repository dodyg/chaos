-- A list or project's default capacity category (NULL = no default): new tasks captured into it take this category.
ALTER TABLE Containers ADD COLUMN DefaultCategory INTEGER NULL CHECK (DefaultCategory IS NULL OR DefaultCategory IN (0,1));
