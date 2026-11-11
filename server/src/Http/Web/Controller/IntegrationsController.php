<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Integrations;
use Custos\Http\Response;
use Custos\Http\Web\Controller;
use Custos\Infra\Cidr;

/** Settings of Zabbix (section 10) and the wall screen (7.9): their tokens, addresses and allow-list. */
final class IntegrationsController extends Controller
{
    public const ZABBIX_TEMPLATE = 'zabbix/custos_template.yaml';

    public function index(): Response
    {
        return $this->form($this->app->settings()->get('ip_allow_integrations'), null);
    }

    public function save(): Response
    {
        try {
            $list = implode("\n", Cidr::parseList($this->post('ip_allow_integrations')));
        } catch (\InvalidArgumentException $e) {
            return $this->form($this->post('ip_allow_integrations'), $this->t('settings.error.cidr', ['entries' => $e->getMessage()]), 422);
        }
        $settings = $this->app->settings();
        if ($settings->get('ip_allow_integrations') !== $list) {
            $settings->set('ip_allow_integrations', $list);
            $this->audit('settings.update', 'settings', null, ['ip_allow_integrations' => $list]);
        }
        $this->session->flash('info', $this->t('settings.flash.saved'));
        return $this->redirect('/settings/integrations');
    }

    /** A new Zabbix or wall token: the old one stops working at once. */
    public function regenerate(string $which): Response
    {
        $name = $which . '_token';
        $this->app->settings()->set($name, Integrations::newToken());
        // Tokens never go into the audit log, only the fact.
        $this->audit('settings.token', 'settings', $name);
        $this->session->flash('info', $this->t('integrations.flash.token.' . $which));
        return $this->redirect('/settings/integrations');
    }

    /** The Zabbix template file, so that it can be imported without unpacking the server package. */
    public function template(): Response
    {
        $file = $this->app->root() . '/' . self::ZABBIX_TEMPLATE;
        if (!is_file($file)) {
            return $this->notFound();
        }
        return Response::file($file, 'application/yaml; charset=utf-8', 'custos_template.yaml');
    }

    private function form(string $list, ?string $error, int $status = 200): Response
    {
        $settings = $this->app->settings();
        $this->app->integrations()->ensureTokens();
        $base = $this->app->config->url();
        if ($base === '') {
            $base = ($this->request->https ? 'https://' : 'http://') . ($this->request->headers['host'] ?? 'localhost') . $this->request->basePath;
        }
        return $this->render('settings/integrations', [
            'section' => 'settings',
            'list' => $list,
            'error' => $error,
            'base' => rtrim($base, '/'),
            'zabbixToken' => $settings->get('zabbix_token'),
            'wallToken' => $settings->get('wall_token'),
            'myIp' => $this->request->ip,
            'cronLast' => $settings->get('cron_last_run'),
        ], $this->t('integrations.title'), $status);
    }
}
