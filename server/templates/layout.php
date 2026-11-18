<?php
/**
 * @var string $title @var string $content @var ?array $user @var array $flash @var string $section
 * @var list<string> $scripts extra files in public/assets that the page needs (vendor/chart.umd.min.js)
 * @var int $live seconds between refreshes of the page's live parts (data-part), 0 for a page without them
 *
 * Material 3 frame: a navigation rail at the left (wide screen: icons with words; narrower: icons with small words
 * under them), a top bar with the search, the theme switch, the bell and the user menu, and the page itself on a
 * rounded sheet. On a phone the rail becomes a bar at the bottom with the four main sections and "More" (app.css).
 */
$view = \Custos\Http\Web\View::$current;
$app = $view->app;
$serverName = $app->settings()->get('server_name');
$counts = $user === null ? [] : $app->navCounts();
$unread = $user === null ? 0 : $app->notices()->unreadCount();
$nav = ['dashboard' => '/', 'agents' => '/agents', 'problems' => '/problems', 'runs' => '/runs', 'templates' => '/templates',
        'updates' => '/updates', 'users' => '/users', 'audit' => '/audit', 'settings' => '/settings'];
$theme = $view->theme();
// The rarer sections stand under the heading "Manage" in the rail and behind "More" in the phone's bottom bar.
$rare = ['templates', 'updates', 'users', 'audit', 'settings'];
$filled = ['dashboard', 'agents', 'problems', 'templates', 'users', 'audit', 'settings'];
$navLink = function (string $key, string $class) use ($nav, $section, $counts, $filled): string {
    $active = $section === $key;
    // A counter is always there (hidden at 0), so that a live page can show it when a machine or a problem appears.
    $count = isset($counts[$key]) ? '<span class="count' . ($key === 'problems' ? ' count-bad' : '') . '" data-nav-count="' . e($key) . '"'
        . ($counts[$key] === 0 ? ' hidden' : '') . '>' . $counts[$key] . '</span>' : '';
    // Both forms of the icon are in the page and app.css shows the one that fits: a click on a section marks it open
    // at once (app.js), before the answer of the server comes.
    $icons = (in_array($key, $filled, true) ? icon('nav-' . $key . '-fill', 'icon-fill') : '') . icon('nav-' . $key, 'icon-line');
    return '<a href="' . e(url($nav[$key])) . '" data-nav="' . e($key) . '" class="' . e($class . ($active ? ' active' : '')) . '"'
        . ($active ? ' aria-current="page"' : '') . '>'
        . '<span class="nav-icon">' . $icons . $count . '</span><span class="nav-label">' . e(t('nav.' . $key)) . '</span></a>';
};
// The fonts every page needs come with the page, not after its styles: the first frame already has its letters. The
// addresses are the ones app.css names (no version: a font file never changes under its name).
$fonts = ['roboto-latin-400-normal', 'roboto-latin-500-normal'];
if ($app->lang()->code === 'ru') {
    array_push($fonts, 'roboto-cyrillic-400-normal', 'roboto-cyrillic-500-normal');
}
// The user menu and the theme switch send their choice and come back to this page.
$query = (string) parse_url($view->request->uri, PHP_URL_QUERY);
$back = $view->request->path() . ($query === '' ? '' : '?' . $query);
$myLanguage = (string) ($user['language'] ?? '');
$check = fn(bool $on): string => $on ? '<span class="menu-check">' . icon('check') . '</span>' : '';
$initial = $user === null ? '' : mb_strtoupper(mb_substr((string) $user['login'], 0, 1));
?>
<!doctype html>
<html lang="<?= e($app->lang()->code) ?>"<?= $theme === 'auto' ? '' : ' data-theme="' . e($theme) . '"' ?>>
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="color-scheme" content="<?= $theme === 'auto' ? 'light dark' : e($theme) ?>">
    <title><?= e($title) ?> · <?= e($serverName) ?></title>
    <?php include __DIR__ . '/_icons.php' ?>
    <?php foreach ($fonts as $font): ?>
        <link rel="preload" href="<?= e(url('/assets/fonts/' . $font . '.woff2')) ?>" as="font" type="font/woff2" crossorigin>
    <?php endforeach ?>
    <link rel="stylesheet" href="<?= e(asset('app.css')) ?>">
    <?php /* blocking="render": the first frame waits for app.js, so that labels are already in their frames and the
             content of a new section comes in with its movement (app.css, "From page to page"). */ ?>
    <script src="<?= e(asset('app.js')) ?>" defer blocking="render"></script>
    <?php foreach ($scripts as $script): ?>
        <script src="<?= e(asset($script)) ?>" defer></script>
    <?php endforeach ?>
</head>
<body class="<?= $user === null ? 'no-user' : 'with-rail' ?>" data-icons="<?= e(asset('icons.svg')) ?>">
<?php if ($user !== null): ?>
<nav class="rail" aria-label="<?= e(t('nav.menu')) ?>">
    <a class="brand" href="<?= e(url('/')) ?>" title="<?= e($serverName) ?>"><span class="brand-mark"><img src="<?= e(url('/favicon.svg')) ?>" alt="" width="24" height="24"></span><span class="brand-name"><?= e($serverName) ?></span></a>
    <?php foreach (array_keys($nav) as $key): ?>
        <?php if ($key === $rare[0]): ?><div class="nav-head"><?= e(t('nav.more')) ?></div><?php endif ?>
        <?= $navLink($key, 'nav-item') ?>
    <?php endforeach ?>
