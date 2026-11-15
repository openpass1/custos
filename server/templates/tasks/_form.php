<?php
/**
 * The task editor (7.5), shared by tasks and templates.
 *
 * @var array $task the task object, possibly with form-only "_" fields
 * @var array<string, array> $credentials accounts the task may use, by id
 * @var bool $templateMode a template: accounts get no passwords here
 * @var bool $readOnly
 * @var list<array{key: string, params: array}> $errors
 */
use Custos\Domain\TaskForm;

$sub = function (string $key) use ($task): array {
    $value = $task[$key] ?? [];
    return is_array($value) ? $value : [];
};
$filters = $sub('filters');
$layout = $sub('layout');
$archive = $sub('archive');
$verify = $sub('verify');
$retention = $sub('retention');
$schedule = $sub('schedule');
$vss = $sub('vss');
$copy = $sub('copy');
$events = $sub('events');
$force = $sub('force_full');
$lines = fn($list) => implode("\n", array_map('strval', is_array($list) ? array_filter($list, 'is_scalar') : []));

/** The account of a source or destination: none, an existing one of the right kind, or a new one typed in here. */
$credentialField = function (string $prefix, array $item) use ($credentials, $templateMode): string {
    $current = $item['credential_id'] ?? null;
    $new = isset($item['_cred']);
    $typed = (array) ($item['_cred'] ?? []);
    ob_start(); ?>
    <?php $cloud = implode('|', TaskForm::CLOUD_KINDS); ?>
    <div class="cred" data-show-if="kind=<?= e(implode('|', array_keys(TaskForm::CREDENTIAL_KINDS))) ?>">
        <label<?= $templateMode ? ' data-show-if="kind!=' . e($cloud) . '"' : '' ?>><?= e(t('task_form.credential')) ?>
            <select name="<?= e($prefix) ?>[credential]" data-credential-select>
                <option value=""><?= e(t('task_form.credential.none')) ?></option>
                <?php foreach ($credentials as $c): ?>
                    <?php if (($c['kind'] ?? '') === 'archive_password') continue; ?>
                    <option value="<?= e($c['id']) ?>" data-kind="<?= e($c['kind'] ?? '') ?>"<?= !$new && $current === $c['id'] ? ' selected' : '' ?>>
                        <?= e(account_label($c)) ?><?= $templateMode ? '' : ' · ' . e(t(!empty($c['has_secret']) ? 'credentials.secret.set' : 'credentials.secret.none')) ?></option>
                <?php endforeach ?>
                <?php if ($current !== null && !$new && !isset($credentials[$current])): ?>
                    <option value="<?= e($current) ?>" selected><?= e(t('task_form.credential.unknown')) ?></option>
                <?php endif ?>
                <?php /* A cloud drive account is connected on the machine only (7.5): no new one here. */ ?>
                <option value="new" data-show-if="kind!=<?= e($cloud) ?>"<?= $new ? ' selected' : '' ?>><?= e(t('task_form.credential.new')) ?></option>
            </select>
        </label>
        <div class="row-fields" data-show-if="credential=new">
            <label><?= e(t('task_form.cred_user')) ?>
                <input type="text" name="<?= e($prefix) ?>[cred_user]" value="<?= e($typed['user'] ?? '') ?>" autocomplete="off" maxlength="255">
            </label>
            <label data-show-if="kind=unc"><?= e(t('task_form.cred_domain')) ?>
                <input type="text" name="<?= e($prefix) ?>[cred_domain]" value="<?= e($typed['domain'] ?? '') ?>" autocomplete="off" maxlength="255"
                       placeholder="<?= e(t('task_form.cred_domain.placeholder')) ?>">
            </label>
            <?php if (!$templateMode): ?>
                <label><?= e(t('task_form.cred_password')) ?>
                    <input type="password" name="<?= e($prefix) ?>[cred_password]" value="" autocomplete="new-password">
                </label>
            <?php endif ?>
        </div>
        <?php if ($templateMode): ?><p class="note" data-show-if="credential=new"><?= e(t('task_form.cred_template')) ?></p><?php endif ?>
        <p class="note" data-show-if="kind=<?= e($cloud) ?>"><?= e(t($templateMode ? 'task_form.cloud.note_template' : 'task_form.cloud.note')) ?></p>
    </div>
    <?php return (string) ob_get_clean();
};

/**
 * Path field whose example follows the kind chosen in its row: the same drive letters and server name everywhere
 * (D:\Data is copied to E:\Backup or \\fileserver\backup). app.js swaps the example when the kind changes.
 */
$pathExamples = [
    'source' => ['folder' => 'D:\\Data', 'file' => 'D:\\Data\\report.xlsx', 'unc' => '\\\\fileserver\\share\\Data'],
    'destination' => ['local' => 'E:\\Backup', 'unc' => '\\\\fileserver\\backup', 'usb' => 'Backup', 'ftp' => '/backup',
                      'ftps' => '/backup', 'sftp' => '/backup', 's3' => 'backup', 'webdav' => '/backup',
                      'gdrive' => 'office/pc01', 'yadisk' => 'office/pc01', 'dropbox' => 'office/pc01'],
];
$pathExample = fn(string $side, string $kind): string =>
    ' placeholder="' . e($pathExamples[$side][$kind] ?? '') . '" data-placeholders="' . e(json_encode($pathExamples[$side], JSON_UNESCAPED_SLASHES)) . '"';

