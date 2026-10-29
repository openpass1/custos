<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Tools\Schedule;
use PHPUnit\Framework\TestCase;

/** Slots of the fake agent's scheduler, including a daylight saving time change. */
final class ScheduleTest extends TestCase
{
    public function testDailyWeeklyMonthly(): void
    {
        $after = strtotime('2026-10-12T12:00:00Z'); // Monday
        $daily = ['type' => 'daily', 'times' => ['00:00', '13:30']];
        $this->assertSame('2026-10-12T13:30:00Z', gmdate('Y-m-d\TH:i:s\Z', Schedule::next($daily, 'UTC', $after)));

        $weekdays = ['type' => 'daily', 'times' => ['09:00'], 'days_of_week' => [6, 7]];
        $this->assertSame('2026-10-17T09:00:00Z', gmdate('Y-m-d\TH:i:s\Z', Schedule::next($weekdays, 'UTC', $after)));

        $weekly = ['type' => 'weekly', 'times' => ['01:00'], 'days_of_week' => [7]];
        $this->assertSame('2026-10-17T22:00:00Z', gmdate('Y-m-d\TH:i:s\Z', Schedule::next($weekly, 'Europe/Moscow', $after)));

        $monthly = ['type' => 'monthly', 'times' => ['02:00'], 'days_of_month' => [-1]];
        $this->assertSame('2026-10-31T02:00:00Z', gmdate('Y-m-d\TH:i:s\Z', Schedule::next($monthly, 'UTC', $after)));

        $this->assertNull(Schedule::next(['type' => 'manual'], 'UTC', $after));
    }

    public function testDaylightSavingTime(): void
    {
        // Berlin goes from UTC+2 to UTC+1 on 25.10.2026: 00:00 local is 22:00Z before and 23:00Z after.
        $daily = ['type' => 'daily', 'times' => ['00:00']];
        $this->assertSame('2026-10-24T22:00:00Z', gmdate('Y-m-d\TH:i:s\Z', Schedule::next($daily, 'Europe/Berlin', strtotime('2026-10-24T12:00:00Z'))));
        $this->assertSame('2026-10-25T23:00:00Z', gmdate('Y-m-d\TH:i:s\Z', Schedule::next($daily, 'Europe/Berlin', strtotime('2026-10-25T12:00:00Z'))));
    }

    public function testInterval(): void
    {
        $interval = ['type' => 'interval', 'interval_minutes' => 15];
        $anchor = strtotime('2026-10-12T12:00:00Z');
        $this->assertSame($anchor + 900, Schedule::next($interval, 'UTC', $anchor, $anchor));
        $this->assertSame($anchor + 1800, Schedule::next($interval, 'UTC', $anchor + 901, $anchor));
    }
}
