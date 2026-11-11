<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Agents;
use Custos\Http\Response;
use Custos\Http\Web\Controller;

/** Machines: the list with approval of new agents, and the machine page (sections 7.3, 8). */
final class AgentsController extends Controller
{
    public function home(): Response
    {
        return $this->redirect('/agents');
    }

    public function index(): Response
    {
        $agents = $this->app->agents()->all();
        $byId = array_column($agents, null, 'id');
        return $this->render('agents/index', [
            'section' => 'agents',
            'pending' => array_values(array_filter($agents, fn($a) => $a['status'] === 'pending')),
            'agents' => array_values(array_filter($agents, fn($a) => $a['status'] !== 'pending')),
            'byId' => $byId,
            'offlineAfter' => $this->app->settings()->int('offline_after_min'),
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
            'online' => Agents::isOnline($agent, $this->app->settings()->int('offline_after_min')),
            'tasks' => $this->app->tasks()->forAgent($agent['id']),
            'progress' => $db->one('SELECT * FROM run_progress WHERE agent_id = ?', [$agent['id']]),
            'running' => $agent['running'] === null ? null : json_decode($agent['running'], true),
            'queue' => json_decode((string) $agent['queue'], true) ?: [],
            'credentials' => json_decode((string) $agent['credentials'], true) ?: [],
            'runs' => $db->all(
                'SELECT r.*, t.name AS task_name FROM runs r
                 LEFT JOIN tasks t ON t.agent_id = r.agent_id AND t.id = r.task_id
                 WHERE r.agent_id = ? ORDER BY r.started_at DESC LIMIT 10',
                [$agent['id']]
            ),
        ], Agents::name($agent));
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
        }
        return $this->redirect($this->post('back') === 'list' ? '/agents' : '/agents/' . $id);
    }
}
