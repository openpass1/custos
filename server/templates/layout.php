<?php
/** @var string $title @var string $content @var ?array $user @var array $flash @var string $section */
$app = \Custos\Http\Web\View::$current->app;
$serverName = $app->settings()->get('server_name');
$counts = $user === null ? [] : [
    'agents' => (int) $app->db()->value("SELECT COUNT(*) FROM agents WHERE status = 'pending'"),
    'problems' => (int) $app->db()->value('SELECT COUNT(*) FROM problems WHERE closed_at IS NULL'),
];
$unread = $user === null ? 0 : $app->notices()->unreadCount();
$nav = ['dashboard' => '/', 'agents' => '/agents', 'problems' => '/problems', 'runs' => '/runs', 'users' => '/users',
        'audit' => '/audit', 'settings' => '/settings'];
?>
<!doctype html>
<html lang="ru">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title><?= e($title) ?> · <?= e($serverName) ?></title>
    <link rel="stylesheet" href="<?= e(asset('app.css')) ?>">
    <script src="<?= e(asset('app.js')) ?>" defer></script>
</head>
<body>
<header class="top">
    <a class="brand" href="<?= e(url('/')) ?>"><?= e($serverName) ?></a>
    <?php if ($user !== null): ?>
        <nav class="menu">
            <?php foreach ($nav as $key => $path): ?>
                <a href="<?= e(url($path)) ?>"<?= $section === $key ? ' class="active"' : '' ?>>
                    <?= e(t('nav.' . $key)) ?><?php if (($counts[$key] ?? 0) > 0): ?> <span class="count<?= $key === 'problems' ? ' count-bad' : '' ?>"><?= $counts[$key] ?></span><?php endif ?>
                </a>
            <?php endforeach ?>
        </nav>
        <a class="bell<?= $section === 'notices' ? ' active' : '' ?>" href="<?= e(url('/notices')) ?>" title="<?= e(t('nav.notices')) ?>">
            <span aria-hidden="true">🔔</span><span class="visually-hidden"><?= e(t('nav.notices')) ?></span>
            <span class="count" data-bell<?= $unread === 0 ? ' hidden' : '' ?>><?= $unread ?></span>
        </a>
        <form class="whoami" method="post" action="<?= e(url('/logout')) ?>">
            <?= csrf_field() ?>
            <span><?= e($user['login']) ?></span>
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
</body>
</html>
