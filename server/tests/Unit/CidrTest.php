<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Infra\Cidr;
use PHPUnit\Framework\TestCase;

final class CidrTest extends TestCase
{
    public function testParseNormalizesAndRejects(): void
    {
        $this->assertSame(['192.0.2.0/24', '198.51.100.7', '2001:db8::/32'], Cidr::parseList("192.0.2.0/24, 198.51.100.7\n2001:DB8::/32 192.0.2.0/24"));
        $this->assertSame([], Cidr::parseList("  \n "));
        try {
            Cidr::parseList('192.0.2.0/33 bad 10.0.0.1');
            $this->fail('Invalid entries must be rejected');
        } catch (\InvalidArgumentException $e) {
            $this->assertSame('192.0.2.0/33, bad', $e->getMessage());
        }
    }

    public function testAllows(): void
    {
        $list = ['192.0.2.0/24', '10.1.0.0/20', '2001:db8::/32', '198.51.100.7'];
        $this->assertTrue(Cidr::allows('192.0.2.200', $list));
        $this->assertTrue(Cidr::allows('10.1.15.1', $list));
        $this->assertFalse(Cidr::allows('10.1.16.1', $list));
        $this->assertTrue(Cidr::allows('2001:db8:1::5', $list));
        $this->assertTrue(Cidr::allows('198.51.100.7', $list));
        $this->assertFalse(Cidr::allows('198.51.100.8', $list));
        $this->assertTrue(Cidr::allows('::ffff:192.0.2.9', $list), 'IPv4-mapped IPv6 addresses count as IPv4');
        $this->assertTrue(Cidr::allows('203.0.113.1', []), 'an empty list allows everything');
        $this->assertFalse(Cidr::allows('garbage', $list));
    }
}
