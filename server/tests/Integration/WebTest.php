<?php

declare(strict_types=1);

namespace Custos\Tests\Integration;

use Custos\Tests\TestEnv;
use PHPUnit\Framework\Attributes\Depends;
use PHPUnit\Framework\TestCase;

/**
 * End to end over real HTTP: bin/install.php on an empty database, the site under "php -S", and
 * tools/fake-agent.php as a separate process. The tests run in order and share one installation.
 */
final class WebTest extends TestCase
{
    private const PASSWORD = 'correct-horse-1';

    /** @var resource|null */
    private static $server = null;
    /** @var resource|null */
    private static $telegram = null;
    private static string $base = '';
    private static string $config = '';
    private static string $cookies = '';

    public static function tearDownAfterClass(): void
    {
        if (self::$server !== null) {
            proc_terminate(self::$server);
            proc_close(self::$server);
            self::$server = null;
        }
        if (self::$telegram !== null) {
            proc_terminate(self::$telegram);
            proc_close(self::$telegram);
            self::$telegram = null;
        }
    }

    public function testInstallAndStart(): void
    {
        TestEnv::emptyDatabase($this);
        self::$config = TestEnv::configFile();
        // A fake Telegram API that answers "ok" and writes down what it was sent. It goes into the configuration
        // now: the site under "php -S" keeps the configuration file in the opcode cache.
        $dir = TestEnv::dir() . '/telegram';
        @mkdir($dir);
        file_put_contents("$dir/router.php", '<?php file_put_contents(__DIR__ . "/sent.log", json_encode($_POST) . "\\n", FILE_APPEND);'
            . ' header("Content-Type: application/json"); echo \'{"ok":true,"result":{}}\';');
        $port = self::freePort();
        self::$telegram = proc_open([PHP_BINARY, '-S', "127.0.0.1:$port", "$dir/router.php"],
            [0 => ['pipe', 'r'], 1 => ['file', '/dev/null', 'w'], 2 => ['file', '/dev/null', 'w']], $pipes, $dir);
        file_put_contents(self::$config, '<?php return ' . var_export(TestEnv::config() + ['telegram_api_url' => "http://127.0.0.1:$port"], true) . ';');
        [$code, $output] = self::php(['bin/install.php', '--admin-login=admin'], ['CUSTOS_ADMIN_PASSWORD' => self::PASSWORD]);
        $this->assertSame(0, $code, $output);
        $this->assertMatchesRegularExpression('/Registration code:\s+([A-Z2-7]{5}-){4}[A-Z2-7]{5}#[0-9A-F-]{19}/', $output);

        [$code, $output] = self::php(['bin/install.php']);
        $this->assertSame(0, $code, 'install.php can run again: ' . $output);
        $this->assertStringContainsString('Users already exist', $output);

        [$code, $output] = self::php(['bin/migrate.php']);
        $this->assertSame(0, $code, $output);
        $this->assertStringContainsString('up to date', $output);

        $port = self::freePort();
        self::$base = "http://127.0.0.1:$port";
        $root = dirname(__DIR__, 2);
        self::$server = proc_open(
            [PHP_BINARY, '-S', "127.0.0.1:$port", '-t', "$root/public", "$root/public/index.php"],
            [0 => ['pipe', 'r'], 1 => ['file', '/dev/null', 'w'], 2 => ['file', '/dev/null', 'w']],
            $pipes,
            $root,
            ['CUSTOS_CONFIG' => self::$config, 'no_proxy' => '127.0.0.1', 'NO_PROXY' => '127.0.0.1'] + getenv()
        );
        for ($i = 0; $i < 50 && @fsockopen('127.0.0.1', $port) === false; $i++) {
            usleep(100000);
        }
        self::$cookies = TestEnv::dir() . '/cookies.txt';
        @unlink(self::$cookies);
        [$status, , $headers] = $this->http('GET', '/agents');
        $this->assertSame(303, $status, 'pages need a login');
        $this->assertStringEndsWith('/login', $headers['location']);
    }

