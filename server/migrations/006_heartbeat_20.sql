-- 0.4.2: agents contact the site every 20 seconds by default instead of 60, so that approval, task edits and commands
-- reach a machine sooner. A site that kept the old default of 60 gets the new one; any other value stays as it was.
UPDATE settings SET value = '20' WHERE name = 'heartbeat_interval_s' AND value = '60';
