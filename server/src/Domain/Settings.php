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
        // Interface language for users who have not chosen one, and the language of Telegram and e-mail messages.
        'default_language' => 'ru',
        'heartbeat_interval_s' => '20',
        'offline_after_min' => '15',
        // Log out after this many days without activity, 0 = never (Settings -> General). It replaces
        // session_timeout_hours of config.php (versions before 0.4.10), which is no longer read.
        'logout_after_days' => '7',
        'ip_allow_site' => '',
        'ip_allow_agents' => '',
        // Zabbix and the wall screen (section 4): their own allow-list and tokens, created by install.php and migrate.php.
        'ip_allow_integrations' => '',
        'zabbix_token' => '',
        'wall_token' => '',
        'registration_key' => '',
        'cron_last_run' => '',
        'cron_last_cleanup' => '',
        'forecast_last_run' => '',
        // Thresholds (7.8).
        'missed_grace_min' => '15',
        'stale_days' => '7',
        'anomaly_drop_pct' => '30',
        'anomaly_growth_factor' => '2',
        // Warn this many days before a storage is full by its forecast (section 11); 0 turns the warning off.
        'space_warn_days' => '14',
        // Retention (7.8): run history, presence, problems and the audit log; full run logs.
        'history_months' => '12',
        'log_days' => '90',
        // Notifications (section 9). The bot token and the SMTP password are needed in plain text to send.
        'telegram_token' => '',
        'telegram_chat_ids' => '',
        'telegram_proxy' => '',
        'smtp_host' => '',
        'smtp_port' => '587',
        'smtp_encryption' => 'starttls',
        'smtp_user' => '',
        'smtp_password' => '',
        'mail_from' => '',
        'mail_to' => '',
        // Updates (section 8): the daily 7-Zip release check can be turned off when the server has no internet.
        'sevenzip_check' => '1',
        'sevenzip_latest' => '',
        'sevenzip_checked_at' => '',
        'sevenzip_check_error' => '',
        'sevenzip_notified' => '',
        'agent_outdated_notified' => '',
        'legacy_outdated_notified' => '',
        'macos_outdated_notified' => '',
        'linux_outdated_notified' => '',
        // CUSTOS releases on GitHub (section 8): the daily check of the site and the program releases, the
        // releases found (JSON) and the versions a notice was given for. See Domain\Releases.
        'releases_check' => '1',
        'releases_checked_at' => '',
        'releases_check_error' => '',
        'release_server' => '',
        'release_client' => '',
        'release_server_notified' => '',
        'release_client_notified' => '',
        'release_legacy' => '',
        'release_legacy_notified' => '',
        'release_macos' => '',
        'release_macos_notified' => '',
        'release_linux' => '',
        'release_linux_notified' => '',
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

    public function float(string $name): float
    {
        return (float) $this->get($name);
    }

    /** A list kept as one item per line (chat ids, e-mail addresses). */
    public function lines(string $name): array
    {
        return self::splitList($this->get($name));
    }

    /** Splits text by new lines, commas, semicolons and spaces into non-empty items. */
    public static function splitList(string $text): array
    {
        return array_values(array_filter(array_map('trim', preg_split('/[\s,;]+/', $text)), fn($s) => $s !== ''));
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
