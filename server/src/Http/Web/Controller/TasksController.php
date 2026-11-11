<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Agents;
use Custos\Domain\Commands;
use Custos\Domain\TaskDiff;
use Custos\Domain\TaskEdits;
use Custos\Domain\TaskForm;
use Custos\Http\Response;
use Custos\Http\Web\Controller;
use Custos\Infra\Time;

/**
 * The task page (7.4), commands for a task, the task editor (7.5) and the version history with differences and
 * "return this version" (7.4).
 */
final class TasksController extends Controller
{
    public const SLOT_DAYS = 30;

    /** Commands of the task page and what they need: an edit is refused by an agent that does not allow remote edits. */
    private const COMMANDS = ['run' => 'task.run', 'stop' => 'task.stop', 'pause' => 'task.pause', 'resume' => 'task.resume',
                              'enable' => 'task.enable', 'disable' => 'task.disable', 'delete' => 'task.delete'];

    public function show(string $agentId, string $taskId): Response
    {
        $agentId = strtolower($agentId);
        $taskId = strtolower($taskId);
        $board = $this->app->board($agentId);
        $agent = $board->agents[$agentId] ?? null;
        $task = $agent === null ? null : $board->task($agentId, $taskId);
        $db = $this->app->db();
        if ($task === null) {
            // A deleted task is still shown, with its history.
            $row = $agent === null ? null : $this->app->tasks()->find($agentId, $taskId);
            if ($row === null) {
                // A task created on the site that the agent has not reported yet.
                $pending = $agent === null ? null : $this->app->taskEdits()->pending($agent, $taskId);
                if ($pending === null) {
                    return $this->notFound();
                }
                $def = $pending['data']['task'] ?? [];
                $row = ['id' => $taskId, 'agent_id' => $agentId, 'name' => (string) ($def['name'] ?? ''), 'enabled' => (int) !empty($def['enabled']),
                        'revision' => 0, 'updated_at' => null, 'updated_by' => null, 'deleted' => 0, 'last_run_id' => null,
                        'last_run_status' => null, 'last_run_at' => null, 'last_success_at' => null, 'first_seen_at' => null,
                        'watch_from' => null, 'data' => json_encode($def), 'new' => true];
            }
            $task = $row + ['def' => json_decode($row['data'], true) ?: [], 'status' => 'disabled', 'flags' => [], 'next_at' => null,
                            'problems' => [], 'offline_slots' => [], 'queued' => false, 'watched' => false];
        }
        return $this->render('tasks/show', [
            'section' => 'agents',
            'agent' => $agent,
            'task' => $task,
            'problems' => $db->all(
                'SELECT * FROM problems WHERE agent_id = ? AND task_id = ? ORDER BY closed_at IS NULL DESC, opened_at DESC LIMIT 20',
                [$agentId, $taskId]
            ),
            'slots' => $db->all(
                'SELECT * FROM expected_slots WHERE agent_id = ? AND task_id = ? AND slot_at >= ? ORDER BY slot_at DESC LIMIT 200',
                [$agentId, $taskId, Time::db(time() - self::SLOT_DAYS * 86400)]
            ),
            'runs' => $db->all(
                'SELECT r.*, ? AS task_name FROM runs r WHERE r.agent_id = ? AND r.task_id = ? ORDER BY r.started_at DESC LIMIT 20',
                [$task['name'], $agentId, $taskId]
            ),
            'progress' => $agent['running_task'] === $taskId
                ? $db->one('SELECT * FROM run_progress WHERE agent_id = ? AND run_id = ?', [$agentId, (string) $agent['running_run']])
                : null,
            'commands' => $this->app->commands()->forTask($agentId, $taskId),
            'pending' => $this->app->taskEdits()->pending($agent, $taskId),
            'templateLink' => $this->app->templates()->linkOf($agentId, $taskId),
            'versionCount' => (int) $db->value('SELECT COUNT(*) FROM task_versions WHERE agent_id = ? AND task_id = ?', [$agentId, $taskId]),
        ], $task['name'] . ' · ' . Agents::name($agent));
    }

