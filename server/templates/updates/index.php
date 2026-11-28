<?php
/**
 * @var list<array> $sevenZipPackages @var list<array> $agents
 * @var list<array> $groups @var array<string, array> $custosOutdated @var bool $custosUpdateAvailable
 * @var array<string, array> $sevenZipOutdated
 * @var ?array $github @var string $checkedAt @var string $checkError @var bool $checkOn @var array<string, array> $waitingAgent
 * @var array<string, array> $waitingSevenZip @var int $uploadLimit
 * @var string $repo @var bool $releasesOn @var string $releasesCheckedAt @var string $releasesError @var ?array $siteLatest
 * @var string $releaseChannel
 * @var ?array $siteNewer @var ?array $siteState @var string $cronLastRun
 */
use Custos\Domain\Agents;
use Custos\Domain\Packages;
use Custos\Domain\Releases;

$storedSevenZip = $sevenZipPackages[0] ?? null;
$published = fn(?array $release) => !empty($release['published_at'])
    ? t('releases.published', ['time' => fmt_time(gmdate('Y-m-d H:i:s', (int) strtotime((string) $release['published_at'])))]) : '';
$preview = fn(array $release) => empty($release['prerelease']) ? '' : badge('warning', t('releases.channel.preview'));
$selection = fn(array $release) => e(Releases::selection($releaseChannel, $release));
$siteStatus = (string) ($siteState['status'] ?? '');
$siteBusy = in_array($siteStatus, ['queued', 'running'], true);
$stateTime = fn(string $field) => isset($siteState[$field]) ? fmt_time((string) $siteState[$field]) : '';
$stateError = function (?array $error): string {
    return $error === null ? '' : t((string) ($error['key'] ?? 'site.error.unexpected'), (array) ($error['params'] ?? []));
};
$cronAge = $cronLastRun === '' ? null : time() - (int) strtotime($cronLastRun . ' UTC');
?>
<h1><?= e(t('updates.title')) ?></h1>
<p class="hint"><?= e(t('updates.hint')) ?></p>

