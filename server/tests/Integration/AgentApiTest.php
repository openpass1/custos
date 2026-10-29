<?php

declare(strict_types=1);

namespace Custos\Tests\Integration;

use Custos\App;
use Custos\Domain\RegistrationKey;
use Custos\Http\Request;
use Custos\Http\Response;
use Custos\Infra\Crypto;
use Custos\Tests\TestEnv;
use Custos\Tools\AgentClient;
use Custos\Tools\FakeAgent;
use PHPUnit\Framework\TestCase;

/**
 * The agent API in-process, driven by the reference agent implementation (tools/src/AgentClient.php).
 */
final class AgentApiTest extends TestCase
{
    private App $app;

    protected function setUp(): void
    {
        $this->app = TestEnv::app($this);
    }

    public function testRegistrationIsCheckedBothWays(): void
    {
        $client = $this->client(1);
        $result = $client->register($this->code(), $this->info());
        $this->assertTrue($result['ok'], $result['reason']);
        $this->assertSame('pending', $result['status']);
        $this->assertSame($this->app->keys()->publicPem(), $client->serverPublicKey);
        $headers = $result['result']->headers;
        $this->assertArrayHasKey('x-custos-registration-mac', $headers);

        $agent = $this->app->agents()->find($client->agentId);
        $this->assertSame('pending', $agent['status']);
        $this->assertSame(Crypto::fingerprint(TestEnv::agentKey(1)), $agent['key_fingerprint']);

        // The same key registering again is the same agent, not a new pending one.
        $again = $this->client(1)->register(RegistrationKey::format($this->key()), $this->info());
        $this->assertTrue($again['ok'], $again['reason']);
        $this->assertSame(1, (int) $this->app->db()->value('SELECT COUNT(*) FROM agents'));
    }

    public function testAgentRejectsServerWithAnotherFingerprint(): void
    {
        $result = $this->client(1)->register(RegistrationKey::format($this->key()) . '#0000-0000-0000-0000', $this->info());
        $this->assertFalse($result['ok']);
        $this->assertStringContainsString('fingerprint', $result['reason']);
    }

    public function testBadRegistrationKeyIsSignedOnlyByServerKey(): void
    {
        $result = $this->client(1)->register('AAAAA-BBBBB-CCCCC-DDDDD-EEEEE', $this->info());
        $this->assertFalse($result['ok']);
        $this->assertSame('the registration key was not accepted', $result['reason']);
        /** @var \Custos\Tools\ApiResult $answer */
        $answer = $result['result'];
        $this->assertSame(403, $answer->status);
        $this->assertSame('bad_registration_key', $answer->error());
        $this->assertArrayNotHasKey('x-custos-registration-mac', $answer->headers);
        $this->assertSame(0, (int) $this->app->db()->value('SELECT COUNT(*) FROM agents'));
    }

    public function testPendingAgentMaySendOnlyHeartbeats(): void
    {
        $client = $this->registered(1);
        $heartbeat = $client->call('POST', 'agent/heartbeat', $this->heartbeat());
        $this->assertTrue($heartbeat->ok(), $heartbeat->describe());
        $this->assertSame('pending', $heartbeat->data()['status']);
        $this->assertFalse($heartbeat->data()['want_config']);
        $this->assertSame([], $heartbeat->data()['commands']);

        $start = $client->call('POST', 'agent/runs/start', $this->runStart(Crypto::uuid4(), Crypto::uuid4()));
        $this->assertTrue($start->signed, 'errors are signed too');
        $this->assertSame(403, $start->status);
        $this->assertSame('pending', $start->error());
    }

