<?php

declare(strict_types=1);

namespace Custos\Tools;

use DateTimeImmutable;
use DateTimeZone;

/**
 * One simulated CUSTOS agent (section 12): registers, sends heartbeats and configuration, runs its tasks on
 * schedule with random results and progress, keeps reports in an outbox while the server is unavailable or the
 * agent waits for approval, and executes server commands.
 *
 * Its state (keys, agent_id, tasks, outbox) is kept in a JSON file, so a restarted fake agent is the same machine.
 */
final class FakeAgent
{
    public const VERSION = '1.0.0';
    /** 7z.dll version a new fake agent reports (--sevenzip changes it). */
    public const SEVENZIP_VERSION = '24.09';

    /** Reports that the agent keeps in its outbox until the server accepts them (A.5, A.11). */
    private const KEY_BITS = 3072;

    private array $state;
    private AgentClient $client;
    private ?array $run = null;
    /** @var list<array{task_id: string, trigger: string, scheduled_for: ?int}> */
    private array $queue = [];
    private int $interval = 60;
    private int $progressEvery = 10;
    private float $nextHeartbeat = 0;
    private float $nextRegistration = 0;
    private float $nextOutbox = 0;
    private ?float $configDue = null;
    private bool $configSent = false;
    private bool $registrationTried = false;
    private bool $onceMode = false;
    private int $heartbeats = 0;
    private string $offlinePhase = 'before';
    private float $offlineUntil = 0;
    /** @var array<string, ?int> next schedule slot per task */
    private array $nextSlots = [];
    /** @var array<string, int> the latest slot each task missed while the machine was off */
    private array $missed = [];
    private float $startedAt;
    public int $problems = 0;

    /**
     * @param array{url: string, code: ?string, hostname: string, time_zone: string, run_seconds: int,
     *              skip_schedule: bool, offline_min: ?int, pause_s: ?int, result?: ?string, sevenzip?: ?string,
     *              once_run?: bool} $options
     * @param \Closure(string): void $log
     */
    public function __construct(
        private readonly string $stateFile,
        private array $options,
        private readonly \Closure $log,
        ?callable $transport = null
    ) {
        $saved = is_file($stateFile) ? json_decode((string) file_get_contents($stateFile), true) : null;
        $this->state = is_array($saved) ? $saved : $this->newState();
        // State files of older fake agents have no versions yet.
        $this->state += ['agent_version' => self::VERSION, 'sevenzip_version' => self::SEVENZIP_VERSION];
        if (array_key_exists('sevenzip', $options)) {
            $this->state['sevenzip_version'] = $options['sevenzip'];
        }
        $key = openssl_pkey_get_private($this->state['private_key']);
        if ($key === false) {
            throw new \RuntimeException("Bad private key in $stateFile");
        }
        $this->client = new AgentClient($options['url'], $key, $this->state['agent_id'], $this->state['server_public_key'], $transport);
        $this->startedAt = microtime(true);
        $this->save();
    }

    public function hostname(): string
    {
        return $this->state['hostname'];
    }

    public function agentId(): ?string
    {
        return $this->state['agent_id'];
    }

    public function status(): ?string
    {
        return $this->state['status'];
    }

    public function tasks(): array
    {
        return $this->state['tasks'];
    }

    public function fingerprint(): string
    {
        return AgentClient::fingerprint(AgentClient::publicPem(openssl_pkey_get_private($this->state['private_key'])));
    }

    /** --run-now: queue every enabled task once. */
    public function runAll(): void
    {
        foreach ($this->state['tasks'] as $task) {
            if ($task['enabled']) {
                $this->enqueue($task['id'], 'manual', null);
            }
        }
    }

    /** One step of the simulation; call it several times a second. */
    public function tick(): void
    {
        $now = microtime(true);
        if ($this->offline($now)) {
            return;
        }
        if (!$this->onceMode) {
            $this->scheduleTick($now);
        }
        $this->runTick($now);
        if ($this->state['agent_id'] === null) {
            if ($now >= $this->nextRegistration) {
                $this->register();
            }
            return;
        }
        if ($now >= $this->nextHeartbeat) {
            $this->heartbeat();
        }
        if ($this->configDue !== null && $now >= $this->configDue && $this->state['status'] === 'approved') {
            $this->sendConfig();
        }
        if ($this->state['outbox'] !== [] && $now >= $this->nextOutbox) {
            $this->flushOutbox();
        }
    }