    #[Depends('testInstallAndStart')]
    public function testLogin(): void
    {
        [$status, $body] = $this->post('/login', ['login' => 'admin', 'password' => 'wrong']);
        $this->assertSame(200, $status);
        $this->assertStringContainsString('Неверный логин или пароль', $body);

        [$status] = $this->http('POST', '/login', ['login' => 'admin', 'password' => self::PASSWORD]);
        $this->assertSame(400, $status, 'a form without the CSRF token is refused');

        [$status, , $headers] = $this->post('/login', ['login' => 'admin', 'password' => self::PASSWORD]);
        $this->assertSame(303, $status);
        [$status, $body] = $this->http('GET', '/agents');
        $this->assertSame(200, $status);
        $this->assertStringContainsString('Машин пока нет', $body);
        $this->assertStringContainsString("frame-ancestors 'none'", $this->http('GET', '/agents')[2]['content-security-policy']);
    }

    #[Depends('testLogin')]
    public function testAgentRegistersAndIsApproved(): void
    {
        [$status, $settings] = $this->http('GET', '/settings');
        $this->assertSame(200, $status);
        $this->assertMatchesRegularExpression('/id="registration-code"[^>]*value="([^"]+)"/', $settings);
        preg_match('/id="registration-code"[^>]*value="([^"]+)"/', $settings, $m);
        $code = html_entity_decode($m[1]);

        [$exit, $output] = $this->fakeAgent(['--code=' . $code, '--result=success', '--skip-schedule']);
        $this->assertSame(0, $exit, $output);
        $this->assertStringContainsString('status pending', $output);
        preg_match('/agent key ([0-9A-F-]{19})/', $output, $fingerprint);

        [, $agents] = $this->http('GET', '/agents');
        $this->assertStringContainsString('Ждут подтверждения', $agents);
        $this->assertStringContainsString('WEB-PC-01', $agents);
        $this->assertStringContainsString($fingerprint[1], $agents, 'the fingerprint can be compared with the agent window');
        preg_match('#/agents/([0-9a-f-]{36})/approve#', $agents, $id);

        [$status] = $this->http('POST', "/agents/{$id[1]}/approve", []);
        $this->assertSame(400, $status);
        [$status] = $this->post("/agents/{$id[1]}/approve", ['back' => 'list']);
        $this->assertSame(303, $status);

        [$exit, $output] = $this->fakeAgent(['--result=error', '--skip-schedule']);
        $this->assertSame(0, $exit, $output);
        $this->assertStringContainsString('configuration sent', $output);

        [, $page] = $this->http('GET', "/agents/{$id[1]}");
        $this->assertStringContainsString('Accounting', $page);
        $this->assertStringContainsString('еженедельно: вс 01:00', $page);
        $this->assertStringContainsString('svc-backup', $page);

        [, $runs] = $this->http('GET', '/runs?status=error');
        $this->assertSame(1, preg_match_all('#href="/runs/([0-9a-f-]{36})"#', $runs, $runIds), $output);
        [$status, $run] = $this->http('GET', '/runs/' . $runIds[1][0]);
        $this->assertSame(200, $status);
        $this->assertStringContainsString('Открыть полный журнал', $run);
        [$status, $log] = $this->http('GET', '/runs/' . $runIds[1][0] . '/log');
        $this->assertSame(200, $status);
        $this->assertStringContainsString('ОШИБКА', $log);
        [$status, $gz, $headers] = $this->http('GET', '/runs/' . $runIds[1][0] . '/log/download');
        $this->assertSame(200, $status);
        $this->assertSame('application/gzip', $headers['content-type']);
        $this->assertStringContainsString('Готово: error', gzdecode($gz));
    }

