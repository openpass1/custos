<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Config;
use Custos\Infra\Db;
use Custos\Infra\Lang;
use Custos\Infra\Log;
use Custos\Infra\Mailer;
use Custos\Infra\Telegram;
use Custos\Infra\Time;

/**
 * Messages about problems in Telegram and by e-mail (section 9).
 *
 * Only problems (error, missed, space) are sent, one message per problem when it opens; there are no repeats, recovery
 * messages or digests. Problems opened since the previous cron pass go into one message grouped by machine.
 * Messages wait in the notifications table, so a channel that is down for a while gets them later (up to an hour).
 */
final class Notifier
{
    public const CHANNELS = ['telegram', 'email'];
    /** Pauses between attempts, in seconds; after an hour a message is given up. */
    private const RETRY_S = [60, 120, 300, 600];
    private const GIVE_UP_S = 3600;

    /** @var ?\Closure(string $channel, string $target, string $subject, string $body): void replaces the real senders in tests */
    public ?\Closure $sender = null;

    public function __construct(
        private readonly Db $db,
        private readonly Settings $settings,
        private readonly Lang $lang,
        private readonly Config $config
    ) {
    }

    /** @return list<string> channels with complete settings */
    public function channels(): array
    {
        $channels = [];
        if ($this->settings->get('telegram_token') !== '' && $this->settings->lines('telegram_chat_ids') !== []) {
            $channels[] = 'telegram';
        }
        if ($this->settings->get('smtp_host') !== '' && $this->settings->get('mail_from') !== '' && $this->settings->lines('mail_to') !== []) {
            $channels[] = 'email';
        }
        return $channels;
    }

    /**
     * Puts the problems opened since the last pass into the outbox as one message per Telegram chat and one e-mail.
     * Problems that closed meanwhile, and all problems while no channel is set up, are not sent.
     *
     * @return int number of problems in the message
     */
    public function queueProblems(int $now): int
    {
        $pending = $this->db->all(
            "SELECT p.*, a.hostname, a.display_name, a.time_zone, t.name AS task_name
             FROM problems p JOIN agents a ON a.id = p.agent_id
             LEFT JOIN tasks t ON t.agent_id = p.agent_id AND t.id = p.task_id
             WHERE p.notify = 'pending'
             ORDER BY COALESCE(a.display_name, a.hostname), a.id, p.opened_at, p.id"
        );
        if ($pending === []) {
            return 0;
        }
        $channels = $this->channels();
        $open = array_values(array_filter($pending, fn($p) => $p['closed_at'] === null));
        $nowDb = Time::db($now);
        if ($channels === [] || $open === []) {
            $this->mark($pending, 'skipped', $nowDb);
            return 0;
        }
        [$subject, $body] = $this->compose($open);
        $this->db->transaction(function () use ($channels, $subject, $body, $open, $pending, $nowDb) {
            foreach ($this->targets($channels) as [$channel, $target]) {
                $this->db->exec(
                    'INSERT INTO notifications (channel, target, created_at, subject, body, problem_count) VALUES (?, ?, ?, ?, ?, ?)',
                    [$channel, $target, $nowDb, $subject, $body, count($open)]
                );
            }
            $this->mark($open, 'queued', $nowDb);
            $this->mark(array_filter($pending, fn($p) => $p['closed_at'] !== null), 'skipped', $nowDb);
        });
        return count($open);
    }

    /** Sends what waits in the outbox. @return array{sent: int, failed: int} */
    public function sendQueued(int $now): array
    {
        $stats = ['sent' => 0, 'failed' => 0];
        $rows = $this->db->all(
            "SELECT * FROM notifications WHERE status = 'queued' AND (next_try_at IS NULL OR next_try_at <= ?) ORDER BY id LIMIT 100",
            [Time::db($now)]
        );
        foreach ($rows as $row) {
            $error = $this->deliver($row['channel'], $row['target'], $row['subject'], $row['body']);
            if ($error === null) {
                $this->db->exec("UPDATE notifications SET status = 'sent', sent_at = ?, attempts = attempts + 1, last_error = NULL WHERE id = ?",
                    [Time::db($now), $row['id']]);
                $stats['sent']++;
                continue;
            }
            $attempts = (int) $row['attempts'] + 1;
            $giveUp = Time::unix($row['created_at']) + self::GIVE_UP_S <= $now;
            $this->db->exec(
                'UPDATE notifications SET status = ?, attempts = ?, last_error = ?, next_try_at = ? WHERE id = ?',
                [$giveUp ? 'failed' : 'queued', $attempts, mb_substr($error, 0, 2000),
                 Time::db($now + self::RETRY_S[min($attempts, count(self::RETRY_S)) - 1]), $row['id']]
            );
            Log::warning("notification {$row['id']} ({$row['channel']}) attempt $attempts: $error" . ($giveUp ? ', given up' : ''));
            $stats['failed']++;
        }
        return $stats;
    }

    /**
     * Sends a test message to every recipient of the channel right away.
     *
     * @return list<array{target: string, error: ?string}>
     */
    public function sendTest(string $channel, int $now): array
    {
        if (!in_array($channel, $this->channels(), true)) {
            return [['target' => '', 'error' => $this->lang->get('notify.error.not_configured')]];
        }
        $server = $this->settings->get('server_name');
        $subject = $this->lang->get('notify.test.subject', ['server' => $server]);
        $body = $this->lang->get('notify.test.body', ['server' => $server]);
        $results = [];
        foreach ($this->targets([$channel]) as [, $target]) {
            $error = $this->deliver($channel, $target, $subject, $body);
            $this->db->exec(
                'INSERT INTO notifications (channel, target, created_at, subject, body, is_test, status, attempts, sent_at, last_error)
                 VALUES (?, ?, ?, ?, ?, 1, ?, 1, ?, ?)',
                [$channel, $target, Time::db($now), $subject, $body, $error === null ? 'sent' : 'failed',
                 $error === null ? Time::db($now) : null, $error]
            );
            $results[] = ['target' => $target, 'error' => $error];
        }
        return $results;
    }

