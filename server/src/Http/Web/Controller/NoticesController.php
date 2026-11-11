<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Http\Response;
use Custos\Http\Web\Controller;

/** The bell (7.7): quiet notices, marked read by hand. */
final class NoticesController extends Controller
{
    public const PER_PAGE = 50;

    public function index(): Response
    {
        $notices = $this->app->notices();
        $page = $this->page();
        return $this->render('notices/index', [
            'section' => 'notices',
            'notices' => $notices->latest(self::PER_PAGE, ($page - 1) * self::PER_PAGE),
            'unread' => $notices->unreadCount(),
            'page' => $page,
            'pages' => max(1, (int) ceil($notices->count() / self::PER_PAGE)),
        ], $this->t('notices.title'));
    }

    /** Marks one notice (id) or all of them read. */
    public function read(): Response
    {
        $id = $this->post('id');
        $this->app->notices()->markRead(ctype_digit($id) ? (int) $id : null, (int) $this->user()['id']);
        $back = $this->post('back');
        return $this->redirect($back === 'dashboard' ? '/' : '/notices');
    }
}
