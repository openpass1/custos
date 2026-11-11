<?php

declare(strict_types=1);

namespace Custos\Domain;

use DateTimeImmutable;
use DateTimeZone;

/**
 * Expected start times of a task schedule (A.9) in the agent's time zone (6.2).
 *
 * The fake agent has its own copy in tools/src/Schedule.php on purpose: it plays the agent, and two independent
 * implementations catch each other's mistakes. Daylight saving time follows the Windows agent (ScheduleCalculator):
 * a local time that does not exist (spring forward) runs right after the change, looked for in 30-minute steps;
 * a time that happens twice (fall back) runs the first time.
 */
final class Schedule
{
    /** Schedules with fixed start times. "interval" has no slots, it is checked by the time since the last run. */
    public const SLOT_TYPES = ['daily', 'weekly', 'monthly'];

    public static function hasSlots(mixed $schedule): bool
    {
        return is_array($schedule) && in_array($schedule['type'] ?? '', self::SLOT_TYPES, true) && self::times($schedule) !== [];
    }

    /** Interval length in seconds for a timer task, null for any other schedule. */
    public static function interval(mixed $schedule): ?int
    {
        if (!is_array($schedule) || ($schedule['type'] ?? '') !== 'interval') {
            return null;
        }
        $minutes = $schedule['interval_minutes'] ?? 0;
        return is_int($minutes) && $minutes > 0 ? $minutes * 60 : null;
    }

    /** True when the server expects runs of this schedule at all (not manual, not empty). */
    public static function isExpected(mixed $schedule): bool
    {
        return self::hasSlots($schedule) || self::interval($schedule) !== null;
    }

    /**
     * The first slot strictly after $after (unix time), or null.
     */
    public static function next(array $schedule, string $timeZone, int $after): ?int
    {
        foreach (self::iterate($schedule, $timeZone, $after) as $slot) {
            return $slot;
        }
        return null;
    }

    /**
     * Slots in ($from; $to], oldest first, at most $limit of them.
     *
     * @return list<int>
     */
    public static function between(array $schedule, string $timeZone, int $from, int $to, int $limit = 1000): array
    {
        $slots = [];
        foreach (self::iterate($schedule, $timeZone, $from) as $slot) {
            if ($slot > $to || count($slots) >= $limit) {
                break;
            }
            $slots[] = $slot;
        }
        return $slots;
    }

    /** @return \Generator<int> slots after $after in ascending order, for at most 400 days ahead */
    private static function iterate(array $schedule, string $timeZone, int $after): \Generator
    {
        if (!self::hasSlots($schedule)) {
            return;
        }
        $type = $schedule['type'];
        $times = self::times($schedule);
        $weekdays = array_values(array_filter((array) ($schedule['days_of_week'] ?? []), 'is_int'));
        $monthDays = array_values(array_filter((array) ($schedule['days_of_month'] ?? []), 'is_int'));
        try {
            $zone = new DateTimeZone($timeZone);
        } catch (\Exception) {
            $zone = new DateTimeZone('UTC');
        }
        // From the day before, like the agent: a time shifted by a DST change may cross midnight.
        $day = (new DateTimeImmutable('@' . $after))->setTimezone($zone)->setTime(0, 0)->modify('-1 day');
        $last = $after;
        for ($i = 0; $i < 401; $i++, $day = $day->modify('+1 day')) {
            if (!self::dayMatches($type, $day, $weekdays, $monthDays)) {
                continue;
            }
            foreach ($times as [$hour, $minute]) {
                $slot = self::toUtc($day->format('Y-m-d'), $hour * 60 + $minute, $zone);
                // "> $last" also drops a repeated slot when two local times map to the same moment around a DST change.
                if ($slot > $last) {
                    $last = $slot;
                    yield $slot;
                }
            }
        }
    }

    /**
     * Unix time of a wall-clock time ($minutes after midnight of $date) in $zone. A time inside the gap of a spring
     * forward moves on in 30-minute steps until it exists; of two moments with the same wall-clock time the earlier
     * one is taken.
     */
    public static function toUtc(string $date, int $minutes, DateTimeZone $zone): int
    {
        $midnight = (new DateTimeImmutable($date, new DateTimeZone('UTC')))->getTimestamp();
        $offsets = [];
        foreach ($zone->getTransitions($midnight - 2 * 86400, $midnight + 3 * 86400) ?: [] as $transition) {
            $offsets[(int) $transition['offset']] = true;
        }
        for ($step = 0; $step <= 8; $step++) {
            $wall = $midnight + ($minutes + 30 * $step) * 60;
            $best = null;
            foreach (array_keys($offsets) as $offset) {
                $utc = $wall - $offset;
                if ($zone->getOffset(new DateTimeImmutable('@' . $utc)) === $offset && ($best === null || $utc < $best)) {
                    $best = $utc;
                }
            }
            if ($best !== null) {
                return $best;
            }
        }
        $wall = $midnight + $minutes * 60;
        return $wall - $zone->getOffset(new DateTimeImmutable('@' . $wall));
    }

    /** @return list<array{0: int, 1: int}> valid "HH:MM" times as [hour, minute], sorted */
    private static function times(array $schedule): array
    {
        $times = [];
        foreach ((array) ($schedule['times'] ?? []) as $time) {
            if (is_string($time) && preg_match('/^([01]\d|2[0-3]):([0-5]\d)$/', $time, $m)) {
                $times[$time] = [(int) $m[1], (int) $m[2]];
            }
        }
        ksort($times);
        return array_values($times);
    }

    private static function dayMatches(string $type, DateTimeImmutable $day, array $weekdays, array $monthDays): bool
    {
        $weekday = (int) $day->format('N');
        return match ($type) {
            'daily' => $weekdays === [] || in_array($weekday, $weekdays, true),
            'weekly' => in_array($weekday, $weekdays, true),
            'monthly' => in_array((int) $day->format('j'), $monthDays, true)
                || (in_array(-1, $monthDays, true) && $day->format('j') === $day->format('t')),
            default => false,
        };
    }
}
