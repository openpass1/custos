<?php

declare(strict_types=1);

namespace Custos\Tests\Integration;

use Custos\App;
use Custos\Cron;
use Custos\Domain\RegistrationKey;
use Custos\Infra\Crypto;
use Custos\Infra\Time;
use Custos\Tests\TestEnv;
use Custos\Tools\AgentClient;
use PHPUnit\Framework\TestCase;

/**
 * Section 6 with a moved clock: presence, expected slots, missed and missed_offline, anomalies, problems and the
 * messages about them. Reports go through the real agent API; time-based checks are run as cron passes at chosen times.
 */
final class MonitorTest extends TestCase
{
    private App $app;
    private AgentClient $client;
    private string $agentId;
    private string $taskId;
    /** @var list<array{0: string, 1: string, 2: string, 3: string}> messages "sent" by the notifier */
    private array $sent = [];

    protected function setUp(): void
    {
        $this->app = TestEnv::app($this);
        $this->client = new AgentClient('http://custos.test', TestEnv::agentKey(1), null, null, TestEnv::transport($this->app));
        $code = RegistrationKey::code($this->app->settings()->get('registration_key'), $this->app->keys()->fingerprint());
        $result = $this->client->register($code, [
            'hostname' => 'PC-01', 'machine_guid' => 'f5a6b7c8-0000-4000-8000-000000000001',
            'os' => 'Windows 11 Pro', 'agent_version' => '1.0.0', 'time_zone' => 'UTC',
        ]);
        $this->assertTrue($result['ok'], $result['reason']);
        $this->agentId = $this->client->agentId;
        $this->app->agents()->approve($this->agentId);
        $this->taskId = Crypto::uuid4();
        $this->app->notifier()->sender = function (string $channel, string $target, string $subject, string $body): void {
            $this->sent[] = [$channel, $target, $subject, $body];
        };
    }

    public function testPresenceExtendsAndStartsNewPeriods(): void
    {
        $monitor = $this->app->monitor();
        $this->db()->exec('DELETE FROM agent_presence');
        $t = 1_800_000_000;
        $monitor->presence($this->agentId, 'approved', $t);
        $monitor->presence($this->agentId, 'approved', $t + 60);
        $monitor->presence($this->agentId, 'approved', $t + 240);
        $this->assertSame(1, (int) $this->db()->value('SELECT COUNT(*) FROM agent_presence'), 'gaps up to three minutes are one period, even with a 20-second interval');
        $monitor->presence($this->agentId, 'approved', $t + 1000);
        $this->assertSame(2, (int) $this->db()->value('SELECT COUNT(*) FROM agent_presence'));
        $this->assertTrue($monitor->wasOnline($this->agentId, $t + 100, $t + 200));
        $this->assertFalse($monitor->wasOnline($this->agentId, $t + 300, $t + 900));
        $this->assertTrue($monitor->wasOnline($this->agentId, $t + 900, $t + 1100));
    }

    public function testMissedOnLineAndMissedOfflineOff(): void
    {
        $day = strtotime('today 00:00 UTC') - 3 * 86400;
        $this->sendTask(['type' => 'daily', 'times' => ['00:00']]);
        $this->watchFrom($day - 3600);
        // On line around the first midnight, off around the second one.
        $this->presenceRow($day - 600, $day + 1200);
        $this->presenceRow($day + 86400 + 3600, $day + 86400 + 4000);

        $this->cron($day + 86400 + 1800);
        $slots = $this->slots();
        $this->assertSame(['missed', 'missed_offline'], array_column($slots, 'result'));
        $problems = $this->problems();
        $this->assertCount(1, $problems, 'a machine that was off is not a problem');
        $this->assertSame('missed', $problems[0]['kind']);
        $this->assertSame('missed', $this->taskStatus());

        // The next midnight is missed while on line too: it joins the open problem, no new message.
        $this->presenceRow($day + 2 * 86400 - 100, $day + 2 * 86400 + 1000);
        $this->setTelegram();
        $this->cron($day + 2 * 86400 + 1800);
        $problems = $this->problems();
        $this->assertCount(1, $problems);
        $this->assertSame(2, (int) $problems[0]['occurrences']);
        $this->assertSame([], $this->sent, 'problems opened while no channel was set up are not sent later');

        // A successful run closes every missed slot before it and the problem (6.3, 6.7).
        $this->finishedRun('success', $day + 2 * 86400 + 3000, trigger: 'manual');
        $this->assertSame(0, (int) $this->db()->value('SELECT COUNT(*) FROM problems WHERE closed_at IS NULL'));
        $this->assertSame(0, (int) $this->db()->value("SELECT COUNT(*) FROM expected_slots WHERE closed_at IS NULL AND result <> 'ok'"));
        $this->assertSame('run', $this->db()->value('SELECT closed_by FROM problems'));
        $this->assertSame('ok', $this->taskStatus());
    }

