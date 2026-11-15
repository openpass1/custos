<?php

declare(strict_types=1);

namespace Custos\Domain;

/**
 * Whether the retention of a task fits the protection from deletion of its destinations: WORM of a NAS, S3 Object
 * Lock (client spec 6.6). A rule that deletes backups before the storage lets them go does not delete them: they stay
 * until their protection is over, and the destination holds more backups than the rule says. The same check as the
 * program's editor makes (Custos.Core.Backup.WormRetention), so that a task saved here is not refused there.
 *
 * Times are in hours. The schedule gives an average time between runs; a task run by hand only has none, and only the
 * rules that do not depend on it are checked then.
 */
final class WormRetention
{
    private const DAY = 24.0;
    private const WEEK = 7 * 24.0;
    /** A full backup forced on a day of the month comes at least this often. */
    private const MONTH = 28 * 24.0;
    /** WORM: the margin for clocks that differ, on top of the delay and the days (client spec 6.3). */
    private const MARGIN = 1.0;

    /**
     * What is wrong, as a message of the task editor (['key' => …, 'params' => […]]); null if the retention fits or
     * nothing is protected. $label gives the name of destination $n (from 1).
     *
     * @param callable(int): array $label
     */
    public static function problem(array $task, callable $label): ?array
    {
        if (($task['backup_type'] ?? '') === 'mirror') {
            return null;
        }

        $best = null;
        foreach (array_values((array) ($task['destinations'] ?? [])) as $n => $d) {
            $hours = self::protection((array) $d);
            if ($hours > 0 && ($best === null || $hours > $best[1])) {
                $best = [$n, $hours, (array) $d];
            }
        }
        if ($best === null) {
            return null;
        }
        [$n, $protection, $destination] = $best;

        $retention = (array) ($task['retention'] ?? []);
        $schedule = (array) ($task['schedule'] ?? []);
        $interval = self::interval($schedule);
        switch ($retention['mode'] ?? '') {
            case 'age':
                $days = (int) ($retention['max_age_days'] ?? 0);
                if ($days <= 0) {
                    return null;
                }
                $youngest = $days * self::DAY;
                $fix = ['key' => 'task_form.worm_fix.age', 'params' => ['days' => self::wholeDays($protection)]];
                break;

            case 'count':
                $full = $interval === null ? null : self::fullEvery($task, $interval);
                if ($interval === null || $full === null) {
                    return null;
                }
                $youngest = (max(1, (int) ($retention['keep_full'] ?? 1)) - 1) * $full + $interval;
                $keep = (int) ceil(($protection - $interval) / $full) + 1;
                $fix = ['key' => 'task_form.worm_fix.count', 'params' => ['count' => max(2, $keep)]];
                break;

            case 'gfs':
                if ($interval === null) {
                    return null;
                }
                if (self::severalADay($schedule) && ($task['backup_type'] ?? '') !== 'incremental') {
                    // Only the last backup of a day is kept; the others of that day go at the next run.
                    $youngest = $interval;
                    $fix = ['key' => 'task_form.worm_fix.gfs_day', 'params' => ['days' => self::wholeDays($protection)]];
                } else {
                    // A day leaves the daily backups after so many days; without them a backup that is not the
                    // weekly or monthly one goes at the next run.
                    $daily = (int) ($retention['gfs']['daily'] ?? 0);
                    $youngest = $daily > 0 ? ($daily - 1) * self::DAY + $interval : $interval;
                    $days = (int) ceil(($protection - $interval) / self::DAY) + 1;
                    $fix = ['key' => 'task_form.worm_fix.gfs', 'params' => ['days' => max(1, $days)]];
                }
                break;

            default:
                return null;
        }

        if ($youngest >= $protection - 1e-9) {
            return null;
        }

        $worm = in_array($destination['kind'] ?? '', TaskForm::FILE_SYSTEM_KINDS, true) && (int) ($destination['worm_days'] ?? 0) > 0;
        return [
            'key' => $worm ? 'task_form.error.worm_retention' : 'task_form.error.lock_retention',
            'params' => [
                'what' => $label($n + 1),
                'days' => $worm ? (int) $destination['worm_days'] : (int) $destination['object_lock_days'],
                'protection' => self::duration($protection),
                'youngest' => self::duration($youngest),
                'fix' => $fix,
            ],
        ];
    }

