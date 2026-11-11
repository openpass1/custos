<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Http\Response;
use Custos\Http\Web\Controller;
use Custos\Infra\Time;

/**
 * The wall screen (7.9): /wall?token=… without a login, for a monitor on the wall. Large tiles, problems first, dark,
 * refreshed every 30 seconds. Web lets a request in here only with the wall token from an allowed address.
 */
final class WallController extends Controller
{
    public const REFRESH_S = 30;

    public function index(): Response
    {
        return $this->view->page('wall/index', [
            'parts' => $this->parts(),
            'refresh' => self::REFRESH_S,
            'token' => (string) $this->request->query['token'],
            'serverName' => $this->app->settings()->get('server_name'),
        ]);
    }

    public function live(): Response
    {
        return Response::json(200, ['parts' => $this->parts()]);
    }

    /** @return array<string, string> */
    private function parts(): array
    {
        $now = time();
        $board = $this->app->board(null, $now);
        $cronLast = $this->app->settings()->get('cron_last_run');
        $vars = [
            'board' => $board,
            'summary' => $board->summary(),
            'soft' => $board->softMarks(),
            'running' => $board->running($this->app->db()),
            'cronLate' => $cronLast === '' || Time::unix($cronLast) < $now - DashboardController::CRON_LATE_S,
            'updated' => Time::db($now),
        ];
        return ['board' => $this->view->partial('wall/_board', $vars)];
    }
}
