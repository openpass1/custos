-- Stage 2 "statuses and attention": machine groups, presence, expected schedule slots, problems, notices on the
-- site (the bell) and the outbox of Telegram and e-mail messages (sections 5, 6, 7.7, 9 of the monitor spec).

CREATE TABLE agent_groups (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    name VARCHAR(100) NOT NULL,
    created_at DATETIME NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_agent_groups_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

ALTER TABLE agents
    ADD COLUMN group_id INT UNSIGNED NULL AFTER display_name,
    ADD KEY ix_agents_group (group_id),
    ADD CONSTRAINT fk_agents_group FOREIGN KEY (group_id) REFERENCES agent_groups (id) ON DELETE SET NULL;

-- watch_from: the server expects scheduled runs only from this moment (6.2): when the task first appeared, when its
-- schedule changed, when it was enabled, or when its machine was approved or enabled.
-- slots_until: expected slots (expected_slots) have been created up to this moment.
-- last_result: status of the latest finished run that was not aborted; anomaly: that run is an anomaly (6.5).
ALTER TABLE tasks
    ADD COLUMN watch_from DATETIME NULL,
    ADD COLUMN slots_until DATETIME NULL,
    ADD COLUMN last_result VARCHAR(16) NULL,
    ADD COLUMN last_result_run_id CHAR(36) CHARACTER SET ascii NULL,
    ADD COLUMN anomaly TINYINT(1) NOT NULL DEFAULT 0;

-- anomaly: JSON with the values that left the usual range, null for a normal run.
ALTER TABLE runs
    ADD COLUMN anomaly TEXT NULL,
    ADD KEY ix_runs_task_slot (agent_id, task_id, scheduled_for);

-- watch_from stays NULL for existing tasks: the first cron pass sets it to "now", so the update does not
-- invent missed runs for the time before it.
UPDATE tasks t SET
    last_result_run_id = (
        SELECT r.id FROM runs r
        WHERE r.agent_id = t.agent_id AND r.task_id = t.id AND r.finished_at IS NOT NULL AND r.status <> 'aborted'
        ORDER BY r.started_at DESC LIMIT 1
    );

UPDATE tasks t JOIN runs r ON r.id = t.last_result_run_id SET t.last_result = r.status;

-- Periods when an agent was on line (6.1). Each heartbeat extends the latest period; after a gap of more than three
-- heartbeat intervals a new period starts.
CREATE TABLE agent_presence (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    from_at DATETIME NOT NULL,
    to_at DATETIME NOT NULL,
    PRIMARY KEY (id),
    KEY ix_presence_agent (agent_id, to_at),
    CONSTRAINT fk_presence_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Moments when a task was expected to start (6.2, 6.3).
--   kind: schedule (daily, weekly, monthly) or interval (a timer task that did not run, 6.3 last paragraph).
--   result: pending until slot + grace has passed; then ok, missed or missed_offline.
--   closed_at: when a missed or missed_offline slot stopped counting; closed_by says why
--   (run, catch_up, task_disabled, task_deleted, agent_disabled).
CREATE TABLE expected_slots (
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    task_id CHAR(36) CHARACTER SET ascii NOT NULL,
    slot_at DATETIME NOT NULL,
    kind ENUM('schedule', 'interval') NOT NULL DEFAULT 'schedule',
    result ENUM('pending', 'ok', 'missed', 'missed_offline') NOT NULL DEFAULT 'pending',
    run_id CHAR(36) CHARACTER SET ascii NULL,
    decided_at DATETIME NULL,
    closed_at DATETIME NULL,
    closed_by VARCHAR(16) NULL,
    closed_run_id CHAR(36) CHARACTER SET ascii NULL,
    PRIMARY KEY (agent_id, task_id, slot_at),
    KEY ix_slots_state (result, closed_at),
    KEY ix_slots_time (slot_at),
    CONSTRAINT fk_slots_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Problems (6.7): only error and missed. A task has at most one open problem of each kind: open_flag is 1 while the
-- problem is open and NULL after, so the unique key allows any number of closed ones.
--   occurrences: failed runs or missed slots added to the problem while it was open.
--   notify: pending until cron has put the message into the outbox (queued) or decided not to send it (skipped).
CREATE TABLE problems (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    task_id CHAR(36) CHARACTER SET ascii NOT NULL,
    kind ENUM('error', 'missed') NOT NULL,
    opened_at DATETIME NOT NULL,
    last_at DATETIME NOT NULL,
    occurrences INT UNSIGNED NOT NULL DEFAULT 1,
    first_run_id CHAR(36) CHARACTER SET ascii NULL,
    last_run_id CHAR(36) CHARACTER SET ascii NULL,
    first_slot_at DATETIME NULL,
    last_slot_at DATETIME NULL,
    slot_kind ENUM('schedule', 'interval') NULL,
    message TEXT NULL,
    closed_at DATETIME NULL,
    closed_by VARCHAR(16) NULL,
    closed_run_id CHAR(36) CHARACTER SET ascii NULL,
    open_flag TINYINT AS (IF(closed_at IS NULL, 1, NULL)) STORED,
    notify ENUM('pending', 'queued', 'skipped') NOT NULL DEFAULT 'pending',
    notified_at DATETIME NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_problems_open (agent_id, task_id, kind, open_flag),
    KEY ix_problems_closed (closed_at),
    KEY ix_problems_notify (notify),
    CONSTRAINT fk_problems_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Quiet notices on the site, the bell (7.7). They are never sent to Telegram or by e-mail.
CREATE TABLE notices (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    created_at DATETIME NOT NULL,
    kind VARCHAR(32) NOT NULL,
    agent_id CHAR(36) CHARACTER SET ascii NULL,
    task_id CHAR(36) CHARACTER SET ascii NULL,
    params TEXT NULL,
    read_at DATETIME NULL,
    read_by INT UNSIGNED NULL,
    PRIMARY KEY (id),
    KEY ix_notices_read (read_at, created_at),
    KEY ix_notices_created (created_at),
    CONSTRAINT fk_notices_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Outbox of messages about problems (section 9): one row per Telegram chat or per e-mail message.
-- A failed attempt is repeated until the message is an hour old, then the row becomes failed.
CREATE TABLE notifications (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    channel ENUM('telegram', 'email') NOT NULL,
    target VARCHAR(1024) NOT NULL,
    created_at DATETIME NOT NULL,
    subject VARCHAR(255) NOT NULL,
    body MEDIUMTEXT NOT NULL,
    problem_count INT UNSIGNED NOT NULL DEFAULT 0,
    is_test TINYINT(1) NOT NULL DEFAULT 0,
    status ENUM('queued', 'sent', 'failed') NOT NULL DEFAULT 'queued',
    attempts INT UNSIGNED NOT NULL DEFAULT 0,
    next_try_at DATETIME NULL,
    sent_at DATETIME NULL,
    last_error TEXT NULL,
    PRIMARY KEY (id),
    KEY ix_notifications_queue (status, next_try_at),
    KEY ix_notifications_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