    /** @return list<array> the latest messages for the settings page */
    public function latest(int $limit): array
    {
        return $this->db->all('SELECT * FROM notifications ORDER BY id DESC LIMIT ?', [$limit]);
    }

    /**
     * The message about a batch of problems, grouped by machine.
     *
     * @return array{0: string, 1: string} subject and body
     */
    public function compose(array $problems): array
    {
        $server = $this->settings->get('server_name');
        $machines = [];
        foreach ($problems as $p) {
            $machines[$p['agent_id']][] = $p;
        }
        $lines = [$this->lang->get('notify.intro', ['n' => count($problems), 'machines' => count($machines)])];
        foreach ($machines as $list) {
            $lines[] = '';
            $lines[] = Agents::name($list[0]);
            foreach ($list as $p) {
                $lines[] = '• ' . $this->describe($p);
                $link = $this->link($p['agent_id'], $p['task_id']);
                if ($link !== null) {
                    $lines[] = '  ' . $link;
                }
            }
        }
        return [
            $this->lang->get('notify.subject', ['server' => $server, 'n' => count($problems)]),
            implode("\n", $lines) . "\n",
        ];
    }

    /** One line about a problem: "Documents — ошибка: …", "… не запустилось по расписанию 12.11.2026 00:00 …" or "… место в D:\Backup …". */
    public function describe(array $p): string
    {
        $task = $p['task_name'] ?? $this->lang->get('runs.unknown_task');
        if ($p['kind'] === 'space') {
            $days = (int) ($p['days_left'] ?? 0);
            return $this->lang->get($days === 0 ? 'notify.space.full' : 'notify.space', ['task' => $task, 'path' => (string) $p['message'], 'n' => $days]);
        }
        if ($p['kind'] === 'error') {
            return $p['message'] === null
                ? $this->lang->get('notify.error.no_message', ['task' => $task])
                : $this->lang->get('notify.error', ['task' => $task, 'message' => $p['message']]);
        }
        $time = self::localTime((string) $p['first_slot_at'], (string) ($p['time_zone'] ?? 'UTC'));
        return $this->lang->get($p['slot_kind'] === 'interval' ? 'notify.missed.interval' : 'notify.missed', ['task' => $task, 'time' => $time]);
    }

    /** A UTC time from the database in the agent's time zone, "12.11.2026 00:00". */
    public static function localTime(string $utc, string $zone): string
    {
        try {
            $tz = new \DateTimeZone($zone);
        } catch (\Exception) {
            $tz = new \DateTimeZone('UTC');
        }
        return (new \DateTimeImmutable($utc, new \DateTimeZone('UTC')))->setTimezone($tz)->format('d.m.Y H:i');
    }

    private function link(string $agentId, string $taskId): ?string
    {
        $url = $this->config->url();
        return $url === '' ? null : "$url/agents/$agentId/tasks/$taskId";
    }

    /** @return list<array{0: string, 1: string}> [channel, target]: one per Telegram chat, one e-mail for all recipients */
    private function targets(array $channels): array
    {
        $targets = [];
        if (in_array('telegram', $channels, true)) {
            foreach ($this->settings->lines('telegram_chat_ids') as $chat) {
                $targets[] = ['telegram', $chat];
            }
        }
        if (in_array('email', $channels, true)) {
            $targets[] = ['email', implode(', ', $this->settings->lines('mail_to'))];
        }
        return $targets;
    }

    /** Sends one message; returns null on success or the error text. */
    private function deliver(string $channel, string $target, string $subject, string $body): ?string
    {
        try {
            if ($this->sender !== null) {
                ($this->sender)($channel, $target, $subject, $body);
                return null;
            }
            if (!in_array($channel, $this->channels(), true)) {
                throw new \RuntimeException($this->lang->get('notify.error.not_configured'));
            }
            if ($channel === 'telegram') {
                $apiUrl = (string) $this->config->get('telegram_api_url', 'https://api.telegram.org');
                (new Telegram($this->settings->get('telegram_token'), $this->settings->get('telegram_proxy'), $apiUrl))->send($target, $body);
            } else {
                (new Mailer([
                    'host' => $this->settings->get('smtp_host'),
                    'port' => $this->settings->int('smtp_port'),
                    'encryption' => $this->settings->get('smtp_encryption'),
                    'user' => $this->settings->get('smtp_user'),
                    'password' => $this->settings->get('smtp_password'),
                    'from' => $this->settings->get('mail_from'),
                ]))->send(Settings::splitList($target), $subject, $body, $this->settings->get('server_name'));
            }
            return null;
        } catch (\Throwable $e) {
            return $e->getMessage();
        }
    }

    private function mark(array $problems, string $state, string $nowDb): void
    {
        foreach ($problems as $p) {
            $this->db->exec("UPDATE problems SET notify = ?, notified_at = ? WHERE id = ? AND notify = 'pending'", [$state, $nowDb, $p['id']]);
        }
    }
}
