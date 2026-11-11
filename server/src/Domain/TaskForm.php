<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Crypto;

/**
 * The task editor (7.5): turns the editor form into a task object (A.9) with the checks the client makes, keeps every
 * field the editor does not know (A.9: unknown fields survive), and collects the passwords that have to be encrypted
 * for the agent.
 *
 * The same form edits templates (section 8): then accounts are the template's own and have no passwords.
 *
 * Form fields follow the task object: name, backup_type, sources[n][path], archive[level], schedule[times]…
 * Items of sources, destinations and events carry their id in a hidden field, so an edited item keeps its unknown
 * fields too. A field whose name starts with "_" is form state only and never reaches the task.
 */
final class TaskForm
{
    public const BACKUP_TYPES = ['full', 'incremental', 'differential', 'mirror'];
    public const MIRROR_MODES = ['exact', 'safe'];
    public const SOURCE_KINDS = ['folder', 'file', 'unc'];
    public const DESTINATION_KINDS = ['local', 'unc', 'usb', 'ftp', 'ftps', 'sftp', 's3', 'webdav'];
    /** Destinations a 7z writer can go back in (client spec 6.5). */
    public const SEEKABLE = ['local', 'unc', 'usb', 'sftp'];
    public const HOST_KINDS = ['ftp', 'ftps', 'sftp', 'webdav'];
    public const ARCHIVE_FORMATS = ['none', 'zip', '7z'];
    public const RETENTION_MODES = ['count', 'age', 'gfs', 'none'];
    public const SCHEDULE_TYPES = ['daily', 'weekly', 'monthly', 'interval', 'manual'];
    public const EVENT_KINDS = ['run', 'service_stop', 'service_start'];
    public const AFTER = ['none', 'shutdown', 'restart', 'sleep', 'hibernate'];
    public const LOG_LEVELS = ['inherit', 'summary', 'files'];
    public const VSS_FAILURE = ['abort', 'continue'];
    public const MAX_NAME = 200;

    /** Lists of a task whose items have their own id and may refer to an account (db_servers: client stage 2b). */
    public const ITEM_LISTS = ['sources', 'destinations', 'db_servers'];
    /** Account kinds an agent knows (A.9); "db" accounts may have an empty password. */
    public const ACCOUNT_KINDS = ['windows', 'ftp', 'sftp', 's3', 'webdav', 'archive_password', 'db'];

    /** Kind of the account a source or destination needs; kinds missing here take no account. */
    public const CREDENTIAL_KINDS = ['unc' => 'windows', 'ftp' => 'ftp', 'ftps' => 'ftp', 'sftp' => 'sftp', 's3' => 's3', 'webdav' => 'webdav'];

    /** @var list<string> */
    public array $errors = [];
    /** @var array<string, array{id: string, kind: string, user: string, domain: ?string, password: string}> new accounts by id */
    public array $newCredentials = [];
    /** @var array<string, string> new passwords of existing accounts, by account id */
    public array $replacedSecrets = [];
    /** The task to show again in the form (with the form-only "_" fields). */
    public array $view = [];

    /**
     * @param array<string, array> $credentials accounts the task may use, by id: {id, kind, user, domain, has_secret}
     * @param bool $template a template: accounts get no passwords
     */
    private function __construct(private readonly array $credentials, private readonly bool $template)
    {
    }

