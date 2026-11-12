-- 0.4.2: a machine counts as off line after 15 minutes without a heartbeat by default instead of 60: with heartbeats
-- every 20 seconds an hour was far too long. A site that kept the old default of 60 gets the new one; any other value
-- stays as it was.
UPDATE settings SET value = '15' WHERE name = 'offline_after_min' AND value = '60';
