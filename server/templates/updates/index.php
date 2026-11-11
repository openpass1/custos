<?php
/**
 * @var list<array> $agentPackages @var list<array> $sevenZipPackages @var ?array $current @var list<array> $agents
 * @var list<array> $groups @var array<string, array> $outdated @var ?array $latestSevenZip @var array<string, array> $sevenZipOutdated
 * @var ?array $github @var string $checkedAt @var string $checkError @var bool $checkOn @var array<string, array> $waitingAgent
 * @var array<string, array> $waitingSevenZip @var int $uploadLimit
 */
use Custos\Domain\Agents;

$storedSevenZip = $sevenZipPackages[0] ?? null;
$canDownload = is_array($github) && isset($github['version'])
    && ($storedSevenZip === null || version_compare($github['version'], $storedSevenZip['version'], '>'));
$short = fn(string $sha) => substr($sha, 0, 16) . '…';
?>
<h1><?= e(t('updates.title')) ?></h1>
<p class="hint"><?= e(t('updates.hint')) ?></p>

<div class="columns">
<section class="panel">
    <h2><?= e(t('updates.agent')) ?></h2>
    <p><?= $current === null ? e(t('updates.current.none')) : e(t('updates.current', ['version' => $current['version']])) ?></p>
    <form method="post" action="<?= e(url('/updates/upload')) ?>" enctype="multipart/form-data" class="stack">
        <?= csrf_field() ?>
        <label><?= e(t('updates.upload')) ?>
            <input type="file" name="package" accept=".zip,application/zip" required>
            <span class="note"><?= e(t('updates.upload.hint', ['limit' => fmt_bytes($uploadLimit)])) ?></span>
        </label>
        <button type="submit"><?= e(t('updates.upload.submit')) ?></button>
    </form>
    <?php if ($agentPackages !== []): ?>
        <div class="table-wrap gap-top">
            <table>
                <thead><tr><th><?= e(t('updates.col.version')) ?></th><th><?= e(t('updates.col.file')) ?></th><th><?= e(t('updates.col.uploaded')) ?></th><th></th></tr></thead>
                <tbody>
                <?php foreach ($agentPackages as $p): ?>
                    <?php $manifest = json_decode((string) $p['manifest'], true) ?: []; ?>
                    <tr>
                        <td><strong><?= e($p['version']) ?></strong> <?php if ($p['is_current']): ?><?= badge('ok', t('updates.is_current')) ?><?php endif ?>
                            <div class="note"><?= e(t('updates.protocol', ['n' => (int) ($manifest['min_protocol'] ?? 0)])) ?></div></td>
                        <td><?= e(fmt_bytes($p['size'])) ?><div class="note mono" title="<?= e($p['sha256']) ?>">SHA-256 <?= e($short($p['sha256'])) ?></div></td>
                        <td class="nowrap"><?= e(fmt_time($p['uploaded_at'])) ?><div class="note"><?= e($p['uploaded_by']) ?></div></td>
                        <td class="actions">
                            <?php if (!$p['is_current']): ?>
                                <form method="post" action="<?= e(url('/updates/agent/' . $p['version'] . '/current')) ?>">
                                    <?= csrf_field() ?><button type="submit" class="secondary"><?= e(t('updates.action.current')) ?></button>
                                </form>
                            <?php endif ?>
                            <form method="post" action="<?= e(url('/updates/agent/' . $p['version'] . '/delete')) ?>"
                                  data-confirm="<?= e(t('updates.confirm.delete', ['version' => $p['version']])) ?>">
                                <?= csrf_field() ?><button type="submit" class="link danger-link"><?= e(t('updates.action.delete')) ?></button>
                            </form>
                        </td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>

