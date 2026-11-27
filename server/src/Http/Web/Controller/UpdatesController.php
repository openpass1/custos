<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Agents;
use Custos\Domain\Packages;
use Custos\Domain\Releases;
use Custos\Http\Response;
use Custos\Http\Web\Controller;

/**
 * Updates (section 8): the site itself (the newest release on GitHub or an uploaded package, run by cron), agent
 * update packages (upload or download of the newest program release, current version, update machines) and 7-Zip
 * (the daily check of the newest release, download or upload of the installer, update of 7z.dll on machines).
 * The editions for Windows 7 and 8.1, macOS and Linux have packages of their own (kind legacy, macos, linux) and their
 * own current versions. Linux also appears before any packages or agents exist, so an admin can download its first
 * package. Machines of these editions are never offered another edition or 7-Zip.
 */
final class UpdatesController extends Controller
{
    /** The machine list of the page (versions, updates on their way) refreshes this often (seconds). */
    public const REFRESH_S = 15;

    public function index(): Response
    {
        $packages = $this->app->packages();
        $agents = array_values(array_filter($this->app->agents()->all(), fn($a) => $a['status'] !== 'pending'));
        $current = $packages->current();
        $custosOutdated = array_column(Packages::outdated(Packages::fitting('agent', $agents), $current['version'] ?? null), null, 'id');
        $custosUpdateAvailable = $current !== null;
        $sevenZipPackages = $packages->all('7zip');
        $settings = $this->app->settings();
        $releases = $this->app->releases();
        $channel = $releases->channel();
        // The editions other than the standard one, each with its own section, packages and current version.
        $editions = [];
        foreach (Agents::EDITIONS as $kind) {
            $editionCurrent = $packages->current($kind);
            $machines = Packages::fitting($kind, $agents);
            $latest = $releases->latest($kind);
            $show = $kind === Agents::LINUX || $packages->inUse($kind) || ($channel === Releases::PREVIEW && $latest !== null);
            $custosOutdated += array_column(Packages::outdated($machines, $editionCurrent['version'] ?? null), null, 'id');
            $custosUpdateAvailable = $custosUpdateAvailable || $editionCurrent !== null;
            $editions[$kind] = [
                'on' => $show,
                'latest' => $latest,
                'newer' => $show ? $releases->newerClient($kind) : null,
                'packages' => $packages->all($kind),
                'current' => $editionCurrent,
            ];
        }
        return $this->render('updates/index', [
            'repo' => $releases->repo,
            'releaseChannel' => $channel,
            'releasesOn' => $releases->checkOn(),
            'releasesCheckedAt' => $settings->get('releases_checked_at'),
            'releasesError' => $settings->get('releases_check_error'),
            'siteLatest' => $releases->latest('server'),
            'siteNewer' => $releases->newerSite(),
            'siteState' => $this->app->siteUpdate()->state(),
            'cronLastRun' => $settings->get('cron_last_run'),
            'clientLatest' => $releases->latest('client'),
            'clientNewer' => $releases->newerClient(),
            'editions' => $editions,
            'section' => 'updates',
            'agentPackages' => $packages->all('agent'),
            'sevenZipPackages' => $sevenZipPackages,
            'current' => $current,
            'agents' => $agents,
            'groups' => $this->app->groups()->all(),
            'custosOutdated' => $custosOutdated,
            'custosUpdateAvailable' => $custosUpdateAvailable,
            'sevenZipOutdated' => array_column(Packages::outdated(Packages::fitting('7zip', $agents), $sevenZipPackages[0]['version'] ?? null, 'sevenzip_version'), null, 'id'),
            'github' => json_decode($settings->get('sevenzip_latest'), true),
            'checkedAt' => $settings->get('sevenzip_checked_at'),
            'checkError' => $settings->get('sevenzip_check_error'),
            'checkOn' => $settings->get('sevenzip_check') !== '0',
            'waitingAgent' => $this->app->commands()->waiting('update.install'),
            'waitingSevenZip' => $this->app->commands()->waiting('sevenzip.update'),
            'uploadLimit' => self::uploadLimit(),
            'live' => self::REFRESH_S,
        ], $this->t('updates.title'));
    }

