<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Agents;
use Custos\Domain\Install;
use Custos\Domain\RegistrationKey;
use Custos\Http\Response;
use Custos\Http\Web\Controller;

/**
 * The page "Install": how to add a machine. Linux servers get one command (Install) that installs CUSTOS from this
 * site and connects it; Windows and macOS get the installers on GitHub and the connection line to paste in them.
 */
final class InstallController extends Controller
{
    public function index(): Response
    {
        $settings = $this->app->settings();
        $key = $settings->get('registration_key');
        $url = $this->app->config->url();
        $install = $this->app->install();
        $package = $install->package();
        $releases = $this->app->releases();
        // Each system from its own newest release: a client-v release may carry the files of only some of them.
        $client = $releases->latest('client');
        $mac = $releases->latest('macos');
        $legacy = $releases->latest('legacy');
        return $this->render('install/index', [
            'section' => 'install',
            // Both come from the same key and address: without one of them there is neither (settings.connection.*).
            'commands' => $install->commands(),
            'connection' => $key === '' || $url === '' ? null
                : RegistrationKey::connection($url, RegistrationKey::code($key, $this->app->keys()->fingerprint())),
            'connectionProblem' => $key === '' ? 'settings.connection.no_key' : 'settings.connection.no_url',
            'package' => $package,
            'packageCurrent' => $package !== null && $this->app->packages()->current(Agents::LINUX) !== null,
            'archives' => $package === null ? [] : array_keys(Install::archiveSums($package)),
            'releasesPage' => 'https://github.com/' . $releases->repo . '/releases',
            'windows' => self::installer($client, '/^CUSTOS-Setup-[0-9.]+\.exe$/'),
            'mac' => self::installer($mac, '/^Custos-[0-9.]+\.pkg$/'),
            'legacy' => self::installer($legacy, '/^CUSTOS-Legacy-Setup-[0-9.]+\.exe$/'),
        ], $this->t('install.title'));
    }

    /**
     * The installer of a release found by the daily check of GitHub: [version, file name, address, prerelease], or null when the
     * check has not found one (turned off, no internet, or a release without it).
     *
     * @return ?array{0: string, 1: string, 2: string, 3: bool}
     */
    private static function installer(?array $release, string $pattern): ?array
    {
        foreach ((array) ($release['installers'] ?? []) as $name => $url) {
            if (preg_match($pattern, (string) $name)) {
                return [(string) $release['version'], (string) $name, (string) $url, !empty($release['prerelease'])];
            }
        }
        return null;
    }
}