$sourceRow = function (string $i, array $item) use ($credentialField, $pathExample): string {
    $prefix = "sources[$i]";
    $kind = (string) ($item['kind'] ?? 'folder');
    ob_start(); ?>
    <div class="form-row" data-row data-scope>
        <input type="hidden" name="<?= e($prefix) ?>[id]" value="<?= e($item['id'] ?? '') ?>">
        <?php if (isset($item['id'], $item['kind']) && !in_array($kind, TaskForm::SOURCE_KINDS, true)): ?>
            <p class="row-fields"><span><?= badge('info', $kind) ?> <span class="mono"><?= e((string) ($item['path'] ?? '')) ?></span></span>
                <span class="note"><?= e(t('task_form.source.unknown')) ?></span></p>
        <?php else: ?>
            <div class="row-fields">
                <label><?= e(t('task_form.kind')) ?>
                    <select name="<?= e($prefix) ?>[kind]">
                        <?php foreach (TaskForm::SOURCE_KINDS as $k): ?>
                            <option value="<?= e($k) ?>"<?= selected($k, $kind) ?>><?= e(t('task_form.source.kind.' . $k)) ?></option>
                        <?php endforeach ?>
                    </select>
                </label>
                <label class="grow"><?= e(t('task_form.path')) ?>
                    <input type="text" name="<?= e($prefix) ?>[path]" value="<?= e((string) ($item['path'] ?? '')) ?>" class="mono"
                           <?= $pathExample('source', $kind) ?> maxlength="1000">
                </label>
                <label class="check" data-show-if="kind=folder|unc">
                    <input type="checkbox" name="<?= e($prefix) ?>[recursive]" value="1"<?= checked((bool) ($item['recursive'] ?? true)) ?>>
                    <?= e(t('task_form.recursive')) ?></label>
            </div>
            <?= $credentialField($prefix, $item) ?>
        <?php endif ?>
        <button type="button" class="link remove" data-remove-row><?= e(t('task_form.remove')) ?></button>
    </div>
    <?php return (string) ob_get_clean();
};

$destinationRow = function (string $i, array $item) use ($credentialField, $pathExample): string {
    $prefix = "destinations[$i]";
    $kind = (string) ($item['kind'] ?? 'local');
    $field = fn(string $name) => e((string) ($item[$name] ?? ''));
    ob_start(); ?>
    <div class="form-row" data-row data-scope>
        <input type="hidden" name="<?= e($prefix) ?>[id]" value="<?= $field('id') ?>">
        <div class="row-fields">
            <label><?= e(t('task_form.kind')) ?>
                <select name="<?= e($prefix) ?>[kind]" data-destination-kind>
                    <?php foreach (TaskForm::DESTINATION_KINDS as $k): ?>
                        <option value="<?= e($k) ?>"<?= selected($k, $kind) ?>><?= e(t('task_form.destination.kind.' . $k)) ?></option>
                    <?php endforeach ?>
                </select>
            </label>
            <label data-show-if="kind=usb"><?= e(t('task_form.usb_label')) ?>
                <input type="text" name="<?= e($prefix) ?>[usb_label]" value="<?= $field('usb_label') ?>" maxlength="64" placeholder="BACKUP">
            </label>
            <label data-show-if="kind=ftp|ftps|sftp|webdav" class="grow"><?= e(t('task_form.host')) ?>
                <input type="text" name="<?= e($prefix) ?>[host]" value="<?= $field('host') ?>" maxlength="255" placeholder="backup.example.org">
            </label>
            <label data-show-if="kind=ftp|ftps|sftp|webdav"><?= e(t('task_form.port')) ?>
                <input type="number" name="<?= e($prefix) ?>[port]" value="<?= $field('port') ?>" min="1" max="65535" class="short">
            </label>
            <label data-show-if="kind=s3" class="grow"><?= e(t('task_form.endpoint')) ?>
                <input type="text" name="<?= e($prefix) ?>[endpoint]" value="<?= $field('endpoint') ?>" maxlength="255" placeholder="https://s3.example.org">
            </label>
            <label data-show-if="kind=s3"><?= e(t('task_form.bucket')) ?>
                <input type="text" name="<?= e($prefix) ?>[bucket]" value="<?= $field('bucket') ?>" maxlength="255">
            </label>
            <label data-show-if="kind=s3"><?= e(t('task_form.region')) ?>
                <input type="text" name="<?= e($prefix) ?>[region]" value="<?= $field('region') ?>" maxlength="64" class="short">
            </label>
        </div>
        <div class="row-fields">
            <label class="grow"><span data-show-if="kind=local|unc"><?= e(t('task_form.path')) ?></span><span data-show-if="kind!=local|unc|gdrive|yadisk|dropbox"><?= e(t('task_form.path.remote')) ?></span><span data-show-if="kind=gdrive|yadisk|dropbox"><?= e(t('task_form.path.cloud')) ?></span>
                <input type="text" name="<?= e($prefix) ?>[path]" value="<?= $field('path') ?>" class="mono" maxlength="1000"
                       <?= $pathExample('destination', $kind) ?>>
            </label>
            <label><?= e(t('task_form.speed')) ?>
                <input type="number" name="<?= e($prefix) ?>[speed_limit_kbps]" value="<?= (int) ($item['speed_limit_kbps'] ?? 0) ?>" min="0" class="short">
            </label>
            <label data-show-if="kind=gdrive|yadisk|dropbox"><?= e(t('task_form.part_mb')) ?>
                <input type="number" name="<?= e($prefix) ?>[part_mb]" value="<?= (int) ($item['part_mb'] ?? 0) ?>" min="0" max="1000000" class="short">
            </label>
            <label class="check" data-show-if="kind=unc">
                <input type="checkbox" name="<?= e($prefix) ?>[disconnect_after]" value="1"<?= checked((bool) ($item['disconnect_after'] ?? true)) ?>>
                <?= e(t('task_form.disconnect_after')) ?></label>
        </div>
        <div class="row-fields" data-show-if="kind=local|unc|usb">
            <label title="<?= e(t('task_form.worm_hint')) ?>"><?= e(t('task_form.worm_days')) ?>
                <input type="number" name="<?= e($prefix) ?>[worm_days]" value="<?= (int) ($item['worm_days'] ?? 0) ?>" min="0" max="36500" class="short">
            </label>
            <label title="<?= e(t('task_form.worm_hint')) ?>" data-show-if="worm_days!=0|"><?= e(t('task_form.worm_lock_hours')) ?>
                <input type="number" name="<?= e($prefix) ?>[worm_lock_hours]" value="<?= (int) (($item['worm_days'] ?? 0) > 0 ? ($item['worm_lock_hours'] ?? 0) : TaskForm::DEFAULT_WORM_LOCK_HOURS) ?>" min="0" max="<?= TaskForm::MAX_WORM_LOCK_HOURS ?>" class="short">
            </label>
        </div>
        <?= $credentialField($prefix, $item) ?>
        <button type="button" class="link remove" data-remove-row><?= e(t('task_form.remove')) ?></button>
    </div>
    <?php return (string) ob_get_clean();
};

