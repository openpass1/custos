-- Stage 4: a warning before a storage fills up (section 11). Problems get a third kind, "space": one open problem per
-- task, for the destination of the task that fills up first. It opens when the forecast is space_warn_days days or less
-- and closes when the storage stops filling or the forecast goes beyond twice that.
--   message: the path of that destination (for space).
--   days_left: its forecast, updated every hour while the problem is open.
ALTER TABLE problems
    MODIFY COLUMN kind ENUM('error', 'missed', 'space') NOT NULL,
    ADD COLUMN days_left INT UNSIGNED NULL AFTER message;
