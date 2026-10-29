<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Http\Response;
use Custos\Http\Web\Controller;

/** The audit log with filters by date and user (section 4). */
final class AuditController extends Controller
{
    public const PER_PAGE = 100;

    public function index(): Response
    {
        $filter = ['from' => $this->query('from'), 'to' => $this->query('to'), 'user' => $this->query('user')];
        $page = $this->page();
        $result = $this->app->audit()->search([
            'from' => $this->dateToUtc($filter['from']),
            'to' => $this->dateToUtc($filter['to'], true),
            'user_id' => ctype_digit($filter['user']) ? (int) $filter['user'] : null,
        ], self::PER_PAGE, ($page - 1) * self::PER_PAGE);
        return $this->render('audit/index', [
            'section' => 'audit',
            'rows' => $result['rows'],
            'filter' => $filter,
            'users' => $this->app->users()->all(),
            'page' => $page,
            'pages' => max(1, (int) ceil($result['total'] / self::PER_PAGE)),
        ], $this->t('audit.title'));
    }
}
