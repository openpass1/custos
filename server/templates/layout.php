<?php
/**
 * @var string $title @var string $content @var ?array $user @var array $flash @var string $section
 * @var list<string> $scripts extra files in public/assets that the page needs (vendor/chart.umd.min.js)
 * @var int $live seconds between refreshes of the page's live parts (data-part), 0 for a page without them
 */
$view = \Custos\Http\Web\View::$current;
$app = $view->app;
$serverName = $app->settings()->get('server_name');
$counts = $user === null ? [] : $app->navCounts();
$unread = $user === null ? 0 : $app->notices()->unreadCount();
$nav = ['dashboard' => '/', 'agents' => '/agents', 'problems' => '/problems', 'runs' => '/runs', 'templates' => '/templates',
        'updates' => '/updates', 'users' => '/users', 'audit' => '/audit', 'settings' => '/settings'];
$theme = $view->theme();
// On a narrow screen the five rarer sections move under "Manage"; on a phone all of them go behind ☰ (app.css).
$rare = ['templates', 'updates', 'users', 'audit', 'settings'];
$navLink = function (string $key, string $class = '') use ($nav, $section, $counts): string {
    $active = $section === $key;
    $classes = trim($class . ($active ? ' active' : ''));
    // A counter is always there (hidden at 0), so that a live page can show it when a machine or a problem appears.
    $count = isset($counts[$key]) ? ' <span class="count' . ($key === 'problems' ? ' count-bad' : '') . '" data-nav-count="' . e($key) . '"'
        . ($counts[$key] === 0 ? ' hidden' : '') . '>' . $counts[$key] . '</span>' : '';
    return '<a href="' . e(url($nav[$key])) . '"' . ($classes === '' ? '' : ' class="' . e($classes) . '"') . ($active ? ' aria-current="page"' : '') . '>'
        . e(t('nav.' . $key)) . $count . '</a>';
};
// The user menu sends its choice and comes back to this page.
$query = (string) parse_url($view->request->uri, PHP_URL_QUERY);
$back = $view->request->path() . ($query === '' ? '' : '?' . $query);
$myLanguage = (string) ($user['language'] ?? '');
$check = fn(bool $on): string => $on ? '<span class="menu-check">' . icon('check') . '</span>' : '';
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
        <nav class="menu" aria-label="<?= e(t('nav.menu')) ?>">
            <?php foreach (array_keys($nav) as $key): ?><?= $navLink($key, in_array($key, $rare, true) ? 'rare' : '') ?><?php endforeach ?>
            <details class="dropdown nav-more<?= in_array($section, $rare, true) ? ' active' : '' ?>">
                <summary><?= e(t('nav.more')) ?><?= icon('chevron') ?></summary>
                <div class="dropdown-menu">
                    <?php foreach ($rare as $key): ?><?= $navLink($key, 'menu-item') ?><?php endforeach ?>
                </div>
            </details>
        </nav>
        <div class="top-tools">
            <a class="bell<?= $section === 'notices' ? ' active' : '' ?>" href="<?= e(url('/notices')) ?>" title="<?= e(t('nav.notices')) ?>">
                <?= icon('bell') ?><span class="visually-hidden"><?= e(t('nav.notices')) ?></span>
                <span class="count" data-bell<?= $unread === 0 ? ' hidden' : '' ?>><?= $unread ?></span>
            </a>
            <details class="dropdown user-menu">
                <summary title="<?= e(t('profile.menu')) ?>"><?= icon('user') ?><span class="user-name"><?= e($user['login']) ?></span><?= icon('chevron') ?></summary>
                <div class="dropdown-menu">
                    <div class="menu-label"><?= e(t('profile.theme')) ?></div>
                    <form method="post" action="<?= e(url('/profile')) ?>">
                        <?= csrf_field() ?><input type="hidden" name="back" value="<?= e($back) ?>"><input type="hidden" name="language" value="<?= e($myLanguage) ?>">
                        <?php foreach (\Custos\Domain\Users::THEMES as $choice): ?>
                            <button type="submit" class="menu-item" name="theme" value="<?= e($choice) ?>" aria-pressed="<?= $theme === $choice ? 'true' : 'false' ?>">
                                <?= icon(['auto' => 'monitor', 'light' => 'sun', 'dark' => 'moon'][$choice]) ?><?= e(t('profile.theme.' . $choice)) ?><?= $check($theme === $choice) ?></button>
                        <?php endforeach ?>
                    </form>
                    <div class="menu-label"><?= e(t('profile.language')) ?></div>
                    <form method="post" action="<?= e(url('/profile')) ?>">
                        <?= csrf_field() ?><input type="hidden" name="back" value="<?= e($back) ?>"><input type="hidden" name="theme" value="<?= e($theme) ?>">
                        <?php $languages = $app->languages(); $default = $app->defaultLanguage(); ?>
                        <button type="submit" class="menu-item" name="language" value="" aria-pressed="<?= $myLanguage === '' ? 'true' : 'false' ?>">
                            <?= icon('globe') ?><?= e(t('profile.language.default', ['name' => $languages[$default] ?? $default])) ?><?= $check($myLanguage === '') ?></button>
                        <?php foreach ($languages as $code => $name): ?>
                            <button type="submit" class="menu-item" name="language" value="<?= e($code) ?>" lang="<?= e($code) ?>" aria-pressed="<?= $myLanguage === $code ? 'true' : 'false' ?>">
                                <span class="menu-code"><?= e(strtoupper($code)) ?></span><?= e($name) ?><?= $check($myLanguage === $code) ?></button>
                        <?php endforeach ?>
                    </form>
                    <div class="menu-sep"></div>
                    <button type="button" class="menu-item" data-dialog="password-dialog"><?= icon('key') ?><?= e(t('profile.password.save')) ?></button>
                    <form method="post" action="<?= e(url('/logout')) ?>">
                        <?= csrf_field() ?><button type="submit" class="menu-item"><?= icon('logout') ?><?= e(t('nav.logout')) ?></button>
                    </form>
                </div>
            </details>
            <details class="dropdown nav-phone">
                <summary title="<?= e(t('nav.menu')) ?>"><?= icon('menu') ?><span class="visually-hidden"><?= e(t('nav.menu')) ?></span></summary>
                <div class="dropdown-menu">
                    <?php foreach (array_keys($nav) as $key): ?><?= $navLink($key, 'menu-item') ?><?php endforeach ?>
                </div>
            </details>
        </div>
    <?php endif ?>