<section class="panel">
    <h2>7-Zip</h2>
    <p class="hint"><?= e(t('sevenzip.hint')) ?></p>
    <dl class="props">
        <dt><?= e(t('sevenzip.github')) ?></dt>
        <dd><?php if (is_array($github) && isset($github['version'])): ?>
                <strong><?= e($github['version']) ?></strong>
                <?php if (!empty($github['published_at'])): ?><span class="note"><?= e(t('sevenzip.published', ['time' => fmt_time(gmdate('Y-m-d H:i:s', strtotime((string) $github['published_at'])))])) ?></span><?php endif ?>
                <?php if (empty($github['url'])): ?><div class="note bad-text"><?= e(t('sevenzip.no_asset', ['asset' => (string) ($github['asset'] ?? '')])) ?></div>
                <?php elseif (empty($github['sha256'])): ?><div class="note bad-text"><?= e(t('sevenzip.no_digest')) ?></div><?php endif ?>
            <?php else: ?>—<?php endif ?></dd>
        <dt><?= e(t('sevenzip.checked')) ?></dt>
        <dd><?= e($checkedAt === '' ? t('time.never') : fmt_time($checkedAt)) ?>
            <?php if ($checkError !== ''): ?><div class="note bad-text"><?= e(t('sevenzip.check_error', ['error' => $checkError])) ?></div><?php endif ?></dd>
        <dt><?= e(t('sevenzip.stored')) ?></dt>
        <dd><?= $storedSevenZip === null ? e(t('sevenzip.stored.none')) : '<strong>' . e($storedSevenZip['version']) . '</strong>' ?></dd>
    </dl>
    <div class="buttons">
        <form method="post" action="<?= e(url('/updates/sevenzip/check')) ?>">
            <?= csrf_field() ?><button type="submit" class="secondary"><?= e(t('sevenzip.action.check')) ?></button>
        </form>
        <?php if ($canDownload): ?>
            <form method="post" action="<?= e(url('/updates/sevenzip/download')) ?>">
                <?= csrf_field() ?><button type="submit"><?= e(t('sevenzip.action.download', ['version' => $github['version']])) ?></button>
            </form>
        <?php endif ?>
        <form method="post" action="<?= e(url('/updates/sevenzip/settings')) ?>" class="inline">
            <?= csrf_field() ?>
            <input type="hidden" name="sevenzip_check" value="<?= $checkOn ? '0' : '1' ?>">
            <button type="submit" class="link"><?= e(t($checkOn ? 'sevenzip.action.check_off' : 'sevenzip.action.check_on')) ?></button>
        </form>
    </div>
    <p class="note"><?= e(t($checkOn ? 'sevenzip.check.on' : 'sevenzip.check.off')) ?></p>

    <details class="gap-top">
        <summary><?= e(t('sevenzip.upload')) ?></summary>
        <form method="post" action="<?= e(url('/updates/sevenzip/upload')) ?>" enctype="multipart/form-data" class="stack gap-top">
            <?= csrf_field() ?>
            <p class="note"><?= e(t('sevenzip.upload.hint')) ?></p>
            <label><?= e(t('sevenzip.upload.file')) ?>
                <input type="file" name="installer" accept=".exe" required></label>
            <label><?= e(t('sevenzip.upload.version')) ?>
                <input type="text" name="version" value="" placeholder="24.09" pattern="\d{1,3}\.\d{2}" required class="short"></label>
            <label><?= e(t('sevenzip.upload.sha256')) ?>
                <input type="text" name="sha256" value="" class="mono" maxlength="64" pattern="[0-9a-fA-F]{64}"></label>
            <button type="submit"><?= e(t('sevenzip.upload.submit')) ?></button>
        </form>
    </details>

    <?php if ($sevenZipPackages !== []): ?>
        <div class="table-wrap gap-top">
            <table>
                <thead><tr><th><?= e(t('updates.col.version')) ?></th><th><?= e(t('updates.col.file')) ?></th><th><?= e(t('updates.col.uploaded')) ?></th><th></th></tr></thead>
                <tbody>
                <?php foreach ($sevenZipPackages as $p): ?>
                    <tr>
                        <td><strong><?= e($p['version']) ?></strong><div class="note"><?= e(t('sevenzip.source.' . $p['source'])) ?></div></td>
                        <td><?= e(fmt_bytes($p['size'])) ?><div class="note mono" title="<?= e($p['sha256']) ?>">SHA-256 <?= e($short($p['sha256'])) ?></div></td>
                        <td class="nowrap"><?= e(fmt_time($p['uploaded_at'])) ?><div class="note"><?= e($p['uploaded_by']) ?></div></td>
                        <td><form method="post" action="<?= e(url('/updates/7zip/' . $p['version'] . '/delete')) ?>"
                                  data-confirm="<?= e(t('updates.confirm.delete', ['version' => '7-Zip ' . $p['version']])) ?>">
                                <?= csrf_field() ?><button type="submit" class="link danger-link"><?= e(t('updates.action.delete')) ?></button>
                            </form></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>