    /** A new task with the defaults of A.9. */
    public static function defaults(): array
    {
        return [
            'id' => Crypto::uuid4(),
            'revision' => 0,
            'name' => '',
            'enabled' => true,
            'comment' => '',
            'priority' => 0,
            'backup_type' => 'incremental',
            'mirror_mode' => null,
            'force_full' => ['every_runs' => 0, 'weekday' => null, 'month_day' => null],
            'sources' => [],
            'filters' => ['include' => [], 'exclude' => ['~$*', '*.tmp', '*.log'], 'exclude_dirs' => [],
                          'skip_hidden' => false, 'skip_system' => true, 'skip_temp' => true],
            'profile_sid' => null,
            'destinations' => [],
            'layout' => ['separate_folders' => true, 'name_template' => '{task} {yyyy}-{MM}-{dd} {HH}-{mm} ({type})',
                         'keep_top_dir' => true, 'delete_empty_dirs' => false],
            'archive' => ['format' => 'none', 'level' => 5, 'split_mb' => 0, 'encrypt' => false, 'encrypt_names' => false,
                          'password_credential_id' => null],
            'verify' => ['every_runs' => 0, 'stored_every_days' => 0],
            'retention' => ['mode' => 'count', 'keep_full' => 12, 'max_age_days' => 0,
                            'gfs' => ['daily' => 7, 'weekly' => 4, 'monthly' => 12], 'safe_mirror_days' => 30],
            'schedule' => ['type' => 'daily', 'times' => ['00:00'], 'days_of_week' => [1, 2, 3, 4, 5, 6, 7], 'days_of_month' => [],
                           'interval_minutes' => 0, 'catch_up' => true],
            'vss' => ['enabled' => true, 'on_failure' => 'abort'],
            'copy' => ['preserve_acl' => true, 'read_retries' => 1],
            'events' => ['pre' => [], 'post' => [], 'abort_on_pre_failure' => true, 'after' => 'none'],
            'log_level' => 'inherit',
        ];
    }

    /**
     * A copy of a task under a new id, for "clone" and "template from task": new ids for the task, its sources and
     * destinations; the accounts stay.
     */
    public static function copy(array $task, ?string $name = null): array
    {
        $task['id'] = Crypto::uuid4();
        $task['revision'] = 0;
        unset($task['updated_at'], $task['updated_by']);
        foreach (self::ITEM_LISTS as $list) {
            foreach ((array) ($task[$list] ?? []) as $i => $item) {
                if (is_array($item)) {
                    $task[$list][$i]['id'] = Crypto::uuid4();
                }
            }
        }
        if ($name !== null) {
            $task['name'] = $name;
        }
        return $task;
    }

    /**
     * Reads the posted form on top of $base, the task as it is now (or defaults() for a new one).
     *
     * @param array<string, array> $credentials accounts by id
     */
    public static function parse(array $base, array $post, array $credentials, bool $template = false): self
    {
        $form = new self($credentials, $template);
        $form->view = $form->read($base, $post);
        return $form;
    }

    public function valid(): bool
    {
        return $this->errors === [];
    }

    /** The task to send: the view without form-only fields. */
    public function task(): array
    {
        return self::clean($this->view);
    }

    /** Removes form-only "_" keys at any depth. */
    public static function clean(array $data): array
    {
        foreach ($data as $key => $value) {
            if (is_string($key) && str_starts_with($key, '_')) {
                unset($data[$key]);
            } elseif (is_array($value)) {
                $data[$key] = self::clean($value);
            }
        }
        return $data;
    }

    /** Account ids the task refers to (sources, destinations, database servers, archive password). */
    public static function credentialRefs(array $task): array
    {
        $ids = [];
        foreach (self::ITEM_LISTS as $list) {
            foreach ((array) ($task[$list] ?? []) as $item) {
                if (is_array($item) && is_string($item['credential_id'] ?? null)) {
                    $ids[] = $item['credential_id'];
                }
            }
        }
        if (is_string($task['archive']['password_credential_id'] ?? null) && !empty($task['archive']['encrypt'])) {
            $ids[] = $task['archive']['password_credential_id'];
        }
        return array_values(array_unique($ids));
    }

    // ---- reading the form ---------------------------------------------------------------------------------------

