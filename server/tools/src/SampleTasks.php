<?php

declare(strict_types=1);

namespace Custos\Tools;

/**
 * Example configuration of a fake agent: tasks like typical office backups (daily and weekly copies to a file
 * server share, 7z and zip archives, keeping 4 to 30 copies) and one quick interval task so that something
 * happens within minutes. All names are neutral examples.
 */
final class SampleTasks
{
    /** @return array{tasks: list<array>, credentials: list<array>} */
    public static function make(string $updatedBy): array
    {
        $credential = ['id' => self::uuid(), 'kind' => 'windows', 'user' => 'svc-backup', 'domain' => '.', 'has_secret' => true];
        $share = fn(string $path) => ['kind' => 'unc', 'path' => $path, 'credential_id' => $credential['id']];
        $tasks = [
            self::task('Documents', $updatedBy, [
                'backup_type' => 'incremental',
                'force_full' => ['every_runs' => 7, 'weekday' => null, 'month_day' => null],
                'sources' => [self::source('folder', 'D:\\Data')],
                'destinations' => [self::destination($share('\\\\fileserver\\backup\\documents'))],
                'retention' => ['keep_full' => 12],
                'verify' => ['every_runs' => 1, 'stored_every_days' => 7],
                'schedule' => ['type' => 'daily', 'times' => ['00:00']],
            ]),
            self::task('Accounting', $updatedBy, [
                'backup_type' => 'full',
                'sources' => [self::source('folder', 'D:\\Accounting')],
                'destinations' => [self::destination($share('\\\\fileserver\\backup\\accounting'))],
                'archive' => ['format' => '7z', 'level' => 5],
                'retention' => ['keep_full' => 4],
                'schedule' => ['type' => 'weekly', 'times' => ['01:00'], 'days_of_week' => [7]],
                'events' => ['pre' => [['kind' => 'service_stop', 'command' => null, 'args' => '', 'timeout_s' => 600, 'service' => 'AccountingSrv']],
                             'post' => [['kind' => 'service_start', 'command' => null, 'args' => '', 'timeout_s' => 600, 'service' => 'AccountingSrv']]],
            ]),
            self::task('Projects', $updatedBy, [
                'backup_type' => 'differential',
                'sources' => [self::source('folder', 'D:\\Projects')],
                'destinations' => [self::destination(['kind' => 'local', 'path' => 'E:\\Backup\\Projects'])],
                'archive' => ['format' => 'zip', 'level' => 3],
                'retention' => ['keep_full' => 30],
                'schedule' => ['type' => 'daily', 'times' => ['12:30'], 'days_of_week' => [1, 2, 3, 4, 5]],
            ]),
            self::task('Desktop', $updatedBy, [
                'backup_type' => 'incremental',
                'sources' => [self::source('folder', '%USERPROFILE%\\Desktop')],
                'destinations' => [self::destination($share('\\\\fileserver\\backup\\desktops'))],
                'retention' => ['mode' => 'age', 'max_age_days' => 30],
                'schedule' => ['type' => 'interval', 'times' => [], 'interval_minutes' => 15],
            ]),
        ];
        return ['tasks' => $tasks, 'credentials' => [$credential]];
    }

    /** Agent settings (A.9). */
    public static function settings(string $serverUrl): array
    {
        return [
            'language' => 'auto',
            'theme' => 'auto',
            'tray_mode' => 'full',
            'allow_all_users_edit' => false,
            'program_password_hash' => null,
            'log_retention_days' => 180,
            'default_log_level' => 'summary',
            'process_priority' => 'low',
            'allow_remote_edit' => true,
            'server_url' => $serverUrl,
            'heartbeat_interval_s' => 60,
        ];
    }

    public static function uuid(): string
    {
        $bytes = random_bytes(16);
        $bytes[6] = chr((ord($bytes[6]) & 0x0f) | 0x40);
        $bytes[8] = chr((ord($bytes[8]) & 0x3f) | 0x80);
        return vsprintf('%s%s-%s-%s-%s-%s%s%s', str_split(bin2hex($bytes), 4));
    }

    /** A full A.9 task: defaults with $overrides merged one level deep. */
    private static function task(string $name, string $updatedBy, array $overrides): array
    {
        $task = [
            'id' => self::uuid(),
            'revision' => 1,
            'updated_at' => gmdate('Y-m-d\TH:i:s\Z'),
            'updated_by' => $updatedBy,
            'name' => $name,
            'enabled' => true,
            'comment' => '',
            'priority' => 0,
            'backup_type' => 'incremental',
            'mirror_mode' => null,
            'force_full' => ['every_runs' => 0, 'weekday' => null, 'month_day' => null],
            'sources' => [],
            'filters' => [
                'include' => [], 'exclude' => ['*.tmp', '~$*'], 'exclude_dirs' => ['Temp'],
                'skip_hidden' => false, 'skip_system' => true, 'skip_temp' => true,
            ],
            'profile_sid' => null,
            'destinations' => [],
            'layout' => [
                'separate_folders' => true,
                'name_template' => '{task} {yyyy}-{MM}-{dd} {HH}-{mm} ({type})',
                'keep_top_dir' => true,
                'delete_empty_dirs' => false,
            ],
            'archive' => ['format' => 'none', 'level' => 5, 'split_mb' => 0, 'encrypt' => false, 'encrypt_names' => false, 'password_credential_id' => null],
            'verify' => ['every_runs' => 0, 'stored_every_days' => 0],
            'retention' => ['mode' => 'count', 'keep_full' => 12, 'max_age_days' => 0, 'gfs' => ['daily' => 7, 'weekly' => 4, 'monthly' => 12], 'safe_mirror_days' => 30],
            'schedule' => ['type' => 'daily', 'times' => ['00:00'], 'days_of_week' => [1, 2, 3, 4, 5, 6, 7], 'days_of_month' => [], 'interval_minutes' => 0, 'catch_up' => true],
            'vss' => ['enabled' => true, 'on_failure' => 'abort'],
            'copy' => ['preserve_acl' => true, 'read_retries' => 1],
            'events' => ['pre' => [], 'post' => [], 'abort_on_pre_failure' => true, 'after' => 'none'],
            'log_level' => 'inherit',
        ];
        foreach ($overrides as $key => $value) {
            $task[$key] = is_array($value) && is_array($task[$key] ?? null) && !array_is_list($task[$key])
                ? array_replace($task[$key], $value)
                : $value;
        }
        return $task;
    }

    private static function source(string $kind, string $path): array
    {
        return ['id' => self::uuid(), 'kind' => $kind, 'path' => $path, 'credential_id' => null, 'recursive' => true];
    }

    private static function destination(array $values): array
    {
        return array_replace([
            'id' => self::uuid(), 'kind' => 'unc', 'path' => null, 'usb_label' => null, 'host' => null, 'port' => null,
            'endpoint' => null, 'bucket' => null, 'region' => null, 'credential_id' => null,
            'disconnect_after' => true, 'speed_limit_kbps' => 0,
        ], $values);
    }
}
