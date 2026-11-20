-- 1.3.0: CUSTOS for macOS (A.3, A.4, A.10). Its machines send edition 'macos' (kept in agents.edition, VARCHAR since
-- 1.2.0) and take only the update packages of their edition: kind 'macos', custos-macos-X.Y.Z.zip. 7-Zip comes inside
-- the program on a Mac, so the site never sends it one.
ALTER TABLE agent_updates
    MODIFY kind ENUM('agent', '7zip', 'legacy', 'macos') NOT NULL;
