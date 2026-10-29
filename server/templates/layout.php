<?php
/** @var string $title @var string $content @var ?array $user @var array $flash @var string $section */
$serverName = \Custos\Http\Web\View::$current->app->settings()->get('server_name');
$pendingCount = $user === null ? 0 : (int) \Custos\Http\Web\View::$current->app->db()->value("SELECT COUNT(*) FROM agents WHERE status = 'pending'");
$nav = ['agents' => '/agents', 'runs' => '/runs', 'users' => '/users', 'audit' => '/audit', 'settings' => '/settings'];
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
                    <?= e(t('nav.' . $key)) ?><?php if ($key === 'agents' && $pendingCount > 0): ?> <span class="count"><?= $pendingCount ?></span><?php endif ?>
                </a>
            <?php endforeach ?>
        </nav>
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
