<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Crypto;
use Custos\Infra\Db;
use Custos\Infra\ServerKeys;
use Custos\Infra\Time;
use Custos\Infra\ZipReader;

/**
 * Files the server hands out to agents (section 8, A.10):
 *   - agent: CUSTOS update packages, zip with manifest.json {version, min_protocol, files};
 *   - 7zip: the 64-bit 7-Zip installer from which agents take 7z.dll (sevenzip.update).
 *
 * Each file is checked, its SHA-256 is computed and signed with the server key, and it is kept in var/packages/<kind>/.
 * The signature goes into the command, so an agent can check the downloaded file without trusting the transport.
 */
final class Packages
{
    public const MAX_MANIFEST_PROTOCOL = 1;
    public const VERSION = '/^\d{1,5}(\.\d{1,5}){1,3}([-+][0-9A-Za-z.-]{1,20})?$/';
    public const SEVENZIP_VERSION = '/^\d{1,3}\.\d{2}$/';
    /** The agent version stored by the fake agent and real agents before the first heartbeat: no version known. */
    private const UNKNOWN = '';

    public function __construct(
        private readonly Db $db,
        private readonly ServerKeys $keys,
        private readonly Commands $commands,
        private readonly string $dir
    ) {
    }

    /** The string the server signs for a package (A.10 and the 7-Zip addition of A.4). */
    public static function signedString(string $kind, string $version, string $sha256): string
    {
        return ($kind === '7zip' ? "7zip\n" : '') . "$version\n$sha256";
    }

    public function all(string $kind): array
    {
        $rows = $this->db->all('SELECT * FROM agent_updates WHERE kind = ?', [$kind]);
        usort($rows, fn($a, $b) => version_compare($b['version'], $a['version']));
        return $rows;
    }

    public function find(string $kind, string $version): ?array
    {
        return $this->db->one('SELECT * FROM agent_updates WHERE kind = ? AND version = ?', [$kind, $version]);
    }

    /** The agent version marked current, or null. */
    public function current(): ?array
    {
        return $this->db->one("SELECT * FROM agent_updates WHERE kind = 'agent' AND is_current = 1");
    }

    /** The newest 7-Zip installer the server has. */
    public function latestSevenZip(): ?array
    {
        return $this->all('7zip')[0] ?? null;
    }

    /** Path of the stored file, or null when the package or its file is missing. */
    public function file(string $kind, string $version): ?string
    {
        $row = $this->find($kind, $version);
        if ($row === null) {
            return null;
        }
        $path = $this->dir . '/' . $kind . '/' . $row['file_name'];
        return is_file($path) ? $path : null;
    }

    /**
     * Adds an agent update package. The zip must have manifest.json with the version, min_protocol not newer than
     * the server speaks, and the list of files, all present in the archive.
     *
     * @throws \InvalidArgumentException with a lang key; the message parameters are in $params
     * @return array the stored row
     */
    public function addAgent(string $tmpFile, string $login, ?array &$params = null): array
    {
        $params = [];
        try {
            $zip = ZipReader::open($tmpFile);
            $json = $zip->read('manifest.json');
        } catch (\RuntimeException $e) {
            $params = ['error' => $e->getMessage()];
            throw new \InvalidArgumentException('updates.error.zip');
        }
        $manifest = $json === null ? null : json_decode($json, true);
        if (!is_array($manifest)) {
            throw new \InvalidArgumentException('updates.error.manifest');
        }
        $version = $manifest['version'] ?? null;
        if (!is_string($version) || !preg_match(self::VERSION, $version)) {
            throw new \InvalidArgumentException('updates.error.version');
        }
        $protocol = $manifest['min_protocol'] ?? null;
        if (!is_int($protocol) || $protocol < 1) {
            throw new \InvalidArgumentException('updates.error.protocol');
        }
        if ($protocol > self::MAX_MANIFEST_PROTOCOL) {
            $params = ['protocol' => $protocol, 'server' => self::MAX_MANIFEST_PROTOCOL];
            throw new \InvalidArgumentException('updates.error.protocol_new');
        }
        $files = $manifest['files'] ?? null;
        if (!is_array($files) || $files === []) {
            throw new \InvalidArgumentException('updates.error.files');
        }
        foreach ($files as $file) {
            $name = is_string($file) ? $file : ($file['path'] ?? $file['name'] ?? null);
            if (is_string($name) && !$zip->has(str_replace('\\', '/', $name))) {
                $params = ['file' => $name];
                throw new \InvalidArgumentException('updates.error.file_missing');
            }
        }
        if ($this->find('agent', $version) !== null) {
            $params = ['version' => $version];
            throw new \InvalidArgumentException('updates.error.exists');
        }
        return $this->store('agent', $version, $tmpFile, "custos-$version.zip", $json, 'upload', $login);
    }