    /**
     * --once: register if needed, heartbeat, configuration, one short manual run of the first task with progress,
     * inventory, command results, a last heartbeat. Returns true when every answer was signed and accepted.
     */
    public function once(int $timeoutSeconds = 120): bool
    {
        $this->onceMode = true;
        $this->options['run_seconds'] = min($this->options['run_seconds'], 3);
        $this->options['pause_s'] = $this->options['pause_s'] === null ? null : min($this->options['pause_s'], 5);
        $this->progressEvery = 1;
        $deadline = microtime(true) + $timeoutSeconds;
        $enqueued = false;
        $finalHeartbeat = false;
        while (microtime(true) < $deadline) {
            $this->tick();
            if ($this->state['agent_id'] === null && $this->registrationTried) {
                break;
            }
            if (!$enqueued && $this->heartbeats > 0 && ($this->state['status'] !== 'approved' || $this->configDue === null)) {
                $first = $this->state['tasks'][0] ?? null;
                // Tests that only need commands and the configuration skip the run (option once_run = false).
                if ($first !== null && ($this->options['once_run'] ?? true)) {
                    $this->enqueue($first['id'], 'manual', null);
                }
                $enqueued = true;
            }
            if ($enqueued && $this->run === null && $this->queue === []) {
                if ($this->state['status'] !== 'approved') {
                    $this->say(sprintf('waiting for approval on the site (key fingerprint %s); %d report(s) kept in the outbox',
                        $this->fingerprint(), count($this->state['outbox'])));
                    break;
                }
                if ($this->state['outbox'] === [] && $this->configDue === null) {
                    if ($finalHeartbeat) {
                        break;
                    }
                    $this->heartbeat();
                    $this->flushOutbox();
                    $finalHeartbeat = true;
                }
            }
            usleep(100000);
        }
        $this->save();
        return $this->problems === 0;
    }

    // ---- registration, heartbeat, configuration -------------------------------------------------------------

    private function register(): void
    {
        $this->registrationTried = true;
        $code = $this->options['code'];
        if ($code === null || $code === '') {
            $this->problem('cannot register: no registration code, pass --code=KEY#FINGERPRINT');
            $this->nextRegistration = microtime(true) + 3600;
            return;
        }
        $result = $this->client->register($code, [
            'hostname' => $this->state['hostname'],
            'machine_guid' => $this->state['machine_guid'],
            'os' => 'Windows 11 Pro 24H2 (26100)',
            'agent_version' => $this->state['agent_version'] ?? self::VERSION,
            'time_zone' => $this->state['time_zone'],
        ]);
        if (!$result['ok']) {
            $this->problem('registration failed: ' . $result['reason']);
            // A real agent retries in 10 minutes (A.3); the simulator is impatient.
            $this->nextRegistration = microtime(true) + 60;
            return;
        }
        $this->state['agent_id'] = $this->client->agentId;
        $this->state['server_public_key'] = $this->client->serverPublicKey;
        $this->state['status'] = $result['status'];
        $this->save();
        $this->say(sprintf('registered: agent_id %s, status %s, server key %s, agent key %s',
            $this->state['agent_id'], $result['status'], AgentClient::fingerprint($this->client->serverPublicKey), $this->fingerprint()));
        $this->nextHeartbeat = 0;
    }

    private function heartbeat(): void
    {
        $now = microtime(true);
        $this->nextHeartbeat = $now + $this->interval;
        $result = $this->client->call('POST', 'agent/heartbeat', [
            'agent_version' => $this->state['agent_version'] ?? self::VERSION,
            'sevenzip_version' => $this->state['sevenzip_version'] ?? null,
            'config_revision' => $this->state['config_revision'],
            'time_zone' => $this->state['time_zone'],
            'service_started_at' => $this->state['service_started_at'],
            'state' => $this->run === null ? 'idle' : ($this->run['paused_at'] !== null ? 'paused' : 'running'),
            'running' => $this->run === null ? null : [
                'run_id' => $this->run['run_id'],
                'task_id' => $this->run['task_id'],
                'paused_at' => $this->run['paused_at'] === null ? null : self::iso((int) $this->run['paused_at']),
            ],
            'queue' => array_column($this->queue, 'task_id'),
        ]);
        $this->heartbeats++;
        if (!$this->accepted($result, 'heartbeat')) {
            return;
        }
        $data = $result->data();
        if ($data['status'] !== $this->state['status']) {
            $this->say('server status: ' . $data['status']);
            $this->state['status'] = $data['status'];
            $this->save();
        }
        // A disabled agent keeps working on its own and only checks in every 10 minutes (A.4).
        $this->interval = $data['status'] === 'disabled' ? 600 : max(5, (int) ($data['heartbeat_interval_s'] ?? 60));
        $this->nextHeartbeat = $now + $this->interval;
        if (!empty($data['want_config'])) {
            $this->configDue = $now;
        }
        foreach ((array) ($data['commands'] ?? []) as $command) {
            $this->execute($command);
        }
        if ($data['status'] === 'approved' && $this->state['outbox'] !== []) {
            $this->nextOutbox = $now;
        }
    }

