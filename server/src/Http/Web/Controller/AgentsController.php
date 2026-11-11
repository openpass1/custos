<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Agents;
use Custos\Http\Response;
use Custos\Http\Web\Controller;

/** Machines: machines × tasks by group with approval of new agents (7.2, 8), and the machine page (7.3). */
final class AgentsController extends Controller
{
    /** The machine page refreshes its "now" block and task statuses this often. */
    public const REFRESH_S = 10;

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
            'live' => $this->liveVars($agent['id']),
            'credentials' => json_decode((string) $agent['credentials'], true) ?: [],
            'runs' => $db->all(
                'SELECT r.*, t.name AS task_name FROM runs r
                 LEFT JOIN tasks t ON t.agent_id = r.agent_id AND t.id = r.task_id
                 WHERE r.agent_id = ? ORDER BY r.started_at DESC LIMIT 10',
                [$agent['id']]
            ),
            'refresh' => self::REFRESH_S,
        ], Agents::name($agent));
    }

    /** Fresh "now", problems and tasks blocks of the machine page. */
    public function live(string $id): Response
    {
        $agent = $this->app->agents()->find($id);
        if ($agent === null) {
            return Response::json(404, ['error' => 'not_found']);
        }
        $vars = $this->liveVars($agent['id']);
        $parts = [];
        foreach (['now', 'problems', 'tasks'] as $name) {
            $parts[$name] = $this->view->partial('agents/_' . $name, $vars);
        }
        return Response::json(200, ['parts' => $parts, 'bell' => $this->app->notices()->unreadCount()]);
    }

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
        ];
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
                break;
            case 'enable':
                $agents->setDisabled($id, false);
                $this->audit('agent.enable', 'agent', $id, $details);
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
                break;
            case 'group':
                $groupId = ctype_digit($this->post('group_id')) ? (int) $this->post('group_id') : null;
                $group = $groupId === null ? null : $this->app->groups()->find($groupId);
                $agents->setGroup($id, $group === null ? null : (int) $group['id']);
                $this->audit('agent.group', 'agent', $id, $details + ['group' => $group['name'] ?? null]);
                break;
        }
        return $this->redirect($this->post('back') === 'list' ? '/agents' : '/agents/' . $id);
    }
}
