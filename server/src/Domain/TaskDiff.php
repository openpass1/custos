<?php

declare(strict_types=1);

namespace Custos\Domain;

/**
 * Differences between two versions of a task (7.4), as a list of changed settings:
 * "schedule.times: 00:00 → 01:00", "destinations[2]: added".
 *
 * Both tasks are flattened into "path = value" lines; list items with an id (sources, destinations) are matched by
 * id, so removing the first source does not show every later one as changed.
 */
final class TaskDiff
{
    /** Fields that change with every save and say nothing about the settings. */
    private const IGNORED = ['revision', 'updated_at', 'updated_by'];

    /**
     * @return list<array{path: string, old: ?string, new: ?string}> old is null for an added value, new for a removed one
     */
    public static function compare(array $old, array $new): array
    {
        $a = self::flatten($old);
        $b = self::flatten($new);
        $paths = array_unique(array_merge(array_keys($a), array_keys($b)));
        $result = [];
        foreach ($paths as $path) {
            $before = $a[$path] ?? null;
            $after = $b[$path] ?? null;
            if ($before !== $after) {
                $result[] = ['path' => $path, 'old' => $before, 'new' => $after];
            }
        }
        return $result;
    }

    /** @return array<string, string> path => value as JSON text */
    public static function flatten(array $data, string $prefix = ''): array
    {
        $result = [];
        $isList = array_is_list($data);
        foreach ($data as $key => $value) {
            if ($prefix === '' && in_array($key, self::IGNORED, true)) {
                continue;
            }
            if ($isList) {
                $name = is_array($value) && is_string($value['id'] ?? null) ? '[' . self::shortId($value['id']) . ']' : "[$key]";
                $path = $prefix . $name;
            } else {
                $path = $prefix === '' ? (string) $key : $prefix . '.' . $key;
            }
            if ($value === TaskEdits::EMPTY_OBJECT) {
                $result[$path] = '{}';
            } elseif (is_array($value) && $value !== [] && !self::isScalarList($value)) {
                $result += self::flatten($value, $path);
            } else {
                $result[$path] = json_encode($value, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
            }
        }
        return $result;
    }

    /** Short lists of plain values (times, days, masks) read better as one line. */
    private static function isScalarList(array $value): bool
    {
        if (!array_is_list($value)) {
            return false;
        }
        foreach ($value as $item) {
            if (is_array($item)) {
                return false;
            }
        }
        return true;
    }

    private static function shortId(string $id): string
    {
        return substr($id, 0, 8);
    }
}
