<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Agents;
use Custos\Domain\TaskForm;
use Custos\Http\Response;
use Custos\Http\Web\Controller;

/**
 * Task templates (section 8): made from scratch or from a task, edited with the task editor, applied to a group or to
 * chosen machines. Passwords are not kept in a template: they are typed in at each application.
 */
final class TemplatesController extends Controller
{
    public function index(): Response
    {
        return $this->render('templates/index', [
            'section' => 'templates',
            'templates' => $this->app->templates()->all(),
        ], $this->t('templates.title'));
    }

    /** A new template, empty or from a task of a machine (?agent=…&task=…). */
    public function create(): Response
    {
        $task = TaskForm::defaults();
        $credentials = [];
        $agentId = $this->query('agent');
        if ($agentId !== '') {
            $agent = $this->app->agents()->find($agentId);
            $current = $agent === null ? null : $this->app->taskEdits()->current($agent, strtolower($this->query('task')));
            if ($current === null) {
                return $this->notFound();
            }
            ['task' => $task, 'credentials' => $credentials] = $this->app->templates()->fromTask($agent, $current['task']);
        }
        return $this->editor(null, $task, $credentials, []);
    }

    public function store(): Response
    {
        $base = TasksController::decodeBase($this->post('_base')) ?? TaskForm::defaults();
        return $this->saveForm(null, $base, $this->postedCredentials());
    }

    public function show(string $id): Response
    {
        $template = $this->app->templates()->find((int) $id);
        if ($template === null) {
            return $this->notFound();
        }
        $links = array_column($this->app->templates()->links($template['id']), null, 'agent_id');
        $agents = array_values(array_filter($this->app->agents()->all(), fn($a) => $a['status'] !== 'pending'));
        $refs = TaskForm::credentialRefs($template['task']);
        return $this->render('templates/show', [
            'section' => 'templates',
            'tpl' => $template,
            'links' => $links,
            'agents' => $agents,
            'groups' => $this->app->groups()->all(),
            'accounts' => array_values(array_filter(array_map(fn($ref) => $template['credential_list'][$ref] ?? null, $refs))),
        ], $this->t('templates.show.title', ['name' => $template['name']]));
    }

    public function edit(string $id): Response
    {
        $template = $this->app->templates()->find((int) $id);
        if ($template === null) {
            return $this->notFound();
        }
        $task = $template['task'];
        $task['id'] = $template['uuid'];
        $task['name'] = $template['name'];
        return $this->editor($template, $task, $template['credential_list'], []);
    }

    public function update(string $id): Response
    {
        $template = $this->app->templates()->find((int) $id);
        if ($template === null) {
            return $this->notFound();
        }
        $base = $template['task'];
        $base['id'] = $template['uuid'];
        return $this->saveForm($template, $base, $template['credential_list']);
    }

    public function delete(string $id): Response
    {
        $template = $this->app->templates()->find((int) $id);
        if ($template === null) {
            return $this->notFound();
        }
        $this->app->templates()->delete($template['id']);
        $this->audit('template.delete', 'template', (string) $template['id'], ['name' => $template['name']]);
        $this->session->flash('info', $this->t('templates.flash.deleted', ['name' => $template['name']]));
        return $this->redirect('/templates');
    }

