<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Domain\TaskDiff;
use Custos\Domain\TaskEdits;
use Custos\Domain\TaskForm;
use PHPUnit\Framework\TestCase;

/** The task editor's parser (7.5): the checks of the client, unknown fields kept, passwords collected. */
final class TaskFormTest extends TestCase
{
    private const CRED = '11111111-1111-4111-8111-111111111111';
    private const ARCHIVE = '22222222-2222-4222-8222-222222222222';
    private const CLOUD = '33333333-3333-4333-8333-333333333333';

    public function testFormOfATaskGivesTheSameTaskBack(): void
    {
        $task = $this->task();
        $form = TaskForm::parse($task, self::formFor($task), $this->credentials());
        $this->assertSame([], $form->errors);
        $this->assertSame([], TaskDiff::compare($task, $form->task()), 'an unchanged form changes nothing, unknown fields included');
        $this->assertSame('kept', $form->task()['future_field']);
        $this->assertSame(['x' => 1], $form->task()['destinations'][0]['future']);
        $this->assertSame(TaskEdits::EMPTY_OBJECT, $form->task()['events']['future_object']);
    }

    public function testChecksOfTheClient(): void
    {
        $task = $this->task();
        $post = self::formFor($task);
        $post['name'] = ' ';
        $post['sources'] = [];
        $post['destinations'][0]['kind'] = 'ftp';
        $post['destinations'][0]['host'] = 'backup.example.org';
        $post['archive']['format'] = '7z';
        $post['schedule']['times'] = '25:00, 07:30';
        $post['retention']['keep_full'] = '0';
        $post['events']['pre'][] = ['kind' => 'run', 'command' => '', 'args' => '', 'timeout_s' => '60'];
        $form = TaskForm::parse($task, $post, $this->credentials());
        $keys = array_column($form->errors, 'key');
        foreach (['task_form.error.name', 'task_form.error.no_sources', 'task_form.error.7z', 'task_form.error.time',
                  'task_form.error.range', 'task_form.error.event_command'] as $key) {
            $this->assertContains($key, $keys);
        }
        $this->assertSame(['07:30'], $form->view['schedule']['times'], 'valid times stay in the form');
        $this->assertSame(['key' => 'task_form.event.pre', 'params' => ['n' => 2]],
            $form->errors[array_search('task_form.error.event_command', $keys, true)]['params']['what']);
    }

    public function testNewAccountsAndPasswords(): void
    {
        $task = $this->task();
        $post = self::formFor($task);
        $post['destinations'][] = ['id' => '', 'kind' => 'unc', 'path' => '\\\\nas\\backup', 'credential' => 'new',
                                   'cred_user' => 'OFFICE\\backup2', 'cred_password' => 'p@ss', 'speed_limit_kbps' => '0'];
        $post['destinations'][] = ['id' => '', 'kind' => 'usb', 'path' => 'E:\\Backups\\PC', 'usb_label' => 'BACKUP', 'speed_limit_kbps' => ''];
        $post['replace_secret'] = [self::CRED => 'new-password', 'unknown-id' => 'ignored'];
        $form = TaskForm::parse($task, $post, $this->credentials());
        $this->assertSame([], $form->errors);
        $this->assertCount(1, $form->newCredentials);
        $new = array_values($form->newCredentials)[0];
        $this->assertSame(['kind' => 'windows', 'user' => 'backup2', 'domain' => 'OFFICE', 'password' => 'p@ss'],
            array_intersect_key($new, ['kind' => 1, 'user' => 1, 'domain' => 1, 'password' => 1]));
        $destinations = $form->task()['destinations'];
        $this->assertSame($new['id'], $destinations[1]['credential_id']);
        $this->assertMatchesRegularExpression('/^[0-9a-f-]{36}$/', $destinations[1]['id'], 'new items get an id');
        $this->assertSame('Backups\\PC', $destinations[2]['path'], 'a USB disk is found by label: the drive letter goes');
        $this->assertSame([self::CRED => 'new-password'], $form->replacedSecrets);
        $this->assertArrayNotHasKey('_cred', $destinations[1], 'form-only fields never reach the task');

        $post['destinations'][1]['cred_password'] = '';
        $this->assertContains('task_form.error.cred_password', array_column(TaskForm::parse($task, $post, $this->credentials())->errors, 'key'));
        $template = TaskForm::parse($task, $post, $this->credentials(), true);
        $this->assertNotContains('task_form.error.cred_password', array_column($template->errors, 'key'), 'templates keep no passwords');
        $this->assertSame([], $template->replacedSecrets);
    }

