<?php
/**
 * @var list<array{version: ?string, n: int}> $versions @var list<array{version: string, n: int}> $sevenZipVersions
 * @var ?string $currentVersion @var ?string $latestSevenZip
 */
$old = fn(?string $have, ?string $want) => $want !== null && (string) $have !== '' && version_compare((string) $have, $want, '<');
?>
<?php if ($versions !== []): ?>
<section class="panel">
    <h2 class="with-link"><?= e(t('dashboard.versions')) ?> <a class="h2-link" href="<?= e(url('/updates')) ?>"><?= e(t('nav.updates')) ?></a></h2>
    <table class="compact">
        <tbody>
        <?php foreach ($versions as $v): ?>
            <tr>
                <td>CUSTOS <?= e((string) $v['version'] === '' ? '?' : $v['version']) ?>
                    <?php if ($currentVersion !== null && $v['version'] === $currentVersion): ?><?= badge('ok', t('updates.is_current')) ?>
                    <?php elseif ($old($v['version'], $currentVersion)): ?><?= badge('warning', t('updates.old')) ?><?php endif ?></td>
                <td class="num"><?= e(t('dashboard.versions.machines', ['n' => (int) $v['n']])) ?></td>
            </tr>
        <?php endforeach ?>
        <?php foreach ($sevenZipVersions as $v): ?>
            <tr>
                <td>7-Zip <?= e($v['version']) ?>
                    <?php if ($old($v['version'], $latestSevenZip)): ?><?= badge('warning', t('updates.old')) ?><?php endif ?></td>
                <td class="num"><?= e(t('dashboard.versions.machines', ['n' => (int) $v['n']])) ?></td>
            </tr>
        <?php endforeach ?>
        </tbody>
    </table>
</section>
<?php endif ?>
