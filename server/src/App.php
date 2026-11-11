<?php

declare(strict_types=1);

namespace Custos;

use Custos\Domain\Agents;
use Custos\Domain\Audit;
use Custos\Domain\Board;
use Custos\Domain\Commands;
use Custos\Domain\ConfigSnapshots;
use Custos\Domain\Groups;
use Custos\Domain\Maintenance;
use Custos\Domain\Monitor;
use Custos\Domain\Notices;
use Custos\Domain\Notifier;
use Custos\Domain\Reports;
use Custos\Domain\Settings;
use Custos\Domain\Tasks;
use Custos\Domain\Users;
use Custos\Http\Api\AgentApi;
use Custos\Http\Request;
use Custos\Http\Response;
use Custos\Http\Web\Web;
use Custos\Infra\Config;
use Custos\Infra\Db;
use Custos\Infra\Lang;
use Custos\Infra\Log;
use Custos\Infra\ServerKeys;

/**
 * The application: configuration, lazily created services, and the entry point for HTTP requests.
 */
final class App
{
    public const VERSION = '0.2.0';

    private ?Db $db = null;
    private array $services = [];

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

    public function lang(): Lang
    {
        return $this->services[Lang::class] ??= new Lang($this->root() . '/lang', 'ru');
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
        return $this->services[Notifier::class] ??= new Notifier($this->db(), $this->settings(), $this->lang(), $this->config);
    }

    public function maintenance(): Maintenance
    {
        return $this->services[Maintenance::class] ??= new Maintenance($this->db(), $this->settings());
    }

    /** Statuses of all machines and tasks right now (or of one machine). */
    public function board(?string $agentId = null, ?int $now = null): Board
    {
        return Board::load($this->db(), $this->settings(), $this->monitor(), $now ?? time(), $agentId);
    }
}