    public function testReportsAreStored(): void
    {
        $client = $this->approved(1);
        $taskId = Crypto::uuid4();
        $runId = Crypto::uuid4();
        $destinationId = Crypto::uuid4();

        $heartbeat = $client->call('POST', 'agent/heartbeat', $this->heartbeat(['config_revision' => 5]));
        $this->assertTrue($heartbeat->data()['want_config'], 'an approved agent without a snapshot is asked for one');
        $this->assertOk($client->call('POST', 'agent/config', $this->snapshot(5, [$this->task($taskId, 3)])));
        $heartbeat = $client->call('POST', 'agent/heartbeat', $this->heartbeat(['config_revision' => 5]));
        $this->assertFalse($heartbeat->data()['want_config']);

        $this->assertOk($client->call('POST', 'agent/runs/start', $this->runStart($runId, $taskId)));
        $this->assertOk($client->call('POST', 'agent/runs/start', $this->runStart($runId, $taskId)), 'start is idempotent');
        $this->assertOk($client->call('POST', 'agent/runs/progress', [
            'run_id' => $runId, 'phase' => 'copy', 'percent' => 37.5, 'files_done' => 1200, 'files_total' => 3200,
            'bytes_done' => 104857600, 'bytes_total' => 279620266, 'speed_bps' => 52428800, 'eta_s' => 210,
            'current_file' => 'D:\\Data\\reports\\2026-10.pdf',
        ]));
        $this->assertSame('37.50', (string) $this->app->db()->value('SELECT percent FROM run_progress'));

        $log = "строка 1\nline 2\n";
        $finish = [
            'run_id' => $runId, 'task_id' => $taskId, 'status' => 'warning', 'finished_at' => gmdate('Y-m-d\TH:i:s\Z', time()),
            'source_files' => 3200, 'source_bytes' => 279620266, 'files_processed' => 3197, 'bytes_written' => 98112000,
            'errors' => 0, 'warnings' => 3, 'error_message' => null,
            'destinations' => [['destination_id' => $destinationId, 'status' => 'success', 'set_name' => 'Documents 2026-10-12 21-00 (Incremental)', 'bytes' => 98112000, 'error' => null]],
            'verify' => ['performed' => true, 'ok' => true, 'scope' => 'new', 'sets_checked' => 1, 'failed_sets' => []],
            'retention' => ['deleted_sets' => 1],
            'log_tail' => $log,
            'log_gz_b64' => base64_encode(gzencode($log)),
        ];
        $this->assertOk($client->call('POST', 'agent/runs/finish', $finish));
        $this->assertOk($client->call('POST', 'agent/runs/finish', ['status' => 'error'] + $finish), 'finish is idempotent');

        $db = $this->app->db();
        $run = $db->one('SELECT * FROM runs WHERE id = ?', [$runId]);
        $this->assertSame('warning', $run['status']);
        $this->assertSame(98112000, (int) $run['bytes_written']);
        $this->assertSame('schedule', $run['trigger_type']);
        $this->assertSame($log, gzdecode($db->value('SELECT log_gz FROM run_logs WHERE run_id = ?', [$runId])));
        $this->assertSame(1, (int) $db->value('SELECT COUNT(*) FROM run_destinations WHERE run_id = ?', [$runId]));
        $this->assertSame(0, (int) $db->value('SELECT COUNT(*) FROM run_progress'), 'finish clears the progress');
        $task = $this->app->tasks()->find($client->agentId, $taskId);
        $this->assertSame($runId, $task['last_run_id']);
        $this->assertSame('warning', $task['last_run_status']);
        $this->assertNotNull($task['last_success_at']);

        $this->assertOk($client->call('POST', "agent/runs/$runId/log", ['log_gz_b64' => base64_encode(gzencode("full\n"))]));
        $this->assertSame("full\n", gzdecode($db->value('SELECT log_gz FROM run_logs WHERE run_id = ?', [$runId])));
        $unknownRun = $client->call('POST', 'agent/runs/' . Crypto::uuid4() . '/log', ['log_gz_b64' => base64_encode(gzencode('x'))]);
        $this->assertSame(400, $unknownRun->status);
        $notGzip = $client->call('POST', "agent/runs/$runId/log", ['log_gz_b64' => base64_encode('plain text')]);
        $this->assertSame(400, $notGzip->status);

        $this->assertOk($client->call('POST', 'agent/inventory', ['destinations' => [[
            'task_id' => $taskId, 'destination_id' => $destinationId, 'kind' => 'unc', 'path' => '\\\\fileserver\\docs',
            'total_bytes' => 4000000000000, 'free_bytes' => 1250000000000,
            'sets' => [['name' => 'Documents 2026-10-12 00-30 (Full)', 'type' => 'full', 'time' => '2026-10-11T21:30:00Z',
                        'bytes' => 5100000000, 'verified_at' => null, 'verify_ok' => null]],
        ]]]));
        $this->assertSame(1, (int) $db->value('SELECT COUNT(*) FROM storage_sets'));
        $this->assertSame(1250000000000, (int) $db->value('SELECT free_bytes FROM storage_space'));

        $bad = $client->call('POST', 'agent/runs/start', ['run_id' => 'not-a-uuid'] + $this->runStart($runId, $taskId));
        $this->assertSame(400, $bad->status);
        $this->assertTrue($bad->signed);
        $this->assertStringContainsString('run_id', $bad->data()['message']);
    }