    public function testRunsSettleSlotsAndCatchUpClosesMissedOffline(): void
    {
        $day = strtotime('today 00:00 UTC') - 3 * 86400;
        $this->sendTask(['type' => 'daily', 'times' => ['00:00']]);
        $this->watchFrom($day - 3600);
        $this->presenceRow($day - 600, $day + 1200);
        $this->finishedRun('success', $day + 5, scheduledFor: $day);
        // A run that starts a little late without scheduled_for counts too: [T - 1 min; T + grace].
        $this->finishedRun('success', $day + 86400 + 600, trigger: 'manual');
        $this->cron($day + 2 * 86400 + 1800);
        $this->assertSame(['ok', 'ok', 'missed_offline'], array_column($this->slots(), 'result'));
        $this->assertSame('missed_offline', $this->taskStatus());

        // The machine is switched on and catches up the slot it missed (A.5).
        $this->finishedRun('error', $day + 2 * 86400 + 7200, scheduledFor: $day + 2 * 86400, trigger: 'catch_up');
        $slot = $this->slots()[2];
        $this->assertSame('catch_up', $slot['closed_by']);
        $this->assertSame('error', $this->taskStatus(), 'the catch-up run failed: an error problem');
    }

    public function testQueuedOrRunningTaskIsNotMissedYet(): void
    {
        $now = time();
        $slot = $now - 20 * 60 - ($now % 60);
        $this->sendTask(['type' => 'daily', 'times' => [gmdate('H:i', $slot)]]);
        $this->watchFrom($slot - 3600);
        $this->presenceRow($slot - 600, $now);
        $this->heartbeat(['queue' => [$this->taskId]]);
        $this->cron($now);
        $this->assertSame('pending', $this->slots()[0]['result'], 'the queue on the agent is sequential, the task may start later');

        $runId = Crypto::uuid4();
        $this->heartbeat(['state' => 'paused', 'running' => ['run_id' => $runId, 'task_id' => $this->taskId, 'paused_at' => gmdate('Y-m-d\TH:i:s\Z')]]);
        $this->cron($now);
        $this->assertSame('pending', $this->slots()[0]['result'], 'paused counts as running');
        $this->assertSame('paused', $this->taskStatus());

        $this->heartbeat();
        $this->cron($now);
        $this->assertSame('missed', $this->slots()[0]['result']);
    }

