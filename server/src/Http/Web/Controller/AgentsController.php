<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Agents;
use Custos\Domain\Packages;
use Custos\Domain\TaskEdits;
use Custos\Http\Response;
use Custos\Http\Web\Controller;
use Custos\Infra\Crypto;
use Custos\Infra\Time;

/**
 * Machines: machines × tasks by group with approval of new agents (7.2, 8), the machine page (7.3) with its commands,
 * and the agent settings sent with settings.update.
 */
final class AgentsController extends Controller
{
    /** The machine page and the agent settings refresh their live parts this often (seconds). */
    public const REFRESH_S = 10;
    /** The machine list refreshes its live parts this often (seconds). */
    public const REFRESH_LIST_S = 15;
    private const TRAY_MODES = ['full', 'status', 'hidden'];

    public function index(): Response
    {
        $board = $this->app->board();
        $groups = $this->app->groups()->all();
        $filter = $this->query('group');
        $rows = [];
        foreach ($board->agents as $agent) {
            if ($agent['status'] === 'pending') {
                continue;
            }
            $key = $agent['group_id'] === null ? '' : (string) $agent['group_id'];
            if ($filter !== '' && $filter !== ($key === '' ? 'none' : $key)) {
                continue;
            }
            $rows[$key][] = $agent;
        }
        // Groups in name order, machines without a group last; machines by name inside a group.
        $sections = [];
        foreach ($groups as $group) {
            if (isset($rows[(string) $group['id']])) {
                $sections[] = ['name' => $group['name'], 'agents' => $rows[(string) $group['id']]];
            }
        }
        if (isset($rows[''])) {
            $sections[] = ['name' => $groups === [] ? null : $this->t('groups.none'), 'agents' => $rows['']];
        }
        foreach ($sections as &$section) {
            usort($section['agents'], fn($a, $b) => strcasecmp($a['name'], $b['name']));
        }
        unset($section);
        $all = $this->app->agents()->all();
        return $this->render('agents/index', [
            'section' => 'agents',
            'board' => $board,
            'sections' => $sections,
            'groups' => $groups,
            'filter' => $filter,
            'pending' => array_values(array_filter($all, fn($a) => $a['status'] === 'pending')),
            'byId' => array_column($all, null, 'id'),
            'hasAgents' => count($all) > 0,
            'live' => self::REFRESH_LIST_S,
        ], $this->t('agents.title'));
    }

    public function show(string $id): Response
    {
        $agent = $this->app->agents()->find($id);
        if ($agent === null) {
            return $this->notFound();
        }
        $db = $this->app->db();
        $replaces = $agent['reregistration_of'] === null ? null : $this->app->agents()->find($agent['reregistration_of']);
        return $this->render('agents/show', [
            'section' => 'agents',
            'agent' => $agent,
            'replaces' => $replaces,
            'groups' => $this->app->groups()->all(),
            'state' => $this->liveVars($agent['id']),
            'credentials' => json_decode((string) $agent['credentials'], true) ?: [],
            'runs' => $db->all(
                'SELECT r.*, t.name AS task_name FROM runs r
                 LEFT JOIN tasks t ON t.agent_id = r.agent_id AND t.id = r.task_id
                 WHERE r.agent_id = ? ORDER BY r.started_at DESC LIMIT 10',
                [$agent['id']]
            ),
            'live' => self::REFRESH_S,
            'commands' => $this->app->commands()->forAgent($agent['id']),
            'currentVersion' => $this->app->packages()->current(Packages::agentKind($agent)),
            // The edition for Windows 7 and 8.1 takes no 7-Zip from the site: nothing to offer.
            'latestSevenZip' => Agents::isLegacy($agent) ? null : $this->app->packages()->latestSevenZip(),
            'settings' => json_decode((string) $agent['settings'], true) ?: [],
            'storages' => $this->app->storage()->forAgent($agent['id']),
        ], Agents::name($agent));
    }