    private function sendConfig(): void
    {
        $result = $this->client->call('POST', 'agent/config', [
            'config_revision' => $this->state['config_revision'],
            'changed' => $this->state['changed'],
            'settings' => $this->state['settings'],
            'credentials' => $this->state['credentials'],
            'tasks' => $this->state['tasks'],
            'deleted_task_ids' => $this->state['deleted_task_ids'],
            'overwritten' => $this->state['overwritten'],
        ]);
        if (!$this->accepted($result, 'config')) {
            $this->configDue = microtime(true) + 60;
            return;
        }
        $this->configDue = null;
        $this->configSent = true;
        $this->state['changed'] = $this->state['deleted_task_ids'] = $this->state['overwritten'] = [];
        $this->save();
        $this->say(sprintf('configuration sent: revision %d, %d task(s)', $this->state['config_revision'], count($this->state['tasks'])));
    }

    /** Sends the outbox strictly in order (A.5); stops at the first report the server does not take. */
    private function flushOutbox(): void
    {
        while (($item = $this->state['outbox'][0] ?? null) !== null) {
            $result = $this->client->call('POST', $item['path'], $item['body']);
            $hasLog = isset($item['body']['log_gz_b64']);
            if ($result->status === 413 && $hasLog && ($result->signed || $item['path'] === 'agent/runs/finish')) {
                // Signed 413, or nginx's own unsigned 413 for a report with a log (A.11): send it without the log.
                if (!$result->signed) {
                    $this->say('unsigned 413 from the web server: check client_max_body_size; sending the report without the log');
                }
                unset($this->state['outbox'][0]['body']['log_gz_b64']);
                continue;
            }
            if ($result->ok()) {
                array_shift($this->state['outbox']);
                $this->save();
                continue;
            }
            if ($result->signed && $result->status === 400) {
                $this->problem("{$item['path']} rejected and dropped: " . $result->describe());
                array_shift($this->state['outbox']);
                $this->save();
                continue;
            }
            if ($result->signed && $result->status === 403) {
                // pending, disabled or not allowed: keep the reports and wait (A.11).
                $this->nextOutbox = microtime(true) + $this->interval;
                return;
            }
            if ($result->signed && $result->status === 429) {
                $this->nextOutbox = microtime(true) + min(3600, (int) ($result->data()['retry_after_s'] ?? 60));
                return;
            }
            $this->accepted($result, $item['path']);
            $this->nextOutbox = microtime(true) + 60;
            return;
        }
    }

    /** Common handling of an answer that is not a signed 200. Returns true for a signed 200. */
    private function accepted(ApiResult $result, string $what): bool
    {
        if ($result->ok()) {
            return true;
        }
        if ($result->signed && $result->status === 404 && $result->error() === 'unknown_agent') {
            $this->say('the server does not know this agent any more, registering again');
            $this->state['agent_id'] = null;
            $this->state['status'] = null;
            $this->client->agentId = null;
            $this->nextRegistration = 0;
            $this->save();
            return false;
        }
        if ($result->signed && $result->status === 403) {
            return false;
        }
        if ($result->signed && $result->status === 401 && $result->error() === 'clock_skew') {
            $this->problem("$what: clock differs from the server, server time " . ($result->data()['server_time'] ?? '?'));
            return false;
        }
        $this->problem("$what: " . $result->describe());
        return false;
    }

    // ---- schedule and runs ------------------------------------------------------------------------------------

    private function scheduleTick(float $now): void
    {
        $this->forDueSlots($now, function (array $task, int $slot) {
            if ($this->options['skip_schedule']) {
                $this->say(sprintf('--skip-schedule: not running %s for the slot %s', $task['name'], self::iso($slot)));
            } else {
                $this->enqueue($task['id'], 'schedule', $slot);
            }
        });
    }

    /** Calls $due for every schedule slot that has come, and moves on to the next slot. */
    private function forDueSlots(float $now, callable $due): void
    {
        foreach ($this->state['tasks'] as $task) {
            $id = $task['id'];
            if (!$task['enabled']) {
                unset($this->nextSlots[$id]);
                continue;
            }
            if (!array_key_exists($id, $this->nextSlots)) {
                $anchor = (int) ($this->state['last_runs'][$id] ?? $this->startedAt);
                $this->nextSlots[$id] = Schedule::next($task['schedule'], $this->state['time_zone'], (int) $now, $anchor);
            }
            while ($this->nextSlots[$id] !== null && $this->nextSlots[$id] <= $now) {
                $slot = $this->nextSlots[$id];
                $due($task, $slot);
                $this->nextSlots[$id] = Schedule::next($task['schedule'], $this->state['time_zone'], $slot, $slot);
            }
        }
    }

