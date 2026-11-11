<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Agents;
use Custos\Domain\Reports;
use Custos\Http\Response;
use Custos\Http\Web\Controller;

/** Run history, run page and run logs (section 7.4). */
final class RunsController extends Controller
{
    public const PER_PAGE = 50;
    /** The CSV export stops at this many runs (newest first); a narrower filter gives the rest. */
    public const EXPORT_MAX = 100000;
    /** The log page shows at most this much unpacked text; the full log can be downloaded. */
    public const LOG_VIEW_BYTES = 16 * 1024 * 1024;

    public function index(): Response
    {
        [$filter, $where, $params] = $this->filter();
        $sqlWhere = $where === [] ? '' : 'WHERE ' . implode(' AND ', $where);
        $db = $this->app->db();
        $total = (int) $db->value("SELECT COUNT(*) FROM runs r $sqlWhere", $params);
        $page = $this->page();
        $runs = $db->all(
            "SELECT r.*, t.name AS task_name, a.hostname, a.display_name
             FROM runs r
             JOIN agents a ON a.id = r.agent_id
             LEFT JOIN tasks t ON t.agent_id = r.agent_id AND t.id = r.task_id
             $sqlWhere
             ORDER BY r.started_at DESC, r.id
             LIMIT ? OFFSET ?",
            [...$params, self::PER_PAGE, ($page - 1) * self::PER_PAGE]
        );
        $task = $filter['agent'] !== '' && $filter['task'] !== ''
            ? $this->app->tasks()->find(strtolower($filter['agent']), strtolower($filter['task']))
            : null;
        return $this->render('runs/index', [
            'section' => 'runs',
            'runs' => $runs,
            'filter' => $filter,
            'task' => $task,
            'agents' => $db->all('SELECT id, hostname, display_name FROM agents ORDER BY COALESCE(display_name, hostname)'),
            'page' => $page,
            'pages' => max(1, (int) ceil($total / self::PER_PAGE)),
            'total' => $total,
        ], $this->t('runs.title'));
    }

    /**
     * The run history with the filters of the page as CSV (section 11): UTF-8 with a BOM and ";" between fields, so
     * that Excel with Russian settings opens it as it is. Times are in the site time zone.
     */
    public function export(): Response
    {
        [$filter, $where, $params] = $this->filter();
        $sqlWhere = $where === [] ? '' : 'WHERE ' . implode(' AND ', $where);
        $rows = $this->app->db()->all(
            "SELECT r.*, t.name AS task_name, a.hostname, a.display_name
             FROM runs r
             JOIN agents a ON a.id = r.agent_id
             LEFT JOIN tasks t ON t.agent_id = r.agent_id AND t.id = r.task_id
             $sqlWhere
             ORDER BY r.started_at DESC, r.id
             LIMIT " . self::EXPORT_MAX,
            $params
        );
        // anomaly_text() and the other helpers speak through the current view.
        \Custos\Http\Web\View::$current = $this->view;
        $columns = ['machine', 'task', 'started', 'finished', 'duration_s', 'status', 'type', 'trigger', 'scheduled_for',
                    'source_files', 'source_bytes', 'files_processed', 'bytes_written', 'errors', 'warnings', 'anomaly', 'error_message', 'run_id'];
        $out = fopen('php://temp', 'w+');
        fwrite($out, "\xEF\xBB\xBF");
        self::csvLine($out, array_map(fn($c) => $this->t('csv.' . $c), $columns));
        $local = fn(?string $utc) => $utc === null ? '' : (new \DateTimeImmutable($utc, new \DateTimeZone('UTC')))
            ->setTimezone(new \DateTimeZone($this->app->config->timezone()))->format('Y-m-d H:i:s');
        foreach ($rows as $r) {
            self::csvLine($out, [
                self::csvText(Agents::name($r)),
                self::csvText((string) ($r['task_name'] ?? '')),
                $local($r['started_at']),
                $local($r['finished_at']),
                (string) ($r['duration_s'] ?? ''),
                self::csvText($this->t('status.' . $r['status'])),
                $r['backup_type'] === null ? '' : self::csvText($this->t('backup.' . $r['backup_type'])),
                $r['trigger_type'] === null ? '' : self::csvText($this->t('trigger.' . $r['trigger_type'])),
                $local($r['scheduled_for']),
                (string) ($r['source_files'] ?? ''),
                (string) ($r['source_bytes'] ?? ''),
                (string) ($r['files_processed'] ?? ''),
                (string) ($r['bytes_written'] ?? ''),
                (string) ($r['errors'] ?? ''),
                (string) ($r['warnings'] ?? ''),
                $r['anomaly'] === null ? '' : self::csvText(anomaly_text($r['anomaly'])),
                self::csvText((string) ($r['error_message'] ?? '')),
                $r['id'],
            ]);
        }
        rewind($out);
        $csv = (string) stream_get_contents($out);
        fclose($out);
        $name = 'custos-runs-' . (new \DateTimeImmutable('now', new \DateTimeZone($this->app->config->timezone())))->format('Y-m-d') . '.csv';
        return new Response(200, $csv, [
            'Content-Type' => 'text/csv; charset=utf-8',
            'Content-Disposition' => 'attachment; filename="' . $name . '"',
        ]);
    }

