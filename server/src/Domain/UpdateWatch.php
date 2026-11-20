<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Time;

/**
 * Keeps an eye on versions (7.1, 7.7, section 8):
 *   - machines with an agent older than the current version of their edition: one notice when their number grows or
 *     the current version changes (agent.outdated; legacy.outdated and macos.outdated for the editions for Windows 7
 *     and 8.1 and for macOS);
 *   - 7-Zip: once a day the newest release is read from GitHub (ip7z/7zip); when machines have an older 7z.dll, one
 *     notice per new 7-Zip version. The installer is downloaded only when an administrator asks for it.
 *
 * The newest release is chosen by version number, not by GitHub's "latest" mark, which is not always moved.
 */
final class UpdateWatch
{
    public const CHECK_EVERY_S = 86400;
    public const GITHUB_REPO = 'ip7z/7zip';
    private const MAX_INSTALLER_BYTES = 50 * 1024 * 1024;

    /** @var \Closure(string, int): array{0: int, 1: string, 2: string} HTTP GET: url, byte limit => [status, body, error] */
    private \Closure $http;

    public function __construct(
        private readonly \Custos\Infra\Db $db,
        private readonly Settings $settings,
        private readonly Packages $packages,
        private readonly Notices $notices,
        private readonly string $apiUrl,
        private readonly string $userAgent,
        ?\Closure $http = null
    ) {
        $this->http = $http ?? (new \Custos\Infra\HttpGet($userAgent))(...);
    }

    /** One cron pass. */
    public function pass(int $now): array
    {
        $stats = ['agents_outdated' => $this->agentNotice('agent')];
        foreach (Agents::EDITIONS as $edition) {
            $stats[$edition . '_outdated'] = $this->agentNotice($edition);
        }
        if ($this->settings->get('sevenzip_check') !== '0') {
            $last = $this->settings->get('sevenzip_checked_at');
            if ($last === '' || Time::unix($last) <= $now - self::CHECK_EVERY_S) {
                $stats['sevenzip_check'] = $this->check($now) === null ? 'failed' : 'ok';
            }
        }
        $stats['sevenzip_outdated'] = $this->sevenZipNotice();
        return $stats;
    }

    /** The newest 7-Zip known: from GitHub, or the newest installer on the server if that one is newer. */
    public function latestSevenZip(): ?array
    {
        $github = json_decode($this->settings->get('sevenzip_latest'), true);
        $stored = $this->packages->latestSevenZip();
        if (!is_array($github) || !isset($github['version'])) {
            return $stored === null ? null : ['version' => $stored['version'], 'url' => null, 'sha256' => $stored['sha256'], 'published_at' => null];
        }
        if ($stored !== null && version_compare($stored['version'], $github['version'], '>')) {
            return ['version' => $stored['version'], 'url' => null, 'sha256' => $stored['sha256'], 'published_at' => null];
        }
        return $github;
    }

    /**
     * Reads the releases of 7-Zip on GitHub and remembers the newest stable one with its 64-bit installer and the
     * installer's SHA-256 (GitHub's "digest" of the asset). Returns it, or null when the check failed.
     */
    public function check(int $now): ?array
    {
        $this->settings->set('sevenzip_checked_at', Time::db($now));
        [$status, $body, $error] = ($this->http)($this->apiUrl . '/repos/' . self::GITHUB_REPO . '/releases?per_page=30', 2 * 1024 * 1024);
        $releases = json_decode($body, true);
        if ($status !== 200 || !is_array($releases)) {
            $this->settings->set('sevenzip_check_error', $error !== '' ? $error : "HTTP $status");
            return null;
        }
        $best = null;
        foreach ($releases as $release) {
            $tag = (string) ($release['tag_name'] ?? '');
            if (!empty($release['draft']) || !empty($release['prerelease']) || !preg_match(Packages::SEVENZIP_VERSION, $tag)) {
                continue;
            }
            if ($best !== null && version_compare($tag, $best['tag_name'], '<=')) {
                continue;
            }
            $best = $release;
        }
        if ($best === null) {
            $this->settings->set('sevenzip_check_error', 'no 7-Zip release found');
            return null;
        }
        $version = (string) $best['tag_name'];
        $assetName = '7z' . str_replace('.', '', $version) . '-x64.exe';
        $latest = ['version' => $version, 'url' => null, 'sha256' => null, 'published_at' => $best['published_at'] ?? null, 'asset' => $assetName];
        foreach ((array) ($best['assets'] ?? []) as $asset) {
            if (($asset['name'] ?? '') === $assetName) {
                $latest['url'] = $asset['browser_download_url'] ?? null;
                $digest = (string) ($asset['digest'] ?? '');
                $latest['sha256'] = preg_match('/^sha256:([0-9a-f]{64})$/i', $digest, $m) ? strtolower($m[1]) : null;
            }
        }
        $this->settings->set('sevenzip_latest', json_encode($latest, JSON_UNESCAPED_SLASHES));
        $this->settings->set('sevenzip_check_error', '');
        return $latest;
    }