    public function testArchivePassword(): void
    {
        $task = $this->task();
        $post = self::formFor($task);
        $post['archive'] = ['format' => 'zip', 'level' => '5', 'encrypt' => '1', 'password_credential' => ''];
        $this->assertContains('task_form.error.archive_password', array_column(TaskForm::parse($task, $post, $this->credentials())->errors, 'key'));

        $post['archive']['password_credential'] = 'new';
        $post['archive']['password'] = 'archive-secret';
        $form = TaskForm::parse($task, $post, $this->credentials());
        $this->assertSame([], $form->errors);
        $id = $form->task()['archive']['password_credential_id'];
        $this->assertSame('archive_password', $form->newCredentials[$id]['kind']);

        $post['archive']['encrypt'] = '';
        $form = TaskForm::parse($task, $post, $this->credentials());
        $this->assertSame([], $form->newCredentials, 'without encryption a typed password is not needed');
        $this->assertSame([], array_diff(TaskForm::credentialRefs($form->task()), [self::CRED]));
    }

    public function testUnknownSourceKindIsKeptAsItWas(): void
    {
        $task = $this->task();
        $remote = ['id' => '33333333-3333-4333-8333-333333333333', 'kind' => 'remote_ssh', 'host' => 'vps.example.org', 'presets' => ['sites']];
        $task['sources'][] = $remote;
        $post = self::formFor($task);
        $post['sources'][1] = ['id' => $remote['id'], 'kind' => 'folder', 'path' => 'ignored'];
        $form = TaskForm::parse($task, $post, $this->credentials());
        $this->assertSame([], $form->errors);
        $this->assertSame($remote, $form->task()['sources'][1]);
    }

    public function testTaskWithDatabasesNeedsNoFileSources(): void
    {
        $db = '66666666-6666-4666-8666-666666666666';
        $task = $this->task();
        $task['sources'] = [];
        $task['db_servers'] = [['id' => '77777777-7777-4777-8777-777777777777', 'engine' => 'mariadb', 'host' => '127.0.0.1',
                                'port' => 3306, 'credential_id' => $db, 'databases' => ['*']]];
        $credentials = $this->credentials() + [$db => ['id' => $db, 'kind' => 'db', 'user' => 'backup', 'domain' => null, 'has_secret' => false]];
        $form = TaskForm::parse($task, self::formFor($task), $credentials);
        $this->assertSame([], $form->errors, 'database servers are enough to have something to copy');
        $this->assertSame($task['db_servers'], $form->task()['db_servers'], 'the editor keeps database servers as they are');
        $this->assertContains($db, TaskForm::credentialRefs($form->task()), 'accounts of database servers are references too');

        $task['db_servers'] = [];
        $form = TaskForm::parse($task, self::formFor($task), $credentials);
        $this->assertContains('task_form.error.no_sources', array_column($form->errors, 'key'));
    }

