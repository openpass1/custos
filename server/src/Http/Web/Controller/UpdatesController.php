<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Agents;
use Custos\Domain\Packages;
use Custos\Http\Response;
use Custos\Http\Web\Controller;

/**
 * Updates (section 8): agent update packages (upload, current version, update machines) and 7-Zip (the daily check of
 * the newest release, download or upload of the installer, update of 7z.dll on machines).
 */
final class UpdatesController extends Controller
{
    /** The machine list of the page (versions, updates on their way) refreshes this often (seconds). */
    public const REFRESH_S = 15;

    public function index(): Response
    {
        $packages = $this->app->packages();
        $watch = $this->app->updateWatch();
        $agents = array_values(array_filter($this->app->agents()->all(), fn($a) => $a['status'] !== 'pending'));
        $current = $packages->current();
        $latestSevenZip = $watch->latestSevenZip();
        $settings = $this->app->settings();
        return $this->render('updates/index', [
            'section' => 'updates',
            'agentPackages' => $packages->all('agent'),
            'sevenZipPackages' => $packages->all('7zip'),
            'current' => $current,
            'agents' => $agents,
            'groups' => $this->app->groups()->all(),
            'outdated' => array_column(Packages::outdated($agents, $current['version'] ?? null), null, 'id'),
            'latestSevenZip' => $latestSevenZip,
            'sevenZipOutdated' => array_column(Packages::outdated($agents, $latestSevenZip['version'] ?? null, 'sevenzip_version'), null, 'id'),
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
        $this->audit('update.upload', 'update', $package['version'], ['version' => $package['version'], 'sha256' => $package['sha256']]);
        $this->session->flash('info', $this->t('updates.flash.uploaded', ['version' => $package['version']]));
        return $this->redirect('/updates');
    }

    public function current(string $version): Response
    {
        if ($this->app->packages()->find('agent', $version) === null) {
            return $this->notFound();
        }
        $this->app->packages()->setCurrent($version);
        $this->audit('update.current', 'update', $version, ['version' => $version]);
        $this->session->flash('info', $this->t('updates.flash.current', ['version' => $version]));
        return $this->redirect('/updates');
    }

    public function delete(string $kind, string $version): Response
    {
        $kind = $kind === '7zip' ? '7zip' : 'agent';
        if ($this->app->packages()->find($kind, $version) === null) {
            return $this->notFound();
        }
        $this->app->packages()->delete($kind, $version);
        $this->audit($kind === '7zip' ? 'sevenzip.delete' : 'update.delete', 'update', $version, ['version' => $version]);
        $this->session->flash('info', $this->t('updates.flash.deleted', ['version' => $version]));
        return $this->redirect('/updates');
    }

    /**
     * Updates machines: kind agent (the current version) or 7zip (the newest installer on the server); target
     * "selected" (agent_ids[]), "outdated" or "group" (group_id).
     */
    public function install(): Response
    {
        $kind = $this->post('kind') === '7zip' ? '7zip' : 'agent';
        $packages = $this->app->packages();
        $package = $kind === 'agent' ? $packages->current() : $packages->latestSevenZip();
        if ($package === null) {
            $this->session->flash('error', $this->t($kind === 'agent' ? 'updates.error.no_current' : 'sevenzip.error.none'));
            return $this->redirect('/updates');
        }
        $all = array_values(array_filter($this->app->agents()->all(), fn($a) => $a['status'] === 'approved'));
        $target = $this->post('target');
        $agents = match ($target) {
            'outdated' => Packages::outdated($all, $package['version'], $kind === '7zip' ? 'sevenzip_version' : 'agent_version'),
            'group' => array_values(array_filter($all, fn($a) => (string) $a['group_id'] === $this->post('group_id') && $this->post('group_id') !== '')),
            default => array_values(array_filter($all, fn($a) => in_array($a['id'], (array) ($this->request->post['agent_ids'] ?? []), true))),
        };
        if ($agents === []) {
            $this->session->flash('error', $this->t('updates.error.no_machines'));
            return $this->redirect('/updates');
        }
        $result = $packages->install($kind, $package['version'], $agents, $this->user());
        if ($result['queued'] !== []) {
            $this->audit($kind === '7zip' ? 'sevenzip.install' : 'update.install', 'update', $package['version'], [
                'version' => $package['version'],
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