$eventRow = function (string $when, string $i, array $item): string {
    $prefix = "events[$when][$i]";
    $kind = (string) ($item['kind'] ?? 'run');
    ob_start(); ?>
    <div class="form-row" data-row data-scope>
        <div class="row-fields">
            <label><?= e(t('task_form.kind')) ?>
                <select name="<?= e($prefix) ?>[kind]">
                    <?php foreach (TaskForm::EVENT_KINDS as $k): ?>
                        <option value="<?= e($k) ?>"<?= selected($k, $kind) ?>><?= e(t('task_form.event.kind.' . $k)) ?></option>
                    <?php endforeach ?>
                </select>
            </label>
            <label class="grow" data-show-if="kind=run"><?= e(t('task_form.event.command')) ?>
                <input type="text" name="<?= e($prefix) ?>[command]" value="<?= e((string) ($item['command'] ?? '')) ?>" class="mono"
                       placeholder="C:\scripts\dump.cmd" maxlength="1000">
            </label>
            <label data-show-if="kind=run"><?= e(t('task_form.event.args')) ?>
                <input type="text" name="<?= e($prefix) ?>[args]" value="<?= e((string) ($item['args'] ?? '')) ?>" class="mono" maxlength="1000">
            </label>
            <label class="grow" data-show-if="kind=service_stop|service_start"><?= e(t('task_form.event.service')) ?>
                <input type="text" name="<?= e($prefix) ?>[service]" value="<?= e((string) ($item['service'] ?? '')) ?>" maxlength="255" placeholder="MSSQLSERVER">
            </label>
            <label><?= e(t('task_form.event.timeout')) ?>
                <input type="number" name="<?= e($prefix) ?>[timeout_s]" value="<?= (int) ($item['timeout_s'] ?? 3600) ?>" min="1" max="86400" class="short">
            </label>
        </div>
        <button type="button" class="link remove" data-remove-row><?= e(t('task_form.remove')) ?></button>
    </div>
    <?php return (string) ob_get_clean();
};

$archivePasswords = array_filter($credentials, fn($c) => ($c['kind'] ?? '') === 'archive_password');
$passwordChoice = !empty($archive['_password_new']) ? 'new' : (string) ($archive['password_credential_id'] ?? '');
$format = (string) ($archive['format'] ?? 'none');
$type = (string) ($task['backup_type'] ?? 'incremental');
$scheduleType = (string) ($schedule['type'] ?? 'daily');

// Rarely changed sections are folded with a one-line summary of what is set; an error on saving opens them all.
$folded = $errors === [];
$archiveSummary = [t('task_form.archive.format.' . $format)];
if ($format !== 'none') {
    $archiveSummary[] = t('task_form.fold.level', ['n' => (int) ($archive['level'] ?? 5)]);
    if (!empty($archive['encrypt'])) {
        $archiveSummary[] = t('task_form.fold.encrypted');
    }
}
$archiveSummary[] = (int) ($verify['every_runs'] ?? 0) > 0 || (int) ($verify['stored_every_days'] ?? 0) > 0
    ? t('task_form.fold.verify') : t('task_form.fold.no_verify');