    /** What the "now", problems and tasks blocks of the machine page show: the board's copy of the machine. */
    private function liveVars(string $agentId): array
    {
        $board = $this->app->board($agentId);
        $agent = $board->agents[$agentId];
        return [
            'board' => $board,
            'agent' => $agent,
            'online' => $agent['online'],
            'tasks' => $board->tasksOf($agentId),
            'running' => $board->running($this->app->db()),
            'queue' => $agent['queue_ids'],
            'problems' => $board->problems,
            'edits' => $this->pendingEdits($agentId),
        ];
    }

    /**
     * Task edits from the site the agent has not confirmed yet, by task id: {status, name}. Tasks created on the site
     * appear here before the agent reports them.
     */
    private function pendingEdits(string $agentId): array
    {
        $result = [];
        foreach ($this->app->db()->all(
            "SELECT task_id, status, payload FROM commands WHERE agent_id = ? AND kind = 'task.upsert' AND status IN ('queued', 'delivered')
             AND expires_at > ? ORDER BY created_at",
            [$agentId, Time::now()]
        ) as $row) {
            $payload = json_decode($row['payload'], true);
            $result[(string) $row['task_id']] = ['status' => $row['status'], 'name' => (string) ($payload['task']['name'] ?? '')];
        }
        return $result;
    }

    public function action(string $id, string $action): Response
    {
        $agents = $this->app->agents();
        $agent = $agents->find($id);
        if ($agent === null) {
            return $this->notFound();
        }
        $id = $agent['id'];
        $name = Agents::name($agent);
        $details = ['name' => $name, 'fingerprint' => $agent['key_fingerprint']];
        switch ($action) {
            case 'approve':
                $agents->approve($id);
                $this->audit('agent.approve', 'agent', $id, $details);
                $this->session->flash('info', $this->t('agents.flash.approved', ['name' => $name]));
                break;
            case 'replace':
                try {
                    $oldId = $agents->replace($id);
                } catch (\InvalidArgumentException $e) {
                    $this->session->flash('error', $this->t($e->getMessage()));
                    return $this->redirect('/agents');
                }
                $this->audit('agent.replace', 'agent', $id, $details + ['replaced' => $oldId]);
                $this->session->flash('info', $this->t('agents.flash.replaced', ['name' => $name]));
                break;
            case 'disable':
                $agents->setDisabled($id, true);
                $this->audit('agent.disable', 'agent', $id, $details);
                $this->session->flash('info', $this->t('agents.flash.disabled', ['name' => $name]));
                break;
            case 'enable':
                $agents->setDisabled($id, false);
                $this->audit('agent.enable', 'agent', $id, $details);
                $this->session->flash('info', $this->t('agents.flash.enabled', ['name' => $name]));
                break;
            case 'delete':
                $agents->delete($id);
                $this->audit('agent.delete', 'agent', $id, $details);
                $this->session->flash('info', $this->t('agents.flash.deleted', ['name' => $name]));
                return $this->redirect('/agents');
            case 'rename':
                $newName = trim($this->post('display_name'));
                $newName = $newName === '' ? null : mb_substr($newName, 0, 255);
                $agents->rename($id, $newName);
                $this->audit('agent.rename', 'agent', $id, $details + ['new_name' => $newName ?? $agent['hostname']]);
                $this->session->flash('info', $this->t('agents.flash.renamed', ['name' => $newName ?? $agent['hostname']]));
                break;
            case 'group':
                $groupId = ctype_digit($this->post('group_id')) ? (int) $this->post('group_id') : null;
                $group = $groupId === null ? null : $this->app->groups()->find($groupId);
                $agents->setGroup($id, $group === null ? null : (int) $group['id']);
                $this->audit('agent.group', 'agent', $id, $details + ['group' => $group['name'] ?? null]);
                $this->session->flash('info', $this->t('agents.flash.group', ['name' => $name, 'group' => $group['name'] ?? $this->t('groups.none')]));
                break;
            case 'inventory':
                if ($this->approved($agent)) {
                    $this->app->commands()->queue($id, 'inventory.refresh', [], $this->user());
                    $this->audit('command', 'agent', $id, ['name' => $name, 'command' => 'inventory.refresh']);
                    $this->session->flash('info', $this->t('commands.flash.sent', ['command' => $this->t('command.inventory.refresh')]));
                }
                break;
            case 'update':
            case 'sevenzip':
                $this->install($agent, $action === 'update' ? Packages::agentKind($agent) : '7zip');
                break;
        }
        return $this->redirect($this->post('back') === 'list' ? '/agents' : '/agents/' . $id);
    }

