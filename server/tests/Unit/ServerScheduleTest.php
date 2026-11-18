<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Domain\Schedule;
use Custos\Tools\Schedule as AgentSchedule;
use PHPUnit\Framework\TestCase;

/** Expected slots on the server side (6.2), checked against the fake agent's own scheduler. */
final class ServerScheduleTest extends TestCase
{
    private static function iso(?int $time): ?string
    {
        return $time === null ? null : gmdate('Y-m-d\TH:i:s\Z', $time);
    }

    public function testKindsOfSchedules(): void
    {
        $this->assertTrue(Schedule::hasSlots(['type' => 'daily', 'times' => ['09:00']]));
        $this->assertFalse(Schedule::hasSlots(['type' => 'daily', 'times' => []]));
        $this->assertFalse(Schedule::hasSlots(['type' => 'interval', 'interval_minutes' => 30]));
        $this->assertSame(1800, Schedule::interval(['type' => 'interval', 'interval_minutes' => 30]));
        $this->assertNull(Schedule::interval(['type' => 'daily', 'times' => ['09:00']]));
        $this->assertTrue(Schedule::isExpected(['type' => 'interval', 'interval_minutes' => 30]));
        $this->assertFalse(Schedule::isExpected(['type' => 'manual']));
        $this->assertFalse(Schedule::isExpected(null));
    }

    public function testSlotsBetween(): void
    {
        $daily = ['type' => 'daily', 'times' => ['01:00', '13:00']];
        $from = strtotime('2026-10-12T00:00:00Z');
        $slots = array_map(self::iso(...), Schedule::between($daily, 'UTC', $from, $from + 2 * 86400));
        $this->assertSame(['2026-10-12T01:00:00Z', '2026-10-12T13:00:00Z', '2026-10-13T01:00:00Z', '2026-10-13T13:00:00Z'], $slots);
        $this->assertCount(2, Schedule::between($daily, 'UTC', $from, $from + 2 * 86400, 2));
        $this->assertSame([], Schedule::between($daily, 'UTC', $from + 3600, $from + 3600), 'the range is ($from; $to]');
        $this->assertSame(['2026-10-12T01:00:00Z'], array_map(self::iso(...), Schedule::between($daily, 'UTC', $from + 3599, $from + 3600)));
    }

    public function testDaylightSavingTime(): void
    {
        // As on the Windows agent. Spring forward in Berlin on 29.03.2026: 02:30 does not exist and runs right after
        // the change, at 03:00 local (01:00Z).
        $night = ['type' => 'daily', 'times' => ['02:30']];
        $this->assertSame('2026-03-29T01:00:00Z', self::iso(Schedule::next($night, 'Europe/Berlin', strtotime('2026-03-28T12:00:00Z'))));
        // Fall back on 25.10.2026: 02:30 happens twice and runs the first time (00:30Z, still summer time).
        $slots = Schedule::between($night, 'Europe/Berlin', strtotime('2026-10-24T12:00:00Z'), strtotime('2026-10-26T12:00:00Z'));
        $this->assertSame(['2026-10-25T00:30:00Z', '2026-10-26T01:30:00Z'], array_map(self::iso(...), $slots));
        // Two times in the gap become one slot.
        $both = ['type' => 'daily', 'times' => ['02:00', '02:30']];
        $this->assertSame(['2026-03-29T01:00:00Z'], array_map(self::iso(...),
            Schedule::between($both, 'Europe/Berlin', strtotime('2026-03-28T12:00:00Z'), strtotime('2026-03-29T12:00:00Z'))));
        // Midnight before and after the change.
        $midnight = ['type' => 'daily', 'times' => ['00:00']];
        $this->assertSame('2026-10-24T22:00:00Z', self::iso(Schedule::next($midnight, 'Europe/Berlin', strtotime('2026-10-24T12:00:00Z'))));
        $this->assertSame('2026-10-25T23:00:00Z', self::iso(Schedule::next($midnight, 'Europe/Berlin', strtotime('2026-10-25T12:00:00Z'))));
    }

    /** The server and the fake agent must agree on every slot, or the server would report missed runs that ran. */
    public function testAgreesWithTheAgentScheduler(): void
    {
        $schedules = [
            ['type' => 'daily', 'times' => ['00:00', '12:15', '23:59']],
            ['type' => 'daily', 'times' => ['09:00'], 'days_of_week' => [1, 2, 3, 4, 5]],
            ['type' => 'weekly', 'times' => ['01:00', '02:30'], 'days_of_week' => [6, 7]],
            ['type' => 'monthly', 'times' => ['03:00'], 'days_of_month' => [1, 15, -1]],
            ['type' => 'monthly', 'times' => ['22:00'], 'days_of_month' => [31]],
            ['type' => 'monthly', 'times' => ['04:00'], 'days_of_month' => [29, 30, -1]],
            ['type' => 'daily', 'times' => ['00:00', '00:30', '01:00', '02:00', '02:15', '02:30', '03:00']],
        ];
        $zones = ['UTC', 'Europe/Moscow', 'Europe/Berlin', 'America/New_York', 'Asia/Kolkata', 'Australia/Lord_Howe', 'America/Santiago'];
        $start = strtotime('2026-01-01T00:00:00Z');
        foreach ($schedules as $schedule) {
            foreach ($zones as $zone) {
                $server = Schedule::between($schedule, $zone, $start, $start + 300 * 86400, 5000);
                $agent = [];
                for ($t = $start, $n = 0; $n < 5000 && ($t = AgentSchedule::next($schedule, $zone, $t)) !== null && $t <= $start + 300 * 86400; $n++) {
                    $agent[] = $t;
                }
                $this->assertSame(array_map(self::iso(...), $agent), array_map(self::iso(...), $server), json_encode($schedule) . " in $zone");
            }
        }
    }
}
