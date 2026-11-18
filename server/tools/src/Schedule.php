<?php

declare(strict_types=1);

namespace Custos\Tools;

use DateTimeImmutable;
use DateTimeZone;

/**
 * Slots of a task schedule (A.9) in the agent's time zone, as the fake agent runs them. Daylight saving time as on
 * the Windows agent: a time skipped by the change runs right after it (30-minute steps), a time that happens twice
 * runs the first time.
 */
final class Schedule
{
    /**
     * The first slot strictly after $after (unix time), or null when the schedule has none.
     *
     * @param ?int $anchor for "interval" schedules: when the interval started counting (the last run)
     */
    public static function next(array $schedule, string $timeZone, int $after, ?int $anchor = null): ?int
    {
        $type = $schedule['type'] ?? 'manual';
        if ($type === 'interval') {
            $step = max(1, (int) ($schedule['interval_minutes'] ?? 0)) * 60;
            $anchor ??= $after;
            return $anchor + (intdiv(max(0, $after - $anchor), $step) + 1) * $step;
        }
        if (!in_array($type, ['daily', 'weekly', 'monthly'], true)) {
            return null;
        }
        $times = array_values(array_filter((array) ($schedule['times'] ?? []), fn($t) => is_string($t) && preg_match('/^\d{2}:\d{2}$/', $t)));
        sort($times);
        if ($times === []) {
            return null;
        }
        $weekdays = array_map('intval', (array) ($schedule['days_of_week'] ?? []));
        $monthDays = array_map('intval', (array) ($schedule['days_of_month'] ?? []));
        $zone = new DateTimeZone($timeZone);
        $day = (new DateTimeImmutable('@' . $after))->setTimezone($zone)->setTime(0, 0)->modify('-1 day');
        for ($i = 0; $i < 401; $i++, $day = $day->modify('+1 day')) {
            if (!self::dayMatches($type, $day, $weekdays, $monthDays)) {
                continue;
            }
            foreach ($times as $time) {
                $slot = self::wallClock($day->format('Y-m-d') . ' ' . $time, $zone);
                if ($slot > $after) {
                    return $slot;
                }
            }
        }
        return null;
    }

    /** "Y-m-d H:i" in $zone as unix time. */
    private static function wallClock(string $local, DateTimeZone $zone): int
    {
        $utc = new DateTimeZone('UTC');
        for ($i = 0; $i < 8; $i++) {
            $time = (new DateTimeImmutable($local, $zone))->getTimestamp();
            if ((new DateTimeImmutable('@' . $time))->setTimezone($zone)->format('Y-m-d H:i') === $local) {
                // The same wall-clock time may have happened earlier too (fall back): take the first one.
                foreach ([7200, 3600, 1800] as $back) {
                    if ((new DateTimeImmutable('@' . ($time - $back)))->setTimezone($zone)->format('Y-m-d H:i') === $local) {
                        return $time - $back;
                    }
                }
                return $time;
            }
            // Skipped by spring forward.
            $local = (new DateTimeImmutable($local, $utc))->modify('+30 minutes')->format('Y-m-d H:i');
        }
        return (new DateTimeImmutable($local, $zone))->getTimestamp();
    }

    private static function dayMatches(string $type, DateTimeImmutable $day, array $weekdays, array $monthDays): bool
    {
        $weekday = (int) $day->format('N');
        return match ($type) {
            'daily' => $weekdays === [] || in_array($weekday, $weekdays, true),
            'weekly' => in_array($weekday, $weekdays, true),
            // A month without the chosen day runs on its last day, as on the Windows agent.
            'monthly' => in_array((int) $day->format('j'), array_map(
                fn(int $d) => $d === -1 ? (int) $day->format('t') : min($d, (int) $day->format('t')),
                $monthDays
            ), true),
        };
    }
}
