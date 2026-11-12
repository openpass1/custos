-- Stage 4 "analytics and integrations": space forecasts of storages and database dumps of runs
-- (sections 7.3, 11 of the monitor spec; A.5 of the protocol).

-- Database dumps of a run (A.5 "databases", client stage 2b): JSON list [{engine, name, bytes, ok, error}], NULL when the
-- task has no database servers. Named db_dumps because DATABASES is a reserved word in MySQL.
ALTER TABLE runs
    ADD COLUMN db_dumps MEDIUMTEXT NULL AFTER retention;

-- Space forecast of every destination (section 11), recalculated by cron once an hour from storage_space:
-- a linear trend of free space over the last 30 days.
--   state: unknown (too few points), steady (free space does not go down), filling (it goes down).
--   slope_bytes_day: change of free space per day, negative while the storage fills up.
--   days_left: days until free space reaches zero at that rate, only for filling.
CREATE TABLE storage_forecasts (
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    task_id CHAR(36) CHARACTER SET ascii NOT NULL,
    destination_id CHAR(36) CHARACTER SET ascii NOT NULL,
    state ENUM('unknown', 'steady', 'filling') NOT NULL,
    points INT UNSIGNED NOT NULL,
    first_at DATETIME NULL,
    last_at DATETIME NULL,
    free_bytes BIGINT UNSIGNED NULL,
    slope_bytes_day DOUBLE NULL,
    days_left INT UNSIGNED NULL,
    computed_at DATETIME NOT NULL,
    PRIMARY KEY (agent_id, task_id, destination_id),
    KEY ix_storage_forecasts_days (state, days_left),
    CONSTRAINT fk_storage_forecasts_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
