-- Q12: rotating repeating tasks (PLAN3 9.10). RotationJson is {"members":[ids],"start":n} on the series;
-- RotationIndex is an occurrence's position in the rotation, fixed when the occurrence is created.
ALTER TABLE RecurrenceSeries ADD COLUMN RotationJson TEXT NULL;
ALTER TABLE RecurrenceOccurrences ADD COLUMN RotationIndex INTEGER NULL CHECK (RotationIndex IS NULL OR RotationIndex >= 0);
