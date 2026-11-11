<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Http\Response;
use Custos\Http\Web\Controller;

/** Site users: create, disable, reset the password (section 4). */
final class UsersController extends Controller
{
    public function index(): Response
    {
        return $this->render('users/index', [
            'section' => 'users',
            'users' => $this->app->users()->all(),
            'me' => $this->user(),
        ], $this->t('users.title'));
    }

    public function create(): Response
    {
        $login = trim($this->post('login'));
        try {
            $id = $this->app->users()->create($login, $this->post('password'));
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage(), ['min' => \Custos\Domain\Users::MIN_PASSWORD]));
            return $this->redirect('/users');
        }
        $this->audit('user.create', 'user', (string) $id, ['login' => $login]);
        $this->session->flash('info', $this->t('users.flash.created', ['login' => $login]));
        return $this->redirect('/users');
    }

    public function action(string $id, string $action): Response
    {
        $users = $this->app->users();
        $user = $users->find((int) $id);
        if ($user === null) {
            return $this->notFound();
        }
        $details = ['login' => $user['login']];
        $self = (int) $user['id'] === (int) $this->user()['id'];
        switch ($action) {
            case 'password':
                try {
                    $users->setPassword((int) $user['id'], $this->post('password'));
                } catch (\InvalidArgumentException $e) {
                    $this->session->flash('error', $this->t($e->getMessage(), ['min' => \Custos\Domain\Users::MIN_PASSWORD]));
                    return $this->redirect('/users');
                }
                $this->audit('user.password', 'user', (string) $user['id'], $details);
                $this->session->flash('info', $this->t('users.flash.password', ['login' => $user['login']]));
                break;
            case 'disable':
                if ($self) {
                    $this->session->flash('error', $this->t('users.error.self'));
                    break;
                }
                $users->setDisabled((int) $user['id'], true);
                $this->audit('user.disable', 'user', (string) $user['id'], $details);
                break;
            case 'enable':
                $users->setDisabled((int) $user['id'], false);
                $this->audit('user.enable', 'user', (string) $user['id'], $details);
                break;
        }
        return $this->redirect('/users');
    }
}
