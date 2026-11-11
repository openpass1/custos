<?php

declare(strict_types=1);

namespace Custos\Tests\Integration;

use Custos\App;
use Custos\Domain\Packages;
use Custos\Domain\RegistrationKey;
use Custos\Domain\TaskDiff;
use Custos\Domain\TaskEdits;
use Custos\Domain\TaskForm;
use Custos\Domain\Templates;
use Custos\Domain\UpdateWatch;
use Custos\Tests\TestEnv;
use Custos\Tests\Unit\TaskFormTest;
use Custos\Tools\AgentClient;
use Custos\Tools\FakeAgent;
use PHPUnit\Framework\TestCase;

/**
 * Stage 3 (7.3–7.5, section 8, A.4, A.10): commands from the site carried out by the fake agent through the real API,
 * task edits and their versions, templates, agent updates and 7-Zip.
 */
final class ManagementTest extends TestCase
{
    private const USER = ['id' => 1, 'login' => 'admin'];

    /** State of a fake agent that has not registered yet: its key is made once for all tests. */
    private static ?string $blank = null;

    private App $app;
    private string $stateFile;

    protected function setUp(): void
    {
        $this->app = TestEnv::app($this);
        $this->stateFile = TestEnv::dir() . '/fake-' . uniqid() . '.json';
        if (self::$blank === null) {
            self::$blank = TestEnv::dir() . '/fake-blank.json';
            $this->agent();
            rename($this->stateFile, self::$blank);
        }
        copy(self::$blank, $this->stateFile);
    }

    public function testEditsVersionsAndReturningAVersion(): void
    {
        $agent = $this->approvedAgent();
        [$task, $credentialId] = $this->taskWithAccount($agent);
        $edits = $this->app->taskEdits();
        $base = $edits->baseRevision($agent, $task['id']);
        $this->assertSame((int) $task['revision'], $base);

        $post = TaskFormTest::formFor($task);
        $post['name'] = 'Renamed once';
        $post['replace_secret'] = [$credentialId => 'new-password'];
        $form = TaskForm::parse($task, $post, TaskEdits::credentials($agent));
        $this->assertSame([], $form->errors);
        $first = $edits->save($agent, $form, $base, self::USER);

        $post['name'] = 'Renamed twice';
        unset($post['replace_secret']);
        $second = $edits->save($agent, TaskForm::parse($task, $post, TaskEdits::credentials($agent)), $edits->baseRevision($agent, $task['id']), self::USER);
        $old = $this->command($first);
        $this->assertSame('cancelled', $old['status'], 'an undelivered edit is replaced by the newer one');
        $this->assertSame($second, $old['superseded_by']);
        $this->assertNull(json_decode($old['payload'], true)['secrets'][$credentialId]['secret_enc'], 'the replaced edit keeps no secrets');
        $payload = json_decode($this->command($second)['payload'], true);
        $this->assertSame($base, $payload['base_revision'], 'a queued edit is replaced, so the next one has the same base');
        $this->assertArrayHasKey($credentialId, $payload['secrets'], 'the password of the replaced edit goes along');
        $this->assertNotSame('new-password', $payload['secrets'][$credentialId]['secret_enc']);
        $this->assertSame('Renamed twice', $edits->current($agent, $task['id'])['task']['name'], 'the editor starts from the edit on its way');

        $this->agentPass();
        $this->assertSame('done', $this->command($second)['status']);
        $agent = $this->app->agents()->find($agent['id']);
        $stored = $this->app->tasks()->find($agent['id'], $task['id']);
        $this->assertSame('Renamed twice', $stored['name']);
        $this->assertSame($base + 1, (int) $stored['revision']);
        $this->assertSame('server:admin', $stored['updated_by']);
        $this->assertNull($edits->pending($agent, $task['id']), 'the edit is in the configuration now');

        $version = $edits->version($agent['id'], $task['id'], $base + 1);
        $this->assertSame($base, $version['previous_revision']);
        $this->assertContains('name', array_column(TaskDiff::compare($version['previous'], $version['task']), 'path'));

        $restored = $edits->restore($agent, $task['id'], $base, self::USER);
        $this->assertSame([], $restored['missing']);
        $this->assertSame($base + 1, json_decode($this->command($restored['command'])['payload'], true)['base_revision']);
        $this->agentPass();
        $stored = $this->app->tasks()->find($agent['id'], $task['id']);
        $this->assertSame($task['name'], $stored['name'], 'the old version is back');
        $this->assertSame($base + 2, (int) $stored['revision'], 'as a new revision, not as a conflict');
        $this->assertSame([$base + 2, $base + 1, $base], array_map('intval', array_column($edits->versions($agent['id'], $task['id']), 'revision')));
        $this->assertSame('0', (string) $this->app->db()->value(
            'SELECT COUNT(*) FROM task_versions WHERE agent_id = ? AND overwritten = 1', [$agent['id']]));

        $this->expectExceptionMessage('task_versions.error.unknown');
        $edits->restore($agent, $task['id'], 999, self::USER);
    }

