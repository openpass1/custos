<?php

declare(strict_types=1);

namespace Custos\Tests\Integration;

use Custos\App;
use Custos\Cron;
use Custos\Domain\RegistrationKey;
use Custos\Domain\Storage;
use Custos\Http\Request;
use Custos\Http\Response;
use Custos\Infra\Crypto;
use Custos\Infra\Time;
use Custos\Tests\TestEnv;
use Custos\Tools\AgentClient;
use PHPUnit\Framework\TestCase;

/**
 * Stage 4 in-process: Zabbix endpoints and /api/health, the wall screen, the heat map, storage forecasts, task charts
 * and database dumps of a run. The agent talks through the real agent API.
 */
final class InsightsTest extends TestCase
{
    private const DEST = 'dddddddd-0000-4000-8000-000000000001';
    private const IP = '192.0.2.10';

    private App $app;
    private AgentClient $client;
    private string $agentId;
    private string $taskId;

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
        $config = $this->client->call('POST', 'agent/config', [
            'config_revision' => 1, 'changed' => [], 'settings' => ['allow_remote_edit' => true], 'credentials' => [],
            'tasks' => [[
                'id' => $this->taskId, 'revision' => 1, 'updated_at' => gmdate('Y-m-d\TH:i:s\Z'), 'updated_by' => 'local:OFFICE\\admin',
                'name' => 'Documents', 'enabled' => true, 'backup_type' => 'incremental',
                'schedule' => ['type' => 'daily', 'times' => ['02:00'], 'catch_up' => true],
                'destinations' => [['id' => self::DEST, 'kind' => 'unc', 'path' => '\\\\nas\\backup']],
            ]],
            'deleted_task_ids' => [], 'overwritten' => [],
        ]);
        $this->assertTrue($config->ok(), $config->describe());
        $this->assertSame(['zabbix_token', 'wall_token'], $this->app->integrations()->ensureTokens());
        $this->assertSame([], $this->app->integrations()->ensureTokens(), 'existing tokens are kept');
    }

    public function testZabbixEndpointsNeedTheTokenAndTheAddress(): void
    {
        $token = $this->app->settings()->get('zabbix_token');
        $this->assertSame(48, strlen($token));

        $this->assertSame(401, $this->get('/api/zabbix/status')->status);
        $this->assertSame(401, $this->get('/api/zabbix/status', ['token' => 'wrong'])->status);
        $this->assertSame(401, $this->get('/api/zabbix/status', ['token' => $this->app->settings()->get('wall_token')])->status,
            'the wall token is not a Zabbix token');
        $this->assertSame(200, $this->get('/api/zabbix/status', ['token' => $token])->status);
        $this->assertSame(200, $this->get('/api/zabbix/status', [], ['x-custos-token' => $token])->status);
        $this->assertSame(404, $this->get('/api/zabbix/nothing', ['token' => $token])->status);
        $post = $this->app->handle(new Request('POST', '/api/zabbix/status', [], self::IP, query: ['token' => $token]));
        $this->assertSame(405, $post->status);

        $this->app->settings()->set('ip_allow_integrations', '198.51.100.0/24');
        $denied = $this->get('/api/zabbix/status', ['token' => $token]);
        $this->assertSame(403, $denied->status);
        $this->assertSame('ip_not_allowed', json_decode($denied->content(), true)['error']);
        $this->assertSame(200, $this->get('/api/zabbix/status', ['token' => $token], [], '198.51.100.7')->status);
        $this->assertNotSame(403, $this->get('/api/health')->status, '/api/health is open to every address');
        $this->app->settings()->set('ip_allow_integrations', '');

        $this->app->settings()->set('zabbix_token', '');
        $this->assertSame(401, $this->get('/api/zabbix/status', ['token' => ''])->status, 'an empty token lets nobody in');
    }

    public function testZabbixDiscoveryAndStatus(): void
    {
        $token = ['token' => $this->app->settings()->get('zabbix_token')];
        $key = $this->agentId . '/' . $this->taskId;

        $agents = $this->json('/api/zabbix/discovery/agents', $token);
        $this->assertSame([['{#AGENT_ID}' => $this->agentId, '{#AGENT}' => 'PC-01', '{#GROUP}' => '']], $agents);
        $tasks = $this->json('/api/zabbix/discovery/tasks', $token);
        $this->assertCount(1, $tasks);
        $this->assertSame($key, $tasks[0]['{#TASK_KEY}']);
        $this->assertSame('Documents', $tasks[0]['{#TASK}']);

        $status = $this->json('/api/zabbix/status', $token);
        $this->assertSame(9, $status['tasks'][$key]['status_code'], 'not run yet');
        $this->assertSame(-1, $status['tasks'][$key]['last_success_age_s']);
        $this->assertSame(1, $status['summary']['agents']);
        $this->assertArrayHasKey($this->agentId, $status['agents']);

        $this->finishedRun('error', time() - 600);
        $status = $this->json('/api/zabbix/status', $token);
        $this->assertSame(6, $status['tasks'][$key]['status_code']);
        $this->assertSame('error', $status['tasks'][$key]['last_run_status']);
        $this->assertSame(1, $status['summary']['problems']);

        $this->finishedRun('success', time() - 300, written: 4096);
        $status = $this->json('/api/zabbix/status', $token);
        $this->assertSame(0, $status['tasks'][$key]['status_code']);
        $this->assertSame(4096, $status['tasks'][$key]['last_bytes_written']);
        $this->assertGreaterThanOrEqual(0, $status['tasks'][$key]['last_success_age_s']);

        // A machine that is not approved any more drops out of discovery and status.
        $this->app->agents()->setDisabled($this->agentId, true);
        $this->assertSame([], $this->json('/api/zabbix/discovery/tasks', $token));
        $this->assertSame([], (array) $this->json('/api/zabbix/status', $token)['tasks']);
    }

    public function testHealthChecksCron(): void
    {
        $response = $this->get('/api/health');
        $this->assertSame(503, $response->status);
        $this->assertSame(['status' => 'fail', 'db' => 'ok', 'cron_lag_s' => null], json_decode($response->content(), true));
        $result = (new Cron($this->app))->run(time());
        $this->assertSame([], $result['errors']);
        $response = $this->get('/api/health');
        $this->assertSame(200, $response->status, $response->content());
        $this->assertSame('ok', json_decode($response->content(), true)['status']);
        $this->assertSame('no-store', $response->header('Cache-Control'));
    }

    public function testWallScreenNeedsItsTokenOnly(): void
    {
        $token = $this->app->settings()->get('wall_token');
        $this->finishedRun('error', time() - 600, error: 'Disk full');
        // The site allow-list does not apply to the wall: it has its own (the integrations one).
        $this->app->settings()->set('ip_allow_site', '198.51.100.0/24');

        $this->assertSame(403, $this->get('/wall')->status);
        $this->assertSame(403, $this->get('/wall', ['token' => 'wrong'])->status);
        $this->assertSame(403, $this->get('/wall', ['token' => $this->app->settings()->get('zabbix_token')])->status);
        $page = $this->get('/wall', ['token' => $token]);
        $this->assertSame(200, $page->status);
        $html = $page->content();
        $this->assertStringContainsString('data-theme="dark"', $html);
        $this->assertStringContainsString('PC-01', $html);
        $this->assertStringContainsString('Documents', $html);
        $this->assertStringContainsString('Disk full', $html);
        $this->assertStringNotContainsString('name="csrf', $html, 'no forms and no session on the wall');

        $live = $this->get('/wall/live', ['token' => $token]);
        $this->assertSame(200, $live->status);
        $this->assertStringContainsString('Documents', json_decode($live->content(), true)['parts']['board']);

        $this->app->settings()->set('ip_allow_integrations', '198.51.100.0/24');
        $this->assertSame(403, $this->get('/wall', ['token' => $token])->status);
        $this->assertSame(200, $this->get('/wall', ['token' => $token], [], '198.51.100.7')->status);

        // The wall token does not open the site: the site keeps its own allow-list and asks for a login.
        $this->assertSame(403, $this->get('/', ['token' => $token])->status);
        $this->app->settings()->set('ip_allow_site', '');
        $site = $this->get('/', ['token' => $token]);
        $this->assertContains($site->status, [302, 303]);
        $this->assertStringContainsString('/login', (string) $site->header('Location'));
    }

    public function testHeatMapTakesTheWorstResultOfADay(): void
    {
        $now = time();
        $today = gmdate('Y-m-d', $now);
        $yesterday = gmdate('Y-m-d', $now - 86400);
        $twoDaysAgo = gmdate('Y-m-d', $now - 2 * 86400);
        $this->finishedRun('success', $now - 86400);
        $this->finishedRun('warning', $now - 86400 + 60);
        $this->finishedRun('success', $now - 2 * 86400);
        $this->finishedRun('aborted', $now - 2 * 86400 + 60);
        $this->db()->exec("INSERT INTO expected_slots (agent_id, task_id, slot_at, result, decided_at) VALUES (?, ?, ?, 'missed_offline', ?)",
            [$this->agentId, $this->taskId, Time::db($now - 2 * 86400 + 120), Time::db($now)]);
        $this->finishedRun('error', $now - 120);

        $map = $this->app->analytics()->heatmap($now, 'UTC');
        $this->assertCount(30, $map['days']);
        $this->assertSame($today, end($map['days']));
        $this->assertCount(1, $map['rows']);
        $cells = $map['rows'][0]['cells'];
        $this->assertSame('error', $cells[$today]['worst']);
        $this->assertSame('warning', $cells[$yesterday]['worst']);
        $this->assertSame(['success' => 1, 'warning' => 1], $cells[$yesterday]['counts']);
        $this->assertSame('missed_offline', $cells[$twoDaysAgo]['worst']);
        $this->assertSame(['success' => 1, 'missed_offline' => 1], $cells[$twoDaysAgo]['counts'], 'aborted runs do not count');
        $this->assertNull($cells[gmdate('Y-m-d', $now - 3 * 86400)]['worst']);
        $this->assertSame('error', $map['total'][$today]['worst']);

        // Runs of deleted tasks leave the map.
        $this->db()->exec('UPDATE tasks SET deleted = 1');
        $this->db()->exec('DELETE FROM tasks');
        $map = $this->app->analytics()->heatmap($now, 'UTC');
        $this->assertSame(['missed_offline' => 1], $map['rows'][0]['cells'][$twoDaysAgo]['counts']);
    }

    public function testTaskChartPoints(): void
    {
        $now = time();
        $first = $this->finishedRun('success', $now - 5 * 86400, sourceFiles: 1000);
        $this->finishedRun('success', $now - 4 * 86400, sourceFiles: 1000);
        $this->finishedRun('success', $now - 3 * 86400, sourceFiles: 1000);
        $this->finishedRun('aborted', $now - 2 * 86400);
        $this->finishedRun('success', $now - 86400, sourceFiles: 300);
        $this->finishedRun('success', $now - 100 * 86400);
        $points = $this->app->analytics()->taskChart($this->agentId, $this->taskId, $now);
        $this->assertCount(4, $points, 'aborted runs and runs older than 90 days are left out');
        $this->assertSame($first, $points[0]['id'], 'oldest first');
        $this->assertSame(1000 * 1000, $points[0]['source_bytes']);
        $this->assertSame(20, $points[0]['duration']);
        $this->assertFalse($points[0]['anomaly']);
        $this->assertTrue($points[3]['anomaly'], 'the source shrank by 70 %');
        $this->assertSame(300 * 1000, $points[3]['source_bytes']);
    }

    public function testInventoryForecastAndSets(): void
    {
        $now = time();
        $result = $this->client->call('POST', 'agent/inventory', ['destinations' => [[
            'task_id' => $this->taskId, 'destination_id' => self::DEST, 'kind' => 'unc', 'path' => '\\\\nas\\backup',
            'total_bytes' => 1000 * 10 ** 9, 'free_bytes' => 50 * 10 ** 9,
            'sets' => [
                ['name' => '2026-11-10 02-00 full', 'type' => 'full', 'time' => '2026-11-10T02:00:00Z', 'bytes' => 10 ** 9, 'verified_at' => '2026-11-10T03:00:00Z', 'verify_ok' => false],
                ['name' => '2026-11-11 02-00 incremental', 'type' => 'incremental', 'time' => '2026-11-11T02:00:00Z', 'bytes' => 10 ** 8, 'verified_at' => null, 'verify_ok' => null],
            ],
        ], [
            // A destination the task does not have (removed from it): not shown.
            'task_id' => $this->taskId, 'destination_id' => 'eeeeeeee-0000-4000-8000-000000000002', 'kind' => 'local', 'path' => 'D:\\Old',
            'total_bytes' => 10 ** 12, 'free_bytes' => 1, 'sets' => [],
        ]]]);
        $this->assertTrue($result->ok(), $result->describe());
        // Ten days of history: 10 GB less free space each day, ending with the 50 GB just reported.
        for ($d = 10; $d >= 1; $d--) {
            $this->db()->exec('INSERT INTO storage_space (agent_id, task_id, destination_id, kind, path, total_bytes, free_bytes, recorded_at)
                               VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
                [$this->agentId, $this->taskId, self::DEST, 'unc', '\\\\nas\\backup', 1000 * 10 ** 9, (50 + 10 * $d) * 10 ** 9, Time::db($now - $d * 86400)]);
        }

        $storage = $this->app->storage();
        $this->assertSame(2, $storage->pass($now));
        $this->assertNull($storage->pass($now + 60), 'once an hour');
        $this->assertSame(2, $storage->pass($now + Storage::EVERY_S + 1));

        $rows = $storage->forAgent($this->agentId);
        $this->assertCount(1, $rows);
        $row = $rows[0];
        $this->assertSame('Documents', $row['task_name']);
        $this->assertSame('filling', $row['forecast']['state']);
        $this->assertSame(5, (int) $row['forecast']['days_left']);
        $this->assertCount(2, $row['sets']);
        $this->assertSame('2026-11-11 02-00 incremental', $row['sets'][0]['name'], 'newest set first');
        $this->assertSame(1, $row['bad_sets']);

        $summary = $storage->summary();
        $this->assertSame(self::DEST, $summary['least']['destination_id'], 'the removed destination with 1 byte free does not count');
        $this->assertSame(5, (int) $summary['soonest']['days_left']);
    }

    public function testRunKeepsDatabaseDumps(): void
    {
        $runId = $this->finishedRun('warning', time() - 60, databases: [
            ['engine' => 'mariadb', 'name' => 'shop', 'bytes' => 123456, 'ok' => true, 'error' => null, 'future' => 'kept'],
            ['engine' => 'postgresql', 'name' => 'crm', 'bytes' => null, 'ok' => false, 'error' => 'connection refused'],
        ]);
        $dumps = json_decode((string) $this->db()->value('SELECT db_dumps FROM runs WHERE id = ?', [$runId]), true);
        $this->assertSame('shop', $dumps[0]['name']);
        $this->assertSame('kept', $dumps[0]['future'], 'fields the server does not know yet are kept');
        $this->assertFalse($dumps[1]['ok']);
        $this->assertSame('connection refused', $dumps[1]['error']);

        $plain = $this->finishedRun('success', time() - 30);
        $this->assertNull($this->db()->value('SELECT db_dumps FROM runs WHERE id = ?', [$plain]), 'older agents send no databases');
    }

    // ---- helpers ----

    private function db(): \Custos\Infra\Db
    {
        return $this->app->db();
    }

    private function get(string $path, array $query = [], array $headers = [], string $ip = self::IP): Response
    {
        $uri = $path . ($query === [] ? '' : '?' . http_build_query($query));
        return $this->app->handle(new Request('GET', $uri, $headers, $ip, query: $query));
    }

    private function json(string $path, array $query): mixed
    {
        $response = $this->get($path, $query);
        $this->assertSame(200, $response->status, $response->content());
        $this->assertStringStartsWith('application/json', (string) $response->header('Content-Type'));
        return json_decode($response->content(), true);
    }

    /** A finished run of the task; returns its id. */
    private function finishedRun(
        string $status,
        int $startedAt,
        int $sourceFiles = 1000,
        int $written = 1000,
        ?string $error = null,
        ?array $databases = null
    ): string {
        $runId = Crypto::uuid4();
        $iso = fn(int $t) => gmdate('Y-m-d\TH:i:s\Z', $t);
        $start = $this->client->call('POST', 'agent/runs/start', [
            'run_id' => $runId, 'task_id' => $this->taskId, 'task_revision' => 1, 'backup_type' => 'incremental',
            'trigger' => 'manual', 'scheduled_for' => null, 'started_at' => $iso($startedAt),
        ]);
        $this->assertTrue($start->ok(), $start->describe());
        $finish = [
            'run_id' => $runId, 'task_id' => $this->taskId, 'status' => $status, 'finished_at' => $iso($startedAt + 20),
            'source_files' => $sourceFiles, 'source_bytes' => $sourceFiles * 1000, 'files_processed' => 10, 'bytes_written' => $written,
            'errors' => $status === 'error' ? 1 : 0, 'warnings' => $status === 'warning' ? 1 : 0,
            'error_message' => $status === 'error' ? ($error ?? 'Disk full') : null,
            'destinations' => [], 'verify' => null, 'retention' => null, 'log_tail' => '',
        ];
        if ($databases !== null) {
            $finish['databases'] = $databases;
        }
        $result = $this->client->call('POST', 'agent/runs/finish', $finish);
        $this->assertTrue($result->ok(), $result->describe());
        return $runId;
    }
}
