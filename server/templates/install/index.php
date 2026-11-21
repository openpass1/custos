<?php
/**
 * How to add a machine (InstallController): one command for a Linux server, the installers for Windows and macOS
 * with the connection line.
 *
 * @var ?array{curl: string, wget: string} $commands the install commands for Linux, null without key or site address
 * @var ?string $connection the connection line (A.3), null without key or site address
 * @var string $connectionProblem the message key that says why there is neither
 * @var ?array $package the package for Linux the command installs @var bool $packageCurrent whether it is the current one
 * @var list<string> $archives the processors the package has the program for
 * @var string $releasesPage @var ?array $windows @var ?array $mac @var ?array $legacy [version, file, address]
 */
$copy = fn(string $id, string $value, int $size = 72): string => '<span class="copy-row">'
    . '<input type="text" id="' . e($id) . '" class="mono" value="' . e($value) . '" readonly size="' . $size . '" spellcheck="false">'
    . '<button type="button" class="secondary" data-copy="' . e($id) . '" data-copied="' . e(t('common.copied')) . '">'
    . icon('copy') . e(t('common.copy')) . '</button></span>';
$download = fn(?array $installer, string $fallback): string => $installer === null
    ? '<a class="button-link" href="' . e($fallback) . '" target="_blank" rel="noopener">' . icon('download') . e(t('install.github')) . '</a>'
    : '<a class="button-link" href="' . e($installer[2]) . '" rel="noopener">' . icon('download') . e($installer[1]) . '</a>';
?>
<h1><?= e(t('install.title')) ?></h1>
<p class="hint"><?= e(t('install.hint')) ?></p>

<section class="panel" id="linux">
    <h2><?= e(t('install.linux')) ?></h2>
    <?php if ($commands === null): ?>
        <div class="alert alert-warn"><?= e(t('install.no_command', ['why' => t($connectionProblem)])) ?></div>
    <?php else: ?>
        <?php if ($package === null): ?>
            <div class="alert alert-warn"><?= e(t('install.linux.no_package')) ?>
                <a href="<?= e(url('/updates')) ?>"><?= e(t('install.linux.to_updates')) ?></a></div>
        <?php endif ?>
        <p><?= e(t('install.linux.run')) ?></p>
        <?= $copy('install-curl', $commands['curl'], 90) ?>
        <p class="note gap-top"><?= e(t('install.linux.wget')) ?></p>
        <?= $copy('install-wget', $commands['wget'], 90) ?>
        <?php if ($package !== null): ?>
            <p class="note gap-top"><?= e(t($packageCurrent ? 'install.linux.version' : 'install.linux.version_newest', [
                'version' => $package['version'], 'arch' => implode(', ', $archives) ?: '—'])) ?>
                <a href="<?= e(url('/updates')) ?>"><?= e(t('nav.updates')) ?></a></p>
        <?php endif ?>
        <ol class="steps">
            <li><?= e(t('install.linux.step.root')) ?></li>
            <li><?= e(t('install.linux.step.reach')) ?></li>
            <li><?= e(t('install.linux.step.approve')) ?> <a href="<?= e(url('/agents')) ?>"><?= e(t('nav.agents')) ?></a></li>
            <li><?= e(t('install.linux.step.tasks')) ?></li>
        </ol>
        <p class="note"><?= e(t('install.linux.systems')) ?></p>
        <p class="note"><?= e(t('install.linux.key')) ?> <a href="<?= e(url('/settings')) ?>"><?= e(t('nav.settings')) ?></a></p>
    <?php endif ?>
</section>

<section class="panel" id="desktop">
    <h2><?= e(t('install.desktop')) ?></h2>
    <p><?= e(t('install.desktop.connection')) ?></p>
    <?php if ($connection === null): ?>
        <div class="alert alert-warn"><?= e(t($connectionProblem)) ?></div>
    <?php else: ?>
        <?= $copy('connection-string', $connection) ?>
    <?php endif ?>

    <h3><?= e(t('install.windows')) ?></h3>
    <p class="note"><?= e(t('install.windows.hint')) ?></p>
    <div class="buttons"><?= $download($windows, $releasesPage) ?></div>
    <p class="note gap-top"><?= e(t('install.legacy')) ?>
        <a href="<?= e($legacy === null ? $releasesPage : $legacy[2]) ?>" rel="noopener"><?= e($legacy === null ? t('install.github') : $legacy[1]) ?></a></p>

    <h3><?= e(t('install.mac')) ?></h3>
    <p class="note"><?= e(t('install.mac.hint')) ?></p>
    <div class="buttons"><?= $download($mac, $releasesPage) ?></div>

    <?php if ($windows === null || $mac === null): ?>
        <p class="note gap-top"><?= e(t('install.github.hint')) ?></p>
    <?php endif ?>
</section>
