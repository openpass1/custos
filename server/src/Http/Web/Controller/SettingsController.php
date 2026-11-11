<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\RegistrationKey;
use Custos\Http\Response;
use Custos\Http\Web\Controller;
use Custos\Infra\Cidr;

/** Settings of section 7.8 needed so far: name, intervals, IP allow-lists, agent registration code. */
final class SettingsController extends Controller
{
    private const NUMBERS = [
        'heartbeat_interval_s' => [10, 3600],
        'offline_after_min' => [1, 10080],
    ];

    public function index(): Response
    {
        return $this->form($this->values(), []);
    }

    public function save(): Response
    {
        $settings = $this->app->settings();
        $values = [
            'server_name' => trim($this->post('server_name')),
            'heartbeat_interval_s' => trim($this->post('heartbeat_interval_s')),
            'offline_after_min' => trim($this->post('offline_after_min')),
            'ip_allow_site' => $this->post('ip_allow_site'),
            'ip_allow_agents' => $this->post('ip_allow_agents'),
        ];
        $errors = [];
        if ($values['server_name'] === '' || mb_strlen($values['server_name']) > 100) {
            $errors['server_name'] = $this->t('settings.error.server_name');
        }
        foreach (self::NUMBERS as $name => [$min, $max]) {
            if (!ctype_digit($values[$name]) || (int) $values[$name] < $min || (int) $values[$name] > $max) {
                $errors[$name] = $this->t('settings.error.range', ['min' => $min, 'max' => $max]);
            }
        }
        foreach (['ip_allow_site', 'ip_allow_agents'] as $name) {
            try {
                $values[$name] = implode("\n", Cidr::parseList($values[$name]));
            } catch (\InvalidArgumentException $e) {
                $errors[$name] = $this->t('settings.error.cidr', ['entries' => $e->getMessage()]);
            }
        }
        if (!isset($errors['ip_allow_site']) && !Cidr::allows($this->request->ip, Cidr::fromStored($values['ip_allow_site']))) {
            $errors['ip_allow_site'] = $this->t('settings.error.lockout', ['ip' => $this->request->ip]);
        }
        if ($errors !== []) {
            return $this->form($values, $errors, 422);
        }

        $changed = [];
        foreach ($values as $name => $value) {
            if ($settings->get($name) !== $value) {
                $changed[$name] = $value;
                $settings->set($name, $value);
            }
        }
        if ($changed !== []) {
            $this->audit('settings.update', 'settings', null, $changed);
        }
        $this->session->flash('info', $this->t('settings.flash.saved'));
        return $this->redirect('/settings');
    }

    public function regenerateKey(): Response
    {
        $this->app->settings()->set('registration_key', RegistrationKey::generate());
        // The key itself never goes into the audit log (section 4).
        $this->audit('settings.registration_key');
        $this->session->flash('info', $this->t('settings.flash.key'));
        return $this->redirect('/settings');
    }

    private function values(): array
    {
        $settings = $this->app->settings();
        $values = [];
        foreach (['server_name', 'heartbeat_interval_s', 'offline_after_min', 'ip_allow_site', 'ip_allow_agents'] as $name) {
            $values[$name] = $settings->get($name);
        }
        return $values;
    }

    private function form(array $values, array $errors, int $status = 200): Response
    {
        $keys = $this->app->keys();
        $key = $this->app->settings()->get('registration_key');
        return $this->render('settings/index', [
            'section' => 'settings',
            'values' => $values,
            'errors' => $errors,
            'registrationCode' => $key === '' ? null : RegistrationKey::code($key, $keys->fingerprint()),
            'serverFingerprint' => $keys->fingerprint(),
            'serverUrl' => $this->app->config->url(),
            'myIp' => $this->request->ip,
        ], $this->t('settings.title'), $status);
    }
}