    public function testConfigSnapshotKeepsTasksAsSent(): void
    {
        $client = $this->approved(1);
        $keep = Crypto::uuid4();
        $gone = Crypto::uuid4();
        $task = $this->task($keep, 2) + ['future_field' => ['nested' => true], 'empty_object' => new \stdClass()];
        $settings = ['allow_remote_edit' => false, 'program_password_hash' => 'pbkdf2-sha256$200000$c2FsdA==$aGFzaA==', 'theme' => 'dark'];
        $this->assertOk($client->call('POST', 'agent/config', $this->snapshot(1, [$task, $this->task($gone, 1)], $settings)));

        $stored = $this->app->tasks()->find($client->agentId, $keep);
        $this->assertSame(json_encode($task, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES), $stored['data'],
            'unknown fields and empty objects survive (A.9)');
        $agent = $this->app->agents()->find($client->agentId);
        $this->assertSame(0, (int) $agent['allow_remote_edit']);
        $this->assertSame(1, (int) $agent['program_password_set']);
        $this->assertStringNotContainsString('pbkdf2', $agent['settings'], 'the program password hash is not kept');
        $this->assertStringNotContainsString('must not be kept', $agent['credentials'], 'only known credential fields are kept');

        $old = $this->task($keep, 2);
        $this->assertOk($client->call('POST', 'agent/config', $this->snapshot(2, [$this->task($keep, 4)], null, [$old])));
        $this->assertSame(1, (int) $this->app->tasks()->find($client->agentId, $gone)['deleted'], 'a task missing from the snapshot is deleted');
        $versions = $this->app->db()->all('SELECT revision, overwritten FROM task_versions WHERE task_id = ? ORDER BY revision', [$keep]);
        $this->assertSame([['revision' => 2, 'overwritten' => 1], ['revision' => 4, 'overwritten' => 0]], $versions);
    }

    public function testCommandsAreDeliveredOnceWithoutKeepingSecrets(): void
    {
        $client = $this->approved(1);
        $publicKey = Crypto::loadPublicKey(Crypto::publicPem(TestEnv::agentKey(1)));
        $credentialId = Crypto::uuid4();
        $id = $this->app->commands()->create($client->agentId, 'task.upsert', [
            'task' => $this->task(Crypto::uuid4(), 1),
            'base_revision' => 0,
            'secrets' => [$credentialId => ['kind' => 'windows', 'user' => 'svc-backup', 'domain' => '.', 'secret_enc' => Crypto::encryptSecret('s3cret', $publicKey)]],
        ], 3600);
        $this->app->commands()->create($client->agentId, 'task.run', ['task_id' => Crypto::uuid4()], -10);

        $commands = $client->call('POST', 'agent/heartbeat', $this->heartbeat())->data()['commands'];
        $this->assertCount(1, $commands, 'expired commands are not delivered');
        $this->assertSame($id, $commands[0]['id']);
        $this->assertSame('s3cret', $client->decryptSecret($commands[0]['payload']['secrets'][$credentialId]['secret_enc']));
        $stored = $this->app->db()->one('SELECT status, payload FROM commands WHERE id = ?', [$id]);
        $this->assertSame('delivered', $stored['status']);
        $this->assertNull(json_decode($stored['payload'], true)['secrets'][$credentialId]['secret_enc'], 'secrets are erased after delivery');
        $this->assertSame([], $client->call('POST', 'agent/heartbeat', $this->heartbeat())->data()['commands']);

        $this->assertOk($client->call('POST', "agent/commands/$id/result", ['status' => 'done', 'message' => 'conflict: local revision 3 overwritten']));
        $this->assertOk($client->call('POST', "agent/commands/$id/result", ['status' => 'failed', 'message' => null]));
        $this->assertSame('done', $this->app->db()->value('SELECT status FROM commands WHERE id = ?', [$id]));
        $this->assertSame(400, $client->call('POST', 'agent/commands/' . Crypto::uuid4() . '/result', ['status' => 'done'])->status);
    }

