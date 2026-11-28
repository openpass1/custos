<?php
use Custos\Domain\Agents;
use Custos\Domain\Packages;

/** @var array<string, array{latest: ?array, download: bool, packages: list<array>, current: ?array}> $catalog */
/** @var ?array $desktopLatest @var ?array $storedSevenZip @var ?array $github @var int $uploadLimit */
$labels = [
    'agent' => t('catalog.windows'), Agents::LEGACY => t('catalog.legacy'), Agents::MACOS => 'macOS',
    Agents::LINUX => t('catalog.linux'), 'desktop' => 'Linux Desktop', '7zip' => '7-Zip',
];
$downloadSevenZip = isset($github['version']) && !empty($github['url']) && !empty($github['sha256'])
    && ($storedSevenZip === null || version_compare($github['version'], $storedSevenZip['version'], '>'));
$rows = $catalog + [
    'desktop' => [
        'latest' => $desktopLatest, 'download' => false,
        'packages' => array_values(array_filter($catalog[Agents::LINUX]['packages'], Packages::hasDesktopInstallers(...))),
        'current' => $catalog[Agents::LINUX]['current'],
    ],
    '7zip' => ['latest' => $github, 'download' => $downloadSevenZip, 'packages' => $sevenZipPackages, 'current' => $storedSevenZip],
];
if ($rows['desktop']['current'] !== null && !Packages::hasDesktopInstallers($rows['desktop']['current'])) {
    $rows['desktop']['current'] = null;
}
$assign = function (string $kind, string $version): void {
    $key = $kind === Agents::LINUX ? 'catalog.confirm.assign_linux' : 'catalog.confirm.assign'; ?>
    <form method="post" action="<?= e(url('/updates/' . $kind . '/' . $version . '/current')) ?>"
          data-confirm="<?= e(t($key, ['version' => $version])) ?>">
        <?= csrf_field() ?><button type="submit" class="secondary"><?= e(t('catalog.assign', ['version' => $version])) ?></button>
    </form>
<?php };
?>
<section class="panel updates-panel" aria-labelledby="package-catalog-title">
    <h2 id="package-catalog-title"><?= e(t('catalog.title')) ?></h2>
    <details class="catalog-disclosure">
        <summary><?= e(t('catalog.upload')) ?></summary>
        <form method="post" action="<?= e(url('/updates/upload')) ?>" enctype="multipart/form-data" class="stack" data-package-upload>
            <?= csrf_field() ?>
            <label><?= e(t('catalog.upload.file')) ?>
                <input type="file" name="package" accept=".zip,.exe" required>
                <span class="note"><?= e(t('catalog.upload.hint')) ?></span>
                <span class="note"><?= e(t('updates.upload.hint', ['limit' => fmt_bytes($uploadLimit)])) ?></span></label>
            <div class="stack" data-installer-fields>
                <label><?= e(t('catalog.upload.version')) ?>
                    <input type="text" name="version" placeholder="26.06" pattern="\d{1,3}\.\d{2}" class="short"></label>
                <label><?= e(t('sevenzip.upload.sha256')) ?>
                    <input type="text" name="sha256" class="mono" maxlength="64" pattern="[0-9a-fA-F]{64}"></label>
            </div>
            <div><button type="submit"><?= e(t('updates.upload.submit')) ?></button></div>
        </form>
    </details>
    <table class="package-catalog" role="table" aria-labelledby="package-catalog-title">
        <thead role="rowgroup"><tr role="row">
            <th scope="col" role="columnheader"><?= e(t('catalog.platform')) ?></th>
            <th scope="col" role="columnheader"><?= e(t('client.github')) ?></th>
            <th scope="col" role="columnheader"><?= e(t('sevenzip.stored')) ?></th>
            <th scope="col" role="columnheader"><?= e(t('catalog.distribution')) ?></th>
            <th scope="col" role="columnheader"><?= e(t('catalog.action')) ?></th>
        </tr></thead>
        <tbody role="rowgroup">
        <?php foreach ($rows as $kind => $row): ?>
            <?php $version = $row['current']['version'] ?? null;
                  $stored = $row['packages'][0]['version'] ?? null;
                  $release = $row['latest']; ?>
            <tr role="row" data-package="<?= e($kind) ?>">
                <th scope="row" role="rowheader"><?= e($labels[$kind]) ?></th>
                <td role="cell">
                    <span class="catalog-cell-label"><?= e(t('client.github')) ?></span>
                    <?php if (isset($release['version'])): ?>
                        <?php if (!empty($release['url']) && $kind !== '7zip'): ?>
                            <a href="<?= e($release['url']) ?>" target="_blank" rel="noopener noreferrer"><?= e($release['version']) ?></a>
                        <?php else: ?><strong><?= e($release['version']) ?></strong><?php endif ?>
                        <?= $preview($release) ?>
                        <?php if ($kind === 'desktop'): ?><div class="note">DEB / RPM</div><?php endif ?>
                    <?php else: ?>—<?php endif ?>
                </td>
                <td role="cell">
                    <span class="catalog-cell-label"><?= e(t('sevenzip.stored')) ?></span>
                    <?php if ($row['packages'] === []): ?>—
                    <?php else: ?>
                        <span class="catalog-versions"><?= e(implode(', ', array_column($row['packages'], 'version'))) ?></span>
                    <?php endif ?>
                    <?php if ($kind === 'desktop' && $row['packages'] !== []): ?><div class="note"><?= e(t('catalog.desktop.shared')) ?></div><?php endif ?>
                </td>
                <td role="cell">
                    <span class="catalog-cell-label"><?= e(t('catalog.distribution')) ?></span>
                    <?php if ($version !== null): ?>
                        <strong><?= e($version) ?></strong>
                        <?php if ($kind === '7zip'): ?><div class="note"><?= e(t('catalog.sevenzip.automatic')) ?></div><?php endif ?>
                        <?php if ($kind === 'desktop'): ?><div class="note"><?= e(t('catalog.desktop.shared')) ?></div><?php endif ?>
                    <?php else: ?>—<?php endif ?>
                </td>
                <td class="catalog-action wrap-actions" role="cell">
                    <?php if ($row['download']): ?>
                        <?php $part = $kind === 'agent' ? 'client' : $kind; ?>
                        <form method="post" action="<?= e(url($kind === '7zip' ? '/updates/sevenzip/download' : "/updates/releases/$part")) ?>">
                            <?= csrf_field() ?>
                            <?php if ($kind !== '7zip'): ?><input type="hidden" name="release_selection" value="<?= $selection($release) ?>"><?php endif ?>
                            <button type="submit" class="secondary"><?= e(t('catalog.download')) ?></button>
                        </form>
                    <?php elseif ($kind === 'desktop' && !empty($release['url'])): ?>
                        <a href="<?= e($release['url']) ?>" target="_blank" rel="noopener noreferrer"><?= e(t('catalog.desktop.install')) ?></a>
                    <?php elseif ($kind !== '7zip' && $kind !== 'desktop' && $stored !== null && ($version === null || version_compare($stored, $version, '>'))): ?>
                        <?php $assign($kind, $stored) ?>
                    <?php else: ?>—<?php endif ?>
                </td>
            </tr>
        <?php endforeach ?>
        </tbody>
    </table>

    <details class="catalog-disclosure">
        <summary><?= e(t('catalog.saved')) ?></summary>
        <?php $anySaved = false; ?>
        <?php foreach ($rows as $kind => $row): ?>
            <?php if ($kind === 'desktop' || $row['packages'] === []) { continue; } ?>
            <?php $anySaved = true; ?>
            <section class="updates-flow catalog-files" aria-label="<?= e($labels[$kind]) ?>">
                <h3><?= e($labels[$kind]) ?></h3>
                <ul>
                <?php foreach ($row['packages'] as $package): ?>
                    <?php $version = $package['version']; $active = $version === ($row['current']['version'] ?? null); ?>
                    <li>
                        <div>
                            <strong><?= e($version) ?></strong> <?php if ($active): ?><?= badge('ok', t('catalog.distribution')) ?><?php endif ?>
                            <div class="note"><?= e($package['file_name']) ?> · <?= e(fmt_bytes($package['size'])) ?></div>
                            <div class="note"><?= e(fmt_time($package['uploaded_at'])) ?> · <?= e($package['uploaded_by']) ?></div>
                            <div class="note mono catalog-checksum">SHA-256 <?= e($package['sha256']) ?></div>
                        </div>
                        <div class="buttons wrap-actions">
                            <?php if (!$active && $kind !== '7zip'): ?><?php $assign($kind, $version) ?><?php endif ?>
                            <?php $deleteKey = in_array($kind, Agents::EDITIONS, true) ? 'updates.confirm.delete_' . $kind : 'updates.confirm.delete'; ?>
                            <form method="post" action="<?= e(url('/updates/' . $kind . '/' . $version . '/delete')) ?>"
                                  data-confirm="<?= e(t($deleteKey, ['version' => $kind === '7zip' ? "7-Zip $version" : $version])) ?>">
                                <?= csrf_field() ?><button type="submit" class="link danger-link"><?= e(t('updates.action.delete')) ?></button>
                            </form>
                        </div>
                    </li>
                <?php endforeach ?>
                </ul>
            </section>
        <?php endforeach ?>
        <?php if (!$anySaved): ?><p class="note"><?= e(t('catalog.saved.empty')) ?></p><?php endif ?>
    </details>

    <details class="catalog-disclosure">
        <summary><?= e(t('catalog.sevenzip.check')) ?></summary>
        <div class="updates-flow">
            <p><?= e(t('sevenzip.checked')) ?> · <?= e($checkedAt === '' ? t('time.never') : fmt_time($checkedAt)) ?></p>
            <?php if ($checkError !== ''): ?><p class="note bad-text"><?= e(t('sevenzip.check_error', ['error' => $checkError])) ?></p><?php endif ?>
            <?php if (isset($github['version']) && empty($github['url'])): ?>
                <p class="note bad-text"><?= e(t('sevenzip.no_asset', ['asset' => (string) ($github['asset'] ?? '')])) ?></p>
            <?php elseif (isset($github['version']) && empty($github['sha256'])): ?>
                <p class="note bad-text"><?= e(t('sevenzip.no_digest')) ?></p>
            <?php endif ?>
            <div class="buttons wrap-actions">
                <form method="post" action="<?= e(url('/updates/sevenzip/check')) ?>">
                    <?= csrf_field() ?><button type="submit" class="secondary"><?= e(t('sevenzip.action.check')) ?></button>
                </form>
                <form method="post" action="<?= e(url('/updates/sevenzip/settings')) ?>">
                    <?= csrf_field() ?>
                    <input type="hidden" name="sevenzip_check" value="<?= $checkOn ? '0' : '1' ?>">
                    <button type="submit" class="link"><?= e(t($checkOn ? 'sevenzip.action.check_off' : 'sevenzip.action.check_on')) ?></button>
                </form>
            </div>
            <p class="note"><?= e(t($checkOn ? 'sevenzip.check.on' : 'sevenzip.check.off')) ?></p>
        </div>
    </details>
</section>