    public function testCommandsSettingsAndCancel(): void
    {
        $agent = $this->approvedAgent();
        $commands = $this->app->commands();
        $tasks = $this->tasks($agent);
        [$first, $second] = [$tasks[0]['id'], $tasks[1]['id']];

        $run = $commands->queue($agent['id'], 'task.run', ['task_id' => $first], self::USER);
        $this->assertSame($run, $commands->queue($agent['id'], 'task.run', ['task_id' => $first], self::USER), 'the same simple command is not queued twice');
        $this->assertNotSame($run, $commands->queue($agent['id'], 'task.run', ['task_id' => $second], self::USER));
        $this->assertTrue($commands->cancel($agent['id'], $run));
        $this->assertFalse($commands->cancel($agent['id'], $run), 'a command can be withdrawn once');
        $this->assertSame('cancelled', $this->command($run)['status']);
        $this->assertEqualsWithDelta(3600, strtotime($this->command($run)['expires_at']) - strtotime($this->command($run)['created_at']), 1, 'runs wait an hour');

        $disable = $commands->queue($agent['id'], 'task.disable', ['task_id' => $first], self::USER);
        $this->assertEqualsWithDelta(7 * 86400, strtotime($this->command($disable)['expires_at']) - strtotime($this->command($disable)['created_at']), 1, 'changes wait a week');
        $settings = $this->app->taskEdits()->updateSettings($agent, ['log_retention_days' => 45, 'tray_mode' => 'hidden'], self::USER);
        $this->agentPass();

        $this->assertSame('done', $this->command($disable)['status']);
        $this->assertSame('done', $this->command($settings)['status']);
        $this->assertFalse($commands->cancel($agent['id'], $disable), 'too late: the agent has it');
        $this->assertSame(0, (int) $this->app->tasks()->find($agent['id'], $first)['enabled']);
        $stored = json_decode((string) $this->app->agents()->find($agent['id'])['settings'], true);
        $this->assertSame(45, $stored['log_retention_days']);
        $this->assertSame('hidden', $stored['tray_mode']);
        $this->assertContains($disable, array_column($commands->forTask($agent['id'], $first), 'id'), 'the task page lists its commands');

        $expiring = $commands->create($agent['id'], 'task.run', ['task_id' => $second], -1);
        $this->assertGreaterThanOrEqual(1, $commands->expire(time()));
        $this->assertSame('expired', $this->command($expiring)['status']);
    }

