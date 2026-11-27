<?php

declare(strict_types=1);

namespace Custos\Tests\Integration;

use Custos\Domain\Releases;
use Custos\Tests\TestEnv;
use PHPUnit\Framework\TestCase;

final class ReleaseChannelTest extends TestCase
{
    public function testChannelCachesAndNoticesAcrossChecksAndSwitches(): void
    {
        $app = TestEnv::app($this);
        $asset = static fn(string $name): array => ['name' => $name, 'browser_download_url' => "https://example.test/$name"];
        $stable = ['tag_name' => 'server-v9.9.0', 'assets' => [$asset('custos-server-9.9.0.zip')]];
        $preview = ['tag_name' => 'server-v9.10.0', 'prerelease' => true, 'assets' => [$asset('custos-server-9.10.0.zip')]];
        $lowerLinuxPreview = ['tag_name' => 'client-v1.3.0', 'prerelease' => true, 'assets' => [$asset('custos-linux-1.3.0.zip')]];
        $higherLinuxStable = ['tag_name' => 'client-v1.4.0', 'assets' => [$asset('custos-linux-1.4.0.zip')]];
        $response = [200, json_encode([$stable, $preview, $lowerLinuxPreview, $higherLinuxStable]), ''];
        $app->setHttp(static function () use (&$response): array {
            return $response;
        });
        $releases = $app->releases();
        $now = time();

        // A pre-existing stable cache row has no prerelease field, and an unknown setting falls back to Stable.
        $app->settings()->set('release_server', '{"version":"9.9.0"}');
        $app->settings()->set('releases_channel', 'unknown');
        $this->assertSame(Releases::STABLE, $releases->channel());
        $this->assertSame('9.9.0', $releases->latest('server')['version']);
        $this->assertTrue($releases->check($now));
        $this->assertSame(['site' => '9.9.0'], $releases->pass($now));

        $releases->setChannel(Releases::PREVIEW);
        $this->assertTrue($releases->latest('server')['prerelease']);
        $this->assertSame('1.4.0', $releases->latest('linux')['version']);
        $this->assertSame(['site' => '9.10.0'], $releases->pass($now));
        $response = [503, '', 'offline'];
        $this->assertFalse($releases->check($now));
        $this->assertSame('9.10.0', $releases->latest('server')['version']);
        $releases->setChannel(Releases::STABLE);
        $this->assertSame('9.9.0', $releases->latest('server')['version']);
        $this->assertSame([], $releases->pass($now));
        $this->assertSame(1, $app->notices()->unreadCount());

        $releases->setChannel(Releases::PREVIEW);
        $this->assertSame([], $releases->pass($now), 'toggling does not repeat a notice');
        $response = [200, json_encode([$stable]), ''];
        $this->assertTrue($releases->check($now));
        $this->assertSame('9.9.0', $releases->latest('server')['version'], 'a withdrawn preview no longer shadows stable');

        $preview['prerelease'] = false;
        $response = [200, json_encode([$stable, $preview]), ''];
        $this->assertTrue($releases->check($now));
        $releases->setChannel(Releases::STABLE);
        $this->assertSame('9.10.0', $releases->latest('server')['version']);
        $this->assertFalse($releases->latest('server')['prerelease'], 'promotion changes the identity of the same tag');
    }
}
