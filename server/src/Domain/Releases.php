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
 *           8.1 (its packages are of kind legacy);
 *   macos   tag client-vX.Y.Z (the same releases as the program for Windows), package custos-macos-X.Y.Z.zip: the
 *           same for CUSTOS for macOS (kind macos);
 *   linux   tag client-vX.Y.Z as well, package custos-linux-X.Y.Z.zip: the same for CUSTOS for Linux (kind linux),
 *           available for download before the first Linux installation.
 * A bell notice tells once per version when the site is older, or when the program is newer than every agent
 * package of its edition on the server. Other than the standard program, only editions in use produce notices.
 * Drafts are skipped. The stable channel skips pre-releases and the preview channel considers both. The check
 * can be turned off (server without internet).
 *
 * Settings: releases_check ('0' = off), releases_checked_at, releases_check_error, release_server, release_client,
 * release_legacy, release_macos and release_linux (JSON of the stable release found, see pick()),
 * releases_channel, release_*_preview and release_*[_preview]_notified (versions noticed).
 */
final class Releases
{
    public const CHECK_EVERY_S = 86400;
    public const DEFAULT_REPO = 'openpass1/custos';
    public const STABLE = 'stable';
    public const PREVIEW = 'preview';
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
        foreach (self::PARTS as $part) {
            if (!in_array($part, ['server', 'client'], true) && !$this->packages->inUse(self::kind($part))) {
                continue;
            }
            $site = $part === 'server';
            $release = $site ? $this->newerSite() : $this->newerClient($part);
            if ($release === null) {
                continue;
            }
            $preview = !empty($release['prerelease']);
            $key = $preview ? "release_{$part}_preview_notified" : "release_{$part}_notified";
            if ($this->settings->get($key) === $release['version'] || ($preview && !$this->allowsPreview())) {
                continue;
            }
            $params = ['version' => $release['version']];
            if ($site) {
                $params['current'] = App::VERSION;
            }
            if ($preview) {
                $params['prerelease'] = true;
            }
            $this->notices->add($site ? 'site.update' : "$part.release", null, null, $params);
            if ($preview && !$this->allowsPreview()) {
                $this->notices->settlePreviewReleases();
            }
            $this->settings->set($key, $release['version']);
            $stats[$site ? 'site' : $part] = $release['version'];
        }
        return $stats;
    }

    public function channel(): string
    {
        return $this->settings->get('releases_channel') === self::PREVIEW ? self::PREVIEW : self::STABLE;
    }

    public function allowsPreview(): bool
    {
        return $this->settings->fresh('releases_channel') === self::PREVIEW;
    }

    public function setChannel(string $channel): void
    {
        if (!in_array($channel, [self::STABLE, self::PREVIEW], true)) {
            throw new \InvalidArgumentException('releases.error.channel');
        }
        $this->settings->set('releases_channel', $channel);
        if ($channel === self::STABLE) {
            $this->notices->settlePreviewReleases();
        } elseif ($this->checkOn() && $this->settings->get('releases_channels_checked') === '') {
            $this->check(time());
        }
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
        if ($status !== 200 || !is_array($releases) || !str_starts_with(ltrim($body), '[')) {
            $this->settings->set('releases_check_error', $error !== '' ? $error : "HTTP $status");
            return false;
        }
        $preview = array_filter($releases, static fn($release) => is_array($release) && !empty($release['prerelease']));
        $tags = array_column($releases, 'tag_name');
        foreach (self::PARTS as $part) {
            $this->rememberStable('release_' . $part, self::pick($releases, $part), $tags);
            $candidate = self::pick($preview, $part, true);
            $this->settings->set('release_' . $part . '_preview', $candidate === null ? '' : $this->encode($candidate));
        }
        $this->settings->set('releases_channels_checked', '1');
        $this->settings->set('releases_check_error', '');
        return true;
    }

    private function rememberStable(string $key, ?array $release, array $tags): void
    {
        if ($release !== null) {
            $this->settings->set($key, $this->encode($release));
            return;
        }
        // Keep a release absent from the list as before. Drop a cached tag that is now ineligible for this cache.
        $cached = $this->cached($key);
        if (isset($cached['tag']) && in_array($cached['tag'], $tags, true)) {
            $this->settings->set($key, '');
        }
    }

    private function encode(array $release): string
    {
        return json_encode($release, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_INVALID_UTF8_SUBSTITUTE);
    }

    /**
     * The newest release of a part in GitHub's list: version, tag, url (the release page), published_at, the package
     * (zip_name, zip_url, zip_size), prerelease, sha256 (GitHub's digest of the package, when given), sums_url (the release's
     * SHA256SUMS.txt, when present) and installers (name => address of each .exe and .pkg, for the page "Install").
     */
    public static function pick(array $releases, string $part, bool $includePreview = false): ?array
    {
        $best = null;
        foreach ($releases as $release) {
            if (!is_array($release) || !empty($release['draft']) || (!$includePreview && !empty($release['prerelease']))
                || !preg_match('/^' . preg_quote(self::TAGS[$part], '/') . '(\d+\.\d+\.\d+)$/', (string) ($release['tag_name'] ?? ''), $m)) {
                continue;
            }
            $version = $m[1];
            $prerelease = !empty($release['prerelease']);
            if ($best !== null && (version_compare($version, $best['version'], '<')
                || ($version === $best['version'] && ($prerelease || !$best['prerelease'])))) {
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
                'prerelease' => $prerelease,
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
        $stable = $this->cached('release_' . $part);
        if ($this->channel() !== self::PREVIEW) {
            return $stable;
        }
        $preview = $this->cached('release_' . $part . '_preview');
        return $preview !== null && ($stable === null || version_compare($preview['version'], $stable['version'], '>'))
            ? $preview : $stable;
    }

    private function cached(string $key): ?array
    {
        $latest = json_decode($this->settings->get($key), true);
        return is_array($latest) && isset($latest['version']) ? $latest : null;
    }

    public static function selection(string $channel, array $release): string
    {
        return $channel . ':' . $release['version'] . ':' . (empty($release['prerelease']) ? self::STABLE : self::PREVIEW);
    }

    public function checkSelection(?string $selection, ?array $release): void
    {
        if ($selection !== null && ($release === null || $selection !== self::selection($this->channel(), $release))) {
            throw new \InvalidArgumentException('releases.error.selection_changed');
        }
    }

    /** The newest site release when it is newer than this site. */
    public function newerSite(): ?array
    {
        $latest = $this->latest('server');
        return $latest !== null && version_compare($latest['version'], App::VERSION, '>') ? $latest : null;
    }

    /**
     * The newest program release of an edition ($part client, legacy, macos or linux) when it is newer than every package of
     * that edition on the server.
     */
    public function newerClient(string $part = 'client'): ?array
    {
        $latest = $this->latest($part);
        if ($latest === null) {
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
    public function downloadClient(string $login, ?array &$params = null, string $part = 'client', ?string $selection = null): array
    {
        $params = [];
        $release = $this->latest($part);
        $this->checkSelection($selection, $release);
        if ($release === null) {
            throw new \InvalidArgumentException('releases.error.not_checked');
        }
        if (!empty($release['prerelease']) && !$this->allowsPreview()) {
            throw new \InvalidArgumentException('releases.error.selection_changed');
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
            if (!empty($release['prerelease']) && !$this->allowsPreview()) {
                throw new \InvalidArgumentException('releases.error.selection_changed');
            }
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