    private function read(array $base, array $post): array
    {
        $task = $base;
        $task['name'] = trim(self::str($post, 'name'));
        if ($task['name'] === '') {
            $this->error('task_form.error.name');
        } elseif (mb_strlen($task['name']) > self::MAX_NAME) {
            $this->error('task_form.error.name_long', ['max' => self::MAX_NAME]);
        }
        $task['enabled'] = self::bool($post, 'enabled');
        $task['comment'] = mb_substr(trim(self::str($post, 'comment')), 0, 2000);
        $task['priority'] = $this->int($post, 'priority', -1000, 1000, 'task_form.field.priority');

        $task['backup_type'] = $this->choice($post, 'backup_type', self::BACKUP_TYPES);
        $task['mirror_mode'] = $task['backup_type'] === 'mirror' ? $this->choice($post, 'mirror_mode', self::MIRROR_MODES) : null;
        $force = (array) ($post['force_full'] ?? []);
        $task['force_full'] = array_replace(self::sub($base, 'force_full'), [
            'every_runs' => $this->int($force, 'every_runs', 0, 1000, 'task_form.field.force_every'),
            'weekday' => self::str($force, 'weekday') === '' ? null : $this->int($force, 'weekday', 1, 7, 'task_form.field.force_weekday'),
            'month_day' => self::str($force, 'month_day') === '' ? null : $this->monthDay(self::str($force, 'month_day')),
        ]);

        $task['sources'] = $this->sources(self::sub($base, 'sources'), (array) ($post['sources'] ?? []));
        // A task that dumps databases may have no files to copy (A.9, client stage 2b). The editor does not change
        // database servers; they come from the program on the machine and are kept as they are.
        if ($task['sources'] === [] && self::sub($base, 'db_servers') === []) {
            $this->error('task_form.error.no_sources');
        }
        $task['filters'] = $this->filters(self::sub($base, 'filters'), (array) ($post['filters'] ?? []));
        $task['destinations'] = $this->destinations(self::sub($base, 'destinations'), (array) ($post['destinations'] ?? []));
        $task['layout'] = $this->layout(self::sub($base, 'layout'), (array) ($post['layout'] ?? []));
        $task['archive'] = $this->archive(self::sub($base, 'archive'), (array) ($post['archive'] ?? []), $task['destinations']);
        $verify = (array) ($post['verify'] ?? []);
        $task['verify'] = array_replace(self::sub($base, 'verify'), [
            'every_runs' => $this->int($verify, 'every_runs', 0, 1000, 'task_form.field.verify_every'),
            'stored_every_days' => $this->int($verify, 'stored_every_days', 0, 3650, 'task_form.field.verify_stored'),
        ]);
        $task['retention'] = $this->retention(self::sub($base, 'retention'), (array) ($post['retention'] ?? []));
        $task['schedule'] = $this->schedule(self::sub($base, 'schedule'), (array) ($post['schedule'] ?? []));
        $vss = (array) ($post['vss'] ?? []);
        $task['vss'] = array_replace(self::sub($base, 'vss'), [
            'enabled' => self::bool($vss, 'enabled'),
            'on_failure' => $this->choice($vss, 'on_failure', self::VSS_FAILURE),
        ]);
        $copy = (array) ($post['copy'] ?? []);
        $task['copy'] = array_replace(self::sub($base, 'copy'), [
            'preserve_acl' => self::bool($copy, 'preserve_acl'),
            'read_retries' => $this->int($copy, 'read_retries', 0, 5, 'task_form.field.read_retries'),
        ]);
        $task['events'] = $this->events(self::sub($base, 'events'), (array) ($post['events'] ?? []));
        $task['log_level'] = $this->choice($post, 'log_level', self::LOG_LEVELS);

        if (!$this->template) {
            foreach ((array) ($post['replace_secret'] ?? []) as $id => $password) {
                if (is_string($id) && is_string($password) && $password !== '' && isset($this->credentials[$id])) {
                    $this->replacedSecrets[$id] = $password;
                }
            }
        }
        foreach (self::credentialRefs($task) as $id) {
            if (!isset($this->credentials[$id]) && !isset($this->newCredentials[$id])) {
                $this->error('task_form.error.credential_missing', ['id' => $id]);
            }
        }
        return $task;
    }

