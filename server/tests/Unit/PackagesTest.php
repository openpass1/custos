<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Domain\Agents;
use Custos\Domain\Packages;
use PHPUnit\Framework\TestCase;

/** Which machine takes which package: the standard edition, the ones for Windows 7 and 8.1 and for macOS (A.10), and 7-Zip. */
final class PackagesTest extends TestCase
{
    public function testEachKindIsSignedUnderItsOwnName(): void
    {
        $sha = str_repeat('0f', 32);
        $this->assertSame("1.2.0\n$sha", Packages::signedString('agent', '1.2.0', $sha), 'unchanged since 0.3');
        $this->assertSame("legacy\n1.2.0\n$sha", Packages::signedString('legacy', '1.2.0', $sha), 'as CustosEdition.PackageSignedString');
        $this->assertSame("macos\n1.2.0\n$sha", Packages::signedString('macos', '1.2.0', $sha), 'as CustosEdition.PackageSignedString');
        $this->assertSame("7zip\n26.06\n$sha", Packages::signedString('7zip', '26.06', $sha));
    }

    public function testMachinesTakeOnlyThePackagesOfTheirEdition(): void
    {
        $standard = ['id' => 's', 'status' => 'approved', 'agent_version' => '1.0.0', 'edition' => ''];
        $legacy = ['id' => 'l', 'status' => 'approved', 'agent_version' => '1.0.0', 'edition' => 'legacy'];
        $old = ['id' => 'o', 'status' => 'approved', 'agent_version' => '0.5.1'];

        $this->assertSame(['agent', 'legacy', 'agent'], [Packages::agentKind($standard), Packages::agentKind($legacy), Packages::agentKind($old)]);
        $this->assertTrue(Agents::isLegacy($legacy));
        $this->assertFalse(Agents::isLegacy($old), 'a site row from before 1.2.0 has no edition: standard');
        $this->assertSame(['s', 'o'], array_column(Packages::fitting('agent', [$standard, $legacy, $old]), 'id'));
        $this->assertSame(['l'], array_column(Packages::fitting('legacy', [$standard, $legacy, $old]), 'id'));
        $this->assertSame(['s', 'o'], array_column(Packages::fitting('7zip', [$standard, $legacy, $old]), 'id'), 'no 7-Zip from the site on Windows 7');

        $this->assertSame('legacy', Agents::edition('legacy'));
        $this->assertSame('macos', Agents::edition('macos'));
        $this->assertSame('', Agents::edition(null));
        $this->assertSame('', Agents::edition('something-new'), 'an edition the site does not know is the standard one');
    }

    public function testAMacTakesOnlyMacPackagesAndNo7Zip(): void
    {
        $standard = ['id' => 's', 'status' => 'approved', 'agent_version' => '1.0.0', 'edition' => ''];
        $legacy = ['id' => 'l', 'status' => 'approved', 'agent_version' => '1.0.0', 'edition' => 'legacy'];
        $mac = ['id' => 'm', 'status' => 'approved', 'agent_version' => '1.0.0', 'edition' => 'macos'];
        $all = [$standard, $legacy, $mac];

        $this->assertSame('macos', Packages::agentKind($mac));
        $this->assertTrue(Agents::isMac($mac));
        $this->assertFalse(Agents::isLegacy($mac));
        $this->assertSame(['m'], array_column(Packages::fitting('macos', $all), 'id'));
        $this->assertSame(['s'], array_column(Packages::fitting('agent', $all), 'id'));
        $this->assertSame(['s'], array_column(Packages::fitting('7zip', $all), 'id'), '7-Zip is inside the program on a Mac');
        $this->assertSame(['agent', 'legacy', 'macos', 'linux'], Packages::PROGRAM_KINDS);
    }

    public function testALinuxServerTakesOnlyLinuxPackagesAndNo7Zip(): void
    {
        $standard = ['id' => 's', 'status' => 'approved', 'agent_version' => '1.0.0', 'edition' => ''];
        $mac = ['id' => 'm', 'status' => 'approved', 'agent_version' => '1.0.0', 'edition' => 'macos'];
        $linux = ['id' => 'x', 'status' => 'approved', 'agent_version' => '1.0.0', 'edition' => 'linux'];
        $all = [$standard, $mac, $linux];

        $this->assertSame('linux', Packages::agentKind($linux));
        $this->assertSame('linux', Agents::edition('linux'));
        $this->assertTrue(Agents::isLinux($linux));
        $this->assertFalse(Agents::isLinux($mac));
        $this->assertSame(['x'], array_column(Packages::fitting('linux', $all), 'id'));
        $this->assertSame(['s'], array_column(Packages::fitting('7zip', $all), 'id'), '7-Zip is inside the program on Linux');
        // Neither Windows 7 nor Linux has cloud drives: the task form leaves them out, in the words of each.
        $this->assertSame('linux', Agents::withoutCloud($linux));
        $this->assertSame('legacy', Agents::withoutCloud(['edition' => 'legacy']));
        $this->assertNull(Agents::withoutCloud($mac));
        $this->assertNull(Agents::withoutCloud($standard));
    }
}
