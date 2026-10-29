<?php
// CUSTOS Monitor configuration.
// Copy this file to config.php next to it and fill in the values. config.php is not overwritten by updates.

return [
    // MariaDB 10.11+ or MySQL 8.0+ database.
    'db' => [
        'host' => '127.0.0.1',
        'port' => 3306,
        'name' => 'custos',
        'user' => 'custos',
        'password' => 'change-me',
    ],

    // Address of the site as users and agents open it, without a trailing slash.
    'url' => 'http://custos.lan',

    // Time zone used to show dates on the site (IANA name, for example Europe/Berlin).
    'timezone' => 'UTC',

    // Log out after this many hours without activity.
    'session_timeout_hours' => 8,

    // Reverse proxies in front of the site whose X-Forwarded-For and X-Forwarded-Proto headers are trusted
    // (list of addresses or CIDR ranges). Leave empty when nginx and PHP run on this server without a proxy.
    'trusted_proxies' => [],
];
