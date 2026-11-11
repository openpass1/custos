<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Http\Request;
use Custos\Http\Web\Session;
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
        $this->assertSame('/custos', Request::basePath('/custos/index.php'));
        $this->assertSame('', Request::basePath('/index.php'));
        $this->assertSame('', Request::basePath('/updates/agent/1.0.1'), 'php -S takes a path with a dot for a script');
        $this->assertSame('/login', (new Request('GET', '/login'))->path());
    }

    public function testHttpsDetection(): void
    {
        $this->assertFalse(Request::serverHttps([]));
        $this->assertTrue(Request::serverHttps(['HTTPS' => 'on']));
        $this->assertFalse(Request::serverHttps(['HTTPS' => 'off']));
        $this->assertTrue(Request::serverHttps(['SERVER_PORT' => '443']));
        $this->assertFalse(
            Request::serverHttps(['REQUEST_SCHEME' => 'http', 'HTTPS' => 'on', 'SERVER_PORT' => '443']),
            'the real scheme wins over a hard-coded HTTPS=on'
        );
        $this->assertTrue(Request::serverHttps(['REQUEST_SCHEME' => 'https']));
    }

    /** A secure cookie over plain HTTP is dropped by the browser, and every form then fails the CSRF check. */
    public function testSessionCookieIsSecureOnlyForAnHttpsSite(): void
    {
        $https = new Request('GET', '/login', https: true);
        $this->assertTrue(Session::secureCookie($https, 'https://custos.example'));
        $this->assertFalse(Session::secureCookie($https, 'http://custos.lan'));
        $this->assertFalse(Session::secureCookie(new Request('GET', '/login'), 'https://custos.example'));
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