    /** @param list<array> $base */
    private function sources(array $base, array $rows): array
    {
        $known = self::byId($base);
        $result = [];
        $n = 0;
        foreach ($rows as $row) {
            if (!is_array($row)) {
                continue;
            }
            $n++;
            $id = self::str($row, 'id');
            $item = $known[$id] ?? ['id' => Crypto::uuid4()];
            if (isset($known[$id]) && !in_array($item['kind'] ?? '', self::SOURCE_KINDS, true)) {
                // A kind this editor does not know (a newer client): kept exactly as it was.
                $result[] = $item;
                continue;
            }
            $kind = $this->choice($row, 'kind', self::SOURCE_KINDS);
            $path = trim(self::str($row, 'path'));
            $item = array_replace($item, [
                'kind' => $kind,
                'path' => $path,
                'recursive' => $kind === 'file' ? false : self::bool($row, 'recursive'),
            ]);
            $item['credential_id'] = $kind === 'unc' ? $this->credential($row, 'windows', 'task_form.source', $n, $item) : null;
            if ($path === '') {
                $this->error('task_form.error.source_path', ['n' => $n]);
            } elseif ($kind === 'unc' && !self::isUnc($path)) {
                $this->error('task_form.error.unc', ['what' => $this->label('task_form.source', $n)]);
            }
            $result[] = $item;
        }
        return $result;
    }

    private function filters(array $base, array $post): array
    {
        return array_replace($base, [
            'include' => self::lines(self::str($post, 'include')),
            'exclude' => self::lines(self::str($post, 'exclude')),
            'exclude_dirs' => self::lines(self::str($post, 'exclude_dirs')),
            'skip_hidden' => self::bool($post, 'skip_hidden'),
            'skip_system' => self::bool($post, 'skip_system'),
            'skip_temp' => self::bool($post, 'skip_temp'),
        ]);
    }

    private function destinations(array $base, array $rows): array
    {
        $known = self::byId($base);
        $result = [];
        $n = 0;
        foreach ($rows as $row) {
            if (!is_array($row)) {
                continue;
            }
            $n++;
            $id = self::str($row, 'id');
            $item = $known[$id] ?? ['id' => Crypto::uuid4()];
            $kind = $this->choice($row, 'kind', self::DESTINATION_KINDS);
            $what = $this->label('task_form.destination', $n);
            $path = trim(self::str($row, 'path'));
            $item = array_replace($item, [
                'kind' => $kind,
                'path' => $path,
                'usb_label' => null,
                'host' => null,
                'port' => null,
                'endpoint' => null,
                'bucket' => null,
                'region' => null,
                'disconnect_after' => self::bool($row, 'disconnect_after'),
                'speed_limit_kbps' => $this->int($row, 'speed_limit_kbps', 0, 10_000_000, 'task_form.field.speed', $n),
            ]);
            switch ($kind) {
                case 'local':
                    if ($path === '') {
                        $this->error('task_form.error.dest_path', ['what' => $what]);
                    }
                    break;
                case 'unc':
                    if ($path === '') {
                        $this->error('task_form.error.dest_path', ['what' => $what]);
                    } elseif (!self::isUnc($path)) {
                        $this->error('task_form.error.unc', ['what' => $what]);
                    }
                    break;
                case 'usb':
                    $item['usb_label'] = trim(self::str($row, 'usb_label'));
                    // The disk is found by its label: a drive letter typed into the folder is dropped (as the client does).
                    $item['path'] = trim(preg_replace('/^[A-Za-z]:/', '', $path), '\\/');
                    if ($item['usb_label'] === '') {
                        $this->error('task_form.error.usb_label', ['what' => $what]);
                    }
                    break;
                case 's3':
                    $item['endpoint'] = self::nullIfEmpty(trim(self::str($row, 'endpoint')));
                    $item['bucket'] = trim(self::str($row, 'bucket'));
                    $item['region'] = self::nullIfEmpty(trim(self::str($row, 'region')));
                    if ($item['bucket'] === '') {
                        $this->error('task_form.error.bucket', ['what' => $what]);
                    }
                    break;
                default:
                    $item['host'] = trim(self::str($row, 'host'));
                    $item['port'] = self::str($row, 'port') === '' ? null : $this->int($row, 'port', 1, 65535, 'task_form.field.port', $n);
                    if ($item['host'] === '') {
                        $this->error('task_form.error.host', ['what' => $what]);
                    }
            }
            $credentialKind = self::CREDENTIAL_KINDS[$kind] ?? null;
            $item['credential_id'] = $credentialKind === null ? null : $this->credential($row, $credentialKind, 'task_form.destination', $n, $item);
            $result[] = $item;
        }
        if ($result === []) {
            $this->error('task_form.error.no_destinations');
        }
        return $result;
    }

