<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Http\Response;
use Custos\Http\Web\Controller;

final class AuthController extends Controller
{
    /** No more than 5 failed logins in 15 minutes for one IP and login (section 4). */
    public const MAX_FAILURES = 5;
    public const FAILURE_WINDOW_MIN = 15;

    public function form(): Response
    {
        if ($this->session->user() !== null) {
            return $this->redirect('/');
        }
        return $this->render('auth/login', ['login' => '', 'error' => null], $this->t('login.title'));
    }

    public function login(): Response
    {
        $login = trim($this->post('login'));
        $password = $this->post('password');
        $audit = $this->app->audit();
        $ip = $this->request->ip;

        if ($audit->recentFailures($ip, $login, self::FAILURE_WINDOW_MIN) >= self::MAX_FAILURES) {
            $audit->log(null, $ip, 'login.blocked', 'user', null, [], $login);
            return $this->fail($login, $this->t('login.too_many', ['minutes' => self::FAILURE_WINDOW_MIN]), 429);
        }
        $user = $login === '' ? null : $this->app->users()->verify($login, $password);
        if ($user === null || $user['disabled']) {
            $audit->log(null, $ip, 'login.failed', 'user', $user === null ? null : (string) $user['id'],
                $user === null ? [] : ['reason' => 'disabled'], $login);
            return $this->fail($login, $this->t('login.failed'), 200);
        }
        $this->session->login($user);
        $this->app->users()->touchLogin((int) $user['id'], $ip);
        $this->audit('login', 'user', (string) $user['id']);
        return $this->redirect('/');
    }

    public function logout(): Response
    {
        $this->audit('logout', 'user', (string) $this->user()['id']);
        $this->session->logout();
        return $this->redirect('/login');
    }

    private function fail(string $login, string $error, int $status): Response
    {
        return $this->render('auth/login', ['login' => $login, 'error' => $error], $this->t('login.title'), $status);
    }
}