</header>
<main<?= $live > 0 ? ' data-live-page="' . $live . '"' : '' ?>>
    <?php /* Messages float over the page (app.js closes a plain one after a few seconds; an error stays until closed). */ ?>
    <?php if ($flash !== []): ?>
        <div class="toasts" data-close-label="<?= e(t('common.close')) ?>">
            <?php foreach ($flash as [$type, $message]): ?>
                <div class="flash flash-<?= e($type) ?>"><?= e($message) ?></div>
            <?php endforeach ?>
        </div>
    <?php endif ?>
    <?= $content ?>
</main>
<footer>CUSTOS Monitor <?= e(\Custos\App::VERSION) ?>
    <?php /* app.js shows it when the system asks for less motion (Windows: animation effects off). */ ?>
    <span class="motion-note" data-motion-note hidden title="<?= e(t('motion.off.hint')) ?>"
          data-windows="<?= e(t('motion.off.windows')) ?>" data-windows-hint="<?= e(t('motion.off.hint.windows')) ?>"><?= e(t('motion.off')) ?></span>
</footer>
<?php /* The user menu's "Change password" opens this dialog; the answer comes back to this page. */ ?>
<?php if ($user !== null): ?>
    <dialog id="password-dialog" class="modal" aria-labelledby="password-title">
        <form method="post" action="<?= e(url('/profile/password')) ?>" class="stack">
            <?= csrf_field() ?><input type="hidden" name="back" value="<?= e($back) ?>">
            <h2 id="password-title"><?= e(t('profile.password.save')) ?> · <?= e($user['login']) ?></h2>
            <label><?= e(t('profile.password.current')) ?>
                <input type="password" name="current" required autocomplete="current-password">
            </label>
            <label><?= e(t('profile.password.new', ['min' => \Custos\Domain\Users::MIN_PASSWORD])) ?>
                <input type="password" name="password" minlength="<?= \Custos\Domain\Users::MIN_PASSWORD ?>" required autocomplete="new-password">
            </label>
            <label><?= e(t('profile.password.repeat')) ?>
                <input type="password" name="repeat" minlength="<?= \Custos\Domain\Users::MIN_PASSWORD ?>" required autocomplete="new-password">
            </label>
            <div class="dialog-buttons">
                <button type="button" class="secondary" data-dialog-close><?= e(t('common.cancel')) ?></button>
                <button type="submit"><?= e(t('profile.password.save')) ?></button>
            </div>
        </form>
    </dialog>
<?php endif ?>
<?php /* The question of a form with data-confirm (app.js): the button repeats the action, Cancel is the safe way out. */ ?>
<dialog id="confirm-dialog" class="modal confirm-dialog" aria-labelledby="confirm-title" aria-describedby="confirm-text">
    <h2 id="confirm-title"></h2>
    <p id="confirm-text"></p>
    <div class="dialog-buttons">
        <button type="button" class="secondary" data-confirm-cancel><?= e(t('common.cancel')) ?></button>
        <button type="button" data-confirm-ok data-default="<?= e(t('common.confirm')) ?>"></button>
    </div>
</dialog>
<?php /* Shown by app.js once the top of the page has scrolled away. */ ?>
<button type="button" class="to-top" data-to-top title="<?= e(t('nav.to_top')) ?>" aria-label="<?= e(t('nav.to_top')) ?>">
    <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true"><path d="M12 19V5M5 12l7-7 7 7" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/></svg>
</button>
</body>
</html>
