<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Http\Response;
use Custos\Http\Web\Controller;

/** Machine groups (7.2): create, rename, delete. Machines are put into groups on their own pages. */
final class GroupsController extends Controller
{
    public function index(): Response
    {
        return $this->render('groups/index', ['section' => 'agents', 'groups' => $this->app->groups()->all()], $this->t('groups.title'));
    }

    public function create(): Response
    {
        try {
            $id = $this->app->groups()->create($this->post('name'));
            $this->audit('group.create', 'group', (string) $id, ['name' => trim($this->post('name'))]);
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage(), ['max' => \Custos\Domain\Groups::MAX_NAME]));
        }
        return $this->redirect('/groups');
    }

    public function action(string $id, string $action): Response
    {
        $groups = $this->app->groups();
        $group = $groups->find((int) $id);
        if ($group === null) {
            return $this->notFound();
        }
        if ($action === 'delete') {
            $groups->delete((int) $group['id']);
            $this->audit('group.delete', 'group', $id, ['name' => $group['name']]);
            $this->session->flash('info', $this->t('groups.flash.deleted', ['name' => $group['name']]));
            return $this->redirect('/groups');
        }
        try {
            $groups->rename((int) $group['id'], $this->post('name'));
            $this->audit('group.rename', 'group', $id, ['name' => $group['name'], 'new_name' => trim($this->post('name'))]);
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage(), ['max' => \Custos\Domain\Groups::MAX_NAME]));
        }
        return $this->redirect('/groups');
    }
}
