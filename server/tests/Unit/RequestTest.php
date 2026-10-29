<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Http\Request;
use Custos\Infra\Migrator;
use Custos\Infra\Time;
use PHPUnit\Framework\TestCase;

final class RequestTest extends TestCase
{
    /** A.2: PATH is everything after the first "/api/v1/", so a subfolder or a proxy prefix does not matter. */
    public function testApiPath(): void
    {
        $this->assertSame('agent/heartbeat', (new Request('POST', '/api/v1/agent/heartbeat'))->apiPath());
        $this->assertSame('agent/updates/1.1.0?x=1', (new Request('GET', '/custos/api/v1/agent/updates/1.1.0?x=1', basePath: '/custos'))->apiPath());
        $this->assertSame('agent/commands/abc/result', (new Request('POST', '/proxy/prefix/api/v1/agent/commands/abc/result'))->apiPath());
        $this->assertNull((new Request('GET', '/agents'))->apiPath());
    }

    public function testPathIsRelativeToBase(): void
    {
        $this->assertSame('/agents', (new Request('GET', '/custos/agents?x=1', basePath: '/custos'))->path());
        $this->assertSame('/', (new Request('GET', '/custos/', basePath: '/custos'))->path());
        $this->assertSame('/login', (new Request('GET', '/login'))->path());
    }

    public function testBodyLimit(): void
    {
        $request = new Request('POST', '/api/v1/agent/heartbeat', ['content-length' => '100'], body: str_repeat('x', 100));
        $this->assertNull($request->body(99));
        $this->assertSame(100, strlen((string) $request->body(100)));
    }

    public function testIsoTimes(): void
    {
        $this->assertSame('2026-10-12 21:00:00', Time::parseIso('2026-10-12T21:00:00Z'));
        $this->assertSame('2026-10-12 21:00:00', Time::parseIso('2026-10-12T21:00:00.1234567Z'), '.NET writes 7 fraction digits');
        $this->assertSame('2026-10-12 18:00:00', Time::parseIso('2026-10-12T21:00:00+03:00'));
        $this->assertNull(Time::parseIso('2026-10-12 21:00:00'));
        $this->assertNull(Time::parseIso('2026-13-45T21:00:00Z'));
        $this->assertSame('2026-10-12T21:00:00Z', Time::iso('2026-10-12 21:00:00'));
    }

    public function testMigrationSplitting(): void
    {
        $sql = "-- comment; not a statement\nCREATE TABLE a (x INT);\n\nCREATE TABLE b (\n  y VARCHAR(5) DEFAULT 'a'\n);";
        $this->assertSame(['CREATE TABLE a (x INT)', "CREATE TABLE b (\n  y VARCHAR(5) DEFAULT 'a'\n)"], Migrator::statements($sql));
    }
}