    public function testSignatureTimeAndReplayChecks(): void
    {
        $client = $this->registered(1);
        $body = json_encode($this->heartbeat());
        $headers = $this->signedHeaders(TestEnv::agentKey(1), 'POST', 'agent/heartbeat', $body) + ['x-custos-agent' => $client->agentId];

        $first = $this->raw('/api/v1/agent/heartbeat', $headers, $body);
        $this->assertSame(200, $first->status);
        $this->assertSignedFor($first, $headers['x-custos-nonce']);

        $replay = $this->raw('/api/v1/agent/heartbeat', $headers, $body);
        $this->assertSame(401, $replay->status);
        $this->assertSame('bad_signature', json_decode($replay->body, true)['error']);
        $this->assertSignedFor($replay, $headers['x-custos-nonce']);

        $tampered = $this->signedHeaders(TestEnv::agentKey(1), 'POST', 'agent/heartbeat', $body) + ['x-custos-agent' => $client->agentId];
        $this->assertSame(401, $this->raw('/api/v1/agent/heartbeat', $tampered, $body . ' ')->status);

        $otherKey = $this->signedHeaders(TestEnv::agentKey(2), 'POST', 'agent/heartbeat', $body) + ['x-custos-agent' => $client->agentId];
        $this->assertSame(401, $this->raw('/api/v1/agent/heartbeat', $otherKey, $body)->status);

        $old = $this->signedHeaders(TestEnv::agentKey(1), 'POST', 'agent/heartbeat', $body, time() - 301) + ['x-custos-agent' => $client->agentId];
        $skew = $this->raw('/api/v1/agent/heartbeat', $old, $body);
        $this->assertSame(401, $skew->status);
        $data = json_decode($skew->body, true);
        $this->assertSame('clock_skew', $data['error']);
        $this->assertArrayHasKey('server_time', $data);
        $this->assertSignedFor($skew, $old['x-custos-nonce']);

        // The signed PATH does not depend on a subfolder in front of /api/v1/.
        $sub = $this->signedHeaders(TestEnv::agentKey(1), 'POST', 'agent/heartbeat', $body) + ['x-custos-agent' => $client->agentId];
        $this->assertSame(200, $this->raw('/custos/api/v1/agent/heartbeat', $sub, $body, '/custos')->status);
    }

    public function testErrorsAreSigned(): void
    {
        $client = $this->registered(1);
        $client->agentId = Crypto::uuid4();
        $unknown = $client->call('POST', 'agent/heartbeat', $this->heartbeat());
        $this->assertTrue($unknown->signed);
        $this->assertSame([404, 'unknown_agent'], [$unknown->status, $unknown->error()]);

        $body = json_encode($this->heartbeat());
        $headers = $this->signedHeaders(TestEnv::agentKey(1), 'POST', 'agent/heartbeat', $body);
        $tooLarge = $this->raw('/api/v1/agent/heartbeat', ['content-length' => (string) (8 * 1024 * 1024 + 1)] + $headers, $body);
        $this->assertSame(413, $tooLarge->status);
        $this->assertSignedFor($tooLarge, $headers['x-custos-nonce']);

        $headers = $this->signedHeaders(TestEnv::agentKey(1), 'POST', 'agent/heartbeat', $body);
        unset($headers['x-custos-protocol']);
        $noProtocol = $this->raw('/api/v1/agent/heartbeat', $headers, $body);
        $this->assertSame(400, $noProtocol->status);
        $this->assertSignedFor($noProtocol, $headers['x-custos-nonce']);

        $headers = $this->signedHeaders(TestEnv::agentKey(1), 'GET', 'agent/nothing', '');
        $unknownEndpoint = $this->raw('/api/v1/agent/nothing', $headers, '', method: 'GET');
        $this->assertSame(400, $unknownEndpoint->status);
        $this->assertSignedFor($unknownEndpoint, $headers['x-custos-nonce']);
    }