    public function testTemplatesGiveEveryMachineItsOwnTask(): void
    {
        $agent = $this->approvedAgent();
        [$task] = $this->taskWithAccount($agent);
        $templates = $this->app->templates();

        $made = $templates->fromTask($agent, $task);
        $this->assertNotSame($task['id'], $made['task']['id']);
        $this->assertCount(1, $made['credentials'], 'the account is copied without its password');
        $templateCredential = array_key_first($made['credentials']);
        $this->assertContains($templateCredential, TaskForm::credentialRefs($made['task']));
        $made['task']['name'] = 'Shared folders';
        $id = $templates->save(null, $made['task'], $made['credentials'], 'admin');
        try {
            $templates->save(null, $made['task'], $made['credentials'], 'admin');
            $this->fail('two templates with one name');
        } catch (\InvalidArgumentException $e) {
            $this->assertSame('templates.error.exists', $e->getMessage());
        }

        $template = $templates->find($id);
        $result = $templates->apply($template, [$agent], [], self::USER);
        $this->assertSame('templates.apply.no_password', $result[0]['error']['key'], 'a new account needs its password');
        $result = $templates->apply($template, [$agent], [$templateCredential => 'share-password'], self::USER);
        $this->assertNull($result[0]['error']);
        $this->agentPass();

        $machineTaskId = Templates::machineId($template, $agent['id'], $template['uuid']);
        $this->assertSame($machineTaskId, Templates::machineTask($template, $agent['id'])['id'], 'ids are the same every time');
        $this->assertNotSame($machineTaskId, Templates::machineTask($template, '00000000-0000-4000-8000-000000000000')['id'], 'and differ between machines');
        $this->assertSame('done', $this->command($result[0]['command'])['status']);
        $created = $this->app->tasks()->find($agent['id'], $machineTaskId);
        $this->assertSame('Shared folders', $created['name']);
        $agent = $this->app->agents()->find($agent['id']);
        $machineCredential = Templates::machineId($template, $agent['id'], $templateCredential);
        $this->assertTrue(TaskEdits::credentials($agent)[$machineCredential]['has_secret'], 'the agent made the account');

        $made['task']['comment'] = 'Second revision';
        $templates->save($id, $made['task'], $made['credentials'], 'admin');
        $this->assertSame(1, (int) $templates->all()[0]['outdated'], 'the machine has an older revision of the template');
        $template = $templates->find($id);
        $result = $templates->apply($template, [$agent], [], self::USER);
        $this->assertNull($result[0]['error'], 'the account made the first time is reused without a password');
        $this->assertSame([], (array) json_decode($this->command($result[0]['command'])['payload'], true)['secrets']);
        $this->agentPass();
        $this->assertSame('Second revision', json_decode($this->app->tasks()->find($agent['id'], $machineTaskId)['data'], true)['comment']);
        $this->assertSame(0, (int) $templates->all()[0]['outdated']);
        $this->assertSame(['id' => $id, 'name' => 'Shared folders', 'revision' => 2, 'template_revision' => 2],
            array_map(fn($v) => is_numeric($v) ? (int) $v : $v, $templates->linkOf($agent['id'], $machineTaskId)));
    }

    public function testAgentAndSevenZipUpdates(): void
    {
        $agent = $this->approvedAgent(['sevenzip' => '23.01']);
        $this->assertSame('23.01', $agent['sevenzip_version'], 'the heartbeat brings the 7z.dll version');
        $packages = $this->app->packages();

        $file = $this->package('1.0.1');
        $row = $packages->addAgent($file, 'admin');
        $this->assertSame(hash_file('sha256', $file), $row['sha256']);
        $this->assertSame('custos-1.0.1.zip', $row['file_name']);
        foreach ([[$file, 'updates.error.exists'], [$this->package('1.0.2', 2), 'updates.error.protocol_new'], [__FILE__, 'updates.error.zip']] as [$bad, $error]) {
            try {
                $packages->addAgent($bad, 'admin');
                $this->fail("$error expected");
            } catch (\InvalidArgumentException $e) {
                $this->assertSame($error, $e->getMessage());
            }
        }
        $packages->setCurrent('1.0.1');
        $this->assertSame([$agent['id']], array_column(Packages::outdated([$agent], '1.0.1'), 'id'));

        $installer = TestEnv::dir() . '/7z-' . uniqid() . '.exe';
        file_put_contents($installer, "MZ fake 7-Zip installer\n" . random_bytes(4096));
        foreach ([[str_repeat('0', 64), 'sevenzip.error.checksum'], [null, null]] as [$sha, $error]) {
            try {
                $packages->addSevenZip($installer, '24.09', $sha, 'upload', 'admin');
                $this->assertNull($error);
            } catch (\InvalidArgumentException $e) {
                $this->assertSame($error, $e->getMessage());
            }
        }
        try {
            $packages->addSevenZip($file, '24.10', null, 'upload', 'admin');
            $this->fail('a zip is not an installer');
        } catch (\InvalidArgumentException $e) {
            $this->assertSame('sevenzip.error.not_exe', $e->getMessage());
        }

        $this->assertCount(1, $packages->install('agent', '1.0.1', [$agent], self::USER)['queued']);
        $this->assertCount(1, $packages->install('7zip', '24.09', [$agent], self::USER)['queued']);
        $this->agentPass();
        $agent = $this->app->agents()->find($agent['id']);
        $this->assertSame('1.0.1', $agent['agent_version']);
        $this->assertSame('24.09', $agent['sevenzip_version']);
        $done = $this->app->db()->all("SELECT kind, status, result_message FROM commands WHERE kind IN ('update.install', 'sevenzip.update') ORDER BY kind");
        $this->assertSame(['done', 'done'], array_column($done, 'status'), (string) json_encode($done));
        $this->assertStringContainsString('7-Zip 24.09 installed', $done[0]['result_message']);
        $this->assertSame(['queued' => [], 'skipped' => [$agent]], $packages->install('agent', '1.0.1', [$agent], self::USER), 'already up to date');

        $client = $this->client();
        $this->assertSame((string) file_get_contents($installer), $client->download('agent/sevenzip/24.09')['body']);
        $missing = $client->download('agent/updates/9.9.9');
        $this->assertFalse($missing['ok']);
        $this->assertStringContainsString('bad_request', $missing['reason'], 'a missing package is a signed error');

        $packages->addAgent($this->package('1.0.3'), 'admin');
        $agent['agent_version'] = '1.0.1';
        $queued = $packages->install('agent', '1.0.3', [$agent], self::USER)['queued'];
        $this->assertCount(1, $queued);
        $packages->delete('agent', '1.0.3');
        $this->assertNull($packages->file('agent', '1.0.3'));
        $this->assertSame('cancelled', $this->app->db()->value(
            "SELECT status FROM commands WHERE kind = 'update.install' AND payload LIKE '%\"1.0.3\"%'"), 'the command for a deleted package is withdrawn');
    }

