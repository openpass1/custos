<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Settings;
use Custos\Http\Response;
use Custos\Http\Web\Controller;
use Custos\Infra\Mailer;

/**
 * Telegram and e-mail settings (section 9) with "send a test message" buttons, and the latest messages sent.
 * The bot token and the SMTP password are never shown again: an empty field keeps the stored value.
 */
final class NotificationsController extends Controller
{
    private const SECRETS = ['telegram_token', 'smtp_password'];
    private const FIELDS = ['telegram_token', 'telegram_chat_ids', 'telegram_proxy', 'smtp_host', 'smtp_port', 'smtp_encryption',
                            'smtp_user', 'smtp_password', 'mail_from', 'mail_to'];

    public function index(): Response
    {
        return $this->form($this->stored(), []);
    }

    public function save(): Response
    {
        $settings = $this->app->settings();
        $values = [];
        foreach (self::FIELDS as $name) {
            $values[$name] = trim($this->post($name));
        }
        foreach (self::SECRETS as $name) {
            if ($this->post('clear_' . $name) === '1') {
                $values[$name] = '';
            } elseif ($values[$name] === '') {
                $values[$name] = $settings->get($name);
            }
        }
        $values['telegram_chat_ids'] = implode("\n", Settings::splitList($values['telegram_chat_ids']));
        $values['mail_to'] = implode("\n", Settings::splitList($values['mail_to']));

        $errors = [];
        if ($values['telegram_token'] !== '' && !preg_match('/^\d+:[A-Za-z0-9_-]{20,}$/', $values['telegram_token'])) {
            $errors['telegram_token'] = $this->t('notifications.error.token');
        }
        foreach (Settings::splitList($values['telegram_chat_ids']) as $chat) {
            if (!preg_match('/^(-?\d{1,20}|@[A-Za-z0-9_]{5,32})$/', $chat)) {
                $errors['telegram_chat_ids'] = $this->t('notifications.error.chat', ['chat' => $chat]);
            }
        }
        if ($values['telegram_proxy'] !== '' && !preg_match('#^(https?|socks5h?)://[^\s/]+$#i', $values['telegram_proxy'])) {
            $errors['telegram_proxy'] = $this->t('notifications.error.proxy');
        }
        if ($values['smtp_host'] !== '' && !preg_match('/^[A-Za-z0-9.-]+$/', $values['smtp_host'])) {
            $errors['smtp_host'] = $this->t('notifications.error.host');
        }
        if (!ctype_digit($values['smtp_port']) || (int) $values['smtp_port'] < 1 || (int) $values['smtp_port'] > 65535) {
            $errors['smtp_port'] = $this->t('settings.error.range', ['min' => 1, 'max' => 65535]);
        }
        if (!in_array($values['smtp_encryption'], Mailer::ENCRYPTIONS, true)) {
            $values['smtp_encryption'] = 'starttls';
        }
        if ($values['mail_from'] !== '' && filter_var($values['mail_from'], FILTER_VALIDATE_EMAIL) === false) {
            $errors['mail_from'] = $this->t('notifications.error.email', ['email' => $values['mail_from']]);
        }
        foreach (Settings::splitList($values['mail_to']) as $address) {
            if (filter_var($address, FILTER_VALIDATE_EMAIL) === false) {
                $errors['mail_to'] = $this->t('notifications.error.email', ['email' => $address]);
            }
        }
        if ($errors !== []) {
            return $this->form($values, $errors, 422);
        }

        $changed = [];
        foreach ($values as $name => $value) {
            if ($settings->get($name) !== $value) {
                // Secrets never go into the audit log.
                $changed[$name] = in_array($name, self::SECRETS, true) ? ($value === '' ? '' : '***') : $value;
                $settings->set($name, $value);
            }
        }
        if ($changed !== []) {
            $this->audit('settings.notifications', 'settings', null, $changed);
        }

        $test = $this->post('test');
        if (in_array($test, ['telegram', 'email'], true)) {
            $results = $this->app->notifier()->sendTest($test, time());
            $this->audit('settings.notifications_test', 'settings', null, ['channel' => $test]);
            foreach ($results as $result) {
                if ($result['error'] === null) {
                    $this->session->flash('info', $this->t('notifications.flash.test_ok', ['target' => $result['target']]));
                } else {
                    $this->session->flash('error', $this->t('notifications.flash.test_failed', ['target' => $result['target'], 'error' => $result['error']]));
                }
            }
        } else {
            $this->session->flash('info', $this->t('settings.flash.saved'));
        }
        return $this->redirect('/settings/notifications');
    }

    private function stored(): array
    {
        $values = [];
        foreach (self::FIELDS as $name) {
            $values[$name] = $this->app->settings()->get($name);
        }
        return $values;
    }

    private function form(array $values, array $errors, int $status = 200): Response
    {
        $settings = $this->app->settings();
        return $this->render('settings/notifications', [
            'section' => 'settings',
            'values' => $values,
            'errors' => $errors,
            'secretSet' => [
                'telegram_token' => $settings->get('telegram_token') !== '',
                'smtp_password' => $settings->get('smtp_password') !== '',
            ],
            'channels' => $this->app->notifier()->channels(),
            'latest' => $this->app->notifier()->latest(20),
            'siteUrl' => $this->app->config->url(),
        ], $this->t('notifications.title'), $status);
    }
}
