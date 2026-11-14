<?php

declare(strict_types=1);

namespace Custos\Http\Web;

use Custos\App;
use Custos\Http\Request;

/**
 * PHP session with the rules of section 4: httponly, SameSite=Lax, secure over HTTPS, a new id at login,
 * logout after a period without activity, and a CSRF token.
 *
 * Session files live in var/sessions, so the system session cleaner (which uses the php.ini lifetime)
 * does not log users out early.
 */
final class Session
{
    private ?array $user = null;
    private bool $userLoaded = false;

    private function __construct(private readonly App $app)
    {
    }

    /** Sessions without a timeout still end when the browser closes; their files are removed after this long unused. */
    private const KEEP_IDLE_S = 365 * 86400;

    /**
     * Minutes without activity after which the login ends, 0 = never: Settings -> General, or until it is saved there,
     * session_timeout_hours of config.php (the setting of versions before 0.4.10).
     */
    public static function timeoutMinutes(App $app): int
    {
        $stored = $app->settings()->get('logout_after_min');
        return $stored !== '' ? max(0, (int) $stored) : max(1, (int) $app->config->get('session_timeout_hours')) * 60;
    }

    /** @param bool $activity false for requests the page makes by itself (live updates): they do not keep the session alive */
    public static function start(App $app, Request $request, bool $activity = true): self
    {
        $timeout = self::timeoutMinutes($app) * 60;
        if (session_status() !== PHP_SESSION_ACTIVE) {
            $dir = $app->config->varDir() . '/sessions';
            if (!is_dir($dir)) {
                @mkdir($dir, 0700, true);
            }
            ini_set('session.use_strict_mode', '1');
            ini_set('session.use_only_cookies', '1');
            ini_set('session.gc_maxlifetime', (string) ($timeout > 0 ? $timeout : self::KEEP_IDLE_S));
            ini_set('session.gc_probability', '1');
            ini_set('session.gc_divisor', '100');
            session_save_path($dir);
            session_name('CUSTOSSID');
            session_set_cookie_params([
                'lifetime' => 0,
                'path' => $request->basePath === '' ? '/' : $request->basePath . '/',
                'secure' => self::secureCookie($request, $app->config->url()),
                'httponly' => true,
                'samesite' => 'Lax',
            ]);
            session_start();
        }
        $session = new self($app);
        $now = time();
        if ($timeout > 0 && isset($_SESSION['user_id']) && $now - (int) ($_SESSION['last_activity'] ?? 0) > $timeout) {
            unset($_SESSION['user_id']);
            $session->flash('info', $app->lang()->get('login.expired'));
        }
        if ($activity || !isset($_SESSION['last_activity'])) {
            $_SESSION['last_activity'] = $now;
        }
        $_SESSION['csrf'] ??= bin2hex(random_bytes(32));
        return $session;
    }

    /**
     * The cookie is secure only over HTTPS and only when the site address in config.php is https://.
     * The second condition keeps login working on a plain HTTP site whose web server claims HTTPS.
     */
    public static function secureCookie(Request $request, string $siteUrl): bool
    {
        return $request->https && str_starts_with(strtolower($siteUrl), 'https://');
    }

    /** The signed-in user, or null. A user disabled meanwhile is logged out. */
    public function user(): ?array
    {
        if (!$this->userLoaded) {
            $this->userLoaded = true;
            $id = $_SESSION['user_id'] ?? null;
            $user = $id === null ? null : $this->app->users()->find((int) $id);
            if ($user !== null && $user['disabled']) {
                $user = null;
            }
            if ($user === null) {
                unset($_SESSION['user_id']);
            }
            $this->user = $user;
        }
        return $this->user;
    }

    public function login(array $user): void
    {
        session_regenerate_id(true);
        $_SESSION['user_id'] = (int) $user['id'];
        $_SESSION['csrf'] = bin2hex(random_bytes(32));
        $this->user = $user;
        $this->userLoaded = true;
    }

    public function logout(): void
    {
        $_SESSION = [];
        session_regenerate_id(true);
        $_SESSION['csrf'] = bin2hex(random_bytes(32));
        $this->user = null;
    }

    public function csrf(): string
    {
        return $_SESSION['csrf'];
    }

    public function checkCsrf(string $token): bool
    {
        return isset($_SESSION['csrf']) && hash_equals($_SESSION['csrf'], $token);
    }

    /** @param 'info'|'error' $type */
    public function flash(string $type, string $message): void
    {
        $_SESSION['flash'][] = [$type, $message];
    }

    /** @return list<array{0: string, 1: string}> */
    public function takeFlash(): array
    {
        $flash = $_SESSION['flash'] ?? [];
        unset($_SESSION['flash']);
        return $flash;
    }
}