    /** One CSV line with ";" between fields and CRLF at the end, as Excel writes it. */
    private static function csvLine($out, array $fields): void
    {
        fputcsv($out, $fields, ';', '"', '', "\r\n");
    }

    /**
     * Text that came from agents or users: a leading = + - @ would make a spreadsheet run it as a formula, so it gets
     * a leading apostrophe, which Excel hides.
     */
    public static function csvText(string $value): string
    {
        return $value !== '' && in_array($value[0], ['=', '+', '-', '@', "\t", "\r"], true) ? "'" . $value : $value;
    }

    /**
     * Filters of the run history from the query string.
     *
     * @return array{0: array<string, string>, 1: list<string>, 2: list<mixed>} the filter, SQL conditions, parameters
     */
    private function filter(): array
    {
        $filter = [
            'agent' => $this->query('agent'),
            'task' => $this->query('task'),
            'status' => $this->query('status'),
            'from' => $this->query('from'),
            'to' => $this->query('to'),
        ];
        $where = [];
        $params = [];
        if ($filter['agent'] !== '') {
            $where[] = 'r.agent_id = ?';
            $params[] = strtolower($filter['agent']);
        }
        if ($filter['task'] !== '') {
            $where[] = 'r.task_id = ?';
            $params[] = strtolower($filter['task']);
        }
        if (in_array($filter['status'], [...Reports::FINAL_STATUSES, 'running'], true)) {
            $where[] = 'r.status = ?';
            $params[] = $filter['status'];
        }
        if (($from = $this->dateToUtc($filter['from'])) !== null) {
            $where[] = 'r.started_at >= ?';
            $params[] = $from;
        }
        if (($to = $this->dateToUtc($filter['to'], true)) !== null) {
            $where[] = 'r.started_at < ?';
            $params[] = $to;
        }
        return [$filter, $where, $params];
    }

    public function show(string $id): Response
    {
        $run = $this->find($id);
        if ($run === null) {
            return $this->notFound();
        }
        $db = $this->app->db();
        return $this->render('runs/show', [
            'section' => 'runs',
            'run' => $run,
            'destinations' => $db->all('SELECT * FROM run_destinations WHERE run_id = ? ORDER BY set_name', [$run['id']]),
            'destinationInfo' => $this->destinationInfo($run),
            'log' => $db->one('SELECT LENGTH(log_gz) AS size, source, received_at FROM run_logs WHERE run_id = ?', [$run['id']]),
            'verify' => json_decode((string) $run['verify'], true),
            'retention' => json_decode((string) $run['retention'], true),
            'dumps' => array_values(array_filter((array) json_decode((string) $run['db_dumps'], true), 'is_array')),
            'logRequest' => $db->one(
                "SELECT status, created_at, result_message FROM commands WHERE agent_id = ? AND kind = 'log.fetch' AND payload LIKE ?
                 ORDER BY created_at DESC LIMIT 1",
                [$run['agent_id'], '%' . $run['id'] . '%']
            ),
        ], $this->runTitle($run));
    }

