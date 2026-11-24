-- The signed registration and heartbeat of a Linux agent advertise support for cloud drives (A.3, A.4).
ALTER TABLE agents
    ADD COLUMN cloud_destinations TINYINT(1) NOT NULL DEFAULT 0;