    /** --offline: once approved and synced, the machine is "switched off" for a while, then catches up (A.5). */
    private function offline(float $now): bool
    {
        if ($this->options['offline_min'] === null || $this->onceMode || $this->offlinePhase === 'after') {
            return false;
        }
        if ($this->offlinePhase === 'before') {
            if (!$this->configSent || $this->run !== null || $this->state['outbox'] !== []) {
                return false;
            }
            $this->offlinePhase = 'off';
            $this->offlineUntil = $now + $this->options['offline_min'] * 60;
            $this->queue = [];
            $this->say(sprintf('--offline: the machine is switched off for %d minute(s)', $this->options['offline_min']));
        }
        $this->forDueSlots($now, function (array $task, int $slot) {
            $this->missed[$task['id']] = $slot;
        });
        if ($now < $this->offlineUntil) {
            return true;
        }
        $this->offlinePhase = 'after';
        $this->state['service_started_at'] = self::iso(time());
        $this->say('--offline: the machine is switched on again');
        foreach ($this->missed as $taskId => $slot) {
            $task = $this->task($taskId);
            if ($task !== null && !empty($task['schedule']['catch_up'])) {
                $this->enqueue($taskId, 'catch_up', $slot);
            }
        }
        $this->missed = [];
        $this->nextHeartbeat = 0;
        return false;
    }

    private function enqueue(string $taskId, string $trigger, ?int $scheduledFor): void
    {
        if (($this->run['task_id'] ?? null) === $taskId || in_array($taskId, array_column($this->queue, 'task_id'), true)) {
            return;
        }
        $this->queue[] = ['task_id' => $taskId, 'trigger' => $trigger, 'scheduled_for' => $scheduledFor];
    }

    private function runTick(float $now): void
    {
        if ($this->run === null) {
            if ($this->queue !== []) {
                $this->startRun(array_shift($this->queue), $now);
            }
            return;
        }
        if ($this->run['paused_at'] !== null) {
            if ($this->run['resume_at'] !== null && $now >= $this->run['resume_at']) {
                $this->resume($now);
            }
            return;
        }
        if ($this->options['pause_s'] !== null && !$this->run['paused_once'] && $now >= $this->run['pause_point']) {
            $this->pause($now, $now + $this->options['pause_s']);
            return;
        }
        if ($now >= $this->run['ends_at']) {
            $this->finishRun(null);
            return;
        }
        if ($now >= $this->run['next_progress']) {
            $this->sendProgress($now);
            $this->run['next_progress'] = $now + $this->progressEvery;
        }
    }

    private function startRun(array $item, float $now): void
    {
        $task = $this->task($item['task_id']);
        if ($task === null) {
            return;
        }
        $duration = max(2.0, $this->options['run_seconds'] * (0.5 + mt_rand() / mt_getrandmax()));
        $this->run = [
            'run_id' => SampleTasks::uuid(),
            'task_id' => $task['id'],
            'task' => $task,
            'trigger' => $item['trigger'],
            'scheduled_for' => $item['scheduled_for'],
            'started' => $now,
            'duration' => $duration,
            'ends_at' => $now + $duration,
            'next_progress' => $now + min(1, $this->progressEvery),
            'paused_at' => null,
            'resume_at' => null,
            'paused_once' => false,
            'pause_point' => $now + $duration / 2,
            'paused_total' => 0.0,
        ];
        $this->state['last_runs'][$task['id']] = (int) $now;
        $this->push('agent/runs/start', [
            'run_id' => $this->run['run_id'],
            'task_id' => $task['id'],
            'task_revision' => $task['revision'],
            'backup_type' => $task['backup_type'],
            'trigger' => $item['trigger'],
            'scheduled_for' => $item['scheduled_for'] === null ? null : self::iso($item['scheduled_for']),
            'started_at' => self::iso((int) $now),
        ]);
        $this->say(sprintf('started %s (%s%s)', $task['name'], $item['trigger'],
            $item['scheduled_for'] === null ? '' : ', slot ' . self::iso($item['scheduled_for'])));
    }

    private function pause(float $now, ?float $resumeAt): void
    {
        $this->run['paused_at'] = $now;
        $this->run['resume_at'] = $resumeAt;
        $this->run['paused_once'] = true;
        $this->nextHeartbeat = 0;
        $this->say('paused ' . $this->run['task']['name'] . ($resumeAt === null ? '' : sprintf(' for %d s', $resumeAt - $now)));
    }

    private function resume(float $now): void
    {
        $paused = $now - $this->run['paused_at'];
        $this->run['paused_total'] += $paused;
        $this->run['ends_at'] += $paused;
        $this->run['paused_at'] = null;
        $this->run['resume_at'] = null;
        $this->nextHeartbeat = 0;
        $this->say('resumed ' . $this->run['task']['name']);
    }