    /** Upload of an agent update package (zip with manifest.json). */
    public function upload(): Response
    {
        $file = $this->uploaded('package');
        if ($file === null) {
            return $this->redirect('/updates');
        }
        try {
            $package = $this->app->packages()->addAgent($file, $this->user()['login'], $params);
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage(), $params ?? []));
            return $this->redirect('/updates');
        }
        $this->audit('update.upload', 'update', $package['version'], ['version' => $package['version'], 'edition' => self::edition($package['kind']), 'sha256' => $package['sha256']]);
        $this->session->flash('info', $this->t($package['kind'] === 'agent' ? 'updates.flash.uploaded' : 'updates.flash.uploaded_' . $package['kind'], ['version' => $package['version']]));
        return $this->redirect('/updates');
    }

    public function current(string $kind, string $version): Response
    {
        $kind = in_array($kind, Packages::PROGRAM_KINDS, true) ? $kind : 'agent';
        if ($this->app->packages()->find($kind, $version) === null) {
            return $this->notFound();
        }
        $this->app->packages()->setCurrent($version, $kind);
        $this->audit('update.current', 'update', $version, ['version' => $version, 'edition' => self::edition($kind)]);
        $this->session->flash('info', $this->t('updates.flash.current', ['version' => $version]));
        return $this->redirect('/updates');
    }

    public function delete(string $kind, string $version): Response
    {
        $kind = in_array($kind, ['7zip', ...Packages::PROGRAM_KINDS], true) ? $kind : 'agent';
        if ($this->app->packages()->find($kind, $version) === null) {
            return $this->notFound();
        }
        $this->app->packages()->delete($kind, $version);
        $this->audit($kind === '7zip' ? 'sevenzip.delete' : 'update.delete', 'update', $version,
            in_array($kind, Agents::EDITIONS, true) ? ['version' => $version, 'edition' => self::edition($kind)] : ['version' => $version]);
        $this->session->flash('info', $this->t('updates.flash.deleted', ['version' => $version]));
        return $this->redirect('/updates');
    }

    /**
     * Queues the current program package for each chosen edition, or the newest 7-Zip installer on the server.
     * The specific program kinds are also accepted by older forms. Targets are selected, outdated or group.
     */
    public function install(): Response
    {
        $kind = $this->post('kind');
        if ($kind === 'custos') {
            return $this->installCustos();
        }
        if (!in_array($kind, ['7zip', ...Packages::PROGRAM_KINDS], true)) {
            return $this->notFound();
        }
        $packages = $this->app->packages();
        $package = $kind === '7zip' ? $packages->latestSevenZip() : $packages->current($kind);
        if ($package === null) {
            $this->session->flash('error', $this->t($kind === '7zip' ? 'sevenzip.error.none' : 'updates.error.no_current'));
            return $this->redirect('/updates');
        }
        $agents = Packages::fitting($kind, $this->installTargets());
        if ($this->post('target') === 'outdated') {
            $agents = Packages::outdated($agents, $package['version'], $kind === '7zip' ? 'sevenzip_version' : 'agent_version');
        }
        if ($agents === []) {
            $this->session->flash('error', $this->t($kind === 'agent' ? 'updates.error.no_machines' : "updates.error.no_machines_$kind"));
            return $this->redirect('/updates');
        }
        $result = $packages->install($kind, $package['version'], $agents, $this->user());
        if ($result['queued'] !== []) {
            $this->audit($kind === '7zip' ? 'sevenzip.install' : 'update.install', 'update', $package['version'], [
                'version' => $package['version'],
                ...(in_array($kind, Agents::EDITIONS, true) ? ['edition' => self::edition($kind)] : []),
                'machines' => implode(', ', array_map(fn($a) => Agents::name($a), $result['queued'])),
            ]);
        }
        // Only approved machines get here, so a skipped one already runs this version or a newer one: say so, and
        // not as an error when nothing needed updating.
        $params = ['version' => $package['version'], 'n' => count($result['queued']), 'skipped' => count($result['skipped'])];
        $this->session->flash('info', $this->t(match (true) {
            $result['queued'] === [] => 'updates.flash.up_to_date',
            $result['skipped'] === [] => 'updates.flash.queued',
            default => 'updates.flash.queued_skipped',
        }, $params));
        return $this->redirect('/updates');
    }

    /** Applies the server-side target selection before looking up any package. */
    private function installTargets(): array
    {
        $agents = array_values(array_filter($this->app->agents()->all(), fn($a) => $a['status'] === 'approved'));
        $group = $this->post('group_id');
        $ids = (array) ($this->request->post['agent_ids'] ?? []);
        return match ($this->post('target')) {
            'outdated' => $agents,
            'group' => $group === '' ? [] : array_values(array_filter($agents, fn($a) => (string) $a['group_id'] === $group)),
            'selected', '' => array_values(array_filter($agents, fn($a) => in_array($a['id'], $ids, true))),
            default => [],
        };
    }

    /** Resolves the current, signed package separately for each edition, then records each roll-out in the audit log. */
    private function installCustos(): Response
    {
        $agents = $this->installTargets();
        if ($agents === []) {
            $this->session->flash('error', $this->t('updates.error.no_machines'));
            return $this->redirect('/updates');
        }
        $packages = $this->app->packages();
        $outdated = $this->post('target') === 'outdated';
        [$queued, $current, $unavailable] = $this->app->db()->transaction(function () use ($agents, $packages, $outdated) {
            $queued = $current = $unavailable = 0;
            foreach (Packages::PROGRAM_KINDS as $kind) {
                $targets = Packages::fitting($kind, $agents);
                if ($targets === []) {
                    continue;
                }
                $package = $packages->current($kind);
                if ($package === null) {
                    $unavailable += $outdated ? 0 : count($targets);
                    continue;
                }
                $version = $package['version'];
                if ($outdated) {
                    $targets = Packages::outdated($targets, $version);
                }
                $result = $packages->install($kind, $version, $targets, $this->user());
                $queued += count($result['queued']);
                $current += count($result['skipped']);
                if ($result['queued'] !== []) {
                    $this->audit('update.install', 'update', $version, [
                        'version' => $version,
                        'edition' => self::edition($kind),
                        'machines' => implode(', ', array_map(Agents::name(...), $result['queued'])),
                    ]);
                }
            }
            return [$queued, $current, $unavailable];
        });

        $messages = [$this->t($queued > 0 ? 'updates.flash.custos_queued' : 'updates.flash.custos_no_updates', ['n' => $queued])];
        if ($current > 0) {
            $messages[] = $this->t('updates.flash.custos_skipped_current', ['n' => $current]);
        }
        if ($unavailable > 0) {
            $messages[] = $this->t('updates.flash.custos_skipped_no_package', ['n' => $unavailable]);
        }
        $this->session->flash('info', implode(' ', $messages));
        return $this->redirect('/updates');
    }

    /** "Check now" for the newest 7-Zip. */
    public function sevenZipCheck(): Response
    {
        $latest = $this->app->updateWatch()->check(time());
        if ($latest === null) {
            $this->session->flash('error', $this->t('sevenzip.flash.check_failed', ['error' => $this->app->settings()->get('sevenzip_check_error')]));
        } else {
            $this->session->flash('info', $this->t('sevenzip.flash.checked', ['version' => $latest['version']]));
        }
        return $this->redirect('/updates');
    }

    /** Downloads the newest 7-Zip installer from GitHub to the server. */
    public function sevenZipDownload(): Response
    {
        try {
            $package = $this->app->updateWatch()->download($this->user()['login'], $params);
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage(), $params ?? []));
            return $this->redirect('/updates');
        }
        $this->audit('sevenzip.download', 'update', $package['version'], ['version' => $package['version'], 'sha256' => $package['sha256']]);
        $this->session->flash('info', $this->t('sevenzip.flash.stored', ['version' => $package['version']]));
        return $this->redirect('/updates');
    }

    /** Manual upload of a 7-Zip installer, for a server without internet. */
    public function sevenZipUpload(): Response
    {
        $file = $this->uploaded('installer');
        if ($file === null) {
            return $this->redirect('/updates');
        }
        try {
            $package = $this->app->packages()->addSevenZip($file, trim($this->post('version')), trim($this->post('sha256')), 'upload',
                $this->user()['login'], $params);
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage(), $params ?? []));
            return $this->redirect('/updates');
        }
        $this->audit('sevenzip.upload', 'update', $package['version'], ['version' => $package['version'], 'sha256' => $package['sha256']]);
        $this->session->flash('info', $this->t('sevenzip.flash.stored', ['version' => $package['version']]));
        return $this->redirect('/updates');
    }

    /** Turns the daily 7-Zip check on or off. */
    public function sevenZipSettings(): Response
    {
        $on = $this->post('sevenzip_check') === '1';
        $this->app->settings()->set('sevenzip_check', $on ? '1' : '0');
        $this->audit('settings.update', 'settings', null, ['sevenzip_check' => $on ? 'on' : 'off']);
        $this->session->flash('info', $this->t('settings.flash.saved'));
        return $this->redirect('/updates');
    }

    /** "Check now" for the newest releases of the site and the program. */
    public function releasesCheck(): Response
    {
        $releases = $this->app->releases();
        if (!$releases->check(time())) {
            $this->session->flash('error', $this->t('releases.flash.check_failed', ['error' => $this->app->settings()->get('releases_check_error')]));
            return $this->redirect('/updates');
        }
        $site = $releases->latest('server');
        $this->session->flash('info', $site === null
            ? $this->t('releases.flash.checked_none')
            : $this->t($releases->newerSite() === null ? 'releases.flash.checked_current' : 'releases.flash.checked_newer', ['version' => $site['version']]));
        return $this->redirect('/updates');
    }

    /** Turns the daily check of CUSTOS releases on or off. */
    public function releasesSettings(): Response
    {
        $on = $this->post('releases_check') === '1';
        $this->app->settings()->set('releases_check', $on ? '1' : '0');
        $this->audit('settings.update', 'settings', null, ['releases_check' => $on ? 'on' : 'off']);
        $this->session->flash('info', $this->t('settings.flash.saved'));
        return $this->redirect('/updates');
    }

    public function releasesChannel(): Response
    {
        $channel = $this->post('releases_channel');
        try {
            $this->app->releases()->setChannel($channel);
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage()));
            return $this->redirect('/updates');
        }
        $this->audit('settings.update', 'settings', null, ['releases_channel' => $channel]);
        $this->session->flash('info', $this->t('settings.flash.saved'));
        return $this->redirect('/updates');
    }

    /**
     * Downloads the newest program release from GitHub as an agent update package (checked and signed): $part client
     * for the standard edition, legacy for the edition for Windows 7 and 8.1, macos and linux for CUSTOS for macOS and Linux.
     */
    public function clientDownload(string $part): Response
    {
        @set_time_limit(300);
        try {
            $package = $this->app->releases()->downloadClient($this->user()['login'], $params,
                in_array($part, Releases::PROGRAM_PARTS, true) ? $part : 'client', $this->post('release_selection'));
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage(), $params ?? []));
            return $this->redirect('/updates');
        }
        $this->audit('update.download', 'update', $package['version'], ['version' => $package['version'], 'edition' => self::edition($package['kind']), 'sha256' => $package['sha256']]);
        $this->session->flash('info', $this->t($package['kind'] === 'agent' ? 'releases.flash.client_stored' : "releases.flash.{$package['kind']}_stored", ['version' => $package['version']]));
        return $this->redirect('/updates');
    }

    /** Queues the update of the site to the newest release; cron runs it within a minute. */
    public function siteInstall(): Response
    {
        try {
            $state = $this->app->siteUpdate()->request($this->user()['login'], $this->post('release_selection'));
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage()));
            return $this->redirect('/updates');
        }
        $this->audit('site.update', 'update', (string) $state['to'], ['from' => (string) $state['from'], 'to' => (string) $state['to']]);
        $this->session->flash('info', $this->t('site.flash.queued', ['version' => (string) $state['to']]));
        return $this->redirect('/updates');
    }

    /** Takes an uploaded site package and queues the update from it. */
    public function siteUpload(): Response
    {
        $file = $this->uploaded('package');
        if ($file === null) {
            return $this->redirect('/updates');
        }
        try {
            $state = $this->app->siteUpdate()->upload($file, $this->user()['login'], $params);
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage(), $params ?? []));
            return $this->redirect('/updates');
        }
        $this->audit('site.upload', 'update', (string) $state['to'], [
            'from' => (string) $state['from'], 'to' => (string) $state['to'], 'sha256' => (string) $state['package_sha256'],
        ]);
        $this->session->flash('info', $this->t('site.flash.queued', ['version' => (string) $state['to']]));
        return $this->redirect('/updates');
    }

    /** Takes back a queued site update. */
    public function siteCancel(): Response
    {
        $state = $this->app->siteUpdate()->cancel();
        if ($state === null) {
            $this->session->flash('error', $this->t('site.error.not_queued'));
            return $this->redirect('/updates');
        }
        $this->audit('site.cancel', 'update', (string) $state['to'], ['to' => (string) $state['to']]);
        $this->session->flash('info', $this->t('site.flash.cancelled', ['version' => (string) $state['to']]));
        return $this->redirect('/updates');
    }

    /** The edition of a package kind as the journal shows it. */
    private static function edition(string $kind): string
    {
        return match ($kind) {
            'legacy' => 'Windows 7, 8.1',
            'macos' => 'macOS',
            'linux' => 'Linux',
            default => 'standard',
        };
    }

    /** Path of an uploaded file, or null with a message in the flash. */
    private function uploaded(string $field): ?string
    {
        $file = $this->request->files[$field] ?? null;
        $error = is_array($file) ? (int) ($file['error'] ?? UPLOAD_ERR_NO_FILE) : UPLOAD_ERR_NO_FILE;
        if ($error === UPLOAD_ERR_OK && is_string($file['tmp_name'] ?? null) && is_file($file['tmp_name'])) {
            return $file['tmp_name'];
        }
        $this->session->flash('error', match ($error) {
            UPLOAD_ERR_INI_SIZE, UPLOAD_ERR_FORM_SIZE => $this->t('updates.error.too_large', ['limit' => fmt_bytes(self::uploadLimit())]),
            UPLOAD_ERR_NO_FILE => $this->t('updates.error.no_file'),
            default => $this->t('updates.error.upload', ['code' => $error]),
        });
        return null;
    }

    /** The largest upload PHP accepts: the smaller of upload_max_filesize and post_max_size. */
    public static function uploadLimit(): int
    {
        $limits = array_filter([self::bytes((string) ini_get('upload_max_filesize')), self::bytes((string) ini_get('post_max_size'))]);
        return $limits === [] ? 0 : min($limits);
    }

    private static function bytes(string $value): int
    {
        $value = trim($value);
        $number = (int) $value;
        return match (strtolower(substr($value, -1))) {
            'g' => $number * 1024 ** 3,
            'm' => $number * 1024 ** 2,
            'k' => $number * 1024,
            default => $number,
        };
    }
}