    public function testCloudDriveTakesOnlyAnAccountConnectedOnTheMachine(): void
    {
        $task = $this->task();
        $post = self::formFor($task);
        $post['destinations'][] = ['id' => '', 'kind' => 'yadisk', 'path' => '/office/pc01/', 'credential' => self::CLOUD, 'part_mb' => '900',
                                   'speed_limit_kbps' => '0'];
        $form = TaskForm::parse($task, $post, $this->credentials());
        $this->assertSame([], $form->errors);
        $cloud = $form->task()['destinations'][1];
        $this->assertSame(['yadisk', 'office/pc01', 900, self::CLOUD], [$cloud['kind'], $cloud['path'], $cloud['part_mb'], $cloud['credential_id']]);
        $this->assertSame([], $form->newCredentials);

        // No account, a new one typed in, or an account of another service: connect it on the machine.
        foreach (['', 'new', self::CRED] as $choice) {
            $post['destinations'][1]['credential'] = $choice;
            $post['destinations'][1]['cred_user'] = 'someone';
            $post['destinations'][1]['cred_password'] = 'secret';
            $form = TaskForm::parse($task, $post, $this->credentials());
            $this->assertSame(['task_form.error.cloud_account'], array_column($form->errors, 'key'), "choice '$choice'");
            $this->assertSame([], $form->newCredentials);
        }

        // 7z cannot go there, and neither can a mirror.
        $post['destinations'][1]['credential'] = self::CLOUD;
        $post['archive']['format'] = '7z';
        $post['backup_type'] = 'mirror';
        $post['mirror_mode'] = 'exact';
        $keys = array_column(TaskForm::parse($task, $post, $this->credentials())->errors, 'key');
        $this->assertContains('task_form.error.7z', $keys);
        $this->assertContains('task_form.error.cloud_mirror', $keys);
    }

    public function testWormOfANasTakesOnlySetsThatAreNeverOverwritten(): void
    {
        $task = $this->task();
        $task['layout']['separate_folders'] = true;
        $post = self::formFor($task);
        $post['destinations'][0]['worm_days'] = '7';
        $post['destinations'][0]['worm_lock_hours'] = '3';
        $post['destinations'][] = ['id' => '', 'kind' => 'sftp', 'host' => 'backup.example.org', 'path' => 'pc01', 'credential' => '',
                                   'worm_days' => '7', 'worm_lock_hours' => '3', 'speed_limit_kbps' => '0'];
        $form = TaskForm::parse($task, $post, $this->credentials());
        $this->assertNotContains('task_form.error.worm_needs_sets', array_column($form->errors, 'key'));
        $destinations = $form->task()['destinations'];
        $this->assertSame([7, 3], [$destinations[0]['worm_days'], $destinations[0]['worm_lock_hours']]);
        $this->assertArrayNotHasKey('worm_days', $destinations[1], 'only folders, shares and USB disks state WORM');

        // Saved without WORM again: the fields stay, set to nothing.
        $post['destinations'][0]['worm_days'] = '0';
        $again = TaskForm::parse($form->task(), $post, $this->credentials())->task()['destinations'][0];
        $this->assertSame([0, 0], [$again['worm_days'], $again['worm_lock_hours']]);

        // A shared folder and a mirror replace files; archives do not.
        $post['destinations'][0]['worm_days'] = '7';
        $post['layout']['separate_folders'] = '';
        $error = array_values(array_filter(TaskForm::parse($task, $post, $this->credentials())->errors,
            fn($e) => $e['key'] === 'task_form.error.worm_needs_sets'));
        $this->assertCount(1, $error);
        $this->assertSame(['what' => ['key' => 'task_form.destination', 'params' => ['n' => 1]], 'days' => 7], $error[0]['params']);
        $post['archive']['format'] = 'zip';
        $this->assertNotContains('task_form.error.worm_needs_sets', array_column(TaskForm::parse($task, $post, $this->credentials())->errors, 'key'));
        $post['archive']['format'] = 'none';
        $post['layout']['separate_folders'] = '1';
        $post['backup_type'] = 'mirror';
        $post['mirror_mode'] = 'exact';
        $this->assertContains('task_form.error.worm_needs_sets', array_column(TaskForm::parse($task, $post, $this->credentials())->errors, 'key'));
    }