    public function log(string $id): Response
    {
        $run = $this->find($id);
        $gz = $run === null ? null : $this->app->db()->value('SELECT log_gz FROM run_logs WHERE run_id = ?', [$run['id']]);
        if ($gz === null) {
            return $this->notFound();
        }
        [$text, $truncated] = self::inflate($gz, self::LOG_VIEW_BYTES);
        return $this->render('runs/log', [
            'section' => 'runs',
            'run' => $run,
            'text' => $text,
            'truncated' => $truncated,
        ], $this->t('runs.log.title') . ' · ' . $this->runTitle($run));
    }

    public function download(string $id): Response
    {
        $run = $this->find($id);
        $gz = $run === null ? null : $this->app->db()->value('SELECT log_gz FROM run_logs WHERE run_id = ?', [$run['id']]);
        if ($gz === null) {
            return $this->notFound();
        }
        return new Response(200, $gz, [
            'Content-Type' => 'application/gzip',
            'Content-Disposition' => 'attachment; filename="custos-run-' . $run['id'] . '.log.gz"',
        ]);
    }

    /** "Ask the agent for the log" (7.4): log.fetch for a run whose full log the server does not have. */
    public function fetchLog(string $id): Response
    {
        $run = $this->find($id);
        if ($run === null) {
            return $this->notFound();
        }
        $agent = $this->app->agents()->find($run['agent_id']);
        if ($agent['status'] !== 'approved') {
            $this->session->flash('error', $this->t('commands.error.not_approved'));
        } else {
            $this->app->commands()->queue($agent['id'], 'log.fetch', ['run_id' => $run['id']], $this->user());
            $this->audit('command', 'agent', $agent['id'], ['name' => Agents::name($agent), 'command' => 'log.fetch', 'task' => $run['task_name']]);
            $this->session->flash('info', $this->t('run.log.requested'));
        }
        return $this->redirect('/runs/' . $run['id']);
    }

    private function find(string $id): ?array
    {
        return $this->app->db()->one(
            'SELECT r.*, t.name AS task_name, t.data AS task_data, a.hostname, a.display_name
             FROM runs r
             JOIN agents a ON a.id = r.agent_id
             LEFT JOIN tasks t ON t.agent_id = r.agent_id AND t.id = r.task_id
             WHERE r.id = ?',
            [strtolower($id)]
        );
    }

    private function runTitle(array $run): string
    {
        return ($run['task_name'] ?? $this->t('runs.unknown_task')) . ' · ' . Agents::name($run);
    }

    /** Destination paths from the task definition, by destination id. */
    private function destinationInfo(array $run): array
    {
        $task = json_decode((string) ($run['task_data'] ?? ''), true);
        $info = [];
        foreach ((array) ($task['destinations'] ?? []) as $d) {
            if (is_array($d) && isset($d['id'])) {
                $info[$d['id']] = trim(($d['kind'] ?? '') . ' ' . ($d['path'] ?? $d['host'] ?? $d['bucket'] ?? ''));
            }
        }
        return $info;
    }

    /** @return array{0: string, 1: bool} text and whether it was cut at $limit bytes */
    private static function inflate(string $gz, int $limit): array
    {
        $context = inflate_init(ZLIB_ENCODING_GZIP);
        $text = '';
        foreach (str_split($gz, 65536) as $chunk) {
            $part = @inflate_add($context, $chunk, ZLIB_SYNC_FLUSH);
            if ($part === false) {
                break;
            }
            $text .= $part;
            if (strlen($text) > $limit) {
                return [mb_strcut($text, 0, $limit), true];
            }
        }
        return [$text, false];
    }
}
