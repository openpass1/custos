<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Http\Response;
use Custos\Http\Web\Controller;

/**
 * The search of the top bar: machines by name, host name, address or group, tasks by name. Several words narrow it down:
 * every word has to be found, and for a task the machine's name counts too ("pc-03 desktop"), as long as one word is in
 * the task's own name. While one types, /search/suggest answers with the first matches (app.js shows them under the
 * field); Enter opens /search with all of them.
 */
final class SearchController extends Controller
{
    /** Matches shown under the field while typing. */
    private const SUGGEST_MACHINES = 5;
    private const SUGGEST_TASKS = 6;
    /** Matches on the result page. */
    private const PAGE_LIMIT = 200;

    public function index(): Response
    {
        $q = $this->text();
        [$machines, $tasks] = $q === '' ? [[], []] : $this->find($q, self::PAGE_LIMIT, self::PAGE_LIMIT);
        return $this->render('search/index', [
            'section' => 'search',
            'q' => $q,
            'words' => self::words($q),
            'machines' => $machines,
            'tasks' => $tasks,
        ], $q === '' ? $this->t('search.title') : $this->t('search.title.q', ['q' => $q]));
    }

    /** The first matches, as a piece of HTML for the list under the field. */
    public function suggest(): Response
    {
        $q = $this->text();
        if ($q === '') {
            return Response::html('');
        }
        [$machines, $tasks] = $this->find($q, self::SUGGEST_MACHINES, self::SUGGEST_TASKS);
        return Response::html($this->view->partial('search/_list', [
            'q' => $q,
            'words' => self::words($q),
            'machines' => $machines,
            'tasks' => $tasks,
            'suggest' => true,
        ]));
    }

    private function text(): string
    {
        return mb_substr(preg_replace('/\s+/u', ' ', $this->query('q')) ?? '', 0, 100);
    }

    /** @return list<string> the words of the query, lower case */
    public static function words(string $q): array
    {
        return array_values(array_unique(array_filter(explode(' ', mb_strtolower(trim($q))), fn($w) => $w !== '')));
    }

    /**
     * Machines and tasks that match, the ones whose name starts with the query first, then by name.
     *
     * @return array{list<array>, list<array>}
     */
    private function find(string $q, int $machineLimit, int $taskLimit): array
    {
        $words = self::words($q);
        $board = $this->app->board();
        $contains = function (string $haystack, array $words): bool {
            foreach ($words as $word) {
                if (!str_contains($haystack, $word)) {
                    return false;
                }
            }
            return true;
        };
        $machines = [];
        foreach ($board->agents as $agent) {
            $haystack = mb_strtolower(implode(' ', [$agent['name'], $agent['hostname'], (string) ($agent['last_ip'] ?? ''), (string) ($agent['group_name'] ?? '')]));
            if ($contains($haystack, $words)) {
                $machines[] = $agent;
            }
        }
        $tasks = [];
        foreach ($board->tasks as $task) {
            $agent = $board->agents[$task['agent_id']] ?? null;
            if ($agent === null) {
                continue;
            }
            $own = mb_strtolower((string) $task['name']);
            $hit = false;
            foreach ($words as $word) {
                $hit = $hit || str_contains($own, $word);
            }
            if ($hit && $contains($own . ' ' . mb_strtolower($agent['name'] . ' ' . $agent['hostname']), $words)) {
                $task['agent'] = $agent;
                $tasks[] = $task;
            }
        }
        $lower = mb_strtolower($q);
        $order = fn(string $name): array => [str_starts_with(mb_strtolower($name), $lower) ? 0 : 1, mb_strtolower($name)];
        usort($machines, fn($a, $b) => $order($a['name']) <=> $order($b['name']));
        usort($tasks, fn($a, $b) => [...$order($a['name']), mb_strtolower($a['agent']['name'])] <=> [...$order($b['name']), mb_strtolower($b['agent']['name'])]);
        return [array_slice($machines, 0, $machineLimit), array_slice($tasks, 0, $taskLimit)];
    }
}