    public function testCloudDriveInATemplateNamesNoAccount(): void
    {
        $task = $this->task();
        $post = self::formFor($task);
        $post['destinations'][] = ['id' => '', 'kind' => 'gdrive', 'path' => 'office', 'credential' => '', 'part_mb' => '0', 'speed_limit_kbps' => '0'];
        $form = TaskForm::parse($task, $post, $this->credentials(), template: true);
        $this->assertSame([], $form->errors);
        $this->assertNull($form->task()['destinations'][1]['credential_id'], 'each machine takes its own connected account');
    }

    public function testDiffMatchesListItemsById(): void
    {
        $old = $this->task();
        $new = $old;
        array_unshift($new['sources'], ['id' => '44444444-4444-4444-8444-444444444444', 'kind' => 'folder', 'path' => 'C:\\New', 'credential_id' => null, 'recursive' => true]);
        $new['schedule']['times'] = ['01:00'];
        $new['revision'] = 99;
        $diff = TaskDiff::compare($old, $new);
        $paths = array_column($diff, 'path');
        $this->assertContains('schedule.times', $paths);
        $this->assertContains('sources[44444444].path', $paths);
        $this->assertNotContains('revision', $paths);
        $this->assertSame([], array_values(array_filter($paths, fn($p) => str_starts_with($p, 'sources[aaaaaaaa]'))),
            'the source that was first before is not reported as changed');
    }

    // ---- helpers ----

    private function credentials(): array
    {
        return [
            self::CRED => ['id' => self::CRED, 'kind' => 'windows', 'user' => 'svc-backup', 'domain' => '.', 'has_secret' => true],
            self::ARCHIVE => ['id' => self::ARCHIVE, 'kind' => 'archive_password', 'user' => '', 'domain' => null, 'has_secret' => true],
            self::CLOUD => ['id' => self::CLOUD, 'kind' => 'yadisk', 'user' => 'backup.office', 'domain' => null, 'has_secret' => true],
        ];
    }

    private function task(): array
    {
        $task = TaskForm::defaults();
        $task['id'] = '55555555-5555-4555-8555-555555555555';
        $task['revision'] = 3;
        $task['updated_at'] = '2026-10-12T10:00:00Z';
        $task['updated_by'] = 'local:OFFICE\\admin';
        $task['name'] = 'Documents';
        $task['sources'] = [['id' => 'aaaaaaaa-0000-4000-8000-000000000001', 'kind' => 'folder', 'path' => 'D:\\Data', 'credential_id' => null, 'recursive' => true]];
        $task['destinations'] = [[
            'id' => 'bbbbbbbb-0000-4000-8000-000000000002', 'kind' => 'unc', 'path' => '\\\\fileserver\\backup', 'usb_label' => null,
            'host' => null, 'port' => null, 'endpoint' => null, 'bucket' => null, 'region' => null, 'credential_id' => self::CRED,
            'disconnect_after' => true, 'speed_limit_kbps' => 0, 'future' => ['x' => 1],
        ]];
        $task['events']['pre'] = [['kind' => 'service_stop', 'command' => '', 'args' => '', 'timeout_s' => 600, 'service' => 'AccountingSrv']];
        $task['events']['future_object'] = TaskEdits::EMPTY_OBJECT;
        $task['future_field'] = 'kept';
        return $task;
    }