    public function testAllowListAndDisabledAgent(): void
    {
        $client = $this->approved(1);
        $this->app->settings()->set('ip_allow_agents', '198.51.100.0/24');
        $blocked = $client->call('POST', 'agent/heartbeat', $this->heartbeat());
        $this->assertTrue($blocked->signed);
        $this->assertSame([403, 'forbidden'], [$blocked->status, $blocked->error()]);
        $this->app->settings()->set('ip_allow_agents', "198.51.100.0/24\n192.0.2.0/24");
        $this->assertOk($client->call('POST', 'agent/heartbeat', $this->heartbeat()));

        $this->app->agents()->setDisabled($client->agentId, true);
        $heartbeat = $client->call('POST', 'agent/heartbeat', $this->heartbeat());
        $this->assertSame('disabled', $heartbeat->data()['status']);
        $this->assertSame([403, 'disabled'], [
            ($r = $client->call('POST', 'agent/runs/start', $this->runStart(Crypto::uuid4(), Crypto::uuid4())))->status, $r->error(),
        ]);
    }

    public function testReplacingAReinstalledMachineKeepsItsHistory(): void
    {
        $old = $this->approved(1);
        $taskId = Crypto::uuid4();
        $runId = Crypto::uuid4();
        $this->assertOk($old->call('POST', 'agent/config', $this->snapshot(1, [$this->task($taskId, 1)])));
        $this->assertOk($old->call('POST', 'agent/runs/start', $this->runStart($runId, $taskId)));
        $this->app->agents()->rename($old->agentId, 'Accounting PC');

        $new = $this->registered(2);
        $pending = $this->app->agents()->find($new->agentId);
        $this->assertSame($old->agentId, $pending['reregistration_of'], 'same machine_guid with a new key is a re-registration');

        $this->assertSame($old->agentId, $this->app->agents()->replace($new->agentId));
        $this->assertNull($this->app->agents()->find($old->agentId));
        $merged = $this->app->agents()->find($new->agentId);
        $this->assertSame('approved', $merged['status']);
        $this->assertSame('Accounting PC', $merged['display_name']);
        $this->assertSame($new->agentId, $this->app->db()->value('SELECT agent_id FROM runs WHERE id = ?', [$runId]));
        $this->assertNotNull($this->app->tasks()->find($new->agentId, $taskId));

        $this->assertSame(404, $old->call('POST', 'agent/heartbeat', $this->heartbeat())->status);
        $heartbeat = $new->call('POST', 'agent/heartbeat', $this->heartbeat());
        $this->assertSame('approved', $heartbeat->data()['status']);
        $this->assertTrue($heartbeat->data()['want_config'], 'the new installation is asked for its configuration');
    }

    public function testFakeAgentRunsAgainstTheApi(): void
    {
        $state = TestEnv::dir() . '/fake-' . uniqid() . '.json';
        $make = fn() => new FakeAgent($state, [
            'url' => 'http://custos.test', 'code' => $this->code(), 'hostname' => 'FAKE-PC-01', 'time_zone' => 'Europe/Berlin',
            'run_seconds' => 2, 'skip_schedule' => false, 'offline_min' => null, 'pause_s' => null, 'result' => 'error',
        ], fn(string $line) => null, TestEnv::transport($this->app));

        $agent = $make();
        $this->assertTrue($agent->once(30));
        $this->assertSame('pending', $agent->status());
        $this->app->agents()->approve($agent->agentId());
        $this->app->commands()->create($agent->agentId(), 'inventory.refresh', new \stdClass(), 3600);

        $agent = $make();
        $this->assertTrue($agent->once(30));
        $db = $this->app->db();
        $this->assertSame(2, (int) $db->value("SELECT COUNT(*) FROM runs WHERE status = 'error'"), 'the run kept while pending arrives too');
        $this->assertSame(2, (int) $db->value('SELECT COUNT(*) FROM run_logs'), 'error runs bring their log');
        $this->assertSame(4, (int) $db->value('SELECT COUNT(*) FROM tasks'));
        $this->assertSame('done', $db->value("SELECT status FROM commands WHERE kind = 'inventory.refresh'"));
    }

    // ---- helpers ----

    private function key(): string
    {
        return $this->app->settings()->get('registration_key');
    }

    private function code(): string
    {
        return RegistrationKey::code($this->key(), $this->app->keys()->fingerprint());
    }