    public function testErrorProblemsAndMessages(): void
    {
        $this->sendTask(['type' => 'manual']);
        $this->setTelegram();
        $this->setEmail();
        $now = time();
        $this->finishedRun('error', $now - 600, error: "Нет доступа к \\\\fileserver\\docs\nsecond line");
        $this->finishedRun('error', $now - 300);
        $this->finishedRun('aborted', $now - 200);
        $problems = $this->problems();
        $this->assertCount(1, $problems, 'one open problem of a kind per task');
        $this->assertSame(2, (int) $problems[0]['occurrences']);
        $this->assertSame('Нет доступа к \\\\fileserver\\docs', $problems[0]['message']);
        $this->assertSame('error', $this->taskStatus());

        $this->cron($now);
        $this->assertCount(3, $this->sent, 'two Telegram chats and one e-mail');
        [$channel, $chat, $subject, $body] = $this->sent[0];
        $this->assertSame(['telegram', '-100123'], [$channel, $chat]);
        $this->assertStringContainsString('PC-01', $body);
        $this->assertStringContainsString('Documents — ошибка: Нет доступа', $body);
        $this->assertStringContainsString("http://custos.test/agents/{$this->agentId}/tasks/{$this->taskId}", $body);
        $this->assertSame(['email', 'admin@example.org, it@example.org'], array_slice($this->sent[2], 0, 2));
        $this->assertStringContainsString('новые проблемы (1)', $subject);
        $this->cron($now + 60);
        $this->assertCount(3, $this->sent, 'no repeats');

        $this->finishedRun('success', $now - 100);
        $this->assertSame([], $this->problems());
        $this->finishedRun('error', $now - 50);
        $this->cron($now + 120);
        $this->assertCount(6, $this->sent, 'a new failure after recovery is a new problem and a new message');
        $this->assertSame(6, (int) $this->db()->value("SELECT COUNT(*) FROM notifications WHERE status = 'sent' AND problem_count = 1"));
    }

    public function testFailedSendingIsRetriedForAnHour(): void
    {
        $this->sendTask(['type' => 'manual']);
        $this->setTelegram('-100123');
        $now = time();
        $this->finishedRun('error', $now - 60);
        $this->app->notifier()->sender = fn() => throw new \RuntimeException('Telegram is not reachable');
        $this->cron($now);
        $row = $this->db()->one('SELECT * FROM notifications');
        $this->assertSame('queued', $row['status']);
        $this->assertSame('Telegram is not reachable', $row['last_error']);
        $this->cron($now + 30);
        $this->assertSame(1, (int) $this->db()->value('SELECT attempts FROM notifications'), 'the next attempt waits a minute');
        $this->cron($now + 3700);
        $this->assertSame('failed', $this->db()->value('SELECT status FROM notifications'));
    }

    public function testDisablingOrDeletingClosesProblems(): void
    {
        $this->sendTask(['type' => 'manual']);
        $this->finishedRun('error', time() - 60);
        $this->sendTask(['type' => 'manual'], enabled: false, revision: 2);
        $this->assertSame('task_disabled', $this->db()->value('SELECT closed_by FROM problems'));
        $this->assertSame('disabled', $this->taskStatus());

        $this->sendTask(['type' => 'manual'], revision: 3);
        $this->finishedRun('error', time() - 30);
        $this->app->agents()->setDisabled($this->agentId, true);
        $this->assertSame(0, (int) $this->db()->value('SELECT COUNT(*) FROM problems WHERE closed_at IS NULL'));
        $this->assertSame('agent_disabled', $this->db()->value('SELECT closed_by FROM problems ORDER BY id DESC LIMIT 1'));
    }

    public function testIntervalTaskThatDoesNotRunIsMissed(): void
    {
        $now = time();
        $this->sendTask(['type' => 'interval', 'interval_minutes' => 15, 'times' => []]);
        $this->watchFrom($now - 7200);
        $this->presenceRow($now - 7200, $now);
        $this->heartbeat();
        $this->finishedRun('success', $now - 40 * 60);
        $this->cron($now);
        $slots = $this->slots();
        $this->assertCount(1, $slots);
        $this->assertSame(['interval', 'missed'], [$slots[0]['kind'], $slots[0]['result']]);
        $this->assertSame(Time::db($now - 25 * 60), $slots[0]['slot_at']);
        $this->cron($now + 60);
        $this->assertCount(1, $this->slots(), 'one open interval miss at a time');
        $this->assertSame(1, (int) $this->db()->value('SELECT occurrences FROM problems'));

        $this->finishedRun('success', $now - 30);
        $this->assertSame([], $this->problems());
    }