    #[Depends('testLogin')]
    public function testUsersSettingsAndAudit(): void
    {
        [$status] = $this->post('/users', ['login' => 'operator', 'password' => 'short']);
        $this->assertSame(303, $status);
        $this->assertStringContainsString('не короче 8', $this->http('GET', '/users')[1]);
        $this->post('/users', ['login' => 'operator', 'password' => 'long-enough-1']);
        $this->assertStringContainsString('Пользователь operator создан', $this->http('GET', '/users')[1]);

        [$status, $body] = $this->post('/settings', $this->settings(['ip_allow_agents' => '192.0.2.0/24 nonsense']));
        $this->assertSame(422, $status);
        $this->assertStringContainsString('nonsense', $body);
        [$status, $body] = $this->post('/settings', $this->settings(['ip_allow_site' => '198.51.100.0/24']));
        $this->assertSame(422, $status, 'saving a list without your own address would lock you out');
        $this->assertStringContainsString('127.0.0.1', $body);
        [$status] = $this->post('/settings', $this->settings(['server_name' => 'Backup monitor', 'ip_allow_site' => '127.0.0.0/8']));
        $this->assertSame(303, $status);
        $this->assertStringContainsString('<title>Настройки · Backup monitor</title>', $this->http('GET', '/settings')[1]);

        $before = $this->registrationCode();
        $this->post('/settings/registration-key', []);
        $this->assertNotSame($before, $this->registrationCode());

        [, $audit] = $this->http('GET', '/audit');
        foreach (['неудачный вход', 'подтверждена машина', 'создан пользователь', 'изменены настройки', 'сгенерирован новый ключ регистрации'] as $entry) {
            $this->assertStringContainsString($entry, $audit);
        }
        $this->assertStringNotContainsString(str_replace('-', '', explode('#', $before)[0]), $audit, 'the registration key is never logged');
    }

    #[Depends('testLogin')]
    public function testLoginAttemptsAreLimited(): void
    {
        $jar = self::$cookies;
        self::$cookies = TestEnv::dir() . '/cookies-attacker.txt';
        try {
            for ($i = 0; $i < 5; $i++) {
                $this->assertSame(200, $this->post('/login', ['login' => 'operator', 'password' => 'guess' . $i])[0]);
            }
            [$status, $body] = $this->post('/login', ['login' => 'operator', 'password' => 'long-enough-1']);
            $this->assertSame(429, $status, 'the sixth attempt within 15 minutes is refused even with the right password');
            $this->assertStringContainsString('Слишком много', $body);
        } finally {
            self::$cookies = $jar;
        }
    }