    /** How many hours after it is written a backup cannot be deleted there; 0 if nothing protects it. */
    public static function protection(array $destination): float
    {
        $kind = $destination['kind'] ?? '';
        $worm = (int) ($destination['worm_days'] ?? 0);
        if ($worm > 0 && in_array($kind, TaskForm::FILE_SYSTEM_KINDS, true)) {
            $lock = min(TaskForm::MAX_WORM_LOCK_HOURS, max(0, (int) ($destination['worm_lock_hours'] ?? 0)));
            return $lock + $worm * self::DAY + self::MARGIN;
        }
        $objectLock = (int) ($destination['object_lock_days'] ?? 0);
        return $kind === 's3' && $objectLock > 0 ? $objectLock * self::DAY : 0.0;
    }

    /** The average time between runs, in hours; null for a task run by hand. */
    public static function interval(array $schedule): ?float
    {
        $times = max(1, count(array_unique((array) ($schedule['times'] ?? []))));
        $count = fn(string $key, int $none) => ($days = array_unique((array) ($schedule[$key] ?? []))) === [] ? $none : count($days);
        return match ($schedule['type'] ?? '') {
            'daily' => self::WEEK / ($times * $count('days_of_week', 7)),
            'weekly' => self::WEEK / ($times * $count('days_of_week', 1)),
            'monthly' => 30 * self::DAY / ($times * $count('days_of_month', 1)),
            'interval' => (int) ($schedule['interval_minutes'] ?? 0) > 0 ? (int) $schedule['interval_minutes'] / 60 : null,
            default => null,
        };
    }

    /** The schedule runs the task more than once on some days. */
    private static function severalADay(array $schedule): bool
    {
        if (($schedule['type'] ?? '') === 'interval') {
            $minutes = (int) ($schedule['interval_minutes'] ?? 0);
            return $minutes > 0 && $minutes < 24 * 60;
        }
        return count(array_unique((array) ($schedule['times'] ?? []))) > 1;
    }

    /** How often a full backup comes, in hours; null if only the first one is full. */
    private static function fullEvery(array $task, float $run): ?float
    {
        if (($task['backup_type'] ?? '') === 'full') {
            return $run;
        }
        $force = (array) ($task['force_full'] ?? []);
        $candidates = [];
        if ((int) ($force['every_runs'] ?? 0) > 0) {
            $candidates[] = (int) $force['every_runs'] * $run;
        }
        $weekday = $force['weekday'] ?? null;
        if ($weekday !== null && (int) $weekday >= 1 && (int) $weekday <= 7) {
            $candidates[] = self::WEEK;
        }
        if (($force['month_day'] ?? null) !== null) {
            $candidates[] = self::MONTH;
        }
        return $candidates === [] ? null : min($candidates);
    }

    /** Days enough to cover the time: 7 days 4 hours need 8. */
    private static function wholeDays(float $hours): int
    {
        return (int) ceil($hours / self::DAY - 1e-9);
    }

    /** "7 дн. 4 ч", "8 дн.", "3 ч" as a message. */
    private static function duration(float $hours): array
    {
        $h = (int) round($hours);
        return $h < 24 ? ['key' => 'task_form.duration.hours', 'params' => ['hours' => $h]]
            : ($h % 24 === 0 ? ['key' => 'task_form.duration.days', 'params' => ['days' => intdiv($h, 24)]]
                : ['key' => 'task_form.duration.days_hours', 'params' => ['days' => intdiv($h, 24), 'hours' => $h % 24]]);
    }
}