    /** Withdraws a command that has not been delivered yet. */
    public function cancel(string $id, string $commandId): Response
    {
        $agent = $this->app->agents()->find($id);
        if ($agent === null) {
            return $this->notFound();
        }
        $command = $this->app->commands()->find($commandId);
        if ($command !== null && $this->app->commands()->cancel($agent['id'], $commandId)) {
            $this->audit('command.cancel', 'agent', $agent['id'], ['name' => Agents::name($agent), 'command' => $command['kind']]);
            $this->session->flash('info', $this->t('commands.flash.cancelled'));
        } else {
            $this->session->flash('error', $this->t('commands.error.too_late'));
        }
        return $this->redirect($this->post('back') !== '' && str_starts_with($this->post('back'), '/agents/' . $agent['id'])
            ? $this->post('back') : '/agents/' . $agent['id']);
    }

    /** Agent settings (7.3) sent with settings.update: only what changed. */
    public function settings(string $id): Response
    {
        $agent = $this->app->agents()->find($id);
        if ($agent === null) {
            return $this->notFound();
        }
        return $this->settingsForm($agent, $this->storedSettings($agent), []);
    }

    public function saveSettings(string $id): Response
    {
        $agent = $this->app->agents()->find($id);
        if ($agent === null) {
            return $this->notFound();
        }
        $stored = $this->storedSettings($agent);
        $values = [
            'tray_mode' => in_array($this->post('tray_mode'), self::TRAY_MODES, true) ? $this->post('tray_mode') : 'full',
            'allow_all_users_edit' => $this->post('allow_all_users_edit') === '1',
            'log_retention_days' => trim($this->post('log_retention_days')),
            'history_retention_days' => trim($this->post('history_retention_days')),
            'default_log_level' => in_array($this->post('default_log_level'), ['summary', 'files'], true) ? $this->post('default_log_level') : 'summary',
            'process_priority' => in_array($this->post('process_priority'), ['low', 'normal'], true) ? $this->post('process_priority') : 'low',
        ];
        $errors = [];
        foreach (['log_retention_days', 'history_retention_days'] as $days) {
            if (!ctype_digit($values[$days]) || (int) $values[$days] < 1 || (int) $values[$days] > 3650) {
                $errors[$days] = $this->t('settings.error.range', ['min' => 1, 'max' => 3650]);
            } else {
                $values[$days] = (int) $values[$days];
            }
        }
        $password = $this->post('password_action');
        $new = $this->post('program_password');
        if ($password === 'set' && mb_strlen($new) < 4) {
            $errors['program_password'] = $this->t('agent_settings.error.password');
        } elseif ($password === 'set' && $new !== $this->post('program_password_again')) {
            $errors['program_password'] = $this->t('agent_settings.error.password_again');
        }
        if ($agent['status'] !== 'approved') {
            $errors['agent'] = $this->t('commands.error.not_approved');
        } elseif (TaskEdits::remoteEditOff($agent)) {
            $errors['agent'] = $this->t('commands.error.remote_edit_off');
        }
        if ($errors !== []) {
            return $this->settingsForm($agent, $values + ['password_action' => $password] + $stored, $errors, 422);
        }

        $changes = [];
        foreach ($values as $key => $value) {
            if (($stored[$key] ?? null) !== $value) {
                $changes[$key] = $value;
            }
        }
        $audit = array_keys($changes);
        if ($password === 'set') {
            $changes['program_password_hash'] = Crypto::programPasswordHash($new);
            $audit[] = 'program_password';
        } elseif ($password === 'reset') {
            $changes['program_password_hash'] = null;
            $audit[] = 'program_password_reset';
        }
        if ($changes === []) {
            $this->session->flash('info', $this->t('agent_settings.flash.nothing'));
            return $this->redirect('/agents/' . $agent['id'] . '/settings');
        }
        $this->app->taskEdits()->updateSettings($agent, $changes, $this->user());
        $this->audit('agent.settings', 'agent', $agent['id'], ['name' => Agents::name($agent), 'changed' => implode(', ', $audit)]);
        $this->session->flash('info', $this->t('agent_settings.flash.saved'));
        return $this->redirect('/agents/' . $agent['id']);
    }