    /**
     * Adds a 7-Zip installer. $expectedSha256, when given, must match (the digest GitHub publishes for the asset).
     *
     * @throws \InvalidArgumentException with a lang key
     */
    public function addSevenZip(string $tmpFile, string $version, ?string $expectedSha256, string $source, string $login, ?array &$params = null): array
    {
        $params = [];
        if (!preg_match(self::SEVENZIP_VERSION, $version)) {
            throw new \InvalidArgumentException('sevenzip.error.version');
        }
        $head = (string) @file_get_contents($tmpFile, false, null, 0, 2);
        if ($head !== 'MZ') {
            throw new \InvalidArgumentException('sevenzip.error.not_exe');
        }
        $sha = hash_file('sha256', $tmpFile);
        if ($expectedSha256 !== null && $expectedSha256 !== '' && !hash_equals(strtolower($expectedSha256), $sha)) {
            $params = ['expected' => strtolower($expectedSha256), 'actual' => $sha];
            throw new \InvalidArgumentException('sevenzip.error.checksum');
        }
        $existing = $this->find('7zip', $version);
        if ($existing !== null) {
            if (hash_equals($existing['sha256'], $sha)) {
                return $existing;
            }
            $params = ['version' => $version];
            throw new \InvalidArgumentException('sevenzip.error.exists');
        }
        return $this->store('7zip', $version, $tmpFile, '7z' . str_replace('.', '', $version) . '-x64.exe', null, $source, $login);
    }

    /** Makes $version the agent version machines should run. */
    public function setCurrent(string $version): void
    {
        $this->db->transaction(function () use ($version) {
            $this->db->exec("UPDATE agent_updates SET is_current = 0 WHERE kind = 'agent'");
            $this->db->exec("UPDATE agent_updates SET is_current = 1 WHERE kind = 'agent' AND version = ?", [$version]);
        });
    }

    /** Deletes a package and withdraws the commands that would download it and have not been delivered. */
    public function delete(string $kind, string $version): void
    {
        $row = $this->find($kind, $version);
        if ($row === null) {
            return;
        }
        $command = $kind === '7zip' ? 'sevenzip.update' : 'update.install';
        foreach ($this->db->all("SELECT id, agent_id, payload FROM commands WHERE kind = ? AND status = 'queued'", [$command]) as $c) {
            if ((json_decode($c['payload'], true)['version'] ?? null) === $version) {
                $this->commands->cancel($c['agent_id'], $c['id']);
            }
        }
        $this->db->exec('DELETE FROM agent_updates WHERE kind = ? AND version = ?', [$kind, $version]);
        @unlink($this->dir . '/' . $kind . '/' . $row['file_name']);
    }

    /**
     * Approved machines whose agent is older than $version (or than the current version).
     *
     * @param list<array> $agents agent rows
     * @return list<array>
     */
    public static function outdated(array $agents, ?string $version, string $field = 'agent_version'): array
    {
        if ($version === null) {
            return [];
        }
        return array_values(array_filter($agents, fn($a) => $a['status'] === 'approved'
            && (string) ($a[$field] ?? self::UNKNOWN) !== self::UNKNOWN
            && version_compare((string) $a[$field], $version, '<')));
    }

    /**
     * Queues the update of the agent (update.install) or of 7z.dll (sevenzip.update) on machines.
     * Machines that are not approved or already run this version or a newer one are skipped.
     *
     * @param list<array> $agents
     * @param array{id: int, login: string} $user
     * @return array{queued: list<array>, skipped: list<array>}
     */
    public function install(string $kind, string $version, array $agents, array $user): array
    {
        $package = $this->find($kind, $version) ?? throw new \InvalidArgumentException('updates.error.unknown');
        $field = $kind === '7zip' ? 'sevenzip_version' : 'agent_version';
        $result = ['queued' => [], 'skipped' => []];
        foreach ($agents as $agent) {
            $have = (string) ($agent[$field] ?? '');
            if ($agent['status'] !== 'approved' || ($have !== '' && version_compare($have, $version, '>='))) {
                $result['skipped'][] = $agent;
                continue;
            }
            $this->commands->queue($agent['id'], $kind === '7zip' ? 'sevenzip.update' : 'update.install', [
                'version' => $version,
                'sha256' => $package['sha256'],
                'signature' => $package['signature'],
            ], $user);
            $result['queued'][] = $agent;
        }
        return $result;
    }

    private function store(string $kind, string $version, string $tmpFile, string $fileName, ?string $manifest, string $source, string $login): array
    {
        $dir = $this->dir . '/' . $kind;
        if (!is_dir($dir) && !@mkdir($dir, 0750, true) && !is_dir($dir)) {
            throw new \RuntimeException("Cannot create $dir");
        }
        $sha = hash_file('sha256', $tmpFile);
        $target = $dir . '/' . $fileName;
        if (!(is_uploaded_file($tmpFile) ? move_uploaded_file($tmpFile, $target) : copy($tmpFile, $target))) {
            throw new \RuntimeException("Cannot write $target");
        }
        @chmod($target, 0640);
        $signature = Crypto::sign(self::signedString($kind, $version, $sha), $this->keys->privateKey());
        $this->db->exec(
            'INSERT INTO agent_updates (kind, version, file_name, size, sha256, signature, manifest, source, is_current, uploaded_at, uploaded_by)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?, 0, ?, ?)',
            [$kind, $version, $fileName, filesize($target), $sha, $signature, $manifest, $source, Time::now(), $login]
        );
        return $this->find($kind, $version);
    }
}
