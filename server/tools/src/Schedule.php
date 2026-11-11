<?php

declare(strict_types=1);

namespace Custos\Tools;

use DateTimeImmutable;
use DateTimeZone;

/**
 * Slots of a task schedule (A.9) in the agent's time zone, as the fake agent runs them.
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
        $day = (new DateTimeImmutable('@' . $after))->setTimezone($zone)->setTime(0, 0);
        for ($i = 0; $i < 400; $i++, $day = $day->modify('+1 day')) {
            if (!self::dayMatches($type, $day, $weekdays, $monthDays)) {
                continue;
            }
            foreach ($times as $time) {
                $slot = new DateTimeImmutable($day->format('Y-m-d') . ' ' . $time, $zone);
                if ($slot->getTimestamp() > $after) {
                    return $slot->getTimestamp();
                }
            }
        }
        return null;
    }

    private static function dayMatches(string $type, DateTimeImmutable $day, array $weekdays, array $monthDays): bool
    {
        $weekday = (int) $day->format('N');
        return match ($type) {
            'daily' => $weekdays === [] || in_array($weekday, $weekdays, true),
            'weekly' => in_array($weekday, $weekdays, true),
            'monthly' => in_array((int) $day->format('j'), $monthDays, true)
                || (in_array(-1, $monthDays, true) && $day->format('j') === $day->format('t')),
        };
    }
}
