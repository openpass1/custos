<?php

declare(strict_types=1);

namespace Custos\Infra;

use PDO;

/**
 * Updates the site itself (the server folder) from a custos-server-X.Y.Z.zip package: from the newest release on
 * GitHub, from a file uploaded on the site, or from a file given to bin/update.php. Runs only from the command line:
 * from bin/cron.php when an administrator pressed «Update» on the site, or from bin/update.php.
 *
 * Steps, each written to var/update/state.json so the site can show them:
 *   download, verify  the GitHub release file and its SHA-256 (asset digest and/or SHA256SUMS.txt)
 *   checks            package contents and version, PHP version and extensions, proc_open, free space, and that
 *                     the site user may move every folder that is replaced
 *   unpack            into a staging folder next to the site
 *   maintenance       var/update/maintenance.json: index.php answers 503 "being updated" from here on
 *   backup            a copy of the tables that the new migrations touch (none when there are no migrations)
 *   files             each top-level entry of the package (bin, src, vendor, ...) replaces the old one; the old
 *                     one moves into the backup folder. config/ (settings, keys) and var/ are never replaced
 *   database          bin/migrate.php of the new version, in a new process
 *   selftest          bin/update.php --self-test of the new version, in a new process
 * Any failure after the first change puts the old entries and tables back and takes the maintenance page away.
 *
 * The previous version stays in the backup folder until the next update: bin/update.php --rollback returns to it.
 *
 * This class keeps working after the files under it were replaced: the classes it needs are loaded before that,
 * and the new version only ever runs in a separate process. The layout of var/update (state.json, backup/ with
 * backup.json) is read by the next versions too, so it only gets new fields, never other meanings.
 */
final class Updater
{
    /** The package holds the server folder itself. */
    public const PREFIX = 'server/';
    public const MAX_PACKAGE_BYTES = 50 * 1024 * 1024;
    /** Files every site package has; bin/update.php is needed for the self-test of the new version. */
    private const REQUIRED = ['src/App.php', 'composer.json', 'vendor/autoload.php', 'public/index.php', 'bin/migrate.php', 'bin/update.php', 'bin/cron.php'];
    /** Parts of the server folder an update never replaces: settings, keys and run-time data. */
    private const KEEP = ['config', 'var'];
    /** Space kept free on top of the package and the database copy. */
    private const SPARE_BYTES = 20 * 1024 * 1024;
    private const OUTPUT_BYTES = 20000;

    private readonly string $stateDir;
    private ?string $workDir = null;
    private ?PDO $pdo = null;
    /** The update being run, as saved in state.json. */
    private array $state = [];

