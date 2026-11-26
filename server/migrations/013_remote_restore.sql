ALTER TABLE agents ADD COLUMN remote_restore TINYINT(1) NOT NULL DEFAULT 0;
ALTER TABLE commands ADD COLUMN result_data MEDIUMTEXT NULL,
                     ADD COLUMN restore_obsolete TINYINT(1) NOT NULL DEFAULT 0,
                     ADD COLUMN queue_sequence BIGINT UNSIGNED NOT NULL AUTO_INCREMENT UNIQUE;