    /** What a browser sends for the editor showing $task (templates/tasks/_form.php). */
    public static function formFor(array $task): array
    {
        $bool = fn($v) => $v ? '1' : '';
        $post = [
            'name' => $task['name'], 'enabled' => $bool($task['enabled']), 'comment' => $task['comment'], 'priority' => (string) $task['priority'],
            'backup_type' => $task['backup_type'], 'mirror_mode' => (string) $task['mirror_mode'],
            'force_full' => ['every_runs' => (string) $task['force_full']['every_runs'], 'weekday' => (string) $task['force_full']['weekday'],
                             'month_day' => (string) $task['force_full']['month_day']],
            'filters' => ['include' => implode("\n", $task['filters']['include']), 'exclude' => implode("\n", $task['filters']['exclude']),
                          'exclude_dirs' => implode("\n", $task['filters']['exclude_dirs'])],
            'layout' => ['name_template' => $task['layout']['name_template']],
            'archive' => ['format' => $task['archive']['format'], 'level' => (string) $task['archive']['level'], 'split_mb' => (string) $task['archive']['split_mb'],
                          'encrypt' => $bool($task['archive']['encrypt']), 'encrypt_names' => $bool($task['archive']['encrypt_names']),
                          'password_credential' => (string) $task['archive']['password_credential_id']],
            'verify' => ['every_runs' => (string) $task['verify']['every_runs'], 'stored_every_days' => (string) $task['verify']['stored_every_days']],
            'retention' => ['mode' => $task['retention']['mode'], 'keep_full' => (string) $task['retention']['keep_full'],
                            'max_age_days' => (string) $task['retention']['max_age_days'], 'safe_mirror_days' => (string) $task['retention']['safe_mirror_days'],
                            'gfs' => array_map('strval', $task['retention']['gfs'])],
            'schedule' => ['type' => $task['schedule']['type'], 'times' => implode(', ', $task['schedule']['times']),
                           'days_of_week' => array_map('strval', $task['schedule']['days_of_week']),
                           'days_of_month' => array_map('strval', $task['schedule']['days_of_month']),
                           'interval_minutes' => (string) $task['schedule']['interval_minutes'], 'catch_up' => $bool($task['schedule']['catch_up'])],
            'vss' => ['enabled' => $bool($task['vss']['enabled']), 'on_failure' => $task['vss']['on_failure']],
            'copy' => ['preserve_acl' => $bool($task['copy']['preserve_acl']), 'read_retries' => (string) $task['copy']['read_retries']],
            'events' => ['abort_on_pre_failure' => $bool($task['events']['abort_on_pre_failure']), 'after' => $task['events']['after']],
            'log_level' => $task['log_level'],
        ];
        foreach (['skip_hidden', 'skip_system', 'skip_temp'] as $k) {
            $post['filters'][$k] = $bool($task['filters'][$k]);
        }
        foreach (['separate_folders', 'keep_top_dir', 'delete_empty_dirs'] as $k) {
            $post['layout'][$k] = $bool($task['layout'][$k]);
        }
        foreach ($task['sources'] as $s) {
            $post['sources'][] = ['id' => $s['id'], 'kind' => $s['kind'] ?? '', 'path' => $s['path'] ?? '', 'recursive' => $bool($s['recursive'] ?? false),
                                  'credential' => (string) ($s['credential_id'] ?? '')];
        }
        foreach ($task['destinations'] as $d) {
            $row = ['id' => $d['id'], 'kind' => $d['kind'], 'credential' => (string) $d['credential_id'], 'disconnect_after' => $bool($d['disconnect_after']),
                    'speed_limit_kbps' => (string) $d['speed_limit_kbps']];
            foreach (['path', 'usb_label', 'host', 'port', 'endpoint', 'bucket', 'region'] as $k) {
                $row[$k] = (string) $d[$k];
            }
            $row['worm_days'] = (string) ($d['worm_days'] ?? 0);
            $row['worm_lock_hours'] = (string) (($d['worm_days'] ?? 0) > 0 ? $d['worm_lock_hours'] : TaskForm::DEFAULT_WORM_LOCK_HOURS);
            $post['destinations'][] = $row;
        }
        foreach (['pre', 'post'] as $when) {
            foreach ($task['events'][$when] as $e) {
                $post['events'][$when][] = ['kind' => $e['kind'], 'command' => (string) $e['command'], 'args' => (string) $e['args'],
                                            'timeout_s' => (string) $e['timeout_s'], 'service' => (string) $e['service']];
            }
        }
        return $post;
    }
}