    /** Run, stop, pause, resume, enable, disable or delete: a command to the agent (A.4). */
    public function command(string $agentId, string $taskId, string $action): Response
    {
        [$agent, $task] = $this->load($agentId, $taskId);
        if ($agent === null || $task === null) {
            return $this->notFound();
        }
        $kind = self::COMMANDS[$action];
        $back = '/agents/' . $agent['id'] . '/tasks/' . $task['id'];
        if ($error = $this->refusal($agent, $kind)) {
            $this->session->flash('error', $error);
            return $this->redirect($back);
        }
        $this->app->commands()->queue($agent['id'], $kind, ['task_id' => $task['id']], $this->user());
        $this->audit('command', 'agent', $agent['id'], ['name' => Agents::name($agent), 'command' => $kind, 'task' => $task['name']]);
        $this->session->flash('info', $this->t('commands.flash.queued', ['command' => $this->t('command.' . $kind), 'task' => $task['name']]));
        return $this->redirect($this->post('back') === 'agent' ? '/agents/' . $agent['id'] : $back);
    }

    public function edit(string $agentId, string $taskId): Response
    {
        $agent = $this->app->agents()->find($agentId);
        $current = $agent === null ? null : $this->app->taskEdits()->current($agent, strtolower($taskId));
        if ($current === null) {
            return $this->notFound();
        }
        return $this->editor($agent, $current['task'], [], $current['pending'], false);
    }

    public function save(string $agentId, string $taskId): Response
    {
        $agent = $this->app->agents()->find($agentId);
        $taskId = strtolower($taskId);
        $current = $agent === null ? null : $this->app->taskEdits()->current($agent, $taskId);
        if ($current === null) {
            return $this->notFound();
        }
        if ($error = $this->refusal($agent, 'task.upsert')) {
            $this->session->flash('error', $error);
            return $this->redirect('/agents/' . $agent['id'] . '/tasks/' . $taskId . '/edit');
        }
        $form = TaskForm::parse($current['task'], $this->request->post, TaskEdits::credentials($agent));
        if (!$form->valid()) {
            return $this->editor($agent, $form->view, $form->errors, $current['pending'], false, 422);
        }
        $base = $this->app->taskEdits()->baseRevision($agent, $taskId);
        $this->app->taskEdits()->save($agent, $form, $base, $this->user());
        $this->audit('task.save', 'agent', $agent['id'], ['name' => Agents::name($agent), 'task' => $form->view['name'], 'base_revision' => $base]);
        $this->session->flash('info', $this->t('task_form.flash.saved'));
        return $this->redirect('/agents/' . $agent['id'] . '/tasks/' . $taskId);
    }

    /** A new task, or a copy of an existing one (?from=task id). */
    public function create(string $agentId): Response
    {
        $agent = $this->app->agents()->find($agentId);
        if ($agent === null) {
            return $this->notFound();
        }
        $task = TaskForm::defaults();
        $from = strtolower($this->query('from'));
        if ($from !== '') {
            $current = $this->app->taskEdits()->current($agent, $from);
            if ($current === null) {
                return $this->notFound();
            }
            $task = TaskForm::copy($current['task'], $this->t('task_form.copy_name', ['name' => (string) ($current['task']['name'] ?? '')]));
        }
        return $this->editor($agent, $task, [], null, true);
    }

    public function store(string $agentId): Response
    {
        $agent = $this->app->agents()->find($agentId);
        if ($agent === null) {
            return $this->notFound();
        }
        if ($error = $this->refusal($agent, 'task.upsert')) {
            $this->session->flash('error', $error);
            return $this->redirect('/agents/' . $agent['id']);
        }
        // The new task travels in the form, so that a copy keeps the fields this editor does not show.
        $base = self::decodeBase($this->post('_base')) ?? TaskForm::defaults();
        if ($this->app->tasks()->find($agent['id'], (string) $base['id']) !== null) {
            $base['id'] = TaskForm::defaults()['id'];
        }
        $form = TaskForm::parse($base, $this->request->post, TaskEdits::credentials($agent));
        if (!$form->valid()) {
            return $this->editor($agent, $form->view, $form->errors, null, true, 422);
        }
        $this->app->taskEdits()->save($agent, $form, 0, $this->user());
        $this->audit('task.create', 'agent', $agent['id'], ['name' => Agents::name($agent), 'task' => $form->view['name']]);
        $this->session->flash('info', $this->t('task_form.flash.created'));
        return $this->redirect('/agents/' . $agent['id'] . '/tasks/' . $base['id']);
    }