$reliabilitySummary = [
    t(!empty($vss['enabled'] ?? true) ? 'task_form.fold.vss' : 'task_form.fold.no_vss'),
    t(!empty($copy['preserve_acl'] ?? true) ? 'task_form.fold.acl' : 'task_form.fold.no_acl'),
    t('task_form.fold.log', ['level' => t('task_form.log_level.' . (string) ($task['log_level'] ?? 'inherit'))]),
];
$eventCount = fn(string $when): int => count(array_filter((array) ($events[$when] ?? []), 'is_array'));
$eventsSummary = [$eventCount('pre') + $eventCount('post') === 0 ? t('task_form.fold.events.none')
    : t('task_form.fold.events', ['pre' => $eventCount('pre'), 'post' => $eventCount('post')])];
if (($events['after'] ?? 'none') !== 'none') {
    $eventsSummary[] = t('task_form.fold.after', ['what' => t('task_form.events.after.' . (string) $events['after'])]);
}
$showAccounts = !$templateMode && $credentials !== [];
$sections = ['general', 'type', 'sources', 'destinations', 'archive', 'retention', 'schedule', 'reliability', 'events'];
if ($showAccounts) {
    $sections[] = 'accounts';
}
$foldOpen = fn(): string => $folded ? '' : ' open';
// An error on saving puts a red dot on its section in the table of contents (where the error says which one).
$errorMap = ['name' => 'general', 'name_long' => 'general', 'source_path' => 'sources', 'no_sources' => 'sources',
             'dest_path' => 'destinations', 'no_destinations' => 'destinations', 'host' => 'destinations', 'bucket' => 'destinations',
             'usb_label' => 'destinations', 'archive_password' => 'archive', 'gfs' => 'retention', 'time' => 'schedule',
             'no_times' => 'schedule', 'no_weekdays' => 'schedule', 'no_month_days' => 'schedule', 'event_service' => 'events',
             'event_command' => 'events'];
$errorSections = [];
foreach ($errors as $error) {
    $section = $errorMap[substr((string) ($error['key'] ?? ''), strlen('task_form.error.'))] ?? null;
    if ($section !== null) {
        $errorSections[$section] = true;
    }
}
?>
<?php if ($errors !== []): ?>
    <div class="alert alert-bad">
        <strong><?= e(t('task_form.errors')) ?></strong>
        <ul><?php foreach ($errors as $error): ?><li><?= e(t_msg($error)) ?></li><?php endforeach ?></ul>
    </div>
<?php endif ?>

<div class="editor-layout">
<nav class="editor-toc" aria-label="<?= e(t('task_form.toc')) ?>" data-toc>
    <?php foreach ($sections as $key): ?>
        <a href="#task-<?= e($key) ?>"<?= isset($errorSections[$key]) ? ' class="has-error"' : '' ?>><?= e(t('task_form.section.' . $key)) ?></a>
    <?php endforeach ?>
