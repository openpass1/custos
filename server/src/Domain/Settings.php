<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;

/**
 * Key-value settings of section 7.8, stored in the settings table.
 */
final class Settings
{
    public const DEFAULTS = [
        'server_name' => 'CUSTOS',
        'heartbeat_interval_s' => '60',
        'offline_after_min' => '60',
        'ip_allow_site' => '',
        'ip_allow_agents' => '',
        'registration_key' => '',
        'cron_last_run' => '',
    ];

    private ?array $values = null;

    public function __construct(private readonly Db $db)
    {
    }

    public function get(string $name): string
    {
        if ($this->values === null) {
            $this->values = [];
            foreach ($this->db->all('SELECT name, value FROM settings') as $row) {
                $this->values[$row['name']] = $row['value'];
            }
        }
        return $this->values[$name] ?? self::DEFAULTS[$name] ?? '';
    }

    public function int(string $name): int
    {
        return (int) $this->get($name);
    }

    public function set(string $name, string $value): void
    {
        $this->db->exec(
            'INSERT INTO settings (name, value) VALUES (?, ?) ON DUPLICATE KEY UPDATE value = ?',
            [$name, $value, $value]
        );
        if ($this->values !== null) {
            $this->values[$name] = $value;
        }
    }
}
