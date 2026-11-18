-- 1.2.0: the edition of the program on a machine (A.3, A.4): '' for the standard one, 'legacy' for the edition for
-- Windows 7 and 8.1 (.NET 6, 32-bit), which has no cloud drives and no 7-Zip update from the site. The site hides
-- those for such machines and gives them only the update packages of their edition (kind 'legacy', A.10).
ALTER TABLE agents
    ADD COLUMN edition VARCHAR(16) NOT NULL DEFAULT '' AFTER agent_version;

ALTER TABLE agent_updates
    MODIFY kind ENUM('agent', '7zip', 'legacy') NOT NULL;