    /**
     * Downloads the newest 7-Zip installer to the server once, checks it against GitHub's SHA-256 and signs it.
     * Without a published checksum nothing is downloaded: the administrator uploads the file instead.
     *
     * @throws \InvalidArgumentException with a lang key
     */
    public function download(string $login, ?array &$params = null): array
    {
        $params = [];
        $latest = json_decode($this->settings->get('sevenzip_latest'), true);
        if (!is_array($latest) || !isset($latest['version'])) {
            throw new \InvalidArgumentException('sevenzip.error.not_checked');
        }
        $existing = $this->packages->find('7zip', $latest['version']);
        if ($existing !== null) {
            return $existing;
        }
        if (empty($latest['url'])) {
            $params = ['asset' => $latest['asset'] ?? ''];
            throw new \InvalidArgumentException('sevenzip.error.no_asset');
        }
        if (empty($latest['sha256'])) {
            throw new \InvalidArgumentException('sevenzip.error.no_digest');
        }
        [$status, $body, $error] = ($this->http)((string) $latest['url'], self::MAX_INSTALLER_BYTES);
        if ($status !== 200 || $body === '') {
            $params = ['error' => $error !== '' ? $error : "HTTP $status"];
            throw new \InvalidArgumentException('sevenzip.error.download');
        }
        $tmp = tempnam(sys_get_temp_dir(), 'custos-7z');
        try {
            file_put_contents($tmp, $body);
            return $this->packages->addSevenZip($tmp, $latest['version'], $latest['sha256'], 'github', $login, $params);
        } finally {
            @unlink($tmp);
        }
    }

    /**
     * Notice "N machines run an older agent" when the number grows or the current version changes; $kind is the
     * edition (agent, legacy or macos), each with its own current version and notice.
     */
    private function agentNotice(string $kind): int
    {
        $current = $this->packages->current($kind);
        if ($current === null) {
            return 0;
        }
        $outdated = Packages::outdated(Packages::fitting($kind, $this->agents()), $current['version']);
        $count = count($outdated);
        $setting = $kind . '_outdated_notified';
        [$version, $notified] = array_pad(explode('|', $this->settings->get($setting)), 2, '0');
        if ($version !== $current['version'] || $count < (int) $notified) {
            $notified = '0';
        }
        if ($count > (int) $notified) {
            $this->notices->add("$kind.outdated", count($outdated) === 1 ? $outdated[0]['id'] : null, null,
                ['version' => $current['version'], 'n' => $count, 'name' => count($outdated) === 1 ? Agents::name($outdated[0]) : '']);
        }
        $this->settings->set($setting, $current['version'] . '|' . $count);
        return $count;
    }

    /** Notice "7-Zip X is out; older 7z.dll on N machines", once per 7-Zip version. */
    private function sevenZipNotice(): int
    {
        $latest = $this->latestSevenZip();
        if ($latest === null) {
            return 0;
        }
        $count = count(Packages::outdated(Packages::fitting('7zip', $this->agents()), $latest['version'], 'sevenzip_version'));
        if ($count > 0 && $this->settings->get('sevenzip_notified') !== $latest['version']) {
            $this->notices->add('sevenzip.outdated', null, null, ['version' => $latest['version'], 'n' => $count]);
            $this->settings->set('sevenzip_notified', $latest['version']);
        }
        return $count;
    }

    private function agents(): array
    {
        return $this->db->all("SELECT id, hostname, display_name, status, agent_version, edition, sevenzip_version FROM agents WHERE status = 'approved'");
    }
}