    public function testAnomalies(): void
    {
        $this->sendTask(['type' => 'manual']);
        $now = time();
        foreach ([1000, 1040, 980] as $i => $files) {
            $this->finishedRun('success', $now - 3600 + $i * 60, sourceFiles: $files);
        }
        $this->assertSame(0, (int) $this->db()->value('SELECT anomaly FROM tasks'));
        $runId = $this->finishedRun('success', $now - 3000, sourceFiles: 400);
        $anomaly = json_decode((string) $this->db()->value('SELECT anomaly FROM runs WHERE id = ?', [$runId]), true);
        $this->assertSame(-60, $anomaly['source_files']['change_pct'], 'a drop of more than 30 %');
        $this->assertSame('anomaly', $this->taskStatus());
        $runId = $this->finishedRun('success', $now - 2900, sourceFiles: 1010);
        $this->assertNull($this->db()->value('SELECT anomaly FROM runs WHERE id = ?', [$runId]));
        $this->assertSame('ok', $this->taskStatus());
        $runId = $this->finishedRun('warning', $now - 2800, sourceFiles: 2500);
        $this->assertNotNull($this->db()->value('SELECT anomaly FROM runs WHERE id = ?', [$runId]), 'more than twice the median');
        $this->assertSame('warning', $this->taskStatus(), 'warning is above anomaly (6.6)');
    }

    public function testStaleAndNewTasks(): void
    {
        $this->sendTask(['type' => 'daily', 'times' => ['03:00']]);
        $this->assertSame('never', $this->taskStatus());
        $this->watchFrom(time() - 8 * 86400);
        $this->assertSame('stale', $this->taskStatus());
        $this->finishedRun('success', time() - 8 * 86400);
        $this->assertSame('stale', $this->taskStatus(), 'the last success is older than 7 days');
        $this->finishedRun('success', time() - 3600);
        $this->assertSame('ok', $this->taskStatus());
        $board = $this->app->board();
        $this->assertNotNull($board->task($this->agentId, $this->taskId)['next_at']);
    }

    public function testCommandsExpireAndLoseSecrets(): void
    {
        $commands = $this->app->commands();
        $id = $commands->create($this->agentId, 'task.upsert', ['secrets' => ['c1' => ['secret_enc' => 'abc']]], 60);
        $this->assertSame(1, $commands->expire(time() + 120));
        $row = $this->db()->one('SELECT status, payload FROM commands WHERE id = ?', [$id]);
        $this->assertSame('expired', $row['status']);
        $this->assertStringNotContainsString('abc', $row['payload']);
    }

    public function testCleanupKeepsRecentData(): void
    {
        $this->sendTask(['type' => 'manual']);
        $old = $this->finishedRun('success', time() - 400 * 86400);
        $this->finishedRun('success', time() - 100 * 86400);
        $this->db()->exec('INSERT INTO run_logs (run_id, log_gz, source, received_at) SELECT id, ?, \'finish\', started_at FROM runs', [gzencode('x')]);
        $result = (new Cron($this->app))->run(time());
        $this->assertSame([], $result['errors']);
        $this->assertSame(1, (int) $this->db()->value('SELECT COUNT(*) FROM runs'));
        $this->assertNull($this->db()->value('SELECT 1 FROM runs WHERE id = ?', [$old]));
        $this->assertSame(0, (int) $this->db()->value('SELECT COUNT(*) FROM run_logs'), 'full logs are kept 90 days');
    }

    // ---- helpers ----

    private function db(): \Custos\Infra\Db
    {
        return $this->app->db();
    }

    private function cron(int $now): void
    {
        $result = (new Cron($this->app))->run($now);
        $this->assertSame([], $result['errors']);
    }