    private function sendProgress(float $now): void
    {
        if ($this->state['agent_id'] === null || $this->state['status'] !== 'approved') {
            return;
        }
        $run = $this->run;
        $percent = min(99.9, max(0, ($now - $run['started'] - $run['paused_total']) / $run['duration'] * 100));
        $phase = match (true) {
            $percent < 3 => 'pre_events', $percent < 6 => 'vss', $percent < 15 => 'scan',
            $percent < 85 => 'copy', $percent < 92 => 'verify', $percent < 97 => 'retention', default => 'post_events',
        };
        [$files, $bytes] = $this->sourceSize($run['task_id']);
        $result = $this->client->call('POST', 'agent/runs/progress', [
            'run_id' => $run['run_id'],
            'phase' => $phase,
            'percent' => round($percent, 1),
            'files_done' => (int) ($files * $percent / 100),
            'files_total' => $files,
            'bytes_done' => (int) ($bytes * $percent / 100),
            'bytes_total' => $bytes,
            'speed_bps' => random_int(20, 80) * 1024 * 1024,
            'eta_s' => (int) max(0, $run['ends_at'] - $now),
            'current_file' => ($run['task']['sources'][0]['path'] ?? 'C:\\') . '\\reports\\' . date('Y') . '-' . random_int(1, 12) . '.pdf',
        ]);
        $this->accepted($result, 'progress');
    }

    /** Finishes the current run with a random result, or with $status (aborted by task.stop). */
    private function finishRun(?string $status): void
    {
        $run = $this->run;
        $task = $run['task'];
        $roll = mt_rand(1, 100);
        $status ??= $this->options['result'] ?? ($roll <= 80 ? 'success' : ($roll <= 92 ? 'warning' : 'error'));
        [$files, $bytes] = $this->sourceSize($task['id']);
        $warnings = $status === 'warning' ? random_int(1, 12) : 0;
        $errors = $status === 'error' ? random_int(1, 3) : 0;
        $written = $task['backup_type'] === 'full' ? (int) ($bytes * 0.6) : (int) ($bytes * random_int(1, 8) / 100);
        if ($status === 'error' || $status === 'aborted') {
            $written = (int) ($written * random_int(0, 50) / 100);
        }
        $setName = $this->setName($task, (int) $run['started']);
        $errorMessage = match ($status) {
            'error' => self::pick([
                'Нет доступа к ' . ($task['destinations'][0]['path'] ?? 'назначению') . ': отказано в доступе (5)',
                'Недостаточно места в назначении: нужно 12,4 ГБ, свободно 3,1 ГБ',
                'Не удалось создать теневую копию тома D: (0x8004230F)',
            ]),
            default => null,
        };
        $destinations = [];
        foreach ($task['destinations'] as $i => $d) {
            $failed = $status === 'error' && $i === 0;
            $destinations[] = [
                'destination_id' => $d['id'],
                'status' => $status === 'aborted' ? 'aborted' : ($failed ? 'error' : ($status === 'warning' ? 'warning' : 'success')),
                'set_name' => $failed || $status === 'aborted' ? null : $setName,
                'bytes' => $failed ? 0 : $written,
                'error' => $failed ? $errorMessage : null,
            ];
        }
        $lines = $this->logLines($task, $status, $files, $warnings, $errorMessage);
        $log = base64_encode(gzencode(implode("\n", $lines) . "\n", 6));
        $finish = [
            'run_id' => $run['run_id'],
            'task_id' => $task['id'],
            'status' => $status,
            'finished_at' => self::iso(time()),
            'source_files' => $files,
            'source_bytes' => $bytes,
            'files_processed' => $status === 'error' ? random_int(0, $files) : $files - $warnings,
            'bytes_written' => $written,
            'errors' => $errors,
            'warnings' => $warnings,
            'error_message' => $errorMessage,
            'destinations' => $destinations,
            'verify' => ($task['verify']['every_runs'] ?? 0) > 0 && in_array($status, ['success', 'warning'], true)
                ? ['performed' => true, 'ok' => true, 'scope' => 'new', 'sets_checked' => 1, 'failed_sets' => []]
                : ['performed' => false, 'ok' => null, 'scope' => null, 'sets_checked' => 0, 'failed_sets' => []],
            'retention' => ['deleted_sets' => $status === 'success' ? random_int(0, 1) : 0],
            'log_tail' => implode("\n", array_slice($lines, -100)),
        ];
        if ($status === 'warning' || $status === 'error') {
            $finish['log_gz_b64'] = $log;
        }
        $this->state['logs'][$run['run_id']] = $log;
        $this->state['logs'] = array_slice($this->state['logs'], -20, null, true);
        if (in_array($status, ['success', 'warning'], true)) {
            foreach ($task['destinations'] as $d) {
                $this->state['sets'][$d['id']][] = [
                    'name' => $setName,
                    'type' => $task['backup_type'] === 'full' ? 'full' : $task['backup_type'],
                    'time' => self::iso((int) $run['started']),
                    'bytes' => $written,
                    'verified_at' => $finish['verify']['performed'] ? self::iso(time()) : null,
                    'verify_ok' => $finish['verify']['performed'] ? true : null,
                ];
                $keep = max(1, (int) ($task['retention']['keep_full'] ?? 10));
                $this->state['sets'][$d['id']] = array_slice($this->state['sets'][$d['id']], -$keep);
                $this->state['free_bytes'][$d['id']] = max(0, $this->freeBytes($d) - $written);
            }
        }
        $this->run = null;
        $this->push('agent/runs/finish', $finish);
        $this->push('agent/inventory', $this->inventory($task['id']));
        $this->nextHeartbeat = min($this->nextHeartbeat, microtime(true) + 1);
        $this->say(sprintf('finished %s: %s', $task['name'], $status));
    }