    /**
     * @param \Closure(): PDO $connect the site database
     * @param \Closure(string, int): array{0: int, 1: string, 2: string} $http GET url with a byte limit
     * @param \Closure(string, array): void $say progress for the command line: a lang key and its parameters
     */
    public function __construct(
        private readonly string $root,
        private readonly string $varDir,
        private readonly \Closure $connect,
        private readonly \Closure $http,
        private readonly \Closure $say,
        private readonly string $php = PHP_BINARY,
        private readonly int $settleSeconds = 2,
    ) {
        $this->stateDir = $varDir . '/update';
        // Everything used after the files are replaced is loaded now, from the version that runs this.
        foreach ([DbSnapshot::class, UpdateError::class, ZipReader::class, Migrator::class, Time::class, Log::class] as $class) {
            class_exists($class);
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // State shared with the site

    public static function readState(string $varDir): ?array
    {
        $file = $varDir . '/update/state.json';
        $state = is_file($file) ? json_decode((string) @file_get_contents($file), true) : null;
        return is_array($state) ? $state : null;
    }

    /** Written through a temporary file, so the site never reads half of it. */
    public static function writeState(string $varDir, array $state): void
    {
        $dir = $varDir . '/update';
        if (!is_dir($dir) && !@mkdir($dir, 0750, true) && !is_dir($dir)) {
            throw new \RuntimeException("cannot create $dir");
        }
        $tmp = $dir . '/state.json.' . getmypid();
        $json = json_encode($state, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_INVALID_UTF8_SUBSTITUTE);
        if (@file_put_contents($tmp, $json) === false || !@rename($tmp, $dir . '/state.json')) {
            @unlink($tmp);
            throw new \RuntimeException("cannot write $dir/state.json");
        }
    }

    /** The maintenance page is on: what index.php checks before anything else. */
    public static function maintenanceFile(string $varDir): string
    {
        return $varDir . '/update/maintenance.json';
    }

    /** index.php resets the PHP code cache once when it finds this file (servers with opcache.validate_timestamps=0). */
    public static function opcacheFile(string $varDir): string
    {
        return $varDir . '/update/opcache-reset';
    }

    /** What cron has to do: 'queued' (an administrator pressed «Update»), 'interrupted' (a run was cut off) or null. */
    public function pending(): ?string
    {
        $state = self::readState($this->varDir);
        if (($state['status'] ?? null) === 'queued') {
            return 'queued';
        }
        if (($state['status'] ?? null) === 'running' || is_file(self::maintenanceFile($this->varDir))) {
            return 'interrupted';
        }
        return null;
    }

    /** A new state of an update, before it is saved. */
    public static function newState(string $source, string $by, array $extra = []): array
    {
        return $extra + [
            'id' => gmdate('Ymd\THis') . '-' . bin2hex(random_bytes(3)),
            'status' => 'queued',
            'source' => $source,
            'by' => $by,
            'requested_at' => Time::now(),
            'from' => null,
            'to' => null,
            'steps' => [],
            'journal' => [],
            'error' => null,
            'output' => '',
        ];
    }

    // -------------------------------------------------------------------------------------------------------------
    // Runs

    /** Runs the queued site update. Null when nothing can run. @param \Closure(): bool $allowsPreview */
    public function runQueued(\Closure $allowsPreview): ?bool
    {
        $state = self::readState($this->varDir);
        if (($state['status'] ?? null) !== 'queued') {
            return null;
        }
        if (($state['source'] ?? '') === 'github' && !empty($state['release']['prerelease']) && !$allowsPreview()) {
            $state['status'] = 'cancelled';
            $state['finished_at'] = Time::now();
            $state['error'] = ['key' => 'site.state.stable_channel', 'params' => []];
            self::writeState($this->varDir, $state);
            return null;
        }
        return $this->run($state);
    }

    /** Updates from the newest GitHub release (an entry of SiteUpdate::latest()). */
    public function installRelease(array $release, string $by): bool
    {
        return $this->run(self::newState('github', $by, ['release' => $release, 'to' => $release['version'] ?? null]));
    }

    /** Updates from a package file; $force installs the same version again (a repair). */
    public function installFile(string $zip, string $by, bool $force = false): bool
    {
        return $this->run(self::newState('cli', $by, ['package' => $zip, 'force' => $force]));
    }

    /** A run that was cut off (the process was killed): puts back what it changed. */
    public function recover(): bool
    {
        $state = self::readState($this->varDir);
        if (($state['status'] ?? null) !== 'running') {
            // Only the maintenance page is left over.
            @unlink(self::maintenanceFile($this->varDir));
            return true;
        }
        $this->state = $state;
        $this->fail(new UpdateError('site.error.interrupted'));
        return false;
    }

    private function run(array $state): bool
    {
        $this->state = $state;
        $this->state['status'] = 'running';
        $this->state['started_at'] = Time::now();
        $this->state['steps'] = [];
        $this->state['journal'] = [];
        $this->state['error'] = null;
        $this->state['output'] = '';
        try {
            $this->save();
            $zip = $this->obtain();

            $this->step('checks');
            if (is_file($this->root . '/phpunit.xml.dist')) {
                // A development copy (packages never hold the tests): its files are not to be replaced.
                throw new UpdateError('site.error.dev_copy', ['root' => $this->root]);
            }
            $info = self::inspect($zip);
            $from = $this->installedVersion();
            $this->state['from'] = $from;
            $this->state['to'] = $info['version'];
            $this->save();
            $force = !empty($this->state['force']);
            if (version_compare($info['version'], $from, $force ? '<' : '<=')) {
                throw new UpdateError('site.error.not_newer', ['version' => $info['version'], 'current' => $from]);
            }
            self::checkPlatform($info);
            self::checkProcOpen();
            $pending = $this->pendingMigrations($zip, $info['migrations']);
            $tables = [];
            if ($pending !== []) {
                $tables = DbSnapshot::touchedTables(array_merge(...array_values($pending))) ?? (new DbSnapshot($this->pdo()))->tables();
                // Its rows say which migrations ran: a restore must forget the ones of the new version.
                $tables = array_values(array_unique([...$tables, 'schema_migrations']));
            }
            $this->checkSpace($info['size'], $tables);
            $this->checkWritable($info['entries']);

            $this->step('unpack');
            $stage = $this->work('stage');
            self::removeTree($stage);
            $this->unpack($zip, $stage, $info['version']);
            $backup = $this->work('backup.new');
            self::removeTree($backup);
            self::makeDir($backup);

            $this->step('maintenance');
            $this->maintenanceOn($info['version']);
            if ($this->settleSeconds > 0) {
                // Requests that started before the page went up finish with the old files.
                sleep($this->settleSeconds);
            }

            if ($pending !== []) {
                $this->step('backup', ['tables' => count($tables)]);
                $snapshot = new DbSnapshot($this->pdo());
                $before = $snapshot->tables();
                $copied = $snapshot->dump($tables, $backup . '/database.sql.gz');
                $this->state['journal']['dump'] = ['file' => 'database.sql.gz', 'tables_before' => $before, 'tables' => $copied];
                $this->save();
            }

            $this->step('files');
            $this->swap($stage, $backup, $info);

            $this->step('database', ['n' => count($pending)]);
            $this->state['journal']['migrating'] = true;
            $this->save();
            [$code, $output] = $this->child([$this->root . '/bin/migrate.php']);
            $this->output($output);
            if ($code !== 0) {
                throw new UpdateError('site.error.migrate');
            }

            $this->step('selftest');
            [$code, $output] = $this->child([$this->root . '/bin/update.php', '--self-test', '--expect=' . $info['version']]);
            $this->output($output);
            if ($code !== 0) {
                throw new UpdateError('site.error.selftest');
            }

            $this->finish($stage, $backup, $info);
            return true;
        } catch (\Throwable $e) {
            $this->fail($e);
            return false;
        }
    }

    /** The package file: downloaded and checked, or the uploaded one. */
    private function obtain(): string
    {
        $source = $this->state['source'] ?? '';
        if ($source === 'github') {
            $release = (array) ($this->state['release'] ?? []);
            $name = (string) ($release['zip_name'] ?? '');
            if (!preg_match('/^custos-server-[0-9.]+\.zip$/', $name) || empty($release['zip_url'])) {
                throw new UpdateError('site.error.package', ['reason' => 'no package in the release']);
            }
            $this->step('download', ['name' => $name]);
            [$status, $body, $error] = ($this->http)((string) $release['zip_url'], self::MAX_PACKAGE_BYTES);
            if ($status !== 200 || $body === '') {
                throw new UpdateError('site.error.download', ['name' => $name, 'error' => $error !== '' ? $error : "HTTP $status"]);
            }
            $dir = $this->stateDir . '/download';
            self::removeTree($dir);
            self::makeDir($dir);
            $zip = $dir . '/' . $name;
            if (@file_put_contents($zip, $body) !== strlen($body)) {
                throw new UpdateError('site.error.download', ['name' => $name, 'error' => "cannot write $zip"]);
            }
            unset($body);

            $this->step('verify');
            $expected = [];
            if (!empty($release['sha256'])) {
                $expected[] = strtolower((string) $release['sha256']);
            }
            if (!empty($release['sums_url'])) {
                [$status, $sums, $error] = ($this->http)((string) $release['sums_url'], 256 * 1024);
                if ($status !== 200) {
                    throw new UpdateError('site.error.download', ['name' => 'SHA256SUMS.txt', 'error' => $error !== '' ? $error : "HTTP $status"]);
                }
                if (preg_match('/^([0-9a-f]{64})\s+\*?' . preg_quote($name, '/') . '\s*$/mi', $sums, $m)) {
                    $expected[] = strtolower($m[1]);
                }
            }
            if ($expected === []) {
                throw new UpdateError('site.error.no_checksum', ['name' => $name]);
            }
            $actual = hash_file('sha256', $zip);
            foreach ($expected as $sha) {
                if (!hash_equals($sha, $actual)) {
                    throw new UpdateError('site.error.checksum', ['name' => $name]);
                }
            }
            return $zip;
        }
        $zip = (string) ($this->state['package'] ?? '');
        if ($zip === '' || !is_file($zip)) {
            throw new UpdateError('site.error.package', ['reason' => "file not found: $zip"]);
        }
        // A file uploaded on the site: the same bytes that were checked when it was uploaded.
        if (!empty($this->state['package_sha256']) && !hash_equals((string) $this->state['package_sha256'], hash_file('sha256', $zip))) {
            throw new UpdateError('site.error.checksum', ['name' => basename($zip)]);
        }
        return $zip;
    }

    /** Applies the files: old entries move into $backup, new ones from $stage take their place. */
    private function swap(string $stage, string $backup, array $info): void
    {
        $names = array_values(array_diff(scandir($stage) ?: [], ['.', '..']));
        sort($names, SORT_STRING);
        $this->state['journal'] += ['moved' => [], 'placed' => [], 'config' => []];
        foreach ($names as $name) {
            if (in_array($name, self::KEEP, true)) {
                continue;
            }
            $target = $this->root . '/' . $name;
            if (file_exists($target) || is_link($target)) {
                if (!@rename($target, $backup . '/' . $name)) {
                    throw new UpdateError('site.error.files', ['reason' => "cannot move $target"]);
                }
                $this->state['journal']['moved'][] = $name;
                $this->save();
            }
            if (!@rename($stage . '/' . $name, $target)) {
                throw new UpdateError('site.error.files', ['reason' => "cannot move the new $name into place"]);
            }
            $this->state['journal']['placed'][] = $name;
            $this->save();
        }
        // config/: only the example file of the package; config.php and keys/ stay as they are.
        $config = $this->root . '/config';
        if (is_dir($stage . '/config') && is_dir($config) && is_writable($config)) {
            foreach (array_diff(scandir($stage . '/config') ?: [], ['.', '..']) as $file) {
                if (!is_file($stage . '/config/' . $file) || $file === 'config.php') {
                    continue;
                }
                if (is_file($config . '/' . $file)) {
                    self::makeDir($backup . '/config');
                    copy($config . '/' . $file, $backup . '/config/' . $file);
                    $this->state['journal']['config'][] = $file;
                    $this->save();
                }
                copy($stage . '/config/' . $file, $config . '/' . $file);
            }
        }
        // public/ may hold things that are not ours (.well-known of a certificate): they come along.
        if (in_array('public', $this->state['journal']['moved'], true) && is_dir($this->root . '/public')) {
            $installed = json_decode((string) @file_get_contents($this->stateDir . '/installed.json'), true);
            $known = array_flip((array) ($installed['public'] ?? []));
            foreach (array_diff(scandir($backup . '/public') ?: [], ['.', '..']) as $item) {
                if (!isset($known[$item]) && !file_exists($this->root . '/public/' . $item)) {
                    self::copyTree($backup . '/public/' . $item, $this->root . '/public/' . $item);
                }
            }
        }
        @touch(self::opcacheFile($this->varDir));
    }

    private function finish(string $stage, string $backup, array $info): void
    {
        $journal = $this->state['journal'];
        $meta = [
            'version' => $this->state['from'],
            'to' => $info['version'],
            'created_at' => Time::now(),
            'moved' => $journal['moved'] ?? [],
            'placed' => $journal['placed'] ?? [],
            'config' => $journal['config'] ?? [],
            'dump' => $journal['dump'] ?? null,
        ];
        file_put_contents($backup . '/backup.json', json_encode($meta, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES));
        file_put_contents($this->stateDir . '/installed.json', json_encode(['version' => $info['version'], 'public' => $info['public']], JSON_UNESCAPED_SLASHES));
        @unlink(self::maintenanceFile($this->varDir));
        $this->state['status'] = 'done';
        $this->state['finished_at'] = Time::now();
        $this->save();
        ($this->say)('site.step.done', ['from' => $this->state['from'], 'to' => $info['version']]);
        Log::info("site updated from {$this->state['from']} to {$info['version']}");
        $this->notice('site.updated', ['from' => $this->state['from'], 'to' => $info['version']], ['site.update']);

        // The previous copy gives way to this one; the old folders are deleted after the site is back.
        $kept = $this->work('backup');
        $old = $this->work('backup.old');
        self::removeTree($old);
        if (is_dir($kept)) {
            @rename($kept, $old);
        }
        if (!@rename($backup, $kept)) {
            Log::warning("cannot rename $backup to $kept");
        }
        foreach ([$old, $stage, $this->stateDir . '/download', $this->stateDir . '/upload'] as $dir) {
            if (!self::removeTree($dir)) {
                Log::warning("cannot delete $dir completely");
            }
        }
    }

    /** Stops the run: puts back what it changed and says why. */
    private function fail(\Throwable $e): void
    {
        Log::error('site update' . (isset($this->state['to']) ? ' to ' . $this->state['to'] : '') . ' failed: ' . $e);
        $this->state['error'] = $e instanceof UpdateError
            ? ['key' => $e->key, 'params' => $e->params]
            : ['key' => 'site.error.unexpected', 'params' => ['message' => $e->getMessage()]];
        if ($e instanceof UpdateError && $e->output !== '') {
            $this->output($e->output);
        }
        $changed = !empty($this->state['journal']['moved']) || !empty($this->state['journal']['placed'])
            || !empty($this->state['journal']['dump']) || !empty($this->state['journal']['maintenance']);
        if ($changed) {
            ($this->say)('site.step.rollback', []);
            $this->state['steps'][] = ['step' => 'rollback', 'at' => Time::now()];
        }
        try {
            $this->undo();
            $this->state['rolled_back'] = $changed;
        } catch (\Throwable $u) {
            // The site stays behind the maintenance page: its files may be half old, half new.
            Log::error('site update: putting the old version back failed: ' . $u);
            $this->state['rollback_error'] = ['key' => 'site.error.rollback', 'params' => ['reason' => $u->getMessage()]];
        }
        $this->state['status'] = 'failed';
        $this->state['finished_at'] = Time::now();
        try {
            $this->save();
        } catch (\Throwable $s) {
            Log::error('site update: ' . $s->getMessage());
        }
        $this->notice('site.update_failed', ['to' => (string) ($this->state['to'] ?? '')]);
    }

    /** Reverses the journal of the run: files, settings example, tables; then removes the maintenance page. */
    private function undo(): void
    {
        $journal = $this->state['journal'] ?? [];
        $backup = $this->work('backup.new');
        $failed = $this->work('failed');
        self::removeTree($failed);
        if (!empty($journal['placed']) || !empty($journal['moved'])) {
            self::makeDir($failed);
        }
        foreach (array_reverse($journal['placed'] ?? []) as $name) {
            $path = $this->root . '/' . $name;
            if ((file_exists($path) || is_link($path)) && !@rename($path, $failed . '/' . $name)) {
                throw new \RuntimeException("cannot move $path away");
            }
        }
        foreach (array_reverse($journal['moved'] ?? []) as $name) {
            if (!@rename($backup . '/' . $name, $this->root . '/' . $name)) {
                throw new \RuntimeException("cannot move $name back from $backup");
            }
        }
        foreach ($journal['config'] ?? [] as $file) {
            @copy($backup . '/config/' . $file, $this->root . '/config/' . $file);
        }
        if (!empty($journal['dump']) && !empty($journal['migrating'])) {
            (new DbSnapshot($this->pdo()))->restore($backup . '/' . $journal['dump']['file'], $journal['dump']['tables_before']);
        }
        if (!empty($journal['placed']) || !empty($journal['moved'])) {
            @touch(self::opcacheFile($this->varDir));
        }
        @unlink(self::maintenanceFile($this->varDir));
        foreach ([$failed, $backup, $this->work('stage')] as $dir) {
            self::removeTree($dir);
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // Going back

    /** The kept copy of the previous version: backup.json with version (the old one) and to (the one it gave way to). */
    public function backupInfo(): ?array
    {
        $meta = json_decode((string) @file_get_contents($this->work('backup') . '/backup.json'), true);
        return is_array($meta) && isset($meta['version'], $meta['to']) ? $meta : null;
    }

    /** Returns to the version before the last update (bin/update.php --rollback). */
    public function rollback(string $by): bool
    {
        $meta = $this->backupInfo();
        $installed = $this->installedVersion();
        $this->state = self::newState('rollback', $by, ['status' => 'running', 'from' => $installed, 'to' => $meta['version'] ?? null, 'started_at' => Time::now()]);
        try {
            if ($meta === null) {
                throw new UpdateError('site.error.no_backup');
            }
            if ($meta['to'] !== $installed) {
                throw new UpdateError('site.error.backup_other', ['backup' => $meta['to'], 'installed' => $installed]);
            }
            $this->save();
            $backup = $this->work('backup');
            $this->step('maintenance');
            $this->maintenanceOn((string) $meta['version']);

            $this->step('files');
            $away = $this->work('rolled-back');
            self::removeTree($away);
            self::makeDir($away);
            foreach ($meta['placed'] as $name) {
                $path = $this->root . '/' . $name;
                if ((file_exists($path) || is_link($path)) && !@rename($path, $away . '/' . $name)) {
                    throw new \RuntimeException("cannot move $path away");
                }
            }
            foreach ($meta['moved'] as $name) {
                if (!@rename($backup . '/' . $name, $this->root . '/' . $name)) {
                    throw new \RuntimeException("cannot move $name back from $backup");
                }
            }
            foreach ($meta['config'] as $file) {
                @copy($backup . '/config/' . $file, $this->root . '/config/' . $file);
            }
            @touch(self::opcacheFile($this->varDir));
            if (!empty($meta['dump'])) {
                $this->step('database', ['n' => 0]);
                (new DbSnapshot($this->pdo()))->restore($backup . '/' . $meta['dump']['file'], $meta['dump']['tables_before']);
            }
            @unlink(self::maintenanceFile($this->varDir));
            @unlink($this->stateDir . '/installed.json');
            $this->state['status'] = 'rolled_back';
            $this->state['finished_at'] = Time::now();
            $this->save();
            Log::info("site returned from $installed to {$meta['version']}");
            ($this->say)('site.step.rolled_back', ['from' => $installed, 'to' => $meta['version']]);
            self::removeTree($away);
            self::removeTree($backup);
            return true;
        } catch (\Throwable $e) {
            Log::error('site rollback failed: ' . $e);
            $this->state['status'] = 'failed';
            $this->state['finished_at'] = Time::now();
            $this->state['error'] = $e instanceof UpdateError
                ? ['key' => $e->key, 'params' => $e->params]
                : ['key' => 'site.error.rollback', 'params' => ['reason' => $e->getMessage()]];
            if (!$e instanceof UpdateError) {
                // Files may be half back: the maintenance page stays until the reason is fixed.
                $this->state['rollback_error'] = $this->state['error'];
            }
            try {
                $this->save();
            } catch (\Throwable) {
            }
            if ($e instanceof UpdateError) {
                throw $e;
            }
            throw new UpdateError('site.error.rollback', ['reason' => $e->getMessage()], '', $e);
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // Package

    /**
     * Reads a package without unpacking it.
     *
     * @return array{version: string, php: ?string, extensions: list<string>, size: int, files: int, entries: list<string>, public: list<string>, migrations: list<string>}
     * @throws UpdateError
     */
    public static function inspect(string $zip): array
    {
        try {
            if (!is_file($zip) || filesize($zip) > self::MAX_PACKAGE_BYTES) {
                throw new \RuntimeException(is_file($zip) ? 'larger than ' . (self::MAX_PACKAGE_BYTES >> 20) . ' MB' : 'file not found');
            }
            $reader = ZipReader::open($zip);
            $size = 0;
            $files = 0;
            $entries = [];
            $public = [];
            $migrations = [];
            foreach ($reader->names() as $name) {
                if (!str_starts_with($name, self::PREFIX)) {
                    throw new \RuntimeException("$name is outside the folder server/");
                }
                $relative = substr($name, strlen(self::PREFIX));
                if ($relative === '') {
                    continue;
                }
                if (!ZipReader::safePath($relative)) {
                    throw new \RuntimeException("unsafe path $name");
                }
                $parts = explode('/', rtrim($relative, '/'));
                if (!in_array($parts[0], self::KEEP, true)) {
                    $entries[$parts[0]] = true;
                }
                if ($parts[0] === 'public' && isset($parts[1])) {
                    $public[$parts[1]] = true;
                }
                if (!str_ends_with($name, '/')) {
                    $size += (int) $reader->size($name);
                    $files++;
                    if (count($parts) === 2 && $parts[0] === 'migrations' && str_ends_with($parts[1], '.sql')) {
                        $migrations[] = $parts[1];
                    }
                }
            }
            foreach (self::REQUIRED as $file) {
                if (!$reader->has(self::PREFIX . $file)) {
                    throw new \RuntimeException("server/$file is missing");
                }
            }
            if (!preg_match("/const VERSION = '(\d+\.\d+\.\d+(?:[.-][0-9A-Za-z.-]+)?)'/", (string) $reader->read(self::PREFIX . 'src/App.php'), $m)) {
                throw new \RuntimeException('no version in server/src/App.php');
            }
            $composer = json_decode((string) $reader->read(self::PREFIX . 'composer.json'), true);
            $require = is_array($composer['require'] ?? null) ? $composer['require'] : [];
            $extensions = [];
            foreach (array_keys($require) as $package) {
                if (str_starts_with((string) $package, 'ext-')) {
                    $extensions[] = substr((string) $package, 4);
                }
            }
        } catch (\RuntimeException $e) {
            throw new UpdateError('site.error.package', ['reason' => $e->getMessage()], '', $e);
        }
        sort($migrations, SORT_STRING);
        return [
            'version' => $m[1],
            'php' => isset($require['php']) ? (string) $require['php'] : null,
            'extensions' => $extensions,
            'size' => $size,
            'files' => $files,
            'entries' => array_keys($entries),
            'public' => array_keys($public),
            'migrations' => $migrations,
        ];
    }

    /** PHP version and extensions the package asks for in composer.json, against the PHP that runs this. */
    public static function checkPlatform(array $info): void
    {
        if ($info['php'] !== null && !self::phpSatisfies($info['php'], PHP_VERSION)) {
            throw new UpdateError('site.error.php', ['version' => $info['version'], 'need' => $info['php'], 'have' => PHP_VERSION]);
        }
        $missing = array_values(array_filter($info['extensions'], fn(string $ext) => !extension_loaded($ext)));
        if ($missing !== []) {
            throw new UpdateError('site.error.extensions', ['version' => $info['version'], 'list' => implode(', ', $missing)]);
        }
    }

    /** Composer constraints of the forms ">=8.3", "^8.3", "~8.3", ">=8.3 <9", "^8.3 || ^9.0", "8.4.*". */
    public static function phpSatisfies(string $constraint, string $version): bool
    {
        $version = (string) preg_replace('/^(\d+\.\d+\.\d+).*$/', '$1', $version);
        foreach (preg_split('/\s*\|\|?\s*/', trim($constraint)) as $alternative) {
            $all = true;
            foreach (preg_split('/[\s,]+/', trim($alternative)) as $atom) {
                if ($atom === '' || $atom === '*' || !preg_match('/^(\^|~|>=|<=|>|<|=|==|!=)?v?(\d+(?:\.\d+){0,2})(\.\*)?$/', $atom, $m)) {
                    continue;
                }
                [$op, $bound] = [$m[1], $m[2]];
                $parts = explode('.', $bound);
                $ok = match (true) {
                    $op === '^' => version_compare($version, $bound, '>=') && (int) explode('.', $version)[0] === (int) $parts[0],
                    $op === '~' => version_compare($version, $bound, '>=') && (count($parts) === 3
                        ? implode('.', array_slice(explode('.', $version), 0, 2)) === $parts[0] . '.' . $parts[1]
                        : (int) explode('.', $version)[0] === (int) $parts[0]),
                    ($m[3] ?? '') !== '' => str_starts_with($version . '.', $bound . '.'),
                    $op === '' || $op === '=' || $op === '==' => version_compare($version, $bound, '=='),
                    default => version_compare($version, $bound, $op),
                };
                $all = $all && $ok;
            }
            if ($all) {
                return true;
            }
        }
        return false;
    }

    private static function checkProcOpen(): void
    {
        $disabled = array_map('trim', explode(',', (string) ini_get('disable_functions')));
        if (!function_exists('proc_open') || in_array('proc_open', $disabled, true)) {
            throw new UpdateError('site.error.proc_open');
        }
    }

    /**
     * @param list<string> $files migration files of the package
     * @return array<string, list<string>> migration file => statements, for files not applied yet
     */
    private function pendingMigrations(string $zip, array $files): array
    {
        $applied = array_flip($this->pdo()->query('SELECT version FROM schema_migrations')->fetchAll(PDO::FETCH_COLUMN));
        $reader = ZipReader::open($zip);
        $pending = [];
        foreach ($files as $file) {
            if (!isset($applied[$file])) {
                $pending[$file] = Migrator::statements((string) $reader->read(self::PREFIX . 'migrations/' . $file));
            }
        }
        return $pending;
    }

    /** @param list<string> $tables tables the database copy will hold */
    private function checkSpace(int $packageBytes, array $tables): void
    {
        $free = @disk_free_space($this->work());
        if ($free === false) {
            return;
        }
        $dump = 0;
        if ($tables !== []) {
            $marks = implode(', ', array_fill(0, count($tables), '?'));
            $statement = $this->pdo()->prepare("SELECT COALESCE(SUM(data_length), 0) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ($marks)");
            $statement->execute($tables);
            $dump = (int) $statement->fetchColumn();
        }
        $need = $packageBytes + $dump + self::SPARE_BYTES;
        if ($free < $need) {
            throw new UpdateError('site.error.space', ['need' => self::megabytes($need), 'free' => self::megabytes((int) $free)]);
        }
    }

    /**
     * Every folder the update moves must be writable by the user that runs it: a folder that changes its parent
     * needs that, and the next update deletes it. Folders unpacked by root once are the usual reason.
     *
     * @param list<string> $entries top-level names of the package
     */
    private function checkWritable(array $entries): void
    {
        $bad = null;
        foreach ([$this->root, $this->work()] as $dir) {
            if (!is_writable($dir)) {
                $bad = $dir;
                break;
            }
        }
        foreach ($entries as $name) {
            $path = $this->root . '/' . $name;
            if ($bad !== null || !is_dir($path) || is_link($path)) {
                continue;
            }
            if (!is_writable($path)) {
                $bad = $path;
                break;
            }
            $items = new \RecursiveIteratorIterator(new \RecursiveDirectoryIterator($path, \FilesystemIterator::SKIP_DOTS), \RecursiveIteratorIterator::SELF_FIRST);
            foreach ($items as $item) {
                if ($item->isDir() && !$item->isLink() && !$item->isWritable()) {
                    $bad = $item->getPathname();
                    break;
                }
            }
        }
        if ($bad !== null) {
            throw new UpdateError('site.error.owner', ['path' => $bad, 'user' => self::currentUser(), 'root' => $this->root]);
        }
    }

    private function unpack(string $zip, string $stage, string $version): void
    {
        self::makeDir($stage);
        try {
            ZipReader::open($zip)->extract(self::PREFIX, $stage, static fn(string $path): bool =>
                $path === 'var' || str_starts_with($path, 'var/')
                || $path === 'config/config.php' || $path === 'config/keys' || str_starts_with($path, 'config/keys/'));
        } catch (\RuntimeException $e) {
            throw new UpdateError('site.error.unpack', ['reason' => $e->getMessage()], '', $e);
        }
        if (self::versionIn($stage . '/src/App.php') !== $version) {
            throw new UpdateError('site.error.unpack', ['reason' => 'the unpacked version differs']);
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // Helpers

    /** The version of the files in the server folder now. */
    public function installedVersion(): string
    {
        return self::versionIn($this->root . '/src/App.php') ?? '0';
    }

    private static function versionIn(string $file): ?string
    {
        return preg_match("/const VERSION = '([^']+)'/", (string) @file_get_contents($file), $m) ? $m[1] : null;
    }

    private function maintenanceOn(string $version): void
    {
        self::makeDir($this->stateDir);
        if (@file_put_contents(self::maintenanceFile($this->varDir), json_encode(['version' => $version, 'since' => time()])) === false) {
            throw new UpdateError('site.error.files', ['reason' => 'cannot write ' . self::maintenanceFile($this->varDir)]);
        }
        $this->state['journal']['maintenance'] = true;
        $this->save();
    }

    /**
     * The folder for the staging copy and the backup: inside var/update when it is on the same disk as the site,
     * otherwise .update in the server folder, so that moving folders is a rename and never a copy.
     */
    private function work(string $name = ''): string
    {
        if ($this->workDir === null) {
            self::makeDir($this->stateDir);
            $same = @stat($this->stateDir)['dev'] === @stat($this->root)['dev'];
            $this->workDir = $same ? $this->stateDir : $this->root . '/.update';
            self::makeDir($this->workDir);
        }
        return $name === '' ? $this->workDir : $this->workDir . '/' . $name;
    }

    private function step(string $step, array $params = []): void
    {
        $this->state['steps'][] = ['step' => $step, 'at' => Time::now()] + ($params === [] ? [] : ['params' => $params]);
        $this->save();
        ($this->say)('site.step.' . $step, $params + ['to' => (string) ($this->state['to'] ?? '')]);
    }

    private function save(): void
    {
        self::writeState($this->varDir, $this->state);
    }

    private function output(string $text): void
    {
        $text = trim(($this->state['output'] ?? '') . "\n" . trim($text));
        $this->state['output'] = strlen($text) > self::OUTPUT_BYTES ? '…' . substr($text, -self::OUTPUT_BYTES) : $text;
    }

    /** @return array{0: int, 1: string} exit code and output (stdout and stderr together) of a PHP script */
    private function child(array $args): array
    {
        $process = @proc_open([$this->php, ...$args], [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['redirect', 1]], $pipes, $this->root);
        if (!is_resource($process)) {
            throw new UpdateError('site.error.proc_open');
        }
        fclose($pipes[0]);
        $output = (string) stream_get_contents($pipes[1]);
        fclose($pipes[1]);
        return [proc_close($process), trim($output)];
    }

    private function pdo(): PDO
    {
        return $this->pdo ??= ($this->connect)();
    }

    /** A bell notice about the result, and the notices it answers marked read; never stops the update. */
    private function notice(string $kind, array $params, array $answers = []): void
    {
        try {
            $statement = $this->pdo()->prepare('INSERT INTO notices (created_at, kind, params) VALUES (?, ?, ?)');
            $statement->execute([Time::now(), $kind, json_encode($params, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)]);
            foreach ($answers as $answered) {
                $this->pdo()->prepare('UPDATE notices SET read_at = ? WHERE kind = ? AND read_at IS NULL')->execute([Time::now(), $answered]);
            }
        } catch (\Throwable $e) {
            Log::warning("site update notice: {$e->getMessage()}");
        }
    }

    public static function currentUser(): string
    {
        if (function_exists('posix_geteuid') && function_exists('posix_getpwuid')) {
            $user = posix_getpwuid(posix_geteuid());
            if (is_array($user) && ($user['name'] ?? '') !== '') {
                return $user['name'];
            }
        }
        return get_current_user();
    }

    private static function megabytes(int $bytes): string
    {
        return number_format($bytes / 1048576, 0, '.', ' ') . ' MB';
    }

    private static function makeDir(string $dir): void
    {
        if (!is_dir($dir) && !@mkdir($dir, 0750, true) && !is_dir($dir)) {
            throw new UpdateError('site.error.files', ['reason' => "cannot create $dir"]);
        }
    }

    /** Deletes a folder with everything in it (a link is removed, not followed). True when nothing is left. */
    public static function removeTree(string $path): bool
    {
        if (is_link($path) || is_file($path)) {
            return @unlink($path);
        }
        if (!is_dir($path)) {
            return true;
        }
        $ok = true;
        foreach (array_diff(scandir($path) ?: [], ['.', '..']) as $item) {
            $ok = self::removeTree($path . '/' . $item) && $ok;
        }
        return @rmdir($path) && $ok;
    }

    private static function copyTree(string $from, string $to): void
    {
        if (is_link($from)) {
            @symlink((string) readlink($from), $to);
            return;
        }
        if (is_file($from)) {
            @copy($from, $to);
            return;
        }
        @mkdir($to, 0755, true);
        foreach (array_diff(scandir($from) ?: [], ['.', '..']) as $item) {
            self::copyTree($from . '/' . $item, $to . '/' . $item);
        }
    }
}
