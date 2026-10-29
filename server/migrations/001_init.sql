-- CUSTOS Monitor, initial schema: users, audit log, settings, agents and everything the agent API stores.
-- All times are UTC. JSON documents are kept as text so that key order and empty objects survive unchanged.

CREATE TABLE users (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    login VARCHAR(64) NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    language VARCHAR(8) NULL,
    theme VARCHAR(8) NOT NULL DEFAULT 'auto',
    disabled TINYINT(1) NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL,
    last_login_at DATETIME NULL,
    last_login_ip VARCHAR(45) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_users_login (login)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE audit_log (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    created_at DATETIME NOT NULL,
    user_id INT UNSIGNED NULL,
    login VARCHAR(64) NOT NULL DEFAULT '',
    ip VARCHAR(45) NOT NULL DEFAULT '',
    action VARCHAR(64) NOT NULL,
    object_type VARCHAR(32) NULL,
    object_id VARCHAR(64) NULL,
    details MEDIUMTEXT NULL,
    PRIMARY KEY (id),
    KEY ix_audit_created (created_at),
    KEY ix_audit_user (user_id, created_at),
    KEY ix_audit_login_failures (action, ip, login, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE settings (
    name VARCHAR(64) NOT NULL,
    value MEDIUMTEXT NOT NULL,
    PRIMARY KEY (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE agents (
    id CHAR(36) CHARACTER SET ascii NOT NULL,
    status ENUM('pending', 'approved', 'disabled') NOT NULL DEFAULT 'pending',
    hostname VARCHAR(255) NOT NULL,
    display_name VARCHAR(255) NULL,
    machine_guid VARCHAR(64) NOT NULL,
    public_key TEXT CHARACTER SET ascii NOT NULL,
    key_sha256 CHAR(64) CHARACTER SET ascii NOT NULL,
    key_fingerprint CHAR(19) CHARACTER SET ascii NOT NULL,
    reregistration_of CHAR(36) CHARACTER SET ascii NULL,
    os VARCHAR(255) NOT NULL DEFAULT '',
    agent_version VARCHAR(32) NOT NULL DEFAULT '',
    time_zone VARCHAR(64) NOT NULL DEFAULT 'UTC',
    last_ip VARCHAR(45) NULL,
    first_seen_at DATETIME NOT NULL,
    last_heartbeat_at DATETIME NULL,
    approved_at DATETIME NULL,
    service_started_at DATETIME NULL,
    state ENUM('idle', 'running', 'paused') NULL,
    running TEXT NULL,
    queue TEXT NULL,
    config_revision BIGINT NULL,
    config_received_at DATETIME NULL,
    allow_remote_edit TINYINT(1) NULL,
    program_password_set TINYINT(1) NULL,
    settings MEDIUMTEXT NULL,
    credentials MEDIUMTEXT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_agents_key (key_sha256),
    KEY ix_agents_machine (machine_guid),
    KEY ix_agents_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Every table that belongs to an agent cascades on agents.id: deleting a machine deletes its history,
-- and replacing a machine after reinstallation renames the old agent to the new agent_id.

CREATE TABLE tasks (
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    id CHAR(36) CHARACTER SET ascii NOT NULL,
    revision BIGINT NOT NULL,
    name VARCHAR(255) NOT NULL,
    enabled TINYINT(1) NOT NULL,
    data MEDIUMTEXT NOT NULL,
    updated_at DATETIME NULL,
    updated_by VARCHAR(255) NULL,
    deleted TINYINT(1) NOT NULL DEFAULT 0,
    deleted_at DATETIME NULL,
    first_seen_at DATETIME NOT NULL,
    last_run_id CHAR(36) CHARACTER SET ascii NULL,
    last_run_status VARCHAR(16) NULL,
    last_run_at DATETIME NULL,
    last_success_at DATETIME NULL,
    PRIMARY KEY (agent_id, id),
    CONSTRAINT fk_tasks_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE task_versions (
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    task_id CHAR(36) CHARACTER SET ascii NOT NULL,
    revision BIGINT NOT NULL,
    data MEDIUMTEXT NOT NULL,
    updated_at DATETIME NULL,
    updated_by VARCHAR(255) NULL,
    source ENUM('local', 'server') NOT NULL,
    overwritten TINYINT(1) NOT NULL DEFAULT 0,
    received_at DATETIME NOT NULL,
    PRIMARY KEY (agent_id, task_id, revision),
    CONSTRAINT fk_task_versions_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE runs (
    id CHAR(36) CHARACTER SET ascii NOT NULL,
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    task_id CHAR(36) CHARACTER SET ascii NOT NULL,
    task_revision BIGINT NULL,
    backup_type VARCHAR(16) NULL,
    trigger_type VARCHAR(16) NULL,
    scheduled_for DATETIME NULL,
    started_at DATETIME NOT NULL,
    finished_at DATETIME NULL,
    duration_s INT UNSIGNED NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'running',
    source_files BIGINT UNSIGNED NULL,
    source_bytes BIGINT UNSIGNED NULL,
    files_processed BIGINT UNSIGNED NULL,
    bytes_written BIGINT UNSIGNED NULL,
    errors INT UNSIGNED NULL,
    warnings INT UNSIGNED NULL,
    error_message TEXT NULL,
    verify TEXT NULL,
    retention TEXT NULL,
    log_tail MEDIUMTEXT NULL,
    received_at DATETIME NOT NULL,
    PRIMARY KEY (id),
    KEY ix_runs_agent (agent_id, started_at),
    KEY ix_runs_task (agent_id, task_id, started_at),
    KEY ix_runs_started (started_at),
    KEY ix_runs_status (status, started_at),
    CONSTRAINT fk_runs_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE run_destinations (
    run_id CHAR(36) CHARACTER SET ascii NOT NULL,
    destination_id CHAR(36) CHARACTER SET ascii NOT NULL,
    status VARCHAR(16) NOT NULL,
    set_name VARCHAR(255) NULL,
    bytes BIGINT UNSIGNED NULL,
    error TEXT NULL,
    PRIMARY KEY (run_id, destination_id),
    CONSTRAINT fk_run_destinations_run FOREIGN KEY (run_id) REFERENCES runs (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE run_logs (
    run_id CHAR(36) CHARACTER SET ascii NOT NULL,
    log_gz MEDIUMBLOB NOT NULL,
    source ENUM('finish', 'fetch') NOT NULL,
    received_at DATETIME NOT NULL,
    PRIMARY KEY (run_id),
    CONSTRAINT fk_run_logs_run FOREIGN KEY (run_id) REFERENCES runs (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE run_progress (
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    run_id CHAR(36) CHARACTER SET ascii NOT NULL,
    phase VARCHAR(16) NOT NULL,
    percent DECIMAL(5, 2) NULL,
    files_done BIGINT UNSIGNED NULL,
    files_total BIGINT UNSIGNED NULL,
    bytes_done BIGINT UNSIGNED NULL,
    bytes_total BIGINT UNSIGNED NULL,
    speed_bps BIGINT UNSIGNED NULL,
    eta_s INT UNSIGNED NULL,
    current_file TEXT NULL,
    updated_at DATETIME NOT NULL,
    PRIMARY KEY (agent_id),
    CONSTRAINT fk_run_progress_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE storage_space (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    task_id CHAR(36) CHARACTER SET ascii NOT NULL,
    destination_id CHAR(36) CHARACTER SET ascii NOT NULL,
    kind VARCHAR(16) NOT NULL,
    path VARCHAR(1024) NULL,
    total_bytes BIGINT UNSIGNED NULL,
    free_bytes BIGINT UNSIGNED NULL,
    recorded_at DATETIME NOT NULL,
    PRIMARY KEY (id),
    KEY ix_storage_space_destination (agent_id, task_id, destination_id, recorded_at),
    CONSTRAINT fk_storage_space_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE storage_sets (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    task_id CHAR(36) CHARACTER SET ascii NOT NULL,
    destination_id CHAR(36) CHARACTER SET ascii NOT NULL,
    name VARCHAR(255) NOT NULL,
    set_type VARCHAR(16) NULL,
    set_time DATETIME NULL,
    bytes BIGINT UNSIGNED NULL,
    verified_at DATETIME NULL,
    verify_ok TINYINT(1) NULL,
    updated_at DATETIME NOT NULL,
    PRIMARY KEY (id),
    KEY ix_storage_sets_destination (agent_id, task_id, destination_id),
    CONSTRAINT fk_storage_sets_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE commands (
    id CHAR(36) CHARACTER SET ascii NOT NULL,
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    kind VARCHAR(32) NOT NULL,
    payload MEDIUMTEXT NOT NULL,
    created_by INT UNSIGNED NULL,
    created_at DATETIME NOT NULL,
    expires_at DATETIME NOT NULL,
    status ENUM('queued', 'delivered', 'done', 'failed', 'rejected', 'expired') NOT NULL DEFAULT 'queued',
    delivered_at DATETIME NULL,
    result_message TEXT NULL,
    result_at DATETIME NULL,
    PRIMARY KEY (id),
    KEY ix_commands_agent (agent_id, status, created_at),
    CONSTRAINT fk_commands_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Replay protection (A.2): nonces seen in the last 10 minutes. scope is the agent id, or 'register' for registrations.
CREATE TABLE nonces (
    scope VARCHAR(36) CHARACTER SET ascii NOT NULL,
    nonce CHAR(32) CHARACTER SET ascii NOT NULL,
    created_at DATETIME NOT NULL,
    PRIMARY KEY (scope, nonce),
    KEY ix_nonces_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