</div>

<section class="panel">
    <h2><?= e(t('updates.machines')) ?></h2>
    <?php if ($agents === []): ?>
        <p class="empty"><?= e(t('updates.machines.none')) ?></p>
    <?php else: ?>
    <form method="post" action="<?= e(url('/updates/install')) ?>" class="stack" data-scope>
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
                            <?php if ($a['status'] !== 'approved'): ?><?= badge($a['status']) ?><?php endif ?></td>
                        <td><?= e($a['agent_version'] === '' ? '—' : $a['agent_version']) ?>
                            <?php if (isset($outdated[$a['id']])): ?><?= badge('warning', t('updates.old')) ?><?php endif ?>
                            <?php if (isset($waitingAgent[$a['id']])): ?><div class="note"><?= e(t('updates.waiting', ['version' => (string) ($waitingAgent[$a['id']]['data']['version'] ?? '')])) ?> <?= badge($waitingAgent[$a['id']]['status']) ?></div><?php endif ?></td>
                        <td><?= e($a['sevenzip_version'] ?? '—') ?>
                            <?php if (isset($sevenZipOutdated[$a['id']])): ?><?= badge('warning', t('updates.old')) ?><?php endif ?>
                            <?php if (isset($waitingSevenZip[$a['id']])): ?><div class="note"><?= e(t('updates.waiting', ['version' => (string) ($waitingSevenZip[$a['id']]['data']['version'] ?? '')])) ?> <?= badge($waitingSevenZip[$a['id']]['status']) ?></div><?php endif ?></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
        <div class="choices">
            <label class="choice"><input type="radio" name="target" value="selected" checked> <span><?= e(t('updates.target.selected')) ?></span></label>
            <label class="choice"><input type="radio" name="target" value="outdated"> <span><?= e(t('updates.target.outdated', ['agent' => count($outdated), 'sevenzip' => count($sevenZipOutdated)])) ?></span></label>
            <?php if ($groups !== []): ?>
                <label class="choice"><input type="radio" name="target" value="group"> <span><?= e(t('updates.target.group')) ?></span></label>
            <?php endif ?>
        </div>
        <?php if ($groups !== []): ?>
            <label data-show-if="target=group"><?= e(t('agent.group')) ?>
                <select name="group_id">
                    <?php foreach ($groups as $g): ?><option value="<?= (int) $g['id'] ?>"><?= e($g['name']) ?></option><?php endforeach ?>
                </select>
            </label>
        <?php endif ?>
        <div class="buttons">
            <?php if ($current !== null): ?>
                <button type="submit" name="kind" value="agent"><?= e(t('updates.install.agent', ['version' => $current['version']])) ?></button>
            <?php endif ?>
            <?php if ($storedSevenZip !== null): ?>
                <button type="submit" name="kind" value="7zip"><?= e(t('updates.install.sevenzip', ['version' => $storedSevenZip['version']])) ?></button>
            <?php endif ?>
            <?php if ($current === null && $storedSevenZip === null): ?><span class="note"><?= e(t('updates.install.nothing')) ?></span><?php endif ?>
        </div>
        <p class="note"><?= e(t('updates.install.hint')) ?></p>
    </form>
    <?php endif ?>
</section>
