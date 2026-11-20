<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\App;
use Custos\Infra\Time;

/**
 * Releases of CUSTOS on GitHub (section 8). Once a day one request reads the list of releases of the repository and
 * remembers the newest published one of each part, chosen by version number by its tag prefix (never GitHub's
 * "latest" mark, which mixes the parts):
 *   server  tag server-vX.Y.Z, package custos-server-X.Y.Z.zip: the site updates itself from it (SiteUpdate);
 *   client  tag client-vX.Y.Z, package custos-X.Y.Z.zip: the agent update package (A.10), which the site downloads
 *           on request, checks and signs like an uploaded one;
 *   legacy  tag client-legacy-vX.Y.Z, package custos-legacy-X.Y.Z.zip: the same for the edition for Windows 7 and
 *           8.1 (its packages are of kind legacy). Shown and noticed only while the site has any of that edition;
 *   macos   tag client-vX.Y.Z (the same releases as the program for Windows), package custos-macos-X.Y.Z.zip: the
 *           same for CUSTOS for macOS (kind macos), likewise only while the site has any of it;
 *   linux   tag client-vX.Y.Z as well, package custos-linux-X.Y.Z.zip: the same for CUSTOS for Linux (kind linux),
 *           likewise only while the site has any of it.
 * A bell notice tells once per version when the site is older, or when the program is newer than every agent
 * package of its edition on the server. Drafts and pre-releases are skipped. The check can be turned off (server
 * without internet).
 *
 * Settings: releases_check ('0' = off), releases_checked_at, releases_check_error, release_server, release_client,
 * release_legacy, release_macos and release_linux (JSON of the release found, see pick()), release_*_notified
 * (versions noticed).
 */
final class Releases
{
    public const CHECK_EVERY_S = 86400;
    public const DEFAULT_REPO = 'openpass1/custos';
    public const PARTS = ['server', 'client', 'legacy', 'macos', 'linux'];
    /** The program parts: the standard edition (client) and the others, named like their package kind. */
    public const PROGRAM_PARTS = ['client', 'legacy', 'macos', 'linux'];
    /** The tag of a release of each part is <prefix>X.Y.Z, its package <zip>X.Y.Z.zip. */
    private const TAGS = ['server' => 'server-v', 'client' => 'client-v', 'legacy' => 'client-legacy-v', 'macos' => 'client-v', 'linux' => 'client-v'];
    private const ZIPS = ['server' => 'custos-server-', 'client' => 'custos-', 'legacy' => 'custos-legacy-', 'macos' => 'custos-macos-',
                          'linux' => 'custos-linux-'];
    /** Far above the size of the program's update packages (the one for Linux holds the program twice, about 80 MB). */
    private const MAX_CLIENT_BYTES = 200 * 1024 * 1024;

    /** @param \Closure(string, int): array{0: int, 1: string, 2: string} $http */
    public function __construct(
        private readonly Settings $settings,
        private readonly Notices $notices,
        private readonly Packages $packages,
        private readonly string $apiUrl,
        public readonly string $repo,
        private readonly \Closure $http,
    ) {
    }

    /** One cron pass: the daily check and the notices. */
    public function pass(int $now): array
    {
        $stats = [];
        if ($this->checkOn()) {
            $last = $this->settings->get('releases_checked_at');
            if ($last === '' || Time::unix($last) <= $now - self::CHECK_EVERY_S) {
                $stats['check'] = $this->check($now) ? 'ok' : 'failed';
            }
        }
        $site = $this->newerSite();
        if ($site !== null && $this->settings->get('release_server_notified') !== $site['version']) {
            $this->notices->add('site.update', null, null, ['version' => $site['version'], 'current' => App::VERSION]);
            $this->settings->set('release_server_notified', $site['version']);
            $stats['site'] = $site['version'];
        }
        foreach (self::PROGRAM_PARTS as $part) {
            $client = $this->newerClient($part);
            if ($client !== null && $this->settings->get("release_{$part}_notified") !== $client['version']) {
                $this->notices->add("$part.release", null, null, ['version' => $client['version']]);
                $this->settings->set("release_{$part}_notified", $client['version']);
                $stats[$part] = $client['version'];
            }
        }
        return $stats;
    }

    public function checkOn(): bool
    {
        return $this->settings->get('releases_check') !== '0';
    }

    /** Reads the releases on GitHub and remembers the newest of each part. False when the list could not be read. */
    public function check(int $now): bool
    {
        $this->settings->set('releases_checked_at', Time::db($now));
        [$status, $body, $error] = ($this->http)($this->apiUrl . '/repos/' . $this->repo . '/releases?per_page=50', 4 * 1024 * 1024);
        $releases = json_decode($body, true);
        if ($status !== 200 || !is_array($releases)) {
            $this->settings->set('releases_check_error', $error !== '' ? $error : "HTTP $status");
            return false;
        }
        foreach (self::PARTS as $part) {
            $found = self::pick($releases, $part);
            // A part without a release keeps what was found before (a release can be taken down for a while).
            if ($found !== null) {
                $this->settings->set('release_' . $part, json_encode($found, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE));
            }
        }
        $this->settings->set('releases_check_error', '');
        return true;
    }

