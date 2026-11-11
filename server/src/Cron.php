<?php

declare(strict_types=1);

namespace Custos;

use Custos\Infra\Log;
use Custos\Infra\Time;

/**
 * One pass of the background work (section 11). bin/cron.php runs it every minute under a lock; tests call it with
 * a moved clock. Each step runs even if an earlier one failed, so a broken mail server does not stop missed-run checks.
 */
final class Cron
{
    public const CLEANUP_EVERY_S = 86400;

    public function __construct(private readonly App $app)
    {
    }

    /**
     * @return array{stats: array<string, mixed>, errors: list<string>}
     */
    public function run(int $now): array
    {
        $stats = [];
        $errors = [];
        $steps = [
            'nonces' => fn() => $this->app->db()->exec('DELETE FROM nonces WHERE created_at < ?', [Time::db($now - 600)]),
            'commands' => fn() => $this->app->commands()->expire($now),
            'monitor' => fn() => $this->app->monitor()->pass($now),
            'notify' => fn() => $this->app->notifier()->queueProblems($now),
            'send' => fn() => $this->app->notifier()->sendQueued($now),
            'updates' => fn() => $this->app->updateWatch()->pass($now),
            'forecast' => fn() => $this->app->storage()->pass($now),
            'cleanup' => fn() => $this->cleanup($now),
        ];
        foreach ($steps as $name => $step) {
            try {
                $stats[$name] = $step();
            } catch (\Throwable $e) {
                Log::error("cron $name: $e");
                $errors[] = "$name: " . $e->getMessage();
            }
        }
        // /api/health and the dashboard warn when this time gets old.
        $this->app->settings()->set('cron_last_run', Time::db($now));
        return ['stats' => $stats, 'errors' => $errors];
    }

    private function cleanup(int $now): ?array
    {
        $settings = $this->app->settings();
        $last = $settings->get('cron_last_cleanup');
        if ($last !== '' && Time::unix($last) > $now - self::CLEANUP_EVERY_S) {
            return null;
        }
        $settings->set('cron_last_cleanup', Time::db($now));
        return $this->app->maintenance()->cleanup($now);
    }
}