    // ---- commands (A.4) ------------------------------------------------------------------------------------

    private function execute(array $command): void
    {
        $id = (string) ($command['id'] ?? '');
        $kind = (string) ($command['kind'] ?? '');
        $payload = (array) ($command['payload'] ?? []);
        $expires = strtotime((string) ($command['expires_at'] ?? '')) ?: 0;
        $remoteEdit = !empty($this->state['settings']['allow_remote_edit']);
        $edits = ['task.upsert', 'task.delete', 'task.enable', 'task.disable', 'settings.update'];
        $taskId = (string) ($payload['task_id'] ?? '');

        [$status, $message] = match (true) {
            $expires < time() => ['rejected', 'expired'],
            in_array($kind, $edits, true) && !$remoteEdit => ['rejected', 'remote editing is turned off on this machine'],
            $kind === 'task.run' => $this->task($taskId) === null ? ['failed', 'unknown task'] : $this->commandRun($taskId),
            $kind === 'task.stop' => $this->commandStop($taskId),
            $kind === 'task.pause' => $this->commandPause($taskId),
            $kind === 'task.resume' => $this->commandResume($taskId),
            $kind === 'task.enable', $kind === 'task.disable' => $this->commandEnable($taskId, $kind === 'task.enable'),
            $kind === 'task.upsert' => $this->commandUpsert($payload),
            $kind === 'task.delete' => $this->commandDelete($taskId),
            $kind === 'settings.update' => $this->commandSettings((array) ($payload['settings'] ?? [])),
            $kind === 'log.fetch' => $this->commandLog((string) ($payload['run_id'] ?? '')),
            $kind === 'inventory.refresh' => $this->commandInventory(),
            $kind === 'update.install' => $this->commandUpdate($payload, false),
            $kind === 'sevenzip.update' => $this->commandUpdate($payload, true),
            default => ['rejected', "unknown command $kind"],
        };
        $this->say("command $kind: $status" . ($message === null ? '' : " ($message)"));
        $this->push("agent/commands/$id/result", ['status' => $status, 'message' => $message]);
    }

    private function commandRun(string $taskId): array
    {
        $this->enqueue($taskId, 'remote', null);
        return ['done', 'queued'];
    }

    private function commandStop(string $taskId): array
    {
        if (($this->run['task_id'] ?? null) !== $taskId) {
            return ['failed', 'the task is not running'];
        }
        $this->finishRun('aborted');
        return ['done', null];
    }

    private function commandPause(string $taskId): array
    {
        if (($this->run['task_id'] ?? null) !== $taskId || $this->run['paused_at'] !== null) {
            return ['failed', 'the task is not running'];
        }
        $this->pause(microtime(true), null);
        return ['done', null];
    }

    private function commandResume(string $taskId): array
    {
        if (($this->run['task_id'] ?? null) !== $taskId || $this->run['paused_at'] === null) {
            return ['failed', 'the task is not paused'];
        }
        $this->resume(microtime(true));
        return ['done', null];
    }

    private function commandEnable(string $taskId, bool $enabled): array
    {
        foreach ($this->state['tasks'] as &$task) {
            if ($task['id'] === $taskId) {
                $task['enabled'] = $enabled;
                $this->touchTask($task, 'server:unknown');
                unset($this->nextSlots[$taskId]);
                $this->configChanged($task);
                return ['done', null];
            }
        }
        return ['failed', 'unknown task'];
    }

    /** task.upsert: the last edit wins; a local revision that differs from base_revision is overwritten (A.4). */
    private function commandUpsert(array $payload): array
    {
        $task = (array) ($payload['task'] ?? []);
        if (!isset($task['id'], $task['name'])) {
            return ['failed', 'payload.task is incomplete'];
        }
        foreach ((array) ($payload['secrets'] ?? []) as $credentialId => $secret) {
            if ($this->client->decryptSecret((string) ($secret['secret_enc'] ?? '')) === null) {
                return ['failed', "cannot decrypt the secret of credential $credentialId"];
            }
            $this->state['credentials'] = array_values(array_filter($this->state['credentials'], fn($c) => $c['id'] !== $credentialId));
            $this->state['credentials'][] = [
                'id' => $credentialId, 'kind' => $secret['kind'] ?? 'windows', 'user' => $secret['user'] ?? '',
                'domain' => $secret['domain'] ?? '.', 'has_secret' => true,
            ];
        }
        $base = (int) ($payload['base_revision'] ?? 0);
        $existing = $this->task($task['id']);
        $message = null;
        if ($existing !== null && $existing['revision'] !== $base) {
            $this->state['overwritten'][] = $existing;
            $message = "conflict: local revision {$existing['revision']} overwritten";
        }
        $task['revision'] = max($existing['revision'] ?? 0, $base) + 1;
        $task['updated_at'] = self::iso(time());
        $task['updated_by'] = str_starts_with((string) ($task['updated_by'] ?? ''), 'server:') ? $task['updated_by'] : 'server:unknown';
        $this->state['tasks'] = array_values(array_filter($this->state['tasks'], fn($t) => $t['id'] !== $task['id']));
        $this->state['tasks'][] = $task;
        unset($this->nextSlots[$task['id']]);
        $this->configChanged($task);
        return ['done', $message];
    }