    #[Depends('testAgentRegistersAndIsApproved')]
    public function testDashboardProblemsAndGroups(): void
    {
        [$status, $home] = $this->http('GET', '/');
        $this->assertSame(200, $status);
        $this->assertStringContainsString('Требует внимания', $home);
        $this->assertStringContainsString('Фоновые задачи (cron) ещё ни разу не запускались', $home);
        $this->assertMatchesRegularExpression('#href="/agents/([0-9a-f-]{36})/tasks/([0-9a-f-]{36})">\s*<strong>WEB-PC-01</strong> · Documents#', $home,
            'the failed run of "Documents" is an open problem');
        preg_match('#href="/agents/([0-9a-f-]{36})/tasks/([0-9a-f-]{36})">\s*<strong>WEB-PC-01</strong> · Documents#', $home, $m);
        [, $agentId, $taskId] = $m;

        [$status, $json, $headers] = $this->http('GET', '/dashboard/live');
        $this->assertSame(200, $status);
        $this->assertStringStartsWith('application/json', $headers['content-type']);
        $live = json_decode($json, true);
        $this->assertSame(['summary', 'attention', 'running', 'notices'], array_keys($live['parts']));
        $this->assertStringContainsString('Documents', $live['parts']['attention']);
        $this->assertGreaterThan(0, $live['bell'], 'registration and the first configuration are notices');

        [, $live] = $this->http('GET', "/agents/$agentId/live");
        $this->assertSame(['now', 'problems', 'tasks'], array_keys(json_decode($live, true)['parts']));

        [$status, $task] = $this->http('GET', "/agents/$agentId/tasks/$taskId");
        $this->assertSame(200, $status);
        $this->assertStringContainsString('Documents', $task);
        $this->assertStringContainsString('Ожидаемые запуски', $task);
        [$status, $problems] = $this->http('GET', '/problems');
        $this->assertSame(200, $status);
        $this->assertStringContainsString('Documents', $problems);

        // The bell: one notice, then all of them.
        [, $notices] = $this->http('GET', '/notices');
        $this->assertStringContainsString('ждёт подтверждения', $notices);
        $this->post('/notices/read', []);
        $this->assertSame(0, json_decode($this->http('GET', '/dashboard/live')[1], true)['bell']);

        // Groups and the filter of the machines page.
        $this->post('/groups', ['name' => 'Office']);
        [, $groups] = $this->http('GET', '/groups');
        $this->assertStringContainsString('Office', $groups);
        preg_match('#/groups/(\d+)/rename#', $groups, $g);
        [$status] = $this->post("/agents/$agentId/group", ['group_id' => $g[1]]);
        $this->assertSame(303, $status);
        [, $agents] = $this->http('GET', '/agents?group=' . $g[1]);
        $this->assertStringContainsString('WEB-PC-01', $agents);
        $this->assertStringContainsString('class="pill pill-bad"', $agents);
        [, $agents] = $this->http('GET', '/agents?group=none');
        $this->assertStringNotContainsString('WEB-PC-01', $agents);

        // Logged out, the live part asks for a login instead of redirecting.
        $jar = self::$cookies;
        self::$cookies = TestEnv::dir() . '/cookies-anonymous.txt';
        try {
            [$status, $json] = $this->http('GET', '/dashboard/live');
            $this->assertSame(401, $status);
            $this->assertSame(['error' => 'login'], json_decode($json, true));
        } finally {
            self::$cookies = $jar;
        }
    }

    #[Depends('testDashboardProblemsAndGroups')]
    public function testNotificationsAndCron(): void
    {
        $dir = TestEnv::dir() . '/telegram';
        $form = ['telegram_token' => '', 'telegram_chat_ids' => '', 'telegram_proxy' => '', 'smtp_host' => '', 'smtp_port' => '587',
                 'smtp_encryption' => 'starttls', 'smtp_user' => '', 'smtp_password' => '', 'mail_from' => '', 'mail_to' => ''];
        [$status, $page] = $this->post('/settings/notifications', ['telegram_token' => 'not a token', 'mail_to' => 'nobody'] + $form);
        $this->assertSame(422, $status);
        $this->assertStringContainsString('@BotFather', $page);

        $token = '123456789:' . str_repeat('A', 35);
        [$status] = $this->post('/settings/notifications', ['telegram_token' => $token, 'telegram_chat_ids' => '-1001234567890, @custos_test', 'test' => 'telegram'] + $form);
        $this->assertSame(303, $status);
        [, $page] = $this->http('GET', '/settings/notifications');
        $this->assertStringContainsString('Тестовое сообщение отправлено: -1001234567890', $page);
        $this->assertStringContainsString('Тестовое сообщение отправлено: @custos_test', $page);
        $this->assertStringNotContainsString($token, $page, 'the token is never shown again');

        // Saving again with an empty token keeps it.
        $this->post('/settings/notifications', ['telegram_chat_ids' => '-1001234567890'] + $form);
        [, $audit] = $this->http('GET', '/audit');
        $this->assertStringNotContainsString($token, $audit, 'the token never goes into the audit log');

        [$code, $output] = self::php(['bin/cron.php', '--verbose']);
        $this->assertSame(0, $code, $output);
        $stats = json_decode(substr($output, strpos($output, '{')), true);
        $this->assertIsArray($stats, $output);
        $sent = array_map(fn($line) => json_decode($line, true), file("$dir/sent.log", FILE_IGNORE_NEW_LINES));
        $this->assertCount(3, $sent, 'two test messages and one message about the problem');
        $this->assertSame('-1001234567890', $sent[2]['chat_id']);
        $this->assertStringContainsString('Documents', $sent[2]['text']);
        $this->assertStringContainsString('http://custos.test/agents/', $sent[2]['text']);

        [, $home] = $this->http('GET', '/');
        $this->assertStringNotContainsString('cron', $home, 'cron has run');
        [, $notifications] = $this->http('GET', '/settings/notifications');
        $this->assertStringContainsString('проблем: 1', $notifications, 'the latest messages are listed');
    }

