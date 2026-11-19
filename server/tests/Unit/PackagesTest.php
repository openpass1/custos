<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Domain\Agents;
use Custos\Domain\Packages;
use PHPUnit\Framework\TestCase;

/** Which machine takes which package: the standard edition, the one for Windows 7 and 8.1 (A.10), and 7-Zip. */
final class PackagesTest extends TestCase
{
    public function testEachKindIsSignedUnderItsOwnName(): void
    {
        $sha = str_repeat('0f', 32);
        $this->assertSame("1.2.0\n$sha", Packages::signedString('agent', '1.2.0', $sha), 'unchanged since 0.3');
        $this->assertSame("legacy\n1.2.0\n$sha", Packages::signedString('legacy', '1.2.0', $sha), 'as CustosEdition.PackageSignedString');
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
        $this->assertSame('', Agents::edition(null));
        $this->assertSame('', Agents::edition('something-new'), 'an edition the site does not know is the standard one');
    }
}
