<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Domain\Analytics;
use Custos\Domain\Integrations;
use Custos\Domain\Storage;
use Custos\Http\Web\Controller\RunsController;
use PHPUnit\Framework\TestCase;

/** Pure parts of stage 4: the space forecast, the worst result of a day, Zabbix codes and CSV cells. */
final class AnalyticsTest extends TestCase
{
    private const GB = 1_000_000_000;

    public function testForecastNeedsEnoughPointsAndTime(): void
    {
        $t = 1_800_000_000;
        $this->assertSame('unknown', Storage::forecast([])['state']);
        $this->assertSame('unknown', Storage::forecast([[$t, 100], [$t + 90000, 90]])['state'], 'two points are not a trend');
        $this->assertSame('unknown', Storage::forecast([[$t, 100], [$t + 3600, 90], [$t + 7200, 80]])['state'], 'less than a day');
        $this->assertSame('unknown', Storage::forecast([[$t, 100], [$t, 90], [$t, 80]])['state']);
    }

    public function testForecastFillingCountsFromTheLatestFreeSpace(): void
    {
        $t = 1_800_000_000;
        // 10 GB a day is used: 100, 90, ..., 50 GB free over five days.
        $points = [];
        for ($d = 0; $d <= 5; $d++) {
            $points[] = [$t + $d * 86400, (100 - 10 * $d) * self::GB];
        }
        $f = Storage::forecast($points);
        $this->assertSame('filling', $f['state']);
        $this->assertEqualsWithDelta(-10 * self::GB, $f['slope'], 1);
        $this->assertSame(5, $f['days_left']);

        // A disk that is already full: zero days.
        $points[] = [$t + 6 * 86400, 0];
        $this->assertSame(0, Storage::forecast($points)['days_left']);

        // Very slow use is capped at ten years.
        $slow = [[$t, 4000 * self::GB], [$t + 86400, 4000 * self::GB - 2_000_000], [$t + 2 * 86400, 4000 * self::GB - 4_000_000]];
        $this->assertSame(Storage::MAX_DAYS, Storage::forecast($slow)['days_left']);
    }

    public function testForecastSteadyWhenSpaceDoesNotShrink(): void
    {
        $t = 1_800_000_000;
        $growing = [[$t, 50 * self::GB], [$t + 86400, 55 * self::GB], [$t + 2 * 86400, 60 * self::GB]];
        $this->assertSame('steady', Storage::forecast($growing)['state']);
        // Less than a megabyte a day is noise.
        $flat = [[$t, 50 * self::GB], [$t + 86400, 50 * self::GB - 500_000], [$t + 2 * 86400, 50 * self::GB - 900_000]];
        $f = Storage::forecast($flat);
        $this->assertSame('steady', $f['state']);
        $this->assertNull($f['days_left']);
    }

    public function testWorstResultOfADay(): void
    {
        $this->assertNull(Analytics::worst([]));
        $this->assertNull(Analytics::worst(['error' => 0]));
        $this->assertSame('success', Analytics::worst(['success' => 3]));
        $this->assertSame('missed_offline', Analytics::worst(['success' => 3, 'missed_offline' => 1]));
        $this->assertSame('warning', Analytics::worst(['missed_offline' => 1, 'warning' => 1]));
        $this->assertSame('missed', Analytics::worst(['warning' => 2, 'missed' => 1]));
        $this->assertSame('error', Analytics::worst(['success' => 9, 'missed' => 1, 'error' => 1]));
    }

    public function testZabbixCodes(): void
    {
        $code = fn(array $flags) => Integrations::code(['flags' => $flags]);
        $this->assertSame(0, $code(['ok']));
        $this->assertSame(9, $code(['never']));
        $this->assertSame(1, $code(['running', 'ok']));
        $this->assertSame(1, $code(['paused', 'never']));
        $this->assertSame(6, $code(['running', 'error', 'ok']), 'an error stays visible while the next run goes');
        $this->assertSame(5, $code(['missed', 'warning', 'ok']));
        $this->assertSame(4, $code(['warning', 'stale', 'ok']), 'the most serious flag wins, not the board order');
        $this->assertSame(3, $code(['anomaly', 'missed_offline', 'ok']));
        $this->assertSame(2, $code(['anomaly', 'ok']));
        $this->assertSame(9, $code(['disabled', 'error', 'ok']), 'a disabled task does not raise triggers');
    }

    public function testCsvCellsCannotStartAFormula(): void
    {
        foreach (['=SUM(A1)', '+1', '-1', '@cmd', "\tx", "\rx"] as $value) {
            $this->assertSame("'" . $value, RunsController::csvText($value));
        }
        foreach (['', 'PC-01', 'a=b', '1-2'] as $value) {
            $this->assertSame($value, RunsController::csvText($value));
        }
    }
}