    private function sendTask(array $schedule, bool $enabled = true, int $revision = 1): void
    {
        static $config = 0;
        $config++;
        $result = $this->client->call('POST', 'agent/config', [
            'config_revision' => $config, 'changed' => [], 'settings' => ['allow_remote_edit' => true], 'credentials' => [],
            'tasks' => [[
                'id' => $this->taskId, 'revision' => $revision, 'updated_at' => gmdate('Y-m-d\TH:i:s\Z'), 'updated_by' => 'local:OFFICE\\admin',
                'name' => 'Documents', 'enabled' => $enabled, 'backup_type' => 'incremental', 'schedule' => $schedule + ['catch_up' => true],
            ]],
            'deleted_task_ids' => [], 'overwritten' => [],
        ]);
        $this->assertTrue($result->ok(), $result->describe());
    }

    private function watchFrom(int $time): void
    {
        $this->db()->exec('UPDATE tasks SET watch_from = ?, slots_until = ?', [Time::db($time), Time::db($time)]);
    }

    private function presenceRow(int $from, int $to): void
    {
        $this->db()->exec('INSERT INTO agent_presence (agent_id, from_at, to_at) VALUES (?, ?, ?)', [$this->agentId, Time::db($from), Time::db($to)]);
    }

    private function heartbeat(array $overrides = []): void
    {
        $result = $this->client->call('POST', 'agent/heartbeat', $overrides + [
            'agent_version' => '1.0.0', 'config_revision' => 1, 'time_zone' => 'UTC',
            'service_started_at' => null, 'state' => 'idle', 'running' => null, 'queue' => [],
        ]);
        $this->assertTrue($result->ok(), $result->describe());
    }

    /** A finished run of the task; returns its id. */
    private function finishedRun(
        string $status,
        int $startedAt,
        ?int $scheduledFor = null,
        string $trigger = 'schedule',
        ?string $error = null,
        int $sourceFiles = 1000
    ): string {
        $runId = Crypto::uuid4();
        $iso = fn(int $t) => gmdate('Y-m-d\TH:i:s\Z', $t);
        if ($scheduledFor === null && in_array($trigger, ['schedule', 'catch_up'], true)) {
            $trigger = 'manual';
        }
        $start = $this->client->call('POST', 'agent/runs/start', [
            'run_id' => $runId, 'task_id' => $this->taskId, 'task_revision' => 1, 'backup_type' => 'incremental',
            'trigger' => $trigger, 'scheduled_for' => $scheduledFor === null ? null : $iso($scheduledFor), 'started_at' => $iso($startedAt),
        ]);
        $this->assertTrue($start->ok(), $start->describe());
        $finish = $this->client->call('POST', 'agent/runs/finish', [
            'run_id' => $runId, 'task_id' => $this->taskId, 'status' => $status, 'finished_at' => $iso($startedAt + 20),
            'source_files' => $sourceFiles, 'source_bytes' => $sourceFiles * 1000, 'files_processed' => 10, 'bytes_written' => 1000,
            'errors' => $status === 'error' ? 1 : 0, 'warnings' => 0, 'error_message' => $status === 'error' ? ($error ?? 'Disk full') : null,
            'destinations' => [], 'verify' => null, 'retention' => null, 'log_tail' => '',
        ]);
        $this->assertTrue($finish->ok(), $finish->describe());
        return $runId;
    }

    private function slots(): array
    {
        return $this->db()->all('SELECT * FROM expected_slots ORDER BY slot_at');
    }

    private function problems(): array
    {
        return $this->db()->all('SELECT * FROM problems WHERE closed_at IS NULL ORDER BY id');
    }

    private function taskStatus(): string
    {
        return $this->app->board()->task($this->agentId, $this->taskId)['status'];
    }

    private function setTelegram(string $chats = "-100123\n-100456"): void
    {
        $this->app->settings()->set('telegram_token', '123:abc');
        $this->app->settings()->set('telegram_chat_ids', $chats);
    }

    private function setEmail(): void
    {
        foreach (['smtp_host' => 'mail.example.org', 'mail_from' => 'custos@example.org', 'mail_to' => "admin@example.org\nit@example.org"] as $name => $value) {
            $this->app->settings()->set($name, $value);
        }
    }
}