    private function layout(array $base, array $post): array
    {
        $template = trim(self::str($post, 'name_template'));
        if ($template === '') {
            $template = (string) self::defaults()['layout']['name_template'];
        }
        return array_replace($base, [
            'separate_folders' => self::bool($post, 'separate_folders'),
            'name_template' => mb_substr($template, 0, 255),
            'keep_top_dir' => self::bool($post, 'keep_top_dir'),
            'delete_empty_dirs' => self::bool($post, 'delete_empty_dirs'),
        ]);
    }

    private function archive(array $base, array $post, array $destinations): array
    {
        $format = $this->choice($post, 'format', self::ARCHIVE_FORMATS);
        $notSeekable = array_values(array_filter(array_unique(array_map(fn($d) => (string) ($d['kind'] ?? ''), $destinations)),
            fn($k) => !in_array($k, self::SEEKABLE, true)));
        if ($format === '7z' && $notSeekable !== []) {
            $this->error('task_form.error.7z', ['kinds' => implode(', ', $notSeekable)]);
        }
        $encrypt = $format !== 'none' && self::bool($post, 'encrypt');
        $archive = array_replace($base, [
            'format' => $format,
            'level' => $this->int($post, 'level', 0, 9, 'task_form.field.level'),
            'split_mb' => $format === '7z' ? $this->int($post, 'split_mb', 0, 10_000_000, 'task_form.field.split') : 0,
            'encrypt' => $encrypt,
            'encrypt_names' => $format === '7z' && $encrypt && self::bool($post, 'encrypt_names'),
        ]);
        $archive['password_credential_id'] = $base['password_credential_id'] ?? null;
        $choice = self::str($post, 'password_credential');
        $password = self::str($post, 'password');
        if ($choice === 'new') {
            $archive['_password_new'] = true;
            if (!$encrypt) {
                // Nothing to protect: the password typed in is not needed.
            } elseif ($this->template || $password !== '') {
                $id = Crypto::uuid4();
                $this->newCredentials[$id] = ['id' => $id, 'kind' => 'archive_password', 'user' => '', 'domain' => null, 'password' => $password];
                $archive['password_credential_id'] = $id;
            } else {
                $this->error('task_form.error.archive_password');
            }
        } elseif ($choice !== '') {
            $archive['password_credential_id'] = isset($this->credentials[$choice]) ? $choice : null;
        }
        if ($encrypt && ($archive['password_credential_id'] ?? null) === null && $choice !== 'new') {
            $this->error('task_form.error.archive_password');
        }
        return $archive;
    }

    private function retention(array $base, array $post): array
    {
        $mode = $this->choice($post, 'mode', self::RETENTION_MODES);
        $gfs = (array) ($post['gfs'] ?? []);
        $retention = array_replace($base, [
            'mode' => $mode,
            'keep_full' => $this->int($post, 'keep_full', 1, 10000, 'task_form.field.keep_full'),
            'max_age_days' => $this->int($post, 'max_age_days', $mode === 'age' ? 1 : 0, 100000, 'task_form.field.max_age'),
            'gfs' => array_replace(self::sub($base, 'gfs'), [
                'daily' => $this->int($gfs, 'daily', 0, 10000, 'task_form.field.gfs_daily'),
                'weekly' => $this->int($gfs, 'weekly', 0, 10000, 'task_form.field.gfs_weekly'),
                'monthly' => $this->int($gfs, 'monthly', 0, 10000, 'task_form.field.gfs_monthly'),
            ]),
            'safe_mirror_days' => $this->int($post, 'safe_mirror_days', 0, 100000, 'task_form.field.safe_mirror'),
        ]);
        if ($mode === 'gfs' && $retention['gfs']['daily'] + $retention['gfs']['weekly'] + $retention['gfs']['monthly'] < 1) {
            $this->error('task_form.error.gfs');
        }
        return $retention;
    }