</nav>
<?php endif ?>
<div class="frame">
<header class="top">
    <a class="brand top-brand" href="<?= e(url('/')) ?>"><span class="brand-mark"><img src="<?= e(url('/favicon.svg')) ?>" alt="" width="22" height="22"></span><span class="brand-name"><?= e($serverName) ?></span></a>
    <?php if ($user !== null): ?>
        <?php /* Search of machines and tasks: the field shows matches as one types (app.js); Enter opens the full list. */ ?>
        <form class="search" method="get" action="<?= e(url('/search')) ?>" role="search" data-search="<?= e(url('/search/suggest')) ?>">
            <label class="search-field"><?= icon('search') ?><span class="visually-hidden"><?= e(t('nav.search')) ?></span>
                <input type="search" name="q" value="<?= e($section === 'search' ? (string) ($view->request->query['q'] ?? '') : '') ?>" placeholder="<?= e(t('nav.search')) ?>"
                       autocomplete="off" spellcheck="false" maxlength="100" aria-autocomplete="list" aria-controls="search-results" aria-expanded="false">
                <kbd class="search-key" title="<?= e(t('search.key')) ?>">/</kbd>
            </label>
            <div class="search-results" id="search-results" role="listbox" hidden></div>
        </form>
        <div class="top-tools">
            <button type="button" class="icon-button search-open" data-search-open title="<?= e(t('nav.search')) ?>" aria-label="<?= e(t('nav.search')) ?>"><?= icon('search') ?></button>
            <form method="post" action="<?= e(url('/profile')) ?>" class="theme-switch">
                <?= csrf_field() ?><input type="hidden" name="back" value="<?= e($back) ?>"><input type="hidden" name="language" value="<?= e($myLanguage) ?>">
                <button type="submit" class="icon-button to-dark" name="theme" value="dark" title="<?= e(t('theme.to_dark')) ?>" aria-label="<?= e(t('theme.to_dark')) ?>"><?= icon('moon') ?></button>
                <button type="submit" class="icon-button to-light" name="theme" value="light" title="<?= e(t('theme.to_light')) ?>" aria-label="<?= e(t('theme.to_light')) ?>"><?= icon('sun') ?></button>
            </form>
            <a class="icon-button bell<?= $section === 'notices' ? ' active' : '' ?>" href="<?= e(url('/notices')) ?>" title="<?= e(t('nav.notices')) ?>">
                <?= icon($section === 'notices' ? 'bell-fill' : 'bell') ?><span class="visually-hidden"><?= e(t('nav.notices')) ?></span>
                <span class="count" data-bell<?= $unread === 0 ? ' hidden' : '' ?>><?= $unread ?></span>
            </a>
            <details class="dropdown user-menu">
                <summary title="<?= e(t('profile.menu')) ?>"><span class="avatar" aria-hidden="true"><?= e($initial) ?></span><span class="user-name"><?= e($user['login']) ?></span><?= icon('drop') ?></summary>
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
        </div>
    <?php endif ?>
</header>
<main class="sheet"<?= $live > 0 ? ' data-live-page="' . $live . '"' : '' ?>>
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
<footer data-version="<?= e(\Custos\App::VERSION) ?>">CUSTOS Monitor <?= e(\Custos\App::VERSION) ?>
    <?php /* A quiet link to the ways of supporting the project (DONATE.md of the repository). */ ?>
    <span class="footer-donate"><a href="https://github.com/<?= e(\Custos\Domain\Releases::DEFAULT_REPO) ?>/blob/main/DONATE.md" target="_blank" rel="noopener"><?= e(t('footer.donate')) ?></a></span>
    <?php /* app.js shows it when the system asks for less motion (Windows: animation effects off). */ ?>
    <span class="motion-note" data-motion-note hidden title="<?= e(t('motion.off.hint')) ?>"
          data-windows="<?= e(t('motion.off.windows')) ?>" data-windows-hint="<?= e(t('motion.off.hint.windows')) ?>"><?= e(t('motion.off')) ?></span>
</footer>
</div>
<?php if ($user !== null): ?>
<?php /* Phone: the main sections at the bottom, the rest behind "More". */ ?>
<nav class="bottom-nav" aria-label="<?= e(t('nav.menu')) ?>">
    <?php foreach (['dashboard', 'agents', 'problems', 'runs'] as $key): ?><?= $navLink($key, 'bar-item') ?><?php endforeach ?>
    <details class="dropdown bar-more<?= in_array($section, $rare, true) ? ' active' : '' ?>">
        <summary class="bar-item"><span class="nav-icon"><?= icon('menu') ?></span><span class="nav-label"><?= e(t('common.more')) ?></span></summary>
        <div class="dropdown-menu">
            <?php foreach ($rare as $key): ?><?= $navLink($key, 'menu-item') ?><?php endforeach ?>
        </div>
    </details>
</nav>
<?php /* The user menu's "Change password" opens this dialog; the answer comes back to this page. */ ?>
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
                <button type="button" class="quiet" data-dialog-close><?= e(t('common.cancel')) ?></button>
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
        <button type="button" class="quiet" data-confirm-cancel><?= e(t('common.cancel')) ?></button>
        <button type="button" data-confirm-ok data-default="<?= e(t('common.confirm')) ?>"></button>
    </div>
</dialog>
<?php /* Shown by app.js once the top of the page has scrolled away. */ ?>
<button type="button" class="to-top" data-to-top title="<?= e(t('nav.to_top')) ?>" aria-label="<?= e(t('nav.to_top')) ?>"><?= icon('up') ?></button>
</body>
</html>