    /** The agent settings as the last configuration snapshot reported them, with the defaults of A.9. */
    private function storedSettings(array $agent): array
    {
        $settings = json_decode((string) $agent['settings'], true) ?: [];
        return [
            'tray_mode' => (string) ($settings['tray_mode'] ?? 'full'),
            'allow_all_users_edit' => (bool) ($settings['allow_all_users_edit'] ?? false),
            'log_retention_days' => (int) ($settings['log_retention_days'] ?? 180),
            'history_retention_days' => (int) ($settings['history_retention_days'] ?? 365),
            'default_log_level' => (string) ($settings['default_log_level'] ?? 'summary'),
            'process_priority' => (string) ($settings['process_priority'] ?? 'low'),
        ];
    }

    private function settingsForm(array $agent, array $values, array $errors, int $status = 200): Response
    {
        return $this->render('agents/settings', [
            'section' => 'agents',
            'agent' => $agent,
            'values' => $values,
            'errors' => $errors,
            'known' => $agent['settings'] !== null,
            'pending' => $this->app->db()->one(
                "SELECT * FROM commands WHERE agent_id = ? AND kind = 'settings.update' AND status IN ('queued', 'delivered')
                 ORDER BY created_at DESC LIMIT 1",
                [$agent['id']]
            ),
            'refusal' => $agent['status'] !== 'approved' ? $this->t('commands.error.not_approved')
                : (TaskEdits::remoteEditOff($agent) ? $this->t('commands.error.remote_edit_off') : null),
            // The form fills itself in when the agent sends its settings, unless it was changed here already (app.js);
            // a form sent back with errors keeps what was typed.
            'live' => $errors === [] && $this->request->method === 'GET' ? self::REFRESH_S : 0,
        ], $this->t('agent_settings.title', ['name' => Agents::name($agent)]), $status);
    }

    private function approved(array $agent): bool
    {
        if ($agent['status'] === 'approved') {
            return true;
        }
        $this->session->flash('error', $this->t('commands.error.not_approved'));
        return false;
    }

    /** "Update the agent" (kind agent or legacy, the machine's edition) / "Update 7-Zip" on one machine. */
    private function install(array $agent, string $kind): void
    {
        if (!$this->approved($agent)) {
            return;
        }
        if (!Packages::fits($kind, $agent)) {
            $this->session->flash('error', $this->t('updates.error.no_machines_7zip'));
            return;
        }
        $packages = $this->app->packages();
        $package = $kind === '7zip' ? $packages->latestSevenZip() : $packages->current($kind);
        if ($package === null) {
            $this->session->flash('error', $this->t($kind === '7zip' ? 'sevenzip.error.none' : 'updates.error.no_current'));
            return;
        }
        $result = $packages->install($kind, $package['version'], [$agent], $this->user());
        if ($result['queued'] === []) {
            // Not an error: the machine already runs this version or a newer one.
            $this->session->flash('info', $this->t('updates.flash.not_needed', ['version' => $package['version']]));
            return;
        }
        $this->audit($kind === '7zip' ? 'sevenzip.install' : 'update.install', 'agent', $agent['id'],
            ['name' => Agents::name($agent), 'version' => $package['version']]);
        $this->session->flash('info', $this->t($kind === '7zip' ? 'sevenzip.flash.queued_one' : 'updates.flash.queued_one', ['version' => $package['version']]));
    }
}
