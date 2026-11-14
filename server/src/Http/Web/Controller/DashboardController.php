<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Http\Response;
use Custos\Http\Web\Controller;
use Custos\Infra\Time;

/**
 * The dashboard (7.1): summary, "needs attention", runs in progress, notices, versions and the 30-day heat map. The page
 * asks /dashboard/live for fresh blocks every 15 seconds; the heat map is history and comes with the page only.
 */
final class DashboardController extends Controller
{
    public const REFRESH_S = 15;
    /** The dashboard warns when cron has not run for this long (it would stop the checks for missed runs). */
    public const CRON_LATE_S = 300;
    /** Parts of the page that are refreshed, in templates/dashboard/_{name}.php */
    private const PARTS = ['summary', 'attention', 'running', 'notices', 'versions'];

    public function index(): Response
    {
        return $this->render('dashboard/index', [
            'section' => 'dashboard',
            'parts' => $this->parts(),
            'refresh' => self::REFRESH_S,
            'heatmap' => $this->app->analytics()->heatmap(time(), $this->app->config->timezone()),
        ], $this->t('dashboard.title'));
    }

    public function live(): Response
    {
        return Response::json(200, ['parts' => $this->parts(), 'bell' => $this->app->notices()->unreadCount(), 'counts' => $this->app->navCounts()]);
    }

    /** @return array<string, string> rendered blocks */
    private function parts(): array
    {
        $app = $this->app;
        $db = $app->db();
        $now = time();
        $board = $app->board(null, $now);
        $cronLast = $app->settings()->get('cron_last_run');
        $vars = [
            'board' => $board,
            'summary' => $board->summary(),
            'soft' => $board->softMarks(),
            'running' => $board->running($db),
            'cronLate' => $cronLast === '' || Time::unix($cronLast) < $now - self::CRON_LATE_S,
            'cronLast' => $cronLast === '' ? null : $cronLast,
            'written' => (int) $db->value(
                'SELECT COALESCE(SUM(bytes_written), 0) FROM runs WHERE finished_at >= ?',
                [Time::db($now - 86400)]
            ),
            'storage' => $app->storage()->summary(),
            'notices' => $app->notices()->latest(5, 0, true),
            'versions' => self::distribution($db->all(
                "SELECT agent_version AS version, COUNT(*) AS n FROM agents WHERE status = 'approved' GROUP BY agent_version"
            )),
            'sevenZipVersions' => self::distribution($db->all(
                "SELECT sevenzip_version AS version, COUNT(*) AS n FROM agents WHERE status = 'approved' AND sevenzip_version IS NOT NULL
                 GROUP BY sevenzip_version"
            )),
            'currentVersion' => $app->packages()->current()['version'] ?? null,
            'latestSevenZip' => $app->updateWatch()->latestSevenZip()['version'] ?? null,
            'unread' => $app->notices()->unreadCount(),
        ];
        $parts = [];
        foreach (self::PARTS as $name) {
            $parts[$name] = $this->view->partial('dashboard/_' . $name, $vars);
        }
        return $parts;
    }

    /** Versions with their number of machines, newest first. */
    private static function distribution(array $rows): array
    {
        usort($rows, fn($a, $b) => version_compare((string) $b['version'], (string) $a['version']));
        return $rows;
    }
}