<section class="panel updates-panel">
    <h2><?= e(t('site.title')) ?></h2>
    <p class="hint"><?= e(t('site.hint')) ?></p>
    <form method="post" action="<?= e(url('/updates/releases/channel')) ?>" class="stack">
        <?= csrf_field() ?>
        <label><?= e(t('releases.channel')) ?>
            <select name="releases_channel" aria-describedby="releases-channel-hint">
                <option value="stable"<?= selected(Releases::STABLE, $releaseChannel) ?>><?= e(t('releases.channel.stable')) ?></option>
                <option value="preview"<?= selected(Releases::PREVIEW, $releaseChannel) ?>><?= e(t('releases.channel.preview')) ?></option>
            </select>
        </label>
        <p class="note" id="releases-channel-hint"><?= e(t('releases.channel.hint')) ?></p>
        <div><button type="submit" class="secondary"><?= e(t('releases.channel.save')) ?></button></div>
    </form>
    <?php /* Live part: the state of an update and the release found change by themselves (cron). */ ?>
    <div class="updates-flow" data-part="site">
    <dl class="props">
        <dt><?= e(t('site.installed')) ?></dt>
        <dd><strong><?= e(\Custos\App::VERSION) ?></strong></dd>
        <dt><?= e(t('site.latest')) ?></dt>
        <dd><?php if ($siteLatest !== null): ?>
                <strong><?= e($siteLatest['version']) ?></strong> <?= $preview($siteLatest) ?>
                <?php if ($siteNewer !== null): ?><?= badge('warning', t('site.newer')) ?><?php else: ?><span class="note"><?= e(t('site.up_to_date')) ?></span><?php endif ?>
                <span class="note"><?= e($published($siteLatest)) ?></span>
                <?php if (($siteLatest['url'] ?? '') !== ''): ?> · <a href="<?= e($siteLatest['url']) ?>" target="_blank" rel="noopener noreferrer"><?= e(t('releases.notes')) ?></a><?php endif ?>
            <?php else: ?><?= e(t('site.latest.none')) ?><?php endif ?></dd>
        <dt><?= e(t('releases.checked')) ?></dt>
        <dd><?= e($releasesCheckedAt === '' ? t('time.never') : fmt_time($releasesCheckedAt)) ?>
            <?php if ($releasesError !== ''): ?><div class="note bad-text"><?= e(t('releases.check_error', ['error' => $releasesError])) ?></div><?php endif ?></dd>
    </dl>

    <?php if ($siteStatus === 'queued'): ?>
        <div class="updates-flow">
            <p><?= badge('queued') ?> <?= e(t('site.state.queued', ['version' => (string) $siteState['to'], 'by' => (string) $siteState['by'], 'time' => $stateTime('requested_at')])) ?></p>
            <?php if ($cronAge === null || $cronAge > 180): ?>
                <p class="bad-text"><?= e($cronAge === null ? t('site.state.cron_never') : t('site.state.cron_stale', ['time' => fmt_time($cronLastRun)])) ?></p>
            <?php endif ?>
            <form method="post" action="<?= e(url('/updates/site/cancel')) ?>">
                <?= csrf_field() ?><button type="submit" class="secondary"><?= e(t('site.action.cancel')) ?></button>
            </form>
        </div>
    <?php elseif ($siteStatus === 'running'): ?>
        <p><?= badge('running') ?> <?= e(t('site.state.running', ['version' => (string) ($siteState['to'] ?? '')])) ?></p>
    <?php elseif ($siteStatus === 'done'): ?>
        <p><?= badge('ok', t('status.done')) ?> <?= e(t('site.state.done', ['from' => (string) $siteState['from'], 'to' => (string) $siteState['to'], 'time' => $stateTime('finished_at'), 'by' => (string) $siteState['by']])) ?></p>
        <p class="note"><?= e(t('site.state.done.hint')) ?></p>
    <?php elseif ($siteStatus === 'failed'): ?>
        <p class="bad-text"><?= e(t('site.state.failed', ['version' => (string) ($siteState['to'] ?? '?'), 'time' => $stateTime('finished_at'), 'error' => $stateError($siteState['error'] ?? null)])) ?></p>
        <?php if (isset($siteState['rollback_error'])): ?>
            <p class="bad-text"><?= e($stateError($siteState['rollback_error'])) ?></p>
        <?php else: ?>
            <p><?= e(!empty($siteState['rolled_back']) ? t('site.state.failed.back', ['version' => (string) ($siteState['from'] ?? '')]) : t('site.state.failed.nothing')) ?></p>
        <?php endif ?>
    <?php elseif ($siteStatus === 'rolled_back'): ?>
        <p><?= e(t('site.state.rolled_back', ['from' => (string) $siteState['from'], 'to' => (string) $siteState['to'], 'time' => $stateTime('finished_at'), 'by' => (string) $siteState['by']])) ?></p>
    <?php elseif ($siteStatus === 'cancelled'): ?>
        <p class="note"><?= e(t('site.state.cancelled', ['version' => (string) ($siteState['to'] ?? ''), 'time' => $stateTime('finished_at')])) ?></p>
        <?php if (isset($siteState['error'])): ?><p class="note"><?= e($stateError($siteState['error'])) ?></p><?php endif ?>
    <?php endif ?>
    <?php if (in_array($siteStatus, ['running', 'done', 'failed'], true) && ($siteState['steps'] ?? []) !== []): ?>
        <details<?= in_array($siteStatus, ['running', 'failed'], true) ? ' open' : '' ?> data-key="site-steps">
            <summary><?= e(t('site.state.steps')) ?></summary>
            <div class="updates-flow">
                <ol>
                    <?php foreach ($siteState['steps'] as $step): ?>
                        <li><span class="note nowrap"><?= e(fmt_time((string) $step['at'])) ?></span>
                            <?= e(t('site.step.' . $step['step'], (array) ($step['params'] ?? []) + ['to' => (string) ($siteState['to'] ?? '')])) ?></li>
                    <?php endforeach ?>
                </ol>
                <?php if (($siteState['output'] ?? '') !== ''): ?>
                    <p class="note"><?= e(t('site.state.output')) ?></p>
                    <pre class="log"><?= e((string) $siteState['output']) ?></pre>
                <?php endif ?>
            </div>
        </details>
    <?php endif ?>

    <div class="buttons wrap-actions">
        <?php if ($siteNewer !== null && !$siteBusy): ?>
            <form method="post" action="<?= e(url('/updates/site/install')) ?>" data-confirm="<?= e(t('site.confirm.install', ['version' => $siteNewer['version']])) ?>">
                <?= csrf_field() ?><input type="hidden" name="release_selection" value="<?= $selection($siteNewer) ?>">
                <button type="submit"><?= e(t('site.action.install', ['version' => $siteNewer['version']])) ?></button>
            </form>
        <?php endif ?>
        <form method="post" action="<?= e(url('/updates/releases/check')) ?>">
            <?= csrf_field() ?><button type="submit" class="secondary"><?= e(t('releases.action.check')) ?></button>
        </form>
        <form method="post" action="<?= e(url('/updates/releases/settings')) ?>" class="inline">
            <?= csrf_field() ?>
            <input type="hidden" name="releases_check" value="<?= $releasesOn ? '0' : '1' ?>">
            <button type="submit" class="link"><?= e(t($releasesOn ? 'releases.action.check_off' : 'releases.action.check_on')) ?></button>
        </form>
    </div>
    <p class="note"><?= e($releasesOn ? t('releases.check.on', ['repo' => $repo]) : t('releases.check.off')) ?></p>
    </div>

    <?php if (!$siteBusy): ?>
    <details>
        <summary><?= e(t('site.upload')) ?></summary>
        <form method="post" action="<?= e(url('/updates/site/upload')) ?>" enctype="multipart/form-data" class="stack"
              data-confirm="<?= e(t('site.upload.confirm')) ?>">
            <?= csrf_field() ?>
            <p class="note"><?= e(t('site.upload.hint')) ?></p>
            <label><?= e(t('site.upload.file')) ?>
                <input type="file" name="package" accept=".zip,application/zip" required>
                <span class="note"><?= e(t('updates.upload.hint', ['limit' => fmt_bytes($uploadLimit)])) ?></span>
            </label>
            <button type="submit"><?= e(t('site.upload.submit')) ?></button>
        </form>
    </details>
    <?php endif ?>