    private function schedule(array $base, array $post): array
    {
        $type = $this->choice($post, 'type', self::SCHEDULE_TYPES);
        $times = [];
        foreach (preg_split('/[\s,;]+/', self::str($post, 'times'), -1, PREG_SPLIT_NO_EMPTY) as $time) {
            if (!preg_match('/^([01]?\d|2[0-3]):([0-5]\d)$/', $time, $m)) {
                $this->error('task_form.error.time', ['time' => $time]);
                continue;
            }
            $times[] = sprintf('%02d:%s', (int) $m[1], $m[2]);
        }
        $times = array_values(array_unique($times));
        sort($times);
        $weekdays = array_values(array_unique(array_filter(array_map('intval', (array) ($post['days_of_week'] ?? [])), fn($d) => $d >= 1 && $d <= 7)));
        sort($weekdays);
        $monthDays = array_values(array_unique(array_filter(array_map('intval', (array) ($post['days_of_month'] ?? [])),
            fn($d) => $d === -1 || ($d >= 1 && $d <= 31))));
        sort($monthDays);
        if ($monthDays !== [] && $monthDays[0] === -1) {
            // "Last day" goes after the numbered days.
            $monthDays = [...array_slice($monthDays, 1), -1];
        }
        $schedule = array_replace($base, [
            'type' => $type,
            'times' => $times,
            'days_of_week' => $weekdays,
            'days_of_month' => $monthDays,
            'interval_minutes' => $type === 'interval'
                ? $this->int($post, 'interval_minutes', 1, 1440, 'task_form.field.interval')
                : (int) ($base['interval_minutes'] ?? 0),
            'catch_up' => self::bool($post, 'catch_up'),
        ]);
        if (in_array($type, ['daily', 'weekly', 'monthly'], true) && $times === [] && !$this->hasError('task_form.error.time')) {
            $this->error('task_form.error.no_times');
        }
        if (in_array($type, ['daily', 'weekly'], true) && $weekdays === []) {
            $this->error('task_form.error.no_weekdays');
        }
        if ($type === 'monthly' && $monthDays === []) {
            $this->error('task_form.error.no_month_days');
        }
        return $schedule;
    }

    private function events(array $base, array $post): array
    {
        $events = $base;
        foreach (['pre', 'post'] as $when) {
            $list = [];
            $n = 0;
            foreach ((array) ($post[$when] ?? []) as $row) {
                if (!is_array($row)) {
                    continue;
                }
                $n++;
                $kind = $this->choice($row, 'kind', self::EVENT_KINDS);
                $item = [
                    'kind' => $kind,
                    'command' => trim(self::str($row, 'command')),
                    'args' => trim(self::str($row, 'args')),
                    'timeout_s' => $this->int($row, 'timeout_s', 1, 86400, 'task_form.field.timeout', $n),
                    'service' => $kind === 'run' ? null : trim(self::str($row, 'service')),
                ];
                $what = $this->label('task_form.event.' . $when, $n);
                if ($kind === 'run' && $item['command'] === '') {
                    $this->error('task_form.error.event_command', ['what' => $what]);
                }
                if ($kind !== 'run') {
                    $item['command'] = '';
                    $item['args'] = '';
                    if ($item['service'] === '') {
                        $this->error('task_form.error.event_service', ['what' => $what]);
                    }
                }
                $list[] = $item;
            }
            $events[$when] = $list;
        }
        $events['abort_on_pre_failure'] = self::bool($post, 'abort_on_pre_failure');
        $events['after'] = $this->choice($post, 'after', self::AFTER);
        return $events;
    }

