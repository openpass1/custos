<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Domain\Input;
use Custos\Domain\Snapshots;
use PHPUnit\Framework\TestCase;

/** The field "snapshots" of an inventory of CUSTOS for Linux (A.8), as the site reads and shows it. */
final class SnapshotsTest extends TestCase
{
    private const TASK = '6f9619ff-8b86-4011-b42d-00c04fc964ff';

    public function testReadsTheFieldOfTheProtocol(): void
    {
        $this->assertNull(Snapshots::read(Input::fromJson('{"destinations": []}')), 'Windows and macOS do not send it');
        $this->assertNull(Snapshots::read(Input::fromJson('{"destinations": [], "snapshots": null}')));
        $this->assertSame([], Snapshots::read(Input::fromJson('{"destinations": [], "snapshots": []}')), 'no task with snapshots on');

        $volumes = Snapshots::read(Input::fromJson(json_encode(['destinations' => [], 'snapshots' => [[
            'mount' => '/', 'device' => '/dev/mapper/ubuntu--vg-ubuntu--lv', 'file_system' => 'ext4',
            'method' => 'lvm', 'state' => 'no_space', 'volume_group' => 'ubuntu-vg', 'logical_volume' => 'ubuntu-lv',
            'free_bytes' => 0, 'volume_bytes' => 107374182400,
            'last_size_bytes' => null, 'last_used_bytes' => null, 'last_run_at' => null,
            'detail' => 'No room in ubuntu-vg', 'task_ids' => [self::TASK],
        ], [
            // A newer agent may leave out null fields and send a state the site does not know.
            'mount' => '/srv', 'method' => 'btrfs', 'state' => 'something_new', 'task_ids' => [],
        ]]])));
        $this->assertCount(2, $volumes);
        $this->assertSame(['/', 'lvm', 'no_space', 'ubuntu-vg', 0, [self::TASK]],
            [$volumes[0]['mount'], $volumes[0]['method'], $volumes[0]['state'], $volumes[0]['volume_group'], $volumes[0]['free_bytes'], $volumes[0]['task_ids']]);
        $this->assertSame(['/srv', 'something_new', null, null], [$volumes[1]['mount'], $volumes[1]['state'], $volumes[1]['volume_group'], $volumes[1]['last_run_at']]);
        $this->assertSame($volumes, Snapshots::of(['snapshots' => json_encode($volumes)]));
        $this->assertNull(Snapshots::of(['snapshots' => null]), 'never sent');
    }

    public function testProblemMessageFixAndColours(): void
    {
        $message = Snapshots::message(['state' => 'overflowed', 'mount' => '/var', 'volume_group' => 'data-vg']);
        $this->assertSame(['state' => 'overflowed', 'mount' => '/var', 'group' => 'data-vg'], Snapshots::problem($message));
        $this->assertSame(['state' => '', 'mount' => 'plain text', 'group' => null], Snapshots::problem('plain text'));
        $this->assertSame('pvcreate /dev/sdX && vgextend data-vg /dev/sdX', Snapshots::fixCommand('data-vg'));
        $this->assertSame('pvcreate /dev/sdX && vgextend <vg> /dev/sdX', Snapshots::fixCommand(null));
        $this->assertSame(['ok', 'warning', 'warning', 'error', 'error', 'muted'],
            array_map(Snapshots::status(...), ['ok', 'low_space', 'unsupported', 'no_space', 'overflowed', 'new']));
    }
}
