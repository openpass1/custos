-- Stage 3 "management": commands from the site, task editor, templates, agent updates and 7-Zip updates
-- (sections 7.3–7.5 and 8 of the monitor spec).

-- task_id: the task a command is about (also for task.upsert), so the task page can show its commands.
-- created_by_login: who queued it, kept when the user is deleted later.
-- cancelled: an administrator withdrew a command that had not been delivered yet, or a newer edit of the same task
-- replaced it (superseded_by names that edit).
ALTER TABLE commands
    MODIFY status ENUM('queued', 'delivered', 'done', 'failed', 'rejected', 'expired', 'cancelled') NOT NULL DEFAULT 'queued',
    ADD COLUMN task_id CHAR(36) CHARACTER SET ascii NULL AFTER kind,
    ADD COLUMN created_by_login VARCHAR(64) NULL AFTER created_by,
    ADD COLUMN superseded_by CHAR(36) CHARACTER SET ascii NULL,
    ADD KEY ix_commands_task (agent_id, task_id, created_at);

-- Version of 7z.dll the agent has loaded, from the heartbeat (A.4); NULL when the agent does not report it.
ALTER TABLE agents
    ADD COLUMN sevenzip_version VARCHAR(32) NULL AFTER agent_version;

-- Task templates (section 8). data is a task object (A.9) whose ids are the template's own; on every machine they are
-- turned into ids of that machine (Templates::machineId), so applying the template again updates the same task.
-- credentials: accounts the template refers to, without secrets: [{id, kind, user, domain}].
CREATE TABLE templates (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    uuid CHAR(36) CHARACTER SET ascii NOT NULL,
    name VARCHAR(200) NOT NULL,
    revision INT UNSIGNED NOT NULL DEFAULT 1,
    data MEDIUMTEXT NOT NULL,
    credentials MEDIUMTEXT NOT NULL,
    created_at DATETIME NOT NULL,
    created_by VARCHAR(64) NOT NULL DEFAULT '',
    updated_at DATETIME NOT NULL,
    updated_by VARCHAR(64) NOT NULL DEFAULT '',
    PRIMARY KEY (id),
    UNIQUE KEY uq_templates_uuid (uuid),
    UNIQUE KEY uq_templates_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Machines a template was applied to: one task per template and machine.
CREATE TABLE template_links (
    template_id INT UNSIGNED NOT NULL,
    agent_id CHAR(36) CHARACTER SET ascii NOT NULL,
    task_id CHAR(36) CHARACTER SET ascii NOT NULL,
    template_revision INT UNSIGNED NOT NULL,
    command_id CHAR(36) CHARACTER SET ascii NULL,
    applied_at DATETIME NOT NULL,
    applied_by VARCHAR(64) NOT NULL DEFAULT '',
    PRIMARY KEY (template_id, agent_id),
    KEY ix_template_links_task (agent_id, task_id),
    CONSTRAINT fk_template_links_template FOREIGN KEY (template_id) REFERENCES templates (id) ON DELETE CASCADE,
    CONSTRAINT fk_template_links_agent FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Packages the server hands out to agents (A.10): kind 'agent' = update packages of CUSTOS (zip with manifest.json),
-- kind '7zip' = 64-bit installers of 7-Zip from which agents take 7z.dll. The file lives in var/packages/<kind>/.
-- signature: server signature of "{version}\n{sha256}" (agent) or "7zip\n{version}\n{sha256}" (7-Zip).
-- is_current: the agent version machines should run (one per kind at most).
CREATE TABLE agent_updates (
    kind ENUM('agent', '7zip') NOT NULL,
    version VARCHAR(32) NOT NULL,
    file_name VARCHAR(255) NOT NULL,
    size BIGINT UNSIGNED NOT NULL,
    sha256 CHAR(64) CHARACTER SET ascii NOT NULL,
    signature TEXT CHARACTER SET ascii NOT NULL,
    manifest MEDIUMTEXT NULL,
    source VARCHAR(16) NOT NULL DEFAULT 'upload',
    is_current TINYINT(1) NOT NULL DEFAULT 0,
    uploaded_at DATETIME NOT NULL,
    uploaded_by VARCHAR(64) NOT NULL DEFAULT '',
    PRIMARY KEY (kind, version)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