    /**
     * The account picked for a source or destination: an existing one, none, or "new" with the fields of the row.
     * Returns the account id or null.
     */
    private function credential(array $row, string $kind, string $labelKey, int $n, array &$item): ?string
    {
        $choice = self::str($row, 'credential');
        unset($item['_cred']);
        if ($choice === 'new') {
            $user = trim(self::str($row, 'cred_user'));
            $domain = trim(self::str($row, 'cred_domain'));
            if ($kind === 'windows' && $domain === '' && str_contains($user, '\\')) {
                [$domain, $user] = explode('\\', $user, 2);
            }
            $password = self::str($row, 'cred_password');
            $item['_cred'] = ['user' => $user, 'domain' => $domain];
            $what = $this->label($labelKey, $n);
            if ($user === '' && $kind !== 'archive_password') {
                $this->error('task_form.error.cred_user', ['what' => $what]);
                return null;
            }
            if (!$this->template && $password === '') {
                $this->error('task_form.error.cred_password', ['what' => $what]);
                return null;
            }
            $id = Crypto::uuid4();
            $this->newCredentials[$id] = [
                'id' => $id, 'kind' => $kind, 'user' => $user, 'domain' => $kind === 'windows' && $domain !== '' ? $domain : null,
                'password' => $password,
            ];
            return $id;
        }
        if ($choice === '') {
            return null;
        }
        return isset($this->credentials[$choice]) ? $choice : null;
    }

    // ---- small helpers --------------------------------------------------------------------------------------------

    private function error(string $key, array $params = []): void
    {
        $this->errors[] = ['key' => $key, 'params' => $params];
    }

    private function hasError(string $key): bool
    {
        foreach ($this->errors as $error) {
            if ($error['key'] === $key) {
                return true;
            }
        }
        return false;
    }

    /** A label such as "источник 2", resolved when shown. */
    private function label(string $key, int $n): array
    {
        return ['key' => $key, 'params' => ['n' => $n]];
    }

    private function int(array $post, string $key, int $min, int $max, string $field, ?int $n = null): int
    {
        $raw = trim(self::str($post, $key));
        if ($raw === '' && $min <= 0 && $max >= 0) {
            return 0;
        }
        if (!preg_match('/^-?\d{1,9}$/', $raw) || (int) $raw < $min || (int) $raw > $max) {
            $this->error('task_form.error.range', ['field' => ['key' => $field, 'params' => ['n' => $n]], 'min' => $min, 'max' => $max]);
            return max($min, min($max, (int) $raw));
        }
        return (int) $raw;
    }

    private function monthDay(string $raw): ?int
    {
        $value = (int) $raw;
        if (!preg_match('/^-?\d{1,2}$/', trim($raw)) || ($value !== -1 && ($value < 1 || $value > 31))) {
            $this->error('task_form.error.range', ['field' => ['key' => 'task_form.field.force_month_day', 'params' => []], 'min' => 1, 'max' => 31]);
            return null;
        }
        return $value;
    }

    /** @param list<string> $allowed the first one is the fallback */
    private function choice(array $post, string $key, array $allowed): string
    {
        $value = self::str($post, $key);
        return in_array($value, $allowed, true) ? $value : $allowed[0];
    }

    /** A nested object of the stored task as an array; an empty JSON object counts as empty. */
    private static function sub(array $base, string $key): array
    {
        $value = $base[$key] ?? [];
        if (!is_array($value)) {
            return [];
        }
        unset($value['{}']);
        return $value;
    }

    private static function str(array $post, string $key): string
    {
        $value = $post[$key] ?? '';
        return is_string($value) ? $value : '';
    }

    private static function bool(array $post, string $key): bool
    {
        return in_array(self::str($post, $key), ['1', 'on', 'true'], true);
    }

    private static function nullIfEmpty(string $value): ?string
    {
        return $value === '' ? null : $value;
    }

    /** @return list<string> one item per line, without empty lines */
    private static function lines(string $text): array
    {
        return array_values(array_filter(array_map('trim', preg_split('/\R/', $text)), fn($l) => $l !== ''));
    }

    private static function isUnc(string $path): bool
    {
        // Variables are expanded on the machine, so a path that starts with one is left to the agent.
        return str_starts_with($path, '\\\\') || str_starts_with($path, '%') || str_starts_with($path, '{');
    }

    /** @return array<string, array> */
    private static function byId(array $items): array
    {
        $result = [];
        foreach ($items as $item) {
            if (is_array($item) && is_string($item['id'] ?? null)) {
                $result[$item['id']] = $item;
            }
        }
        return $result;
    }
}
