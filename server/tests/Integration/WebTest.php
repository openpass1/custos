<?php

declare(strict_types=1);

namespace Custos\Tests\Integration;

use Custos\Http\Web\Controller\AgentsController;
use Custos\Infra\Db;
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
        // Fake Telegram and GitHub APIs: Telegram answers "ok" and writes down what it was sent; GitHub lists one 7-Zip
        // release whose installer it serves. They go into the configuration now: the site under "php -S" keeps the
        // configuration file in the opcode cache.
        $dir = TestEnv::dir() . '/telegram';
        @mkdir($dir);
        $port = self::freePort();
        $installer = "MZ fake 7-Zip 24.10 installer\n" . random_bytes(2048);
        file_put_contents("$dir/7z2410-x64.exe", $installer);
        file_put_contents("$dir/releases.json", json_encode([
            ['tag_name' => '24.10', 'published_at' => '2026-11-01T10:00:00Z', 'assets' => [[
                'name' => '7z2410-x64.exe', 'browser_download_url' => "http://127.0.0.1:$port/7z2410-x64.exe",
                'digest' => 'sha256:' . hash('sha256', $installer),
            ]]],
            ['tag_name' => '24.09', 'assets' => []],
        ]));
        file_put_contents("$dir/router.php", <<<'PHP'
            <?php
            $path = parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH);
            if ($path === '/repos/ip7z/7zip/releases' || $path === '/7z2410-x64.exe') {
                header('Content-Type: ' . ($path === '/7z2410-x64.exe' ? 'application/octet-stream' : 'application/json'));
                readfile(__DIR__ . ($path === '/7z2410-x64.exe' ? $path : '/releases.json'));
                return;
            }
            file_put_contents(__DIR__ . '/sent.log', json_encode($_POST) . "\n", FILE_APPEND);
            header('Content-Type: application/json');
            echo '{"ok":true,"result":{}}';
            PHP);
        self::$telegram = proc_open([PHP_BINARY, '-S', "127.0.0.1:$port", "$dir/router.php"],
            [0 => ['pipe', 'r'], 1 => ['file', '/dev/null', 'w'], 2 => ['file', '/dev/null', 'w']], $pipes, $dir);
        file_put_contents(self::$config, '<?php return ' . var_export(TestEnv::config()
            + ['telegram_api_url' => "http://127.0.0.1:$port", 'github_api_url' => "http://127.0.0.1:$port"], true) . ';');
        [$code, $output] = self::php(['bin/install.php', '--admin-login=admin'], ['CUSTOS_ADMIN_PASSWORD' => self::PASSWORD]);
        $this->assertSame(0, $code, $output);
        // The site address from the config, "#" and the registration code (A.3).
        $this->assertMatchesRegularExpression('~Connection string:\s+http://custos\.test#([A-Z2-7]{5}-){4}[A-Z2-7]{5}#[0-9A-F-]{19}\n~', $output);

        [$code, $output] = self::php(['bin/install.php', '--language=ru']);
        $this->assertSame(0, $code, 'install.php can run again: ' . $output);
        $this->assertStringContainsString('Users already exist', $output);
        $this->assertStringContainsString('Default language: ru', $output);
        [$code, $output] = self::php(['bin/install.php', '--language=de']);
        $this->assertNotSame(0, $code, 'an unknown language is refused');

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
        // One line holds what a machine needs: the site address from the config, "#" and the registration code (A.3).
        // The page shows nothing else to copy. The fake agent takes the line in --connect; the test server listens
        // elsewhere than the configured address, so --url still gives the real one.
        $connection = $this->connectionString();
        $this->assertMatchesRegularExpression('~^http://custos\.test#([A-Z2-7]{5}-){4}[A-Z2-7]{5}#[0-9A-F-]{19}$~', $connection);
        $this->assertStringNotContainsString('registration-code', $settings);
        $this->assertSame(1, substr_count($settings, 'data-copy='), 'one copy button in the connection block');

        [$exit, $output] = $this->fakeAgent(['--connect=' . $connection, '--result=success', '--skip-schedule']);
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
        // The notice that the machine waited for approval stays in the list but is no longer unread.
        [, $notices] = $this->http('GET', '/notices');
        $this->assertStringContainsString('ждёт подтверждения', $notices);
        $this->assertStringNotContainsString('class="unread"', $notices);

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

        // Log out after inactivity (Settings → General): days, 7 by default, 0 = never.
        $this->assertMatchesRegularExpression('/name="logout_after_days" value="7"/', $this->http('GET', '/settings')[1]);
        [$status, $body] = $this->post('/settings', ['logout_after_days' => '-5']);
        $this->assertSame(422, $status);
        $this->assertStringContainsString('Нужно целое число от 0 до 365', $body);
        [$status] = $this->post('/settings', ['logout_after_days' => '1']);
        $this->assertSame(303, $status, 'the other fields of the page keep their values');
        $this->idle(12 * 3600);
        $this->assertSame(200, $this->http('GET', '/settings')[0], 'half a day is not enough');
        $this->idle(30 * 3600);
        [$status, , $headers] = $this->http('GET', '/settings');
        $this->assertSame(303, $status, 'more than a day without activity ends the login');
        $this->assertStringEndsWith('/login', $headers['location']);
        $this->assertStringContainsString('Сеанс завершён из-за бездействия', $this->http('GET', '/login')[1]);
        $this->post('/login', ['login' => 'admin', 'password' => self::PASSWORD]);
        $this->post('/settings', ['logout_after_days' => '0']);
        $this->idle(400 * 86400);
        $this->assertSame(200, $this->http('GET', '/settings')[0], '0: no logout for inactivity');
        $this->post('/settings', ['logout_after_days' => '7']);
        $this->assertSame('7', (string) Db::connect(TestEnv::db())->value("SELECT value FROM settings WHERE name = 'logout_after_days'"));

        $before = $this->connectionString();
        $this->post('/settings/registration-key', []);
        $this->assertNotSame($before, $this->connectionString());

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
        $this->assertSame(['summary', 'attention', 'running', 'notices', 'versions'], array_keys($live['parts']));
        $this->assertStringContainsString('Documents', $live['parts']['attention']);
        $this->assertSame(0, $live['bell'], 'the notice of the registration was settled by the approval');

        // A page with live parts asks for itself again (app.js): the same page with its parts, but a message after an
        // action is left for the page the person opens next.
        [$status, $machine] = $this->http('GET', "/agents/$agentId");
        $this->assertSame(200, $status);
        $this->assertStringContainsString('<main data-live-page="' . AgentsController::REFRESH_S . '">', $machine);
        foreach (['head', 'facts', 'problems', 'now', 'tasks', 'storage', 'runs', 'commands'] as $part) {
            $this->assertStringContainsString('data-part="' . $part . '"', $machine);
        }
        $this->assertMatchesRegularExpression('#data-nav-count="problems">\d+</span>#', $machine, 'the open problem is counted in the menu');
        $this->post("/agents/$agentId/rename", ['display_name' => '']);
        [$status, $live] = $this->http('GET', "/agents/$agentId", null, ['X-Custos-Live: 1']);
        $this->assertSame(200, $status);
        $this->assertStringContainsString('data-part="tasks"', $live);
        $this->assertStringContainsString('Documents', $live);
        $this->assertStringNotContainsString('class="toasts"', $live, 'a refresh does not take the message of the rename');
        [, $machine] = $this->http('GET', "/agents/$agentId");
        $this->assertStringContainsString('class="toasts"', $machine, 'the message waits for the page the person opens');
        foreach (['/agents' => ['pending', 'chips', 'list'], "/agents/$agentId/tasks/$taskId" => ['head', 'state', 'problems', 'slots', 'runs', 'commands'],
                  '/runs' => ['list'], '/problems' => ['open', 'closed'], '/notices' => ['head', 'list'], '/updates' => ['machines'],
                  "/agents/$agentId/settings" => ['state', 'form']] as $path => $parts) {
            [$status, $page] = $this->http('GET', $path, null, ['X-Custos-Live: 1']);
            $this->assertSame(200, $status, $path);
            $this->assertStringContainsString('<main data-live-page=', $page, $path);
            foreach ($parts as $part) {
                $this->assertStringContainsString('data-part="' . $part . '"', $page, "$path: $part");
            }
        }
        [, $groups] = $this->http('GET', '/groups');
        $this->assertStringNotContainsString('data-live-page', $groups, 'a page without live parts does not ask for itself');

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
            [$status, $json] = $this->http('GET', "/agents/$agentId", null, ['X-Custos-Live: 1']);
            $this->assertSame(401, $status, 'a page refreshing itself is told to log in, not sent to the login page');
            $this->assertSame(['error' => 'login'], json_decode($json, true));
            [$status] = $this->http('GET', "/agents/$agentId");
            $this->assertContains($status, [302, 303], 'opened by a person, the page sends them to the login');
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
        $this->assertSame('ok', $stats['updates']['sevenzip_check'] ?? null, 'cron asked (the fake) GitHub for 7-Zip releases: ' . $output);
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

    /** Stage 3 through the pages: the task editor, versions, agent settings, a template, updates of CUSTOS and 7-Zip. */
    #[Depends('testNotificationsAndCron')]
    public function testManagementFromTheSite(): void
    {
        [, $agents] = $this->http('GET', '/agents');
        $this->assertSame(1, preg_match('#href="/agents/([0-9a-f-]{36})"#', $agents, $m));
        $agentUrl = '/agents/' . $m[1];
        [, $page] = $this->http('GET', $agentUrl);
        $this->assertSame(1, preg_match('#href="' . $agentUrl . '/tasks/([0-9a-f-]{36})"><strong>Documents</strong>#', $page, $m));
        $taskUrl = $agentUrl . '/tasks/' . $m[1];

        // The editor: rename the task, the agent applies it, the versions page shows the difference.
        [$status, $editor] = $this->http('GET', "$taskUrl/edit");
        $this->assertSame(200, $status);
        $form = self::formFields($editor, "$taskUrl/edit");
        [$status, $page] = $this->http('POST', "$taskUrl/edit", self::encode(self::withValues($form, ['name' => 'Documents and photos'])));
        $this->assertSame(303, $status, $page);
        [, $page] = $this->http('GET', $taskUrl);
        $this->assertStringContainsString('Изменение сохранено и ждёт доставки агенту.', $page);
        $this->assertStringContainsString('ещё не подтверждено агентом', $page);
        [$status, $page] = $this->http('POST', "$taskUrl/edit", self::encode(self::withValues($form, ['schedule[times]' => '25:00'])));
        $this->assertSame(422, $status, 'a wrong time is refused like the client refuses it');
        $this->assertStringContainsString('25:00', $page);

        // Agent settings and a command for the task go along with the next heartbeat.
        [$status, $settings] = $this->http('GET', "$agentUrl/settings");
        $this->assertSame(200, $status);
        $fields = self::withValues(self::formFields($settings, "$agentUrl/settings"), ['log_retention_days' => '45']);
        [$status, , $headers] = $this->http('POST', "$agentUrl/settings", self::encode($fields));
        $this->assertSame(303, $status);
        $this->assertStringContainsString('Настройки отправлены агенту.', $this->http('GET', parse_url($headers['location'], PHP_URL_PATH))[1]);
        [$status] = $this->post("$taskUrl/disable", []);
        $this->assertSame(303, $status);
        [, $page] = $this->http('GET', $agentUrl);
        $this->assertSame(3, preg_match_all('#/commands/[0-9a-f-]{36}/cancel"#', $page), 'the edit, the settings and the command wait and can be cancelled');

        [$exit, $output] = $this->fakeAgent(['--result=success', '--skip-schedule', '--sevenzip=23.01']);
        $this->assertSame(0, $exit, $output);
        [, $page] = $this->http('GET', $taskUrl);
        $this->assertStringContainsString('Documents and photos', $page);
        $this->assertStringNotContainsString('ещё не подтверждено агентом', $page);
        [$status, $versions] = $this->http('GET', "$taskUrl/versions");
        $this->assertSame(200, $status);
        $this->assertStringContainsString('с сайта', $versions);
        $this->assertMatchesRegularExpression('#<td class="mono">name</td>\s*<td class="mono old">&quot;Documents&quot;</td>\s*<td class="mono new">&quot;Documents and photos&quot;</td>#', $versions,
            'the difference with the version before');
        [, $settings] = $this->http('GET', "$agentUrl/settings");
        $this->assertMatchesRegularExpression('/name="log_retention_days"[^>]*value="45"/', $settings);

        // A template from the task, applied to the machine: a second task appears there.
        [$status, $editor] = $this->http('GET', '/templates/new?agent=' . basename($agentUrl) . '&task=' . basename($taskUrl));
        $this->assertSame(200, $status);
        // The timer field of a daily task is hidden; with 0 (below its min) the browser refused to send the form.
        $this->assertMatchesRegularExpression('/name="schedule\[interval_minutes\]" value="60"/', $editor);
        $fields = self::withValues(self::formFields($editor, '/templates/new'), ['name' => 'Office documents']);
        [$status, , $headers] = $this->http('POST', '/templates/new', self::encode($fields));
        $this->assertSame(303, $status);
        $templateUrl = parse_url($headers['location'], PHP_URL_PATH);
        [, $template] = $this->http('GET', $templateUrl);
        $this->assertStringContainsString('Шаблон «Office documents» создан.', $template);
        $fields = self::withValues(self::formFields($template, "$templateUrl/apply"), ['agent_ids[]' => basename($agentUrl)]);
        [$status] = $this->http('POST', "$templateUrl/apply", self::encode($fields));
        $this->assertSame(303, $status);
        [, $template] = $this->http('GET', $templateUrl);
        $this->assertStringContainsString('WEB-PC-01: не применён — нужен пароль для svc-backup', $template);
        $fields = array_map(fn($f) => str_starts_with($f[0], 'passwords[') ? [$f[0], 'share-password'] : $f, $fields);
        $this->http('POST', "$templateUrl/apply", self::encode($fields));
        [, $template] = $this->http('GET', $templateUrl);
        $this->assertStringContainsString('Шаблон «Office documents» отправлен машинам: 1.', $template);

        // Updates: a CUSTOS package uploaded and made current, the 7-Zip installer from (the fake) GitHub.
        $package = TestEnv::dir() . '/custos-1.0.1.zip';
        [$code, $output] = self::php(['tools/make-update-package.php', '--version=1.0.1', '--out=' . $package]);
        $this->assertSame(0, $code, $output);
        [, $updates] = $this->http('GET', '/updates');
        preg_match('/name="_csrf" value="([0-9a-f]+)"/', $updates, $csrf);
        [$status] = $this->http('POST', '/updates/upload', ['_csrf' => $csrf[1], 'package' => new \CURLFile($package, 'application/zip', 'custos-1.0.1.zip')]);
        $this->assertSame(303, $status);
        $this->assertStringContainsString('Пакет 1.0.1 загружен.', $this->http('GET', '/updates')[1]);
        [$status] = $this->post('/updates/agent/1.0.1/current', []);
        $this->assertSame(303, $status);
        [$status] = $this->post('/updates/sevenzip/download', []);
        $this->assertSame(303, $status);
        [, $updates] = $this->http('GET', '/updates');
        $this->assertStringContainsString('Установщик 7-Zip 24.10 сохранён на сервере.', $updates);
        foreach (['agent', '7zip'] as $kind) {
            $fields = self::withValues(self::formFields($updates, '/updates/install'), ['agent_ids[]' => basename($agentUrl), 'kind' => $kind]);
            [$status] = $this->http('POST', '/updates/install', self::encode($fields));
            $this->assertSame(303, $status);
        }
        [, $updates] = $this->http('GET', '/updates');
        $this->assertStringContainsString('Обновление до 24.10 отправлено машинам: 1.', $updates);

        [$exit, $output] = $this->fakeAgent(['--result=success', '--skip-schedule']);
        $this->assertSame(0, $exit, $output);
        $this->assertStringContainsString('CUSTOS 1.0.1 installed', $output);
        $this->assertStringContainsString('7-Zip 24.10 installed', $output);
        // Again on a machine that already runs 1.0.1: nothing is sent, and it is said why, not as an error.
        $fields = self::withValues(self::formFields($this->http('GET', '/updates')[1], '/updates/install'), ['agent_ids[]' => basename($agentUrl), 'kind' => 'agent']);
        $this->http('POST', '/updates/install', self::encode($fields));
        $this->assertStringContainsString('<div class="flash flash-info">Обновлять нечего: на выбранных машинах (1) уже 1.0.1 или новее.</div>',
            $this->http('GET', '/updates')[1]);
        [, $page] = $this->http('GET', $agentUrl);
        $this->assertStringContainsString('Office documents', $page, 'the task made from the template');
        $this->assertStringContainsString('24.10', $page);
        $versions = json_decode($this->http('GET', '/dashboard/live')[1], true)['parts']['versions'];
        $this->assertStringContainsString('1.0.1', $versions);
    }

    /** Stage 4 through the pages: icons, heat map, charts, storages, CSV, profile (English, dark), Zabbix and the wall. */
    #[Depends('testManagementFromTheSite')]
    public function testAnalyticsProfileAndIntegrations(): void
    {
        foreach (['/favicon.svg' => '<svg', '/favicon.ico' => "\x00\x00\x01\x00", '/apple-touch-icon.png' => "\x89PNG"] as $path => $start) {
            [$status, $body] = $this->http('GET', $path);
            $this->assertSame(200, $status, $path);
            $this->assertStringStartsWith($start, $body, $path);
        }

        [$exit, $output] = $this->fakeAgent(['--result=success', '--skip-schedule']);
        $this->assertSame(0, $exit, $output);
        $db = Db::connect(TestEnv::db());
        // The fake agent runs its first task on every pass: that task has the most runs.
        $task = $db->one('SELECT t.agent_id, t.id, t.name FROM tasks t JOIN runs r ON r.agent_id = t.agent_id AND r.task_id = t.id
                          WHERE t.deleted = 0 GROUP BY t.agent_id, t.id, t.name ORDER BY COUNT(*) DESC LIMIT 1');
        $taskUrl = "/agents/{$task['agent_id']}/tasks/{$task['id']}";

        [$status, $home] = $this->http('GET', '/');
        $this->assertSame(200, $status);
        $this->assertStringContainsString('<link rel="icon" href="/favicon.svg"', $home);
        $this->assertStringContainsString('class="heatmap"', $home);
        $this->assertStringContainsString('Результаты за 30 дней', $home);
        $this->assertStringContainsString('href="/runs?agent=' . $task['agent_id'] . '&amp;from=' . gmdate('Y-m-d') . '&amp;to=' . gmdate('Y-m-d') . '"', $home,
            'a cell of today leads to the runs of that day');

        [, $page] = $this->http('GET', $taskUrl);
        $this->assertStringContainsString('src="/assets/vendor/chart.umd.min.js', $page);
        $this->assertSame(1, preg_match('#<script type="application/json" id="task-chart-data">(.*?)</script>#s', $page, $m));
        $chart = json_decode($m[1], true);
        $this->assertGreaterThanOrEqual(2, count($chart['runs']));
        $this->assertSame(count($chart['runs']), count($chart['source']));
        $this->assertStringStartsWith('/runs/', $chart['runs'][0]);

        [, $agent] = $this->http('GET', '/agents/' . $task['agent_id']);
        $this->assertStringContainsString('Хранилища', $agent, 'the fake agent sends an inventory after each run');
        $this->assertStringContainsString('class="storage', $agent);

        [$status, $csv, $headers] = $this->http('GET', '/runs/export?agent=' . $task['agent_id']);
        $this->assertSame(200, $status);
        $this->assertStringStartsWith('text/csv', $headers['content-type']);
        $this->assertStringContainsString('attachment', $headers['content-disposition']);
        $this->assertStringStartsWith("\u{FEFF}Машина;Задание;", $csv);
        $lines = explode("\r\n", trim($csv));
        $this->assertGreaterThanOrEqual(3, count($lines));
        $this->assertStringContainsString(';' . $task['name'] . ';', implode("\n", $lines));

        // A run with database dumps (client stage 2b) shows them.
        $runId = $db->value("SELECT id FROM runs WHERE agent_id = ? AND task_id = ? ORDER BY started_at DESC LIMIT 1", [$task['agent_id'], $task['id']]);
        $db->exec('UPDATE runs SET db_dumps = ? WHERE id = ?', [json_encode([
            ['engine' => 'mariadb', 'name' => 'shop', 'bytes' => 2048, 'ok' => true, 'error' => null],
            ['engine' => 'postgresql', 'name' => 'crm', 'bytes' => null, 'ok' => false, 'error' => 'connection refused'],
        ]), $runId]);
        [, $run] = $this->http('GET', "/runs/$runId");
        $this->assertStringContainsString('Дампы баз данных', $run);
        $this->assertStringContainsString('connection refused', $run);

        // The user menu (no profile page any more): English and the dark theme for this user only, back to the same page.
        [$status, , $headers] = $this->http('GET', '/profile');
        $this->assertSame(303, $status);
        [, $home] = $this->http('GET', '/');
        $this->assertStringContainsString('class="dropdown user-menu"', $home);
        $this->assertStringContainsString('Как на сайте (Русский)', $home);
        $this->assertStringContainsString('id="password-dialog"', $home);
        [$status, , $headers] = $this->post('/profile', ['language' => 'en', 'theme' => 'dark', 'back' => '/runs?status=error']);
        $this->assertSame(303, $status);
        $this->assertStringEndsWith('/runs?status=error', $headers['location']);
        [, $home, $headers] = $this->http('GET', '/');
        $this->assertStringContainsString('<html lang="en" data-theme="dark"', $home);
        $this->assertSame('en', $headers['content-language']);
        $this->assertStringContainsString('Results for 30 days', $home);
        $this->assertMatchesRegularExpression('/name="theme" value="dark" aria-pressed="true"/', $home);
        // Another host is never a way back.
        [, , $headers] = $this->post('/profile', ['language' => 'en', 'theme' => 'dark', 'back' => '//evil.example/']);
        $this->assertStringNotContainsString('evil', $headers['location']);
        $this->post('/profile/password', ['current' => 'wrong', 'password' => 'another-password-1', 'repeat' => 'another-password-1', 'back' => '/agents']);
        $this->assertStringContainsString('The current password is wrong.', $this->http('GET', '/agents')[1]);
        $this->post('/profile/password', ['current' => self::PASSWORD, 'password' => 'another-password-1', 'repeat' => 'other']);
        $this->assertStringContainsString('The new password and its repeat do not match.', $this->http('GET', '/')[1]);
        $this->post('/profile', ['language' => '', 'theme' => 'auto']);
        [, $home] = $this->http('GET', '/');
        $this->assertStringContainsString('<html lang="ru">', $home);

        // Zabbix and the wall screen.
        [$status, $integrations] = $this->http('GET', '/settings/integrations');
        $this->assertSame(200, $status);
        $this->assertSame(1, preg_match('/id="zabbix-token"[^>]*value="([0-9a-f]{48})"/', $integrations, $zabbix));
        $this->assertSame(1, preg_match('#id="wall-url"[^>]*value="http://custos\.test/wall\?token=([0-9a-f]{48})"#', $integrations, $wall));
        [$status, $yaml, $headers] = $this->http('GET', '/settings/integrations/zabbix-template');
        $this->assertSame(200, $status);
        $this->assertStringContainsString("template: 'CUSTOS by HTTP'", $yaml);
        $this->assertStringContainsString('custos_template.yaml', $headers['content-disposition']);

        $jar = self::$cookies;
        self::$cookies = TestEnv::dir() . '/cookies-wall.txt';
        try {
            [$status, $json] = $this->http('GET', '/api/zabbix/status?token=' . $zabbix[1]);
            $this->assertSame(200, $status, $json);
            $this->assertSame(1, json_decode($json, true)['summary']['agents']);
            [$status, $json] = $this->http('GET', '/api/zabbix/discovery/tasks?token=' . $zabbix[1]);
            $this->assertStringContainsString('"{#TASK}":"' . $task['name'] . '"', $json);
            [$status, $json] = $this->http('GET', '/api/health');
            $this->assertSame('ok', json_decode($json, true)['db']);
            [$status, $wallPage] = $this->http('GET', '/wall?token=' . $wall[1]);
            $this->assertSame(200, $status);
            $this->assertStringContainsString('<small> / 1</small>', $wallPage, 'one machine on the wall');
            $this->assertStringContainsString('src="/assets/app.js', $wallPage);
        } finally {
            self::$cookies = $jar;
        }

        [$status] = $this->post('/settings/integrations/wall-token', []);
        $this->assertSame(303, $status);
        [, $integrations] = $this->http('GET', '/settings/integrations');
        preg_match('#/wall\?token=([0-9a-f]{48})#', $integrations, $newWall);
        $this->assertNotSame($wall[1], $newWall[1]);
        self::$cookies = TestEnv::dir() . '/cookies-wall.txt';
        try {
            $this->assertSame(403, $this->http('GET', '/wall?token=' . $wall[1])[0], 'the old link stops working');
            $this->assertSame(200, $this->http('GET', '/wall?token=' . $newWall[1])[0]);
        } finally {
            self::$cookies = $jar;
        }
        [, $audit] = $this->http('GET', '/audit');
        $this->assertStringContainsString('сгенерирован новый токен', $audit);
        $this->assertStringNotContainsString($newWall[1], $audit, 'tokens never go into the audit log');
    }

    #[Depends('testInstallAndStart')]
    public function testCron(): void
    {
        [$code, $output] = self::php(['bin/cron.php']);
        $this->assertSame(0, $code, $output);
        [, $settings] = $this->http('GET', '/settings');
        $this->assertStringContainsString('connection-string', $settings);
    }

    // ---- helpers ----

    private function settings(array $overrides): array
    {
        return $overrides + ['server_name' => 'CUSTOS', 'heartbeat_interval_s' => '60', 'offline_after_min' => '60', 'ip_allow_site' => '', 'ip_allow_agents' => ''];
    }

    /** Makes the login of this cookie jar look idle for so many seconds (moves last_activity in its session file). */
    private function idle(int $seconds): void
    {
        preg_match('/\tCUSTOSSID\t(\S+)/', (string) file_get_contents(self::$cookies), $m);
        $file = TestEnv::dir() . '/var/sessions/sess_' . ($m[1] ?? 'none');
        $data = (string) file_get_contents($file);
        $this->assertMatchesRegularExpression('/last_activity\|i:\d+;/', $data);
        file_put_contents($file, preg_replace('/last_activity\|i:\d+;/', 'last_activity|i:' . (time() - $seconds) . ';', $data));
    }

    /** The connection string of the settings page: the site address, "#" and the registration code. */
    private function connectionString(): string
    {
        preg_match('/id="connection-string"[^>]*value="([^"]+)"/', $this->http('GET', '/settings')[1], $m);
        return html_entity_decode($m[1] ?? '');
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

    /**
     * The fields a browser sends with the form that has this action: nothing from <template> rows or disabled fields,
     * only checked boxes, the selected option of each list.
     *
     * @return list<array{0: string, 1: string}>
     */
    private static function formFields(string $html, string $action): array
    {
        $doc = new \DOMDocument();
        @$doc->loadHTML('<?xml encoding="UTF-8">' . $html);
        $xpath = new \DOMXPath($doc);
        $form = $xpath->query('//form[@action="' . $action . '"]')->item(0) ?? throw new \RuntimeException("No form for $action");
        $fields = [];
        foreach ($xpath->query('.//input | .//select | .//textarea', $form) as $field) {
            $name = $field->getAttribute('name');
            if ($name === '' || $field->hasAttribute('disabled') || $xpath->query('ancestor::template | ancestor::fieldset[@disabled]', $field)->length > 0) {
                continue;
            }
            $type = strtolower($field->getAttribute('type') ?: 'text');
            if ($field->nodeName === 'select') {
                $option = $xpath->query('.//option[@selected]', $field)->item(0) ?? $xpath->query('.//option', $field)->item(0);
                if ($option !== null) {
                    $fields[] = [$name, $option->hasAttribute('value') ? $option->getAttribute('value') : $option->textContent];
                }
            } elseif ($field->nodeName === 'textarea') {
                $fields[] = [$name, $field->textContent];
            } elseif (in_array($type, ['checkbox', 'radio'], true)) {
                if ($field->hasAttribute('checked')) {
                    $fields[] = [$name, $field->hasAttribute('value') ? $field->getAttribute('value') : 'on'];
                }
            } elseif (!in_array($type, ['submit', 'button', 'reset', 'file'], true)) {
                $fields[] = [$name, $field->getAttribute('value')];
            }
        }
        return $fields;
    }

    /** Sets fields as a user would: a field that is there gets the value, any other is added (a checked box). */
    private static function withValues(array $fields, array $values): array
    {
        foreach ($values as $name => $value) {
            $found = false;
            foreach ($fields as $i => $field) {
                if ($field[0] === $name && !str_ends_with($name, '[]')) {
                    $fields[$i][1] = (string) $value;
                    $found = true;
                }
            }
            if (!$found) {
                $fields[] = [$name, (string) $value];
            }
        }
        return $fields;
    }

    private static function encode(array $fields): string
    {
        return implode('&', array_map(fn($field) => rawurlencode($field[0]) . '=' . rawurlencode($field[1]), $fields));
    }

    /**
     * $fields: form fields (urlencoded), a ready body, or fields with a \CURLFile (multipart).
     *
     * @return array{0: int, 1: string, 2: array<string, string>}
     */
    /** @param list<string> $send extra request headers ("Name: value") */
    private function http(string $method, string $path, array|string|null $fields = null, array $send = []): array
    {
        $headers = [];
        $curl = curl_init(self::$base . $path);
        curl_setopt_array($curl, [
            CURLOPT_CUSTOMREQUEST => $method,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_HTTPHEADER => $send,
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
        ] + match (true) {
            $fields === null => [],
            is_string($fields) => [CURLOPT_POSTFIELDS => $fields],
            array_filter($fields, fn($v) => $v instanceof \CURLFile) !== [] => [CURLOPT_POSTFIELDS => $fields],
            default => [CURLOPT_POSTFIELDS => http_build_query($fields)],
        });
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