    private function commandDelete(string $taskId): array
    {
        if ($this->task($taskId) === null) {
            return ['failed', 'unknown task'];
        }
        $this->state['tasks'] = array_values(array_filter($this->state['tasks'], fn($t) => $t['id'] !== $taskId));
        $this->state['deleted_task_ids'][] = $taskId;
        $this->state['config_revision']++;
        $this->configDue = microtime(true) + ($this->onceMode ? 0 : 10);
        $this->save();
        return ['done', null];
    }

    private function commandSettings(array $settings): array
    {
        // allow_remote_edit and server_url are local settings the server cannot change (A.9).
        unset($settings['allow_remote_edit'], $settings['server_url']);
        $this->state['settings'] = array_replace($this->state['settings'], $settings);
        $this->state['config_revision']++;
        $this->configDue = microtime(true) + ($this->onceMode ? 0 : 10);
        $this->save();
        return ['done', null];
    }

    private function commandLog(string $runId): array
    {
        if (!isset($this->state['logs'][$runId])) {
            return ['failed', 'the log has been deleted'];
        }
        $this->push("agent/runs/$runId/log", ['log_gz_b64' => $this->state['logs'][$runId]]);
        return ['done', null];
    }

    /**
     * update.install and sevenzip.update (A.10): check the server signature from the command, download the file,
     * check its SHA-256, then "install" it: the fake agent only reports the new version from now on.
     */
    private function commandUpdate(array $payload, bool $sevenZip): array
    {
        $version = (string) ($payload['version'] ?? '');
        $sha256 = strtolower((string) ($payload['sha256'] ?? ''));
        $signature = (string) ($payload['signature'] ?? '');
        $signed = $sevenZip ? $this->client->verifySevenZip($version, $sha256, $signature) : $this->client->verifyUpdate($version, $sha256, $signature);
        if (!$signed || !preg_match('/^[0-9A-Za-z.+-]{1,32}$/', $version)) {
            return ['rejected', 'bad signature'];
        }
        $download = $this->client->download(($sevenZip ? 'agent/sevenzip/' : 'agent/updates/') . $version);
        if (!$download['ok']) {
            return ['failed', 'download failed: ' . $download['reason']];
        }
        if (!hash_equals($sha256, hash('sha256', $download['body']))) {
            return ['failed', 'SHA-256 of the downloaded file does not match'];
        }
        if ($sevenZip && !str_starts_with($download['body'], 'MZ')) {
            return ['failed', 'the file is not a Windows program'];
        }
        $this->state[$sevenZip ? 'sevenzip_version' : 'agent_version'] = $version;
        $this->save();
        // A real agent restarts after an update; the new version reaches the server with the next heartbeat.
        $this->nextHeartbeat = min($this->nextHeartbeat, microtime(true) + 2);
        return ['done', sprintf('%s %s installed (%s)', $sevenZip ? '7-Zip' : 'CUSTOS', $version, self::size(strlen($download['body'])))];
    }

    private static function size(int $bytes): string
    {
        return $bytes >= 1048576 ? round($bytes / 1048576, 1) . ' MB' : round($bytes / 1024) . ' KB';
    }

