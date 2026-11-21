-- 1.4.0: CUSTOS for Linux (A.3, A.4, A.8, A.10). Its machines send edition 'linux' (agents.edition) and take only the
-- update packages of their edition: kind 'linux', custos-linux-X.Y.Z.zip with the program for each processor. The
-- site also installs the program on a new server from the current one of these packages (Domain\Install).
ALTER TABLE agent_updates
    MODIFY kind ENUM('agent', '7zip', 'legacy', 'macos', 'linux') NOT NULL;

-- Snapshots of the volumes of the sources (inventory field "snapshots", A.8): what the agent last said, as JSON, and
-- when. NULL for machines that never sent the field (Windows, macOS).
ALTER TABLE agents
    ADD COLUMN snapshots MEDIUMTEXT NULL,
    ADD COLUMN snapshots_at DATETIME NULL;

-- A fourth kind of problem, "snapshot": a task with snapshots on whose volume cannot have one (no free room in the
-- volume group, the last snapshot ran full, or a file system without snapshots). One open problem per task; message
-- is the mount point and state ("/ no_space"). It closes when the agent says the volume is fine again or the task no
-- longer asks for snapshots there.
ALTER TABLE problems
    MODIFY COLUMN kind ENUM('error', 'missed', 'space', 'snapshot') NOT NULL;