</nav>
<fieldset class="editor" data-scope data-editor
          data-seekable="<?= e(implode('|', TaskForm::SEEKABLE)) ?>"
          data-credential-kinds="<?= e(json_encode(TaskForm::CREDENTIAL_KINDS)) ?>"<?= $readOnly ? ' disabled' : '' ?>>

    <section class="panel" id="task-general">
        <h2><?= e(t('task_form.section.general')) ?></h2>
        <div class="row-fields">
            <label class="grow"><?= e(t($templateMode ? 'templates.field.name' : 'task_form.name')) ?>
                <input type="text" name="name" value="<?= e((string) ($task['name'] ?? '')) ?>" maxlength="<?= TaskForm::MAX_NAME ?>" required>
            </label>
            <label><?= e(t('task_form.priority')) ?>
                <input type="number" name="priority" value="<?= (int) ($task['priority'] ?? 0) ?>" min="-1000" max="1000" class="short">
            </label>
        </div>
        <label class="check"><input type="checkbox" name="enabled" value="1"<?= checked((bool) ($task['enabled'] ?? true)) ?>> <?= e(t('task_form.enabled')) ?></label>
        <label class="block"><?= e(t('task_form.comment')) ?>
            <textarea name="comment" rows="2" maxlength="2000"><?= e((string) ($task['comment'] ?? '')) ?></textarea>
        </label>
        <p class="note"><?= e(t('task_form.priority.hint')) ?></p>
    </section>

    <section class="panel" id="task-type">
        <h2><?= e(t('task_form.section.type')) ?></h2>
        <?php $sentence = fn(string $text): string => mb_strtoupper(mb_substr($text, 0, 1)) . mb_substr($text, 1); ?>
        <div class="choices choice-cards">
            <?php foreach (TaskForm::BACKUP_TYPES as $k): ?>
                <label class="choice"><input type="radio" name="backup_type" value="<?= e($k) ?>"<?= checked($type === $k) ?>>
                    <span><strong><?= e(t('backup.' . $k)) ?></strong><small><?= e($sentence(t('task_form.type.hint.' . $k))) ?></small></span></label>
            <?php endforeach ?>
        </div>
        <div class="choices choice-cards" data-show-if="backup_type=mirror">
            <?php foreach (TaskForm::MIRROR_MODES as $k): ?>
                <label class="choice"><input type="radio" name="mirror_mode" value="<?= e($k) ?>"<?= checked(($task['mirror_mode'] ?? 'exact') === $k) ?>>
                    <span><strong><?= e(t('summary.mirror.' . $k)) ?></strong><small><?= e($sentence(t('task_form.mirror.hint.' . $k))) ?></small></span></label>
            <?php endforeach ?>
        </div>
        <div data-show-if="backup_type=incremental|differential">
            <h3><?= e(t('task_form.force_full')) ?></h3>
            <div class="row-fields">
                <label><?= e(t('task_form.force_full.every')) ?>
                    <input type="number" name="force_full[every_runs]" value="<?= (int) ($force['every_runs'] ?? 0) ?>" min="0" max="1000" class="short">
                </label>
                <label><?= e(t('task_form.force_full.weekday')) ?>
                    <select name="force_full[weekday]">
                        <option value=""><?= e(t('task_form.not_set')) ?></option>
                        <?php for ($d = 1; $d <= 7; $d++): ?>
                            <option value="<?= $d ?>"<?= selected($d, $force['weekday'] ?? '') ?>><?= e(t('weekday.' . $d)) ?></option>
                        <?php endfor ?>
                    </select>
                </label>
                <label><?= e(t('task_form.force_full.month_day')) ?>
                    <select name="force_full[month_day]">
                        <option value=""><?= e(t('task_form.not_set')) ?></option>
                        <?php for ($d = 1; $d <= 31; $d++): ?>
                            <option value="<?= $d ?>"<?= selected($d, $force['month_day'] ?? '') ?>><?= $d ?></option>
                        <?php endfor ?>
                        <option value="-1"<?= selected(-1, $force['month_day'] ?? '') ?>><?= e(t('schedule.last_day')) ?></option>
                    </select>
                </label>
            </div>
            <p class="note"><?= e(t('task_form.force_full.hint')) ?></p>
        </div>
    </section>

    <section class="panel" id="task-sources">
        <h2><?= e(t('task_form.section.sources')) ?></h2>
        <div data-rows="sources">
            <?php foreach (array_values(array_filter((array) ($task['sources'] ?? []), 'is_array')) as $i => $item): ?>
                <?= $sourceRow((string) $i, $item) ?>
            <?php endforeach ?>
        </div>
        <template data-row-template="sources"><?= $sourceRow('__i__', ['kind' => 'folder', 'recursive' => true]) ?></template>
        <button type="button" class="secondary" data-add-row="sources"><?= e(t('task_form.add.source')) ?></button>
        <p class="note"><?= e(t('task_form.sources.hint')) ?></p>
        <?php if (db_servers_label($task) !== ''): ?>
            <div class="alert alert-info gap-top"><?= e(t('task_form.databases', ['servers' => db_servers_label($task)])) ?></div>
        <?php endif ?>

        <h3><?= e(t('task_form.filters')) ?></h3>
        <div class="row-fields">
            <label class="grow"><?= e(t('task_form.filters.include')) ?>
                <textarea name="filters[include]" rows="3" class="mono"><?= e($lines($filters['include'] ?? [])) ?></textarea></label>
            <label class="grow"><?= e(t('task_form.filters.exclude')) ?>
                <textarea name="filters[exclude]" rows="3" class="mono"><?= e($lines($filters['exclude'] ?? [])) ?></textarea></label>
            <label class="grow"><?= e(t('task_form.filters.exclude_dirs')) ?>
                <textarea name="filters[exclude_dirs]" rows="3" class="mono"><?= e($lines($filters['exclude_dirs'] ?? [])) ?></textarea></label>
        </div>
        <p class="note"><?= e(t('task_form.filters.hint')) ?></p>
        <div class="row-fields">
            <?php foreach (['skip_hidden', 'skip_system', 'skip_temp'] as $k): ?>
                <label class="check"><input type="checkbox" name="filters[<?= $k ?>]" value="1"<?= checked((bool) ($filters[$k] ?? false)) ?>> <?= e(t('task_form.filters.' . $k)) ?></label>
            <?php endforeach ?>
        </div>
    </section>

    <section class="panel" id="task-destinations">
        <h2><?= e(t('task_form.section.destinations')) ?></h2>
        <div data-rows="destinations">
            <?php foreach (array_values(array_filter((array) ($task['destinations'] ?? []), 'is_array')) as $i => $item): ?>
                <?= $destinationRow((string) $i, $item) ?>
            <?php endforeach ?>
        </div>
        <template data-row-template="destinations"><?= $destinationRow('__i__', ['kind' => 'local', 'disconnect_after' => true]) ?></template>
        <button type="button" class="secondary" data-add-row="destinations"><?= e(t('task_form.add.destination')) ?></button>
        <p class="note"><?= e(t('task_form.destinations.hint')) ?></p>

        <h3><?= e(t('task_form.layout')) ?></h3>
        <label class="block"><?= e(t('task_form.layout.name_template')) ?>
            <input type="text" name="layout[name_template]" value="<?= e((string) ($layout['name_template'] ?? '')) ?>" class="mono" maxlength="255">
            <span class="note"><?= e(t('task_form.layout.name_template.hint')) ?></span>
        </label>
        <div class="row-fields">
            <?php foreach (['separate_folders', 'keep_top_dir', 'delete_empty_dirs'] as $k): ?>
                <label class="check"><input type="checkbox" name="layout[<?= $k ?>]" value="1"<?= checked((bool) ($layout[$k] ?? false)) ?>> <?= e(t('task_form.layout.' . $k)) ?></label>
            <?php endforeach ?>
        </div>
    </section>

    <details class="panel facts fold" id="task-archive"<?= $foldOpen() ?>>
        <summary><?= icon('chevron') ?><h2><?= e(t('task_form.section.archive')) ?></h2> <small class="fold-summary" data-fold-summary="<?= e(t('task_form.fold.changed')) ?>"><?= e(implode(', ', $archiveSummary)) ?></small></summary>
        <div class="choices">
            <?php foreach (TaskForm::ARCHIVE_FORMATS as $k): ?>
                <label class="choice"><input type="radio" name="archive[format]" value="<?= e($k) ?>"<?= checked($format === $k) ?><?= $k === '7z' ? ' data-needs-seekable' : '' ?>>
                    <span><strong><?= e(t('task_form.archive.format.' . $k)) ?></strong></span></label>
            <?php endforeach ?>
        </div>
        <p class="note warn-text" data-seekable-hint hidden><?= e(t('task_form.archive.7z_hint')) ?></p>
        <div class="row-fields" data-show-if="archive[format]=zip|7z">
            <label><?= e(t('task_form.archive.level')) ?>
                <select name="archive[level]">
                    <?php for ($l = 0; $l <= 9; $l++): ?>
                        <option value="<?= $l ?>"<?= selected($l, $archive['level'] ?? 5) ?>><?= $l ?><?= $l === 0 ? ' — ' . e(t('task_form.archive.level.0')) : ($l === 9 ? ' — ' . e(t('task_form.archive.level.9')) : '') ?></option>
                    <?php endfor ?>
                </select>
            </label>
            <label data-show-if="archive[format]=7z"><?= e(t('task_form.archive.split')) ?>
                <input type="number" name="archive[split_mb]" value="<?= (int) ($archive['split_mb'] ?? 0) ?>" min="0" list="split-sizes" class="short">
                <datalist id="split-sizes"><option value="700"></option><option value="4095"></option><option value="4400"></option></datalist>
            </label>
        </div>
        <div data-show-if="archive[format]=zip|7z">
            <label class="check"><input type="checkbox" name="archive[encrypt]" value="1"<?= checked((bool) ($archive['encrypt'] ?? false)) ?>> <?= e(t('task_form.archive.encrypt')) ?></label>
            <label class="check" data-show-if="archive[format]=7z&archive[encrypt]=1"><input type="checkbox" name="archive[encrypt_names]" value="1"<?= checked((bool) ($archive['encrypt_names'] ?? false)) ?>> <?= e(t('task_form.archive.encrypt_names')) ?></label>
            <div class="row-fields" data-show-if="archive[encrypt]=1">
                <label><?= e(t('task_form.archive.password')) ?>
                    <select name="archive[password_credential]">
                        <option value=""><?= e(t('task_form.credential.none')) ?></option>
                        <?php foreach ($archivePasswords as $c): ?>
                            <option value="<?= e($c['id']) ?>"<?= selected($c['id'], $passwordChoice) ?>>
                                <?= e(t('task_form.archive.password.existing', ['id' => substr($c['id'], 0, 8)])) ?><?= $templateMode ? '' : ' · ' . e(t(!empty($c['has_secret']) ? 'credentials.secret.set' : 'credentials.secret.none')) ?></option>
                        <?php endforeach ?>
                        <option value="new"<?= selected('new', $passwordChoice) ?>><?= e(t('task_form.archive.password.new')) ?></option>
                    </select>
                </label>
                <?php if (!$templateMode): ?>
                    <label data-show-if="archive[password_credential]=new"><?= e(t('task_form.archive.password.value')) ?>
                        <input type="password" name="archive[password]" value="" autocomplete="new-password">
                    </label>
                <?php endif ?>
            </div>
            <p class="note" data-show-if="archive[encrypt]=1"><?= e(t($templateMode ? 'task_form.cred_template' : 'task_form.archive.password.hint')) ?></p>
        </div>

        <h3><?= e(t('task_form.verify')) ?></h3>
        <div class="row-fields">
            <label><?= e(t('task_form.verify.every_runs')) ?>
                <input type="number" name="verify[every_runs]" value="<?= (int) ($verify['every_runs'] ?? 0) ?>" min="0" max="1000" class="short"></label>
            <label><?= e(t('task_form.verify.stored_every_days')) ?>
                <input type="number" name="verify[stored_every_days]" value="<?= (int) ($verify['stored_every_days'] ?? 0) ?>" min="0" max="3650" class="short"></label>
        </div>
        <p class="note"><?= e(t('task_form.verify.hint')) ?></p>
    </details>

    <section class="panel" id="task-retention">
        <h2><?= e(t('task_form.section.retention')) ?></h2>
        <label><?= e(t('task_form.retention.mode')) ?>
            <select name="retention[mode]">
                <?php foreach (TaskForm::RETENTION_MODES as $k): ?>
                    <option value="<?= e($k) ?>"<?= selected($k, $retention['mode'] ?? 'count') ?>><?= e(t('task_form.retention.mode.' . $k)) ?></option>
                <?php endforeach ?>
            </select>
        </label>
        <div class="row-fields">
            <label data-show-if="retention[mode]=count"><?= e(t('task_form.retention.keep_full')) ?>
                <input type="number" name="retention[keep_full]" value="<?= (int) ($retention['keep_full'] ?? 12) ?>" min="1" max="10000" class="short"></label>
            <label data-show-if="retention[mode]=age"><?= e(t('task_form.retention.max_age_days')) ?>
                <input type="number" name="retention[max_age_days]" value="<?= (int) ($retention['max_age_days'] ?? 0) ?>" min="0" max="100000" class="short"></label>
            <?php foreach (['daily', 'weekly', 'monthly'] as $k): ?>
                <label data-show-if="retention[mode]=gfs"><?= e(t('task_form.retention.gfs.' . $k)) ?>
                    <input type="number" name="retention[gfs][<?= $k ?>]" value="<?= (int) ($retention['gfs'][$k] ?? 0) ?>" min="0" max="10000" class="short"></label>
            <?php endforeach ?>
            <label data-show-if="backup_type=mirror&mirror_mode=safe"><?= e(t('task_form.retention.safe_mirror_days')) ?>
                <input type="number" name="retention[safe_mirror_days]" value="<?= (int) ($retention['safe_mirror_days'] ?? 30) ?>" min="0" max="100000" class="short"></label>
        </div>
        <p class="note"><?= e(t('task_form.retention.hint')) ?></p>
    </section>

    <section class="panel" id="task-schedule">
        <h2><?= e(t('task_form.section.schedule')) ?></h2>
        <div class="row-fields">
            <label><?= e(t('task_form.schedule.type')) ?>
                <select name="schedule[type]">
                    <?php foreach (TaskForm::SCHEDULE_TYPES as $k): ?>
                        <option value="<?= e($k) ?>"<?= selected($k, $scheduleType) ?>><?= e(t('task_form.schedule.type.' . $k)) ?></option>
                    <?php endforeach ?>
                </select>
            </label>
            <label data-show-if="schedule[type]=daily|weekly|monthly"><?= e(t('task_form.schedule.times')) ?>
                <input type="text" name="schedule[times]" value="<?= e(implode(', ', array_map('strval', (array) ($schedule['times'] ?? [])))) ?>"
                       class="mono" placeholder="00:00, 12:30" maxlength="500"></label>
            <label data-show-if="schedule[type]=interval"><?= e(t('task_form.schedule.interval')) ?>
                <input type="number" name="schedule[interval_minutes]" value="<?= (int) ($schedule['interval_minutes'] ?? 0) ?: 60 ?>" min="1" max="1440" class="short"></label>
        </div>
        <div data-show-if="schedule[type]=daily|weekly">
            <span class="label-text"><?= e(t('task_form.schedule.days_of_week')) ?></span>
            <div class="row-fields">
                <?php $weekdays = array_map('intval', (array) ($schedule['days_of_week'] ?? [])); ?>
                <?php for ($d = 1; $d <= 7; $d++): ?>
                    <label class="check"><input type="checkbox" name="schedule[days_of_week][]" value="<?= $d ?>"<?= checked(in_array($d, $weekdays, true)) ?>> <?= e(t('weekday.' . $d)) ?></label>
                <?php endfor ?>
            </div>
        </div>
        <div data-show-if="schedule[type]=monthly">
            <span class="label-text"><?= e(t('task_form.schedule.days_of_month')) ?></span>
            <div class="row-fields days">
                <?php $monthDays = array_map('intval', (array) ($schedule['days_of_month'] ?? [])); ?>
                <?php foreach ([...range(1, 31), -1] as $d): ?>
                    <label class="check"><input type="checkbox" name="schedule[days_of_month][]" value="<?= $d ?>"<?= checked(in_array($d, $monthDays, true)) ?>> <?= $d === -1 ? e(t('schedule.last_day')) : $d ?></label>
                <?php endforeach ?>
            </div>
        </div>
        <label class="check" data-show-if="schedule[type]!=manual"><input type="checkbox" name="schedule[catch_up]" value="1"<?= checked((bool) ($schedule['catch_up'] ?? true)) ?>> <?= e(t('task_form.schedule.catch_up')) ?></label>
        <p class="note"><?= e(t('task_form.schedule.hint')) ?></p>
    </section>

    <details class="panel facts fold" id="task-reliability"<?= $foldOpen() ?>>
        <summary><?= icon('chevron') ?><h2><?= e(t('task_form.section.reliability')) ?></h2> <small class="fold-summary" data-fold-summary="<?= e(t('task_form.fold.changed')) ?>"><?= e(implode(', ', $reliabilitySummary)) ?></small></summary>
        <div class="row-fields">
            <label class="check"><input type="checkbox" name="vss[enabled]" value="1"<?= checked((bool) ($vss['enabled'] ?? true)) ?>> <?= e(t('task_form.vss')) ?></label>
            <label data-show-if="vss[enabled]=1"><?= e(t('task_form.vss.on_failure')) ?>
                <select name="vss[on_failure]">
                    <?php foreach (TaskForm::VSS_FAILURE as $k): ?>
                        <option value="<?= e($k) ?>"<?= selected($k, $vss['on_failure'] ?? 'abort') ?>><?= e(t('task_form.vss.on_failure.' . $k)) ?></option>
                    <?php endforeach ?>
                </select>
            </label>
        </div>
        <div class="row-fields">
            <label class="check"><input type="checkbox" name="copy[preserve_acl]" value="1"<?= checked((bool) ($copy['preserve_acl'] ?? true)) ?>> <?= e(t('task_form.copy.preserve_acl')) ?></label>
            <label><?= e(t('task_form.copy.read_retries')) ?>
                <input type="number" name="copy[read_retries]" value="<?= (int) ($copy['read_retries'] ?? 1) ?>" min="0" max="5" class="short"></label>
            <label><?= e(t('task_form.log_level')) ?>
                <select name="log_level">
                    <?php foreach (TaskForm::LOG_LEVELS as $k): ?>
                        <option value="<?= e($k) ?>"<?= selected($k, $task['log_level'] ?? 'inherit') ?>><?= e(t('task_form.log_level.' . $k)) ?></option>
                    <?php endforeach ?>
                </select>
            </label>
        </div>
    </details>

    <details class="panel facts fold" id="task-events"<?= $foldOpen() ?>>
        <summary><?= icon('chevron') ?><h2><?= e(t('task_form.section.events')) ?></h2> <small class="fold-summary" data-fold-summary="<?= e(t('task_form.fold.changed')) ?>"><?= e(implode(', ', $eventsSummary)) ?></small></summary>
        <?php foreach (['pre', 'post'] as $when): ?>
            <h3><?= e(t('task_form.events.' . $when)) ?></h3>
            <div data-rows="events-<?= $when ?>">
                <?php foreach (array_values(array_filter((array) ($events[$when] ?? []), 'is_array')) as $i => $item): ?>
                    <?= $eventRow($when, (string) $i, $item) ?>
                <?php endforeach ?>
            </div>
            <template data-row-template="events-<?= $when ?>"><?= $eventRow($when, '__i__', ['kind' => 'run', 'timeout_s' => 3600]) ?></template>
            <button type="button" class="secondary" data-add-row="events-<?= $when ?>"><?= e(t('task_form.add.event')) ?></button>
        <?php endforeach ?>
        <p class="note"><?= e(t('task_form.events.hint')) ?></p>
        <label class="check"><input type="checkbox" name="events[abort_on_pre_failure]" value="1"<?= checked((bool) ($events['abort_on_pre_failure'] ?? true)) ?>> <?= e(t('task_form.events.abort')) ?></label>
        <label><?= e(t('task_form.events.after')) ?>
            <select name="events[after]">
                <?php foreach (TaskForm::AFTER as $k): ?>
                    <option value="<?= e($k) ?>"<?= selected($k, $events['after'] ?? 'none') ?>><?= e(t('task_form.events.after.' . $k)) ?></option>
                <?php endforeach ?>
            </select>
        </label>
    </details>

    <?php if ($showAccounts): ?>
        <details class="panel facts fold" id="task-accounts"<?= $foldOpen() ?>>
            <summary><?= icon('chevron') ?><h2><?= e(t('task_form.section.accounts')) ?></h2> <small class="fold-summary" data-fold-summary="<?= e(t('task_form.fold.changed')) ?>"><?= e(t('task_form.fold.accounts', ['n' => count($credentials)])) ?></small></summary>
            <p class="note"><?= e(t('task_form.accounts.hint')) ?></p>
            <div class="table-wrap">
                <table>
                    <thead><tr><th><?= e(t('credentials.user')) ?></th><th><?= e(t('credentials.secret')) ?></th><th></th></tr></thead>
                    <tbody>
                    <?php foreach ($credentials as $c): ?>
                        <tr>
                            <td><?= e(account_label($c)) ?><?php if (($c['kind'] ?? '') === 'archive_password'): ?> <span class="note"><?= e(substr($c['id'], 0, 8)) ?></span><?php endif ?></td>
                            <td><?= e(t(!empty($c['has_secret']) ? 'credentials.secret.set' : 'credentials.secret.none')) ?></td>
                            <?php if (in_array($c['kind'] ?? '', TaskForm::MACHINE_ONLY_SECRETS, true)): ?>
                                <td class="note"><?= e(t('task_form.accounts.machine_only')) ?></td>
                            <?php else: ?>
                                <td><details class="reveal"><summary><?= e(t('task_form.accounts.replace')) ?></summary>
                                        <input type="password" name="replace_secret[<?= e($c['id']) ?>]" value="" autocomplete="new-password"
                                               aria-label="<?= e(t('task_form.accounts.new_password')) ?>" placeholder="<?= e(t('task_form.accounts.new_password')) ?>">
                                    </details></td>
                            <?php endif ?>
                        </tr>
                    <?php endforeach ?>
                    </tbody>
                </table>
            </div>
        </details>
    <?php endif ?>
</fieldset>
</div>
