<?php
/**
 * @var string $title @var string $content @var ?array $user @var array $flash @var string $section
 * @var list<string> $scripts extra files in public/assets that the page needs (vendor/chart.umd.min.js)
 */
$view = \Custos\Http\Web\View::$current;
$app = $view->app;
$serverName = $app->settings()->get('server_name');
$counts = $user === null ? [] : [
    'agents' => (int) $app->db()->value("SELECT COUNT(*) FROM agents WHERE status = 'pending'"),
    'problems' => (int) $app->db()->value('SELECT COUNT(*) FROM problems WHERE closed_at IS NULL'),
];
$unread = $user === null ? 0 : $app->notices()->unreadCount();
$nav = ['dashboard' => '/', 'agents' => '/agents', 'problems' => '/problems', 'runs' => '/runs', 'templates' => '/templates',
        'updates' => '/updates', 'users' => '/users', 'audit' => '/audit', 'settings' => '/settings'];
$theme = $view->theme();
?>
<!doctype html>
<html lang="<?= e($app->lang()->code) ?>"<?= $theme === 'auto' ? '' : ' data-theme="' . e($theme) . '"' ?>>
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="color-scheme" content="<?= $theme === 'auto' ? 'light dark' : e($theme) ?>">
    <title><?= e($title) ?> · <?= e($serverName) ?></title>
    <?php include __DIR__ . '/_icons.php' ?>
    <link rel="stylesheet" href="<?= e(asset('app.css')) ?>">
    <script src="<?= e(asset('app.js')) ?>" defer></script>
    <?php foreach ($scripts as $script): ?>
        <script src="<?= e(asset($script)) ?>" defer></script>
    <?php endforeach ?>
</head>
<body>
<?php include __DIR__ . '/_sprite.php' ?>
<header class="top">
    <a class="brand" href="<?= e(url('/')) ?>"><img src="<?= e(url('/favicon.svg')) ?>" alt="" width="20" height="20"><?= e($serverName) ?></a>
    <?php if ($user !== null): ?>
        <nav class="menu">
            <?php foreach ($nav as $key => $path): ?>
                <a href="<?= e(url($path)) ?>"<?= $section === $key ? ' class="active"' : '' ?>>
                    <?= e(t('nav.' . $key)) ?><?php if (($counts[$key] ?? 0) > 0): ?> <span class="count<?= $key === 'problems' ? ' count-bad' : '' ?>"><?= $counts[$key] ?></span><?php endif ?>
                </a>
            <?php endforeach ?>
        </nav>
        <a class="bell<?= $section === 'notices' ? ' active' : '' ?>" href="<?= e(url('/notices')) ?>" title="<?= e(t('nav.notices')) ?>">
            <?= icon('bell') ?><span class="visually-hidden"><?= e(t('nav.notices')) ?></span>
            <span class="count" data-bell<?= $unread === 0 ? ' hidden' : '' ?>><?= $unread ?></span>
        </a>
        <form class="whoami" method="post" action="<?= e(url('/logout')) ?>">
            <?= csrf_field() ?>
            <a href="<?= e(url('/profile')) ?>" title="<?= e(t('nav.profile')) ?>"<?= $section === 'profile' ? ' class="active"' : '' ?>><?= e($user['login']) ?></a>
            <button type="submit" class="link"><?= e(t('nav.logout')) ?></button>
        </form>
    <?php endif ?>
</header>
<main>
    <?php foreach ($flash as [$type, $message]): ?>
        <div class="flash flash-<?= e($type) ?>"><?= e($message) ?></div>
    <?php endforeach ?>
    <?= $content ?>
</main>
<footer>CUSTOS Monitor <?= e(\Custos\App::VERSION) ?></footer>
<?php /* Shown by app.js once the top of the page has scrolled away. */ ?>
<button type="button" class="to-top" data-to-top title="<?= e(t('nav.to_top')) ?>" aria-label="<?= e(t('nav.to_top')) ?>">
    <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true"><path d="M12 19V5M5 12l7-7 7 7" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/></svg>
</button>
</body>
</html>