    public function testSevenZipReleasesOnGitHub(): void
    {
        $agent = $this->approvedAgent(['sevenzip' => '23.01']);
        $installer = "MZ 7-Zip 24.09 for x64\n" . random_bytes(1024);
        $digest = hash('sha256', $installer);
        $asset = fn(string $version, ?string $sha) => [
            'name' => '7z' . str_replace('.', '', $version) . '-x64.exe',
            'browser_download_url' => "https://example.org/7z$version.exe",
        ] + ($sha === null ? [] : ['digest' => "sha256:$sha"]);
        $releases = [
            ['tag_name' => '25.00', 'prerelease' => true, 'assets' => [$asset('25.00', $digest)]],
            ['tag_name' => '26.00', 'draft' => true, 'assets' => []],
            ['tag_name' => '24.08', 'assets' => [$asset('24.08', $digest)]],
            ['tag_name' => '24.09', 'published_at' => '2024-11-29T12:00:00Z', 'assets' => [
                ['name' => '7z2409-arm64.exe', 'browser_download_url' => 'https://example.org/arm.exe'], $asset('24.09', $digest)]],
            ['tag_name' => 'v-test', 'assets' => []],
        ];
        $body = $installer;
        $status = 200;
        $requests = [];
        $http = function (string $url, int $limit) use (&$releases, &$body, &$status, &$requests): array {
            $requests[] = $url;
            return str_contains($url, '/releases') ? [$status, (string) json_encode($releases), ''] : [200, $body, ''];
        };
        $settings = $this->app->settings();
        $watch = new UpdateWatch($this->app->db(), $settings, $this->app->packages(), $this->app->notices(), 'https://api.test', 'CUSTOS-test', $http);

        $settings->set('sevenzip_check', '1');
        $stats = $watch->pass(time());
        $this->assertSame('ok', $stats['sevenzip_check']);
        $this->assertSame(['https://api.test/repos/ip7z/7zip/releases?per_page=30'], $requests);
        $latest = $watch->latestSevenZip();
        $this->assertSame(['24.09', 'https://example.org/7z24.09.exe', $digest], [$latest['version'], $latest['url'], $latest['sha256']],
            'the highest stable version, not a prerelease or a draft');
        $this->assertSame(1, $stats['sevenzip_outdated']);
        $watch->pass(time());
        $this->assertCount(1, $requests, 'GitHub is asked once a day');
        $this->assertSame(1, (int) $this->app->db()->value("SELECT COUNT(*) FROM notices WHERE kind = 'sevenzip.outdated'"), 'one notice per version');

        $body = "MZ something else\n";
        try {
            $watch->download('admin');
            $this->fail('a file with another checksum is refused');
        } catch (\InvalidArgumentException $e) {
            $this->assertSame('sevenzip.error.checksum', $e->getMessage());
        }
        $body = $installer;
        $package = $watch->download('admin');
        $this->assertSame(['24.09', 'github', $digest], [$package['version'], $package['source'], $package['sha256']]);
        $this->assertSame($package, $watch->download('admin'), 'downloaded once');

        $releases[3]['assets'] = [$asset('24.09', null)];
        $releases[] = ['tag_name' => '24.10', 'assets' => [$asset('24.10', null)]];
        $this->assertSame('24.10', $watch->check(time())['version']);
        try {
            $watch->download('admin');
            $this->fail('without a published checksum nothing is downloaded');
        } catch (\InvalidArgumentException $e) {
            $this->assertSame('sevenzip.error.no_digest', $e->getMessage());
        }

        $status = 403;
        $this->assertNull($watch->check(time()));
        $this->assertSame('HTTP 403', $settings->get('sevenzip_check_error'));
        $this->assertSame('24.10', $watch->latestSevenZip()['version'], 'a failed check keeps what was known');

        $this->app->packages()->addAgent($this->package('2.0.0'), 'admin');
        $this->app->packages()->setCurrent('2.0.0');
        $watch->pass(time());
        $notice = $this->app->db()->one("SELECT agent_id, params FROM notices WHERE kind = 'agent.outdated'");
        $this->assertSame($agent['id'], $notice['agent_id']);
        $this->assertSame(['version' => '2.0.0', 'n' => 1, 'name' => 'PC-01'], json_decode($notice['params'], true));
    }