    private function commandInventory(): array
    {
        $this->push('agent/inventory', $this->inventory(null));
        return ['done', null];
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private function newState(): array
    {
        $key = openssl_pkey_new(['private_key_bits' => self::KEY_BITS, 'private_key_type' => OPENSSL_KEYTYPE_RSA]);
        openssl_pkey_export($key, $private);
        $sample = SampleTasks::make('local:OFFICE\\admin');
        return [
            'private_key' => $private,
            'agent_id' => null,
            'server_public_key' => null,
            'status' => null,
            'hostname' => $this->options['hostname'],
            'machine_guid' => SampleTasks::uuid(),
            'time_zone' => $this->options['time_zone'],
            'service_started_at' => self::iso(time()),
            'config_revision' => 1,
            'tasks' => $sample['tasks'],
            'credentials' => $sample['credentials'],
            'settings' => SampleTasks::settings($this->options['url']),
            'changed' => [],
            'deleted_task_ids' => [],
            'overwritten' => [],
            'outbox' => [],
            'logs' => [],
            'sets' => [],
            'free_bytes' => [],
            'last_runs' => [],
            'size_seed' => random_int(1, 1000),
            'agent_version' => self::VERSION,
            'sevenzip_version' => array_key_exists('sevenzip', $this->options) ? $this->options['sevenzip'] : self::SEVENZIP_VERSION,
        ];
    }

    private function task(string $id): ?array
    {
        foreach ($this->state['tasks'] as $task) {
            if ($task['id'] === $id) {
                return $task;
            }
        }
        return null;
    }

    private function touchTask(array &$task, string $updatedBy): void
    {
        $task['revision']++;
        $task['updated_at'] = self::iso(time());
        $task['updated_by'] = $updatedBy;
    }

    private function configChanged(array $task): void
    {
        $this->state['config_revision']++;
        $this->state['changed'][] = [
            'task_id' => $task['id'], 'revision' => $task['revision'],
            'updated_by' => $task['updated_by'], 'updated_at' => $task['updated_at'],
        ];
        // The agent sends a snapshot 10 seconds after the last change (A.6).
        $this->configDue = microtime(true) + ($this->onceMode ? 0 : 10);
        $this->save();
    }

    private function push(string $path, array $body): void
    {
        $this->state['outbox'][] = ['path' => $path, 'body' => $body];
        $this->nextOutbox = 0;
        $this->save();
    }

    private function inventory(?string $taskId): array
    {
        $destinations = [];
        foreach ($this->state['tasks'] as $task) {
            if ($taskId !== null && $task['id'] !== $taskId) {
                continue;
            }
            foreach ($task['destinations'] as $d) {
                $destinations[] = [
                    'task_id' => $task['id'],
                    'destination_id' => $d['id'],
                    'kind' => $d['kind'],
                    'path' => $d['path'],
                    'total_bytes' => $this->totalBytes($d),
                    'free_bytes' => $this->freeBytes($d),
                    'sets' => $this->state['sets'][$d['id']] ?? [],
                ];
            }
        }
        return ['destinations' => $destinations];
    }

    private function totalBytes(array $destination): int
    {
        return $destination['kind'] === 'local' ? 2_000_000_000_000 : 4_000_000_000_000;
    }

    private function freeBytes(array $destination): int
    {
        return $this->state['free_bytes'][$destination['id']] ?? (int) ($this->totalBytes($destination) * 0.4);
    }

    /** Source size of a task: stable per task with a few percent of noise, so later anomaly checks have a baseline. */
    private function sourceSize(string $taskId): array
    {
        $seed = (crc32($taskId) % 1000) + $this->state['size_seed'];
        $noise = 1 + random_int(-30, 30) / 1000;
        return [(int) (($seed * 37 + 1000) * $noise), (int) (($seed * 1_048_576 * 12 + 50_000_000) * $noise)];
    }

    private function setName(array $task, int $time): string
    {
        $local = (new DateTimeImmutable('@' . $time))->setTimezone(new DateTimeZone($this->state['time_zone']));
        return strtr($task['layout']['name_template'] ?? '{task} {yyyy}-{MM}-{dd} {HH}-{mm} ({type})', [
            '{task}' => $task['name'], '{yyyy}' => $local->format('Y'), '{MM}' => $local->format('m'), '{dd}' => $local->format('d'),
            '{HH}' => $local->format('H'), '{mm}' => $local->format('i'), '{type}' => ucfirst($task['backup_type']),
        ]);
    }

    private function logLines(array $task, string $status, int $files, int $warnings, ?string $error): array
    {
        $time = fn() => date('Y-m-d H:i:s');
        $source = $task['sources'][0]['path'] ?? 'C:\\';
        $lines = [$time() . " Начало: задание «{$task['name']}», тип {$task['backup_type']}"];
        $lines[] = $time() . ' Теневая копия создана';
        $count = min(150, $files);
        for ($i = 1; $i <= $count; $i++) {
            $lines[] = $time() . " Скопирован $source\\docs\\file-$i.docx";
        }
        for ($i = 1; $i <= $warnings; $i++) {
            $lines[] = $time() . " ПРЕДУПРЕЖДЕНИЕ: файл занят другим процессом, пропущен: $source\\locked-$i.xlsx";
        }
        if ($error !== null) {
            $lines[] = $time() . " ОШИБКА: $error";
        }
        $lines[] = $time() . " Готово: $status";
        return $lines;
    }

    private function say(string $message): void
    {
        ($this->log)($this->state['hostname'] . ': ' . $message);
    }

    private function problem(string $message): void
    {
        $this->problems++;
        $this->say($message);
    }

    private function save(): void
    {
        $dir = dirname($this->stateFile);
        if (!is_dir($dir)) {
            mkdir($dir, 0700, true);
        }
        file_put_contents($this->stateFile . '.tmp', json_encode($this->state, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE));
        rename($this->stateFile . '.tmp', $this->stateFile);
    }

    private static function pick(array $items): mixed
    {
        return $items[array_rand($items)];
    }

    private static function iso(int $time): string
    {
        return gmdate('Y-m-d\TH:i:s\Z', $time);
    }
}
