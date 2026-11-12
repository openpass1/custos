<?php
/** @var array<string, string> $parts @var int $refresh @var string $token @var string $serverName */
$app = \Custos\Http\Web\View::$current->app;
?>
<!doctype html>
<html lang="<?= e($app->lang()->code) ?>" data-theme="dark">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="color-scheme" content="dark">
    <meta name="robots" content="noindex">
    <title><?= e(t('wall.title')) ?> · <?= e($serverName) ?></title>
    <?php include __DIR__ . '/../_icons.php' ?>
    <link rel="stylesheet" href="<?= e(asset('app.css')) ?>">
    <script src="<?= e(asset('app.js')) ?>" defer></script>
</head>
<body class="wall">
<div data-live="<?= e(url('/wall/live', ['token' => $token])) ?>" data-live-every="<?= (int) $refresh ?>" data-live-public>
    <header class="wall-head">
        <span class="wall-brand"><img src="<?= e(url('/favicon.svg')) ?>" alt="" width="28" height="28"><?= e($serverName) ?></span>
        <span class="wall-offline" data-live-offline hidden><?= e(t('wall.offline')) ?></span>
    </header>
    <div data-part="board"><?= $parts['board'] ?></div>
</div>
</body>
</html>