    private function client(int $key): AgentClient
    {
        return new AgentClient('http://custos.test', TestEnv::agentKey($key), null, null, TestEnv::transport($this->app));
    }

    private function registered(int $key): AgentClient
    {
        $client = $this->client($key);
        $result = $client->register($this->code(), $this->info());
        $this->assertTrue($result['ok'], $result['reason']);
        return $client;
    }

    private function approved(int $key): AgentClient
    {
        $client = $this->registered($key);
        $this->app->agents()->approve($client->agentId);
        return $client;
    }

    private function info(): array
    {
        return [
            'hostname' => 'PC-01', 'machine_guid' => 'f5a6b7c8-0000-4000-8000-000000000001',
            'os' => 'Windows 11 Pro 24H2 (26100)', 'agent_version' => '1.0.0', 'time_zone' => 'Europe/Berlin',
        ];
    }

    private function heartbeat(array $overrides = []): array
    {
        return $overrides + [
            'agent_version' => '1.0.0', 'config_revision' => 1, 'time_zone' => 'Europe/Berlin',
            'service_started_at' => '2026-10-12T05:01:10Z', 'state' => 'idle', 'running' => null, 'queue' => [],
        ];
    }

    private function runStart(string $runId, string $taskId): array
    {
        return [
            'run_id' => $runId, 'task_id' => $taskId, 'task_revision' => 1, 'backup_type' => 'incremental',
            'trigger' => 'schedule', 'scheduled_for' => '2026-10-12T21:00:00Z', 'started_at' => '2026-10-12T21:00:02Z',
        ];
    }

    private function task(string $id, int $revision): array
    {
        return [
            'id' => $id, 'revision' => $revision, 'updated_at' => '2026-10-12T10:00:00Z', 'updated_by' => 'local:OFFICE\\admin',
            'name' => 'Documents', 'enabled' => true, 'backup_type' => 'incremental',
            'schedule' => ['type' => 'daily', 'times' => ['00:00'], 'days_of_week' => [1, 2, 3, 4, 5, 6, 7]],
        ];
    }

    private function snapshot(int $revision, array $tasks, ?array $settings = null, array $overwritten = []): array
    {
        return [
            'config_revision' => $revision,
            'changed' => [],
            'settings' => $settings ?? ['allow_remote_edit' => true],
            'credentials' => [['id' => Crypto::uuid4(), 'kind' => 'windows', 'user' => 'svc-backup', 'domain' => '.', 'has_secret' => true, 'secret' => 'must not be kept']],
            'tasks' => $tasks,
            'deleted_task_ids' => [],
            'overwritten' => $overwritten,
        ];
    }

    private function signedHeaders(\OpenSSLAsymmetricKey $key, string $method, string $path, string $body, ?int $time = null): array
    {
        $time = (string) ($time ?? time());
        $nonce = bin2hex(random_bytes(16));
        openssl_sign(AgentClient::requestString($method, $path, $time, $nonce, $body), $signature, $key, OPENSSL_ALGO_SHA256);
        return [
            'content-type' => 'application/json', 'x-custos-protocol' => '1', 'x-custos-time' => $time,
            'x-custos-nonce' => $nonce, 'x-custos-signature' => base64_encode($signature),
        ];
    }

    private function raw(string $uri, array $headers, string $body, string $basePath = '', string $method = 'POST'): Response
    {
        $headers += ['content-length' => (string) strlen($body)];
        return $this->app->handle(new Request($method, $uri, $headers, '192.0.2.10', $basePath, body: $body));
    }

    private function assertSignedFor(Response $response, string $nonce): void
    {
        $string = AgentClient::responseString($response->status, (string) $response->header('X-Custos-Time'), $nonce, $response->body);
        $signature = base64_decode((string) $response->header('X-Custos-Signature'), true);
        $this->assertSame(1, openssl_verify($string, (string) $signature, $this->app->keys()->publicPem(), OPENSSL_ALGO_SHA256),
            "HTTP {$response->status} must be signed with the server key");
    }

    private function assertOk(\Custos\Tools\ApiResult $result, string $message = ''): void
    {
        $this->assertTrue($result->ok(), trim($message . ' ' . $result->describe()));
    }
}