    /** Applies the template: target "group" (group_id) or "selected" (agent_ids[]); passwords[account id]. */
    public function apply(string $id): Response
    {
        $templates = $this->app->templates();
        $template = $templates->find((int) $id);
        if ($template === null) {
            return $this->notFound();
        }
        $back = '/templates/' . $template['id'];
        $all = array_values(array_filter($this->app->agents()->all(), fn($a) => $a['status'] !== 'pending'));
        $agents = match ($this->post('target')) {
            'group' => array_values(array_filter($all, fn($a) => $this->post('group_id') !== ''
                && ($this->post('group_id') === 'none' ? $a['group_id'] === null : (string) $a['group_id'] === $this->post('group_id')))),
            'linked' => array_values(array_filter($all, fn($a) => in_array($a['id'], array_column($templates->links($template['id']), 'agent_id'), true))),
            default => array_values(array_filter($all, fn($a) => in_array($a['id'], (array) ($this->request->post['agent_ids'] ?? []), true))),
        };
        if ($agents === []) {
            $this->session->flash('error', $this->t('updates.error.no_machines'));
            return $this->redirect($back);
        }
        $passwords = [];
        foreach ((array) ($this->request->post['passwords'] ?? []) as $credentialId => $password) {
            if (is_string($credentialId) && is_string($password)) {
                $passwords[$credentialId] = $password;
            }
        }
        $results = $templates->apply($template, $agents, $passwords, $this->user());
        $applied = array_values(array_filter($results, fn($r) => $r['command'] !== null));
        $failed = array_values(array_filter($results, fn($r) => $r['command'] === null));
        if ($applied !== []) {
            $this->audit('template.apply', 'template', (string) $template['id'], [
                'name' => $template['name'], 'revision' => (int) $template['revision'],
                'machines' => implode(', ', array_map(fn($r) => Agents::name($r['agent']), $applied)),
            ]);
            $this->session->flash('info', $this->t('templates.flash.applied', ['n' => count($applied), 'name' => $template['name']]));
        }
        foreach ($failed as $r) {
            $this->session->flash('error', $this->t('templates.flash.not_applied', [
                'machine' => Agents::name($r['agent']), 'reason' => $this->t($r['error']['key'], array_map(
                    fn($v) => is_array($v) && isset($v['key']) ? $this->t((string) $v['key'], (array) ($v['params'] ?? [])) : $v,
                    $r['error']['params'])),
            ]));
        }
        return $this->redirect($back);
    }

    // ---- helpers ----

    /**
     * @param ?array $template the stored template, null for a new one
     * @param array<string, array> $credentials the template's accounts by id
     */
    private function saveForm(?array $template, array $base, array $credentials): Response
    {
        $form = TaskForm::parse($base, $this->request->post, $credentials, true);
        $all = $credentials;
        foreach ($form->newCredentials as $credentialId => $c) {
            $all[$credentialId] = ['id' => $credentialId, 'kind' => $c['kind'], 'user' => $c['user'], 'domain' => $c['domain'], 'has_secret' => false];
        }
        if ($form->valid()) {
            try {
                $id = $this->app->templates()->save($template === null ? null : (int) $template['id'], $form->task(), $all, $this->user()['login']);
            } catch (\InvalidArgumentException $e) {
                return $this->editor($template, $form->view, $credentials, [['key' => $e->getMessage(), 'params' => []]], 422);
            }
            $name = (string) $form->view['name'];
            $this->audit($template === null ? 'template.create' : 'template.update', 'template', (string) $id, ['name' => $name]);
            $this->session->flash('info', $this->t($template === null ? 'templates.flash.created' : 'templates.flash.saved', ['name' => $name]));
            return $this->redirect('/templates/' . $id);
        }
        return $this->editor($template, $form->view, $credentials, $form->errors, 422);
    }

    /** Accounts of a template that is not saved yet travel with the form, without passwords. */
    private function postedCredentials(): array
    {
        $json = base64_decode($this->post('_credentials'), true);
        $result = [];
        foreach ((array) json_decode($json === false ? '' : $json, true) as $c) {
            if (is_array($c) && is_string($c['id'] ?? null) && preg_match('/^[0-9a-f-]{36}$/', $c['id'])
                && in_array($c['kind'] ?? null, TaskForm::ACCOUNT_KINDS, true)) {
                $result[$c['id']] = ['id' => $c['id'], 'kind' => $c['kind'], 'user' => mb_substr((string) ($c['user'] ?? ''), 0, 255),
                                     'domain' => is_string($c['domain'] ?? null) ? mb_substr($c['domain'], 0, 255) : null, 'has_secret' => false];
            }
        }
        return $result;
    }

    private function editor(?array $template, array $task, array $credentials, array $errors, int $status = 200): Response
    {
        $isNew = $template === null;
        return $this->render('templates/edit', [
            'section' => 'templates',
            'tpl' => $template,
            'task' => $task,
            'credentials' => $credentials,
            'errors' => $errors,
            'isNew' => $isNew,
            'action' => $isNew ? '/templates/new' : '/templates/' . $template['id'] . '/edit',
            'base' => $isNew ? TasksController::encodeBase($task) : null,
            'credentialsField' => $isNew ? base64_encode(json_encode(array_values($credentials), JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)) : null,
        ], $isNew ? $this->t('templates.title.new') : $this->t('templates.title.edit', ['name' => $template['name']]), $status);
    }
}