    /** Version history (7.4) with the differences of one version from the one before. */
    public function versions(string $agentId, string $taskId): Response
    {
        [$agent, $task] = $this->load($agentId, $taskId);
        if ($agent === null || $task === null) {
            return $this->notFound();
        }
        $edits = $this->app->taskEdits();
        $versions = $edits->versions($agent['id'], $task['id']);
        $selected = ctype_digit($this->query('rev')) ? (int) $this->query('rev') : (int) ($versions[0]['revision'] ?? 0);
        $version = $versions === [] ? null : $edits->version($agent['id'], $task['id'], $selected);
        return $this->render('tasks/versions', [
            'section' => 'agents',
            'agent' => $agent,
            'task' => $task,
            'versions' => $versions,
            'version' => $version,
            'diff' => $version === null || $version['previous'] === null ? null : TaskDiff::compare($version['previous'], $version['task']),
            'refusal' => $this->refusal($agent, 'task.upsert'),
        ], $this->t('versions.title', ['name' => $task['name']]));
    }

    public function restore(string $agentId, string $taskId, string $revision): Response
    {
        [$agent, $task] = $this->load($agentId, $taskId);
        if ($agent === null || $task === null) {
            return $this->notFound();
        }
        $back = '/agents/' . $agent['id'] . '/tasks/' . $task['id'];
        if ($error = $this->refusal($agent, 'task.upsert')) {
            $this->session->flash('error', $error);
            return $this->redirect($back . '/versions');
        }
        try {
            $result = $this->app->taskEdits()->restore($agent, $task['id'], (int) $revision, $this->user());
        } catch (\InvalidArgumentException) {
            return $this->notFound();
        }
        if ($result['command'] === null) {
            $this->session->flash('error', $this->t('versions.error.credentials'));
            return $this->redirect($back . '/versions', ['rev' => $revision]);
        }
        $this->audit('task.restore', 'agent', $agent['id'], ['name' => Agents::name($agent), 'task' => $task['name'], 'revision' => (int) $revision]);
        $this->session->flash('info', $this->t('versions.flash.restored', ['revision' => (int) $revision]));
        return $this->redirect($back);
    }

    // ---- helpers ----

    /** @return array{0: ?array, 1: ?array} the agent and its task (deleted tasks included) */
    private function load(string $agentId, string $taskId): array
    {
        $agent = $this->app->agents()->find($agentId);
        $task = $agent === null ? null : $this->app->tasks()->find($agent['id'], strtolower($taskId));
        return [$agent, $task];
    }

    /** Why a command cannot be sent to this machine now, or null. */
    private function refusal(array $agent, string $kind): ?string
    {
        if ($agent['status'] !== 'approved') {
            return $this->t('commands.error.not_approved');
        }
        if (in_array($kind, Commands::EDITS, true) && TaskEdits::remoteEditOff($agent)) {
            return $this->t('commands.error.remote_edit_off');
        }
        return null;
    }

    private function editor(array $agent, array $task, array $errors, ?array $pending, bool $isNew, int $status = 200): Response
    {
        $refusal = $this->refusal($agent, 'task.upsert');
        return $this->render('tasks/edit', [
            'section' => 'agents',
            'agent' => $agent,
            'task' => $task,
            'errors' => $errors,
            'pending' => $pending,
            'isNew' => $isNew,
            'readOnly' => $refusal !== null,
            'refusal' => $refusal,
            'credentials' => TaskEdits::credentials($agent),
            'action' => $isNew ? '/agents/' . $agent['id'] . '/tasks/new' : '/agents/' . $agent['id'] . '/tasks/' . $task['id'] . '/edit',
            'base' => $isNew ? self::encodeBase($task) : null,
        ], $isNew ? $this->t('task_form.title.new') : $this->t('task_form.title.edit', ['name' => (string) ($task['name'] ?? '')]), $status);
    }

    /** A task carried through the form as base64 JSON (it holds no secrets: those never are in a task object). */
    public static function encodeBase(array $task): string
    {
        return base64_encode(json_encode(TaskEdits::object(TaskForm::clean($task)), JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES));
    }

    public static function decodeBase(string $value): ?array
    {
        $json = base64_decode($value, true);
        if ($json === false || $json === '') {
            return null;
        }
        $task = TaskEdits::decode($json);
        if (!is_string($task['id'] ?? null) || !preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/', $task['id'])) {
            return null;
        }
        return $task;
    }
}
