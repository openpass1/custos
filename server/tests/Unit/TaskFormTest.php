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