    #[Depends('testInstallAndStart')]
    public function testCron(): void
    {
        [$code, $output] = self::php(['bin/cron.php']);
        $this->assertSame(0, $code, $output);
        [, $settings] = $this->http('GET', '/settings');
        $this->assertStringContainsString('registration-code', $settings);
    }

    // ---- helpers ----

    private function settings(array $overrides): array
    {
        return $overrides + ['server_name' => 'CUSTOS', 'heartbeat_interval_s' => '60', 'offline_after_min' => '60', 'ip_allow_site' => '', 'ip_allow_agents' => ''];
    }

    private function registrationCode(): string
    {
        preg_match('/id="registration-code"[^>]*value="([^"]+)"/', $this->http('GET', '/settings')[1], $m);
        return $m[1];
    }

    private function fakeAgent(array $args): array
    {
        return self::php(['tools/fake-agent.php', '--url=' . self::$base, '--once', '--name=WEB-PC', '--state=' . TestEnv::dir() . '/fake-agents', ...$args]);
    }

    /** POST with the CSRF token of the current session. */
    private function post(string $path, array $fields): array
    {
        [, $page] = $this->http('GET', str_starts_with($path, '/login') ? '/login' : '/settings');
        preg_match('/name="_csrf" value="([0-9a-f]+)"/', $page, $m);
        return $this->http('POST', $path, ['_csrf' => $m[1] ?? ''] + $fields);
    }

    /** @return array{0: int, 1: string, 2: array<string, string>} */
    private function http(string $method, string $path, ?array $fields = null): array
    {
        $headers = [];
        $curl = curl_init(self::$base . $path);
        curl_setopt_array($curl, [
            CURLOPT_CUSTOMREQUEST => $method,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_COOKIEJAR => self::$cookies,
            CURLOPT_COOKIEFILE => self::$cookies,
            CURLOPT_PROXY => '',
            CURLOPT_HEADERFUNCTION => function ($curl, string $line) use (&$headers): int {
                if (str_contains($line, ':')) {
                    [$name, $value] = explode(':', $line, 2);
                    $headers[strtolower(trim($name))] = trim($value);
                }
                return strlen($line);
            },
        ] + ($fields === null ? [] : [CURLOPT_POSTFIELDS => http_build_query($fields)]));
        $body = (string) curl_exec($curl);
        return [(int) curl_getinfo($curl, CURLINFO_RESPONSE_CODE), $body, $headers];
    }

    private static function freePort(): int
    {
        $socket = stream_socket_server('tcp://127.0.0.1:0');
        $port = (int) substr(strrchr(stream_socket_get_name($socket, false), ':'), 1);
        fclose($socket);
        return $port;
    }

    /** @return array{0: int, 1: string} exit code and output */
    private static function php(array $args, array $env = []): array
    {
        $process = proc_open(
            [PHP_BINARY, ...$args],
            [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['redirect', 1]],
            $pipes,
            dirname(__DIR__, 2),
            ['CUSTOS_CONFIG' => self::$config, 'no_proxy' => '127.0.0.1', 'NO_PROXY' => '127.0.0.1'] + $env + getenv()
        );
        fclose($pipes[0]);
        $output = stream_get_contents($pipes[1]);
        fclose($pipes[1]);
        return [proc_close($process), (string) $output];
    }
}
