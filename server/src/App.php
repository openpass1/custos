<?php

declare(strict_types=1);

namespace Custos;

use Custos\Domain\Agents;
use Custos\Domain\Analytics;
use Custos\Domain\Audit;
use Custos\Domain\Board;
use Custos\Domain\Commands;
use Custos\Domain\ConfigSnapshots;
use Custos\Domain\Groups;
use Custos\Domain\Install;
use Custos\Domain\Integrations;
use Custos\Domain\Maintenance;
use Custos\Domain\Monitor;
use Custos\Domain\Notices;
use Custos\Domain\Notifier;
use Custos\Domain\Packages;
use Custos\Domain\Releases;
use Custos\Domain\Reports;
use Custos\Domain\Settings;
use Custos\Domain\SiteUpdate;
use Custos\Domain\Storage;
use Custos\Domain\TaskEdits;
use Custos\Domain\Tasks;
use Custos\Domain\Templates;
use Custos\Domain\UpdateWatch;
use Custos\Domain\Users;
use Custos\Http\Api\AgentApi;
use Custos\Http\Api\InstallApi;
use Custos\Http\Api\IntegrationApi;
use Custos\Http\Request;
use Custos\Http\Response;
use Custos\Http\Web\Web;
use Custos\Infra\Config;
use Custos\Infra\Db;
use Custos\Infra\HttpGet;
use Custos\Infra\Lang;
use Custos\Infra\Log;
use Custos\Infra\ServerKeys;
use Custos\Infra\Updater;

/**
 * The application: configuration, lazily created services, and the entry point for HTTP requests.
 */
final class App
{
    public const VERSION = '1.4.5';
    /** Interface languages: lang/<code>.php. */
    public const LANGUAGES = ['ru', 'en'];
    public const DEFAULT_LANGUAGE = 'ru';

    private ?Db $db = null;
    private array $services = [];
    /** Language of the interface for this request (the user's choice), null = the site default. */
    private ?string $language = null;

    public function __construct(public readonly Config $config)
    {
        Log::setDir($config->varDir() . '/log');
        date_default_timezone_set('UTC');
    }

    /** Loads config/config.php (or the file named by CUSTOS_CONFIG). */
    public static function boot(string $root): self
    {
        return new self(Config::load($root));
    }

    public function root(): string
    {
        return $this->config->root;
    }

    public function handle(Request $request): Response
    {
        if (str_starts_with($request->path(), '/api/v1/')) {
            return (new AgentApi($this))->handle($request);
        }
        if (IntegrationApi::handles($request->path())) {
            return (new IntegrationApi($this))->handle($request);
        }
        if (InstallApi::handles($request->path())) {
            return (new InstallApi($this))->handle($request);
        }
        return (new Web($this))->handle($request);
    }

    public function db(): Db
    {
        return $this->db ??= Db::connect($this->config->db());
    }

    public function keys(): ServerKeys
    {
        return $this->services[ServerKeys::class] ??= new ServerKeys($this->config->keysDir());
    }

    /** Strings in the language of this request: the signed-in user's choice, otherwise the site default. */
    public function lang(): Lang
    {
        $code = $this->language ?? $this->defaultLanguage();
        return $this->services[Lang::class . $code] ??= new Lang($this->root() . '/lang', $code);
    }

    /** Strings in the site default language: messages to Telegram and e-mail, which go to everybody. */
    public function systemLang(): Lang
    {
        $code = $this->defaultLanguage();
        return $this->services[Lang::class . $code] ??= new Lang($this->root() . '/lang', $code);
    }

    /** Switches the interface language for this request; an unknown or empty code means the site default. */
    public function useLanguage(?string $code): void
    {
        $this->language = in_array($code, self::LANGUAGES, true) ? $code : null;
    }

    /** The site default language from the settings; Russian while the database cannot be read. */
    public function defaultLanguage(): string
    {
        try {
            $code = $this->settings()->get('default_language');
        } catch (\Throwable) {
            return self::DEFAULT_LANGUAGE;
        }
        return in_array($code, self::LANGUAGES, true) ? $code : self::DEFAULT_LANGUAGE;
    }

    /** @return array<string, string> language code => its own name ("Русский", "English") */
    public function languages(): array
    {
        $names = [];
        foreach (self::LANGUAGES as $code) {
            $names[$code] = (new Lang($this->root() . '/lang', $code))->get('lang.name');
        }
        return $names;
    }

    public function settings(): Settings
    {
        return $this->services[Settings::class] ??= new Settings($this->db());
    }

    public function users(): Users
    {
        return $this->services[Users::class] ??= new Users($this->db());
    }

    public function audit(): Audit
    {
        return $this->services[Audit::class] ??= new Audit($this->db());
    }

    public function agents(): Agents
    {
        return $this->services[Agents::class] ??= new Agents($this->db(), $this->monitor(), $this->notices());
    }

    public function tasks(): Tasks
    {
        return $this->services[Tasks::class] ??= new Tasks($this->db());
    }