</section>

<?php require __DIR__ . '/_packages.php'; ?>

<?php /* Live part: refresh machine versions until the administrator starts choosing an update target. */ ?>
<div data-part="machines">
<section class="panel updates-panel">
    <h2><?= e(t('updates.machines')) ?></h2>
    <?php if ($agents === []): ?>
        <p class="empty"><?= e(t('updates.machines.none')) ?></p>
    <?php else: ?>
    <form method="post" action="<?= e(url('/updates/install')) ?>" class="stack" autocomplete="off" data-update-selection>
        <?= csrf_field() ?>
        <div class="table-wrap">
            <table>
                <thead><tr><th></th><th><?= e(t('runs.col.agent')) ?></th><th><?= e(t('agent.version')) ?></th><th><?= e(t('agent.sevenzip')) ?></th></tr></thead>
                <tbody>
                <?php foreach ($agents as $a): ?>
                    <tr>
                        <td><input type="checkbox" name="agent_ids[]" value="<?= e($a['id']) ?>" aria-label="<?= e(Agents::name($a)) ?>"<?= $a['status'] !== 'approved' ? ' disabled' : '' ?>></td>
                        <td><a href="<?= e(url('/agents/' . $a['id'])) ?>"><?= e(Agents::name($a)) ?></a>
                            <?php if ($a['group_name'] !== null): ?><span class="note"><?= e($a['group_name']) ?></span><?php endif ?>
                            <?= edition_badge($a) ?>
                            <?php if ($a['status'] !== 'approved'): ?><?= badge($a['status']) ?><?php endif ?></td>
                        <td><?= e($a['agent_version'] === '' ? '—' : $a['agent_version']) ?>
                            <?php if (isset($custosOutdated[$a['id']])): ?><?= badge('warning', t('updates.old')) ?><?php endif ?>
                            <?php if (isset($waitingAgent[$a['id']])): ?><div class="note"><?= e(t('updates.waiting', ['version' => (string) ($waitingAgent[$a['id']]['data']['version'] ?? '')])) ?> <?= badge($waitingAgent[$a['id']]['status']) ?></div><?php endif ?></td>
                        <td><?= e($a['sevenzip_version'] ?? '—') ?>
                            <?php if (Packages::agentKind($a) !== 'agent'): ?><div class="note"><?= e(t('updates.sevenzip.' . Packages::agentKind($a))) ?></div><?php endif ?>
                            <?php if (isset($sevenZipOutdated[$a['id']])): ?><?= badge('warning', t('updates.old')) ?><?php endif ?>
                            <?php if (isset($waitingSevenZip[$a['id']])): ?><div class="note"><?= e(t('updates.waiting', ['version' => (string) ($waitingSevenZip[$a['id']]['data']['version'] ?? '')])) ?> <?= badge($waitingSevenZip[$a['id']]['status']) ?></div><?php endif ?></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
        <div class="choices" role="radiogroup" aria-label="<?= e(t('updates.target.label')) ?>">
            <?php /* Without JavaScript the administrator explicitly chooses the scope of the update. */ ?>
            <label class="choice"><input type="radio" name="target" value="outdated" required> <span><?= e(t('updates.target.outdated', ['agent' => count($custosOutdated), 'sevenzip' => count($sevenZipOutdated)])) ?></span></label>
            <label class="choice"><input type="radio" name="target" value="selected"> <span><?= e(t('updates.target.selected')) ?></span></label>
            <?php if ($groups !== []): ?>
                <label class="choice"><input type="radio" name="target" value="group"> <span><?= e(t('updates.target.group')) ?></span></label>
            <?php endif ?>
        </div>
        <?php if ($groups !== []): ?>
            <label><?= e(t('agent.group')) ?>
                <select name="group_id">
                    <option value=""><?= e(t('updates.target.choose_group')) ?></option>
                    <?php foreach ($groups as $g): ?><option value="<?= (int) $g['id'] ?>"><?= e($g['name']) ?></option><?php endforeach ?>
                </select>
            </label>
        <?php endif ?>
        <p class="note" role="status" data-update-selection-empty hidden><?= e(t('updates.target.select_empty')) ?></p>
        <div class="buttons wrap-actions">
            <?php if ($custosUpdateAvailable): ?>
                <button type="submit" name="kind" value="custos"><?= e(t('updates.install.custos')) ?></button>
            <?php endif ?>
            <?php if ($storedSevenZip !== null): ?>
                <button type="submit" name="kind" value="7zip" class="secondary"><?= e(t('updates.install.sevenzip', ['version' => $storedSevenZip['version']])) ?></button>
            <?php endif ?>
            <?php if (!$custosUpdateAvailable && $storedSevenZip === null): ?><span class="note"><?= e(t('updates.install.nothing')) ?></span><?php endif ?>
        </div>
        <p class="note"><?= e(t('updates.install.hint')) ?></p>
        <?php if (in_array(Agents::LINUX, array_column($agents, 'edition'), true)): ?>
            <p class="note"><?= e(t('updates.install.hint_linux')) ?></p>
        <?php endif ?>
    </form>
    <?php endif ?>
</section>
</div>
