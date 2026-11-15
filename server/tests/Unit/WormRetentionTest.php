<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Domain\TaskForm;
use Custos\Domain\WormRetention;
use PHPUnit\Framework\TestCase;

/** The same cases as the program's WormRetentionTests: WORM for 7 days, locked 3 hours after writing = 7 days 4 hours. */
final class WormRetentionTest extends TestCase
{
    private static function task(string $type = 'full', array $schedule = []): array
    {
        return [
            'backup_type' => $type,
            'force_full' => ['every_runs' => 0, 'weekday' => null, 'month_day' => null],
            'destinations' => [['kind' => 'unc', 'path' => '\\\\nas\\backup', 'worm_days' => 7, 'worm_lock_hours' => 3]],
            'schedule' => array_replace(['type' => 'daily', 'times' => ['00:00'], 'days_of_week' => [1, 2, 3, 4, 5, 6, 7],
                                         'days_of_month' => [], 'interval_minutes' => 0], $schedule),
            'retention' => ['mode' => 'count', 'keep_full' => 3, 'max_age_days' => 0, 'gfs' => ['daily' => 7, 'weekly' => 4, 'monthly' => 12]],
        ];
    }

    private static function problem(array $task): ?array
    {
        return WormRetention::problem($task, fn(int $n) => ['key' => 'task_form.destination', 'params' => ['n' => $n]]);
    }

    public function testTooFewFullBackupsAreRefusedWithTheNumberThatFits(): void
    {
        $task = self::task();
        $problem = self::problem($task);
        $this->assertSame('task_form.error.worm_retention', $problem['key']);
        $this->assertSame(['key' => 'task_form.destination', 'params' => ['n' => 1]], $problem['params']['what']);
        $this->assertSame(7, $problem['params']['days']);
        $this->assertSame(['key' => 'task_form.duration.days_hours', 'params' => ['days' => 7, 'hours' => 4]], $problem['params']['protection']);
        $this->assertSame(['key' => 'task_form.duration.days', 'params' => ['days' => 3]], $problem['params']['youngest']);
        $this->assertSame(['key' => 'task_form.worm_fix.count', 'params' => ['count' => 8]], $problem['params']['fix']);

        $task['retention']['keep_full'] = 8;
        $this->assertNull(self::problem($task));
    }

    public function testIncrementalChainsCountByTheirFullBackups(): void
    {
        $task = self::task('incremental');
        $task['force_full']['weekday'] = 7;
        $task['retention']['keep_full'] = 2;
        $this->assertNull(self::problem($task));
        $task['retention']['keep_full'] = 1;
        $this->assertSame(['count' => 2], self::problem($task)['params']['fix']['params']);
        $task['force_full']['weekday'] = null;
        $this->assertNull(self::problem($task), 'only the first backup is full: nothing is ever deleted');
    }

    public function testAgeAndGfs(): void
    {
        $task = self::task('incremental', ['type' => 'interval', 'interval_minutes' => 60]);
        $task['retention'] = ['mode' => 'age', 'max_age_days' => 7] + $task['retention'];
        $this->assertSame(['key' => 'task_form.worm_fix.age', 'params' => ['days' => 8]], self::problem($task)['params']['fix']);
        $task['retention']['max_age_days'] = 8;
        $this->assertNull(self::problem($task));

        // Incremental backups of a day are needed by its last one; differential ones go at the next run.
        $task['retention']['mode'] = 'gfs';
        $task['retention']['gfs']['daily'] = 8;
        $this->assertSame(['key' => 'task_form.worm_fix.gfs', 'params' => ['days' => 9]], self::problem($task)['params']['fix']);
        $task['retention']['gfs']['daily'] = 9;
        $this->assertNull(self::problem($task));
        $task['backup_type'] = 'differential';
        $problem = self::problem($task);
        $this->assertSame(['key' => 'task_form.duration.hours', 'params' => ['hours' => 1]], $problem['params']['youngest']);
        $this->assertSame(['key' => 'task_form.worm_fix.gfs_day', 'params' => ['days' => 8]], $problem['params']['fix']);
    }

    public function testS3ObjectLockMirrorAndManualTasks(): void
    {
        $task = self::task();
        $task['destinations'] = [['kind' => 's3', 'bucket' => 'office-backup', 'object_lock_days' => 30]];
        $task['retention']['keep_full'] = 14;
        $problem = self::problem($task);
        $this->assertSame('task_form.error.lock_retention', $problem['key']);
        $this->assertSame(30, $problem['params']['days']);
        $this->assertSame(['count' => 30], $problem['params']['fix']['params']);
        $task['retention']['keep_full'] = 30;
        $this->assertNull(self::problem($task));

        $task['backup_type'] = 'mirror';
        $task['retention']['keep_full'] = 1;
        $this->assertNull(self::problem($task));

        // By hand only: the age rule does not depend on the schedule, the count does.
        $task = self::task('full', ['type' => 'manual']);
        $task['retention']['keep_full'] = 1;
        $this->assertNull(self::problem($task));
        $task['retention'] = ['mode' => 'age', 'max_age_days' => 3] + $task['retention'];
        $this->assertNotNull(self::problem($task));

        // WORM stated on a server is not WORM.
        $task['destinations'][0]['kind'] = 'sftp';
        $this->assertNull(self::problem($task));
        $this->assertSame(0.0, WormRetention::protection(['kind' => 'sftp', 'worm_days' => 7]));
        $this->assertSame(7 * 24 + TaskForm::MAX_WORM_LOCK_HOURS + 1.0, WormRetention::protection(['kind' => 'usb', 'worm_days' => 7, 'worm_lock_hours' => 1000]));
    }

    public function testIntervals(): void
    {
        $this->assertSame(24.0, WormRetention::interval(['type' => 'daily', 'times' => ['00:00'], 'days_of_week' => [1, 2, 3, 4, 5, 6, 7]]));
        $this->assertSame(12.0, WormRetention::interval(['type' => 'daily', 'times' => ['08:00', '20:00'], 'days_of_week' => [1, 2, 3, 4, 5, 6, 7]]));
        $this->assertSame(168.0, WormRetention::interval(['type' => 'weekly', 'times' => ['21:00'], 'days_of_week' => [5]]));
        $this->assertSame(360.0, WormRetention::interval(['type' => 'monthly', 'times' => ['23:00'], 'days_of_month' => [1, 15]]));
        $this->assertSame(1.5, WormRetention::interval(['type' => 'interval', 'interval_minutes' => 90]));
        $this->assertNull(WormRetention::interval(['type' => 'manual']));
    }
}