    public function reports(): Reports
    {
        return $this->services[Reports::class] ??= new Reports($this->db(), $this->tasks(), $this->monitor());
    }

    public function configSnapshots(): ConfigSnapshots
    {
        return $this->services[ConfigSnapshots::class] ??= new ConfigSnapshots($this->db(), $this->tasks(), $this->monitor(), $this->notices());
    }

    public function commands(): Commands
    {
        return $this->services[Commands::class] ??= new Commands($this->db(), $this->notices());
    }

    public function monitor(): Monitor
    {
        return $this->services[Monitor::class] ??= new Monitor($this->db(), $this->settings());
    }

    public function notices(): Notices
    {
        return $this->services[Notices::class] ??= new Notices($this->db());
    }

    public function groups(): Groups
    {
        return $this->services[Groups::class] ??= new Groups($this->db());
    }

    public function notifier(): Notifier
    {
        return $this->services[Notifier::class] ??= new Notifier($this->db(), $this->settings(), $this->systemLang(), $this->config);
    }

    public function maintenance(): Maintenance
    {
        return $this->services[Maintenance::class] ??= new Maintenance($this->db(), $this->settings());
    }

    public function taskEdits(): TaskEdits
    {
        return $this->services[TaskEdits::class] ??= new TaskEdits($this->db(), $this->tasks(), $this->commands());
    }

    public function templates(): Templates
    {
        return $this->services[Templates::class] ??= new Templates($this->db(), $this->taskEdits());
    }

    /** Agent update packages and 7-Zip installers, kept in var/packages. */
    public function packages(): Packages
    {
        return $this->services[Packages::class] ??= new Packages($this->db(), $this->keys(), $this->commands(), $this->config->varDir() . '/packages');
    }

    /** Installing CUSTOS for Linux with one command from the site (page "Install"). */
    public function install(): Install
    {
        return $this->services[Install::class] ??= new Install($this->settings(), $this->packages(), $this->keys(), $this->config->url());
    }

    public function updateWatch(): UpdateWatch
    {
        return $this->services[UpdateWatch::class] ??= new UpdateWatch(
            $this->db(), $this->settings(), $this->packages(), $this->notices(), $this->githubApi(), $this->userAgent(), $this->http()
        );
    }

    /** CUSTOS releases on GitHub: the site and the program (config "update_repo" names another repository, a fork). */
    public function releases(): Releases
    {
        return $this->services[Releases::class] ??= new Releases(
            $this->settings(), $this->notices(), $this->packages(), $this->githubApi(),
            (string) $this->config->get('update_repo', Releases::DEFAULT_REPO), $this->http()
        );
    }

    /** Updates of the site itself, as the site queues them. */
    public function siteUpdate(): SiteUpdate
    {
        return $this->services[SiteUpdate::class] ??= new SiteUpdate($this->releases(), $this->config->varDir());
    }

    /**
     * The engine that replaces the site's files (command line only: cron and bin/update.php).
     *
     * @param \Closure(string, array): void|null $say progress, a lang key with its parameters
     */
    public function updater(?\Closure $say = null): Updater
    {
        return new Updater($this->root(), $this->config->varDir(), fn() => $this->db()->pdo, $this->http(), $say ?? static function (): void {
        });
    }

    /** GET for GitHub; tests replace it (setHttp) with a fake GitHub. */
    public function http(): \Closure
    {
        return $this->services['http'] ??= (new HttpGet($this->userAgent()))(...);
    }

    public function setHttp(\Closure $http): void
    {
        $this->services['http'] = $http;
        unset($this->services[UpdateWatch::class], $this->services[Releases::class], $this->services[SiteUpdate::class]);
    }

    private function githubApi(): string
    {
        return rtrim((string) $this->config->get('github_api_url', 'https://api.github.com'), '/');
    }

    private function userAgent(): string
    {
        return 'CUSTOS-Monitor/' . self::VERSION;
    }

    public function storage(): Storage
    {
        return $this->services[Storage::class] ??= new Storage($this->db(), $this->settings());
    }

    public function analytics(): Analytics
    {
        return $this->services[Analytics::class] ??= new Analytics($this->db());
    }

    /** Zabbix and the wall screen: tokens and status data. */
    public function integrations(): Integrations
    {
        return $this->services[Integrations::class] ??= new Integrations($this->db(), $this->settings());
    }

    /** Statuses of all machines and tasks right now (or of one machine). */
    /** @return array{agents: int, problems: int} counters of the menu: machines waiting for approval, open problems */
    public function navCounts(): array
    {
        return [
            'agents' => (int) $this->db()->value("SELECT COUNT(*) FROM agents WHERE status = 'pending'"),
            'problems' => (int) $this->db()->value('SELECT COUNT(*) FROM problems WHERE closed_at IS NULL'),
        ];
    }

    public function board(?string $agentId = null, ?int $now = null): Board
    {
        return Board::load($this->db(), $this->settings(), $this->monitor(), $now ?? time(), $agentId);
    }
}
