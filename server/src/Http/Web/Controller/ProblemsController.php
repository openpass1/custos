<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Http\Response;
use Custos\Http\Web\Controller;

/** Problems (6.7): the open ones and the history of closed ones. */
final class ProblemsController extends Controller
{
    public const PER_PAGE = 50;
    /** The page refreshes its lists this often (seconds). */
    public const REFRESH_S = 15;

    public function index(): Response
    {
        $db = $this->app->db();
        $select = 'SELECT p.*, a.hostname, a.display_name, a.time_zone, t.name AS task_name
                   FROM problems p JOIN agents a ON a.id = p.agent_id
                   LEFT JOIN tasks t ON t.agent_id = p.agent_id AND t.id = p.task_id';
        $page = $this->page();
        $closedTotal = (int) $db->value('SELECT COUNT(*) FROM problems WHERE closed_at IS NOT NULL');
        return $this->render('problems/index', [
            'section' => 'problems',
            'open' => $db->all("$select WHERE p.closed_at IS NULL ORDER BY p.kind = 'error' DESC, p.opened_at DESC"),
            'closed' => $db->all("$select WHERE p.closed_at IS NOT NULL ORDER BY p.closed_at DESC, p.id DESC LIMIT ? OFFSET ?",
                [self::PER_PAGE, ($page - 1) * self::PER_PAGE]),
            'page' => $page,
            'pages' => max(1, (int) ceil($closedTotal / self::PER_PAGE)),
            'live' => self::REFRESH_S,
        ], $this->t('problems.title'));
    }
}