    // ---- helpers ----

    private function agent(array $options = []): FakeAgent
    {
        $code = RegistrationKey::code($this->app->settings()->get('registration_key'), $this->app->keys()->fingerprint());
        return new FakeAgent($this->stateFile, $options + [
            'url' => 'http://custos.test', 'code' => $code, 'hostname' => 'PC-01', 'time_zone' => 'UTC', 'run_seconds' => 1,
            'skip_schedule' => true, 'offline_min' => null, 'pause_s' => null, 'result' => 'success', 'once_run' => false,
        ], fn(string $line) => null, TestEnv::transport($this->app));
    }

    /** One pass of the fake agent: heartbeat with commands, their results, the configuration, a last heartbeat. */
    private function agentPass(array $options = []): void
    {
        $this->assertTrue($this->agent($options)->once(60), 'the fake agent pass went well');
    }

    /** Registers the fake agent, approves the machine and lets the agent send its configuration. */
    private function approvedAgent(array $options = []): array
    {
        $this->agentPass($options);
        $id = $this->agent($options)->agentId();
        $this->app->agents()->approve($id);
        $this->agentPass($options);
        return $this->app->agents()->find($id);
    }

    /** Tasks of the machine as the server stores them, decoded. */
    private function tasks(array $agent): array
    {
        $rows = $this->app->db()->all('SELECT data FROM tasks WHERE agent_id = ? AND deleted = 0 ORDER BY name', [$agent['id']]);
        return array_map(fn($row) => TaskEdits::decode($row['data']), $rows);
    }

    /** A task of the machine with a network share account, and that account's id. */
    private function taskWithAccount(array $agent): array
    {
        foreach ($this->tasks($agent) as $task) {
            $refs = TaskForm::credentialRefs($task);
            if ($refs !== []) {
                return [$task, $refs[0]];
            }
        }
        $this->fail('the fake agent has no task with an account');
    }

    private function command(string $id): array
    {
        return $this->app->commands()->find($id) ?? $this->fail("no command $id");
    }

    /** A test update package made by tools/make-update-package.php. */
    private function package(string $version, int $protocol = 1): string
    {
        $file = TestEnv::dir() . "/custos-$version-" . uniqid() . '.zip';
        exec(escapeshellarg(PHP_BINARY) . ' ' . escapeshellarg(dirname(__DIR__, 2) . '/tools/make-update-package.php')
            . " --version=$version --min-protocol=$protocol --out=" . escapeshellarg($file), $output, $code);
        $this->assertSame(0, $code, implode("\n", $output));
        return $file;
    }

    /** An agent client with the fake agent's key, for requests the fake agent does not make by itself. */
    private function client(): AgentClient
    {
        $state = json_decode((string) file_get_contents($this->stateFile), true);
        return new AgentClient('http://custos.test', openssl_pkey_get_private($state['private_key']), $state['agent_id'],
            $state['server_public_key'], TestEnv::transport($this->app));
    }
}