    /**
     * The newest release of a part in GitHub's list: version, tag, url (the release page), published_at, the package
     * (zip_name, zip_url, zip_size), sha256 (GitHub's digest of the package, when given), sums_url (the release's
     * SHA256SUMS.txt, when present) and installers (name => address of each .exe and .pkg, for the page "Install").
     */
    public static function pick(array $releases, string $part): ?array
    {
        $best = null;
        foreach ($releases as $release) {
            if (!is_array($release) || !empty($release['draft']) || !empty($release['prerelease'])
                || !preg_match('/^' . preg_quote(self::TAGS[$part], '/') . '(\d+\.\d+\.\d+)$/', (string) ($release['tag_name'] ?? ''), $m)) {
                continue;
            }
            $version = $m[1];
            if ($best !== null && version_compare($version, $best['version'], '<=')) {
                continue;
            }
            $zipName = self::ZIPS[$part] . "$version.zip";
            $zip = $sums = null;
            $installers = [];
            foreach ((array) ($release['assets'] ?? []) as $asset) {
                $name = (string) ($asset['name'] ?? '');
                if ($name === $zipName) {
                    $zip = $asset;
                } elseif ($name === 'SHA256SUMS.txt') {
                    $sums = $asset;
                } elseif (preg_match('/\.(exe|pkg)$/', $name) && !empty($asset['browser_download_url'])) {
                    $installers[$name] = (string) $asset['browser_download_url'];
                }
            }
            if ($zip === null || empty($zip['browser_download_url'])) {
                continue;
            }
            $digest = (string) ($zip['digest'] ?? '');
            $best = [
                'version' => $version,
                'tag' => (string) $release['tag_name'],
                'url' => (string) ($release['html_url'] ?? ''),
                'published_at' => $release['published_at'] ?? null,
                'zip_name' => $zipName,
                'zip_url' => (string) $zip['browser_download_url'],
                'zip_size' => (int) ($zip['size'] ?? 0),
                'sha256' => preg_match('/^sha256:([0-9a-f]{64})$/i', $digest, $d) ? strtolower($d[1]) : null,
                'sums_url' => $sums !== null && !empty($sums['browser_download_url']) ? (string) $sums['browser_download_url'] : null,
                'installers' => $installers,
            ];
        }
        return $best;
    }

    /** The newest release of a part found by the last check. */
    public function latest(string $part): ?array
    {
        $latest = json_decode($this->settings->get('release_' . $part), true);
        return is_array($latest) && isset($latest['version']) ? $latest : null;
    }

    /** The newest site release when it is newer than this site. */
    public function newerSite(): ?array
    {
        $latest = $this->latest('server');
        return $latest !== null && version_compare($latest['version'], App::VERSION, '>') ? $latest : null;
    }

    /**
     * The newest program release of an edition ($part client, legacy, macos or linux) when it is newer than every package of
     * that edition on the server. The editions other than the standard one count only while the site has any of them.
     */
    public function newerClient(string $part = 'client'): ?array
    {
        $latest = $this->latest($part);
        if ($latest === null || ($part !== 'client' && !$this->packages->inUse(self::kind($part)))) {
            return null;
        }
        $stored = $this->packages->all(self::kind($part))[0]['version'] ?? null;
        return $stored === null || version_compare($latest['version'], $stored, '>') ? $latest : null;
    }

    /**
     * Downloads the newest program release to the server and adds it like an uploaded package (checked and signed).
     * It does not become current by itself.
     *
     * @throws \InvalidArgumentException with a lang key; $params fills it
     */
    public function downloadClient(string $login, ?array &$params = null, string $part = 'client'): array
    {
        $params = [];
        $release = $this->latest($part);
        if ($release === null) {
            throw new \InvalidArgumentException('releases.error.not_checked');
        }
        $existing = $this->packages->find(self::kind($part), $release['version']);
        if ($existing !== null) {
            return $existing;
        }
        $params = ['name' => $release['zip_name']];
        [$status, $body, $error] = ($this->http)($release['zip_url'], self::MAX_CLIENT_BYTES);
        if ($status !== 200 || $body === '') {
            $params['error'] = $error !== '' ? $error : "HTTP $status";
            throw new \InvalidArgumentException('releases.error.download');
        }
        $expected = $this->expectedSha256($release, $params);
        if ($expected === []) {
            throw new \InvalidArgumentException('releases.error.no_checksum');
        }
        $actual = hash('sha256', $body);
        foreach ($expected as $sha) {
            if (!hash_equals($sha, $actual)) {
                throw new \InvalidArgumentException('releases.error.checksum');
            }
        }
        $tmp = tempnam(sys_get_temp_dir(), 'custos-client');
        try {
            file_put_contents($tmp, $body);
            unset($body);
            $package = $this->packages->addAgent($tmp, $login, $params);
            if ($package['kind'] !== self::kind($part)) {
                // The release of one edition holds a package of the other: not for this button.
                $this->packages->delete($package['kind'], $package['version']);
                $params = ['name' => $release['zip_name']];
                throw new \InvalidArgumentException('releases.error.edition');
            }
            $this->notices->settleKinds(["$part.release"]);
            return $package;
        } finally {
            @unlink($tmp);
        }
    }

    /** The kind of agent packages of a program part: agent for client, the edition for the others. */
    public static function kind(string $part): string
    {
        return $part === 'client' ? 'agent' : $part;
    }

    /**
     * SHA-256 values the release publishes for its package: GitHub's digest and the line of SHA256SUMS.txt.
     *
     * @return list<string>
     */
    private function expectedSha256(array $release, array &$params): array
    {
        $expected = [];
        if (!empty($release['sha256'])) {
            $expected[] = (string) $release['sha256'];
        }
        if (!empty($release['sums_url'])) {
            [$status, $sums, $error] = ($this->http)((string) $release['sums_url'], 256 * 1024);
            if ($status !== 200) {
                $params = ['name' => 'SHA256SUMS.txt', 'error' => $error !== '' ? $error : "HTTP $status"];
                throw new \InvalidArgumentException('releases.error.download');
            }
            if (preg_match('/^([0-9a-f]{64})\s+\*?' . preg_quote($release['zip_name'], '/') . '\s*$/mi', $sums, $m)) {
                $expected[] = strtolower($m[1]);
            }
        }
        return $expected;
    }
}
