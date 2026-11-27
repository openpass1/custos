<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\App;
use Custos\Infra\Time;
use Custos\Infra\UpdateError;
use Custos\Infra\Updater;

/**
 * Updates of the site itself, the part the site does (section 8): an administrator queues the update to the newest
 * release found by Releases, or to an uploaded package (a server without access to GitHub), and cron runs it
 * (Infra\Updater) within a minute. The state of the current or last update is var/update/state.json.
 */
final class SiteUpdate
{
    public function __construct(
        private readonly Releases $releases,
        private readonly string $varDir,
    ) {
    }

    /** The current or the last update (Infra\Updater's state.json), null when there was none. */
    public function state(): ?array
    {
        return Updater::readState($this->varDir);
    }

    public function busy(): bool
    {
        return in_array($this->state()['status'] ?? null, ['queued', 'running'], true);
    }

    /**
     * Queues the update to the newest release; cron runs it.
     *
     * @throws \InvalidArgumentException with a lang key
     */
    public function request(string $login, ?string $selection = null): array
    {
        $release = $this->releases->newerSite();
        $this->releases->checkSelection($selection, $release);
        if ($release === null) {
            throw new \InvalidArgumentException('site.error.nothing');
        }
        if ($this->busy()) {
            throw new \InvalidArgumentException('site.error.busy');
        }
        $state = Updater::newState('github', $login, ['release' => $release, 'from' => App::VERSION, 'to' => $release['version']]);
        Updater::writeState($this->varDir, $state);
        return $state;
    }

    /**
     * Checks an uploaded package and queues the update from it (a server without access to GitHub).
     *
     * @throws \InvalidArgumentException with a lang key; $params fills it
     */
    public function upload(string $file, string $login, ?array &$params = null): array
    {
        $params = [];
        if ($this->busy()) {
            throw new \InvalidArgumentException('site.error.busy');
        }
        try {
            $info = Updater::inspect($file);
            if (version_compare($info['version'], App::VERSION, '<=')) {
                throw new UpdateError('site.error.not_newer', ['version' => $info['version'], 'current' => App::VERSION]);
            }
            Updater::checkPlatform($info);
        } catch (UpdateError $e) {
            $params = $e->params;
            throw new \InvalidArgumentException($e->key);
        }
        $dir = $this->varDir . '/update/upload';
        Updater::removeTree($dir);
        if (!@mkdir($dir, 0750, true) && !is_dir($dir)) {
            $params = ['reason' => "cannot create $dir"];
            throw new \InvalidArgumentException('site.error.files');
        }
        $target = $dir . '/custos-server-' . $info['version'] . '.zip';
        if (!@copy($file, $target)) {
            $params = ['reason' => "cannot write $target"];
            throw new \InvalidArgumentException('site.error.files');
        }
        $state = Updater::newState('file', $login, [
            'package' => $target,
            'package_sha256' => hash_file('sha256', $target),
            'from' => App::VERSION,
            'to' => $info['version'],
        ]);
        Updater::writeState($this->varDir, $state);
        return $state;
    }

    /** Takes back a queued update that has not started. */
    public function cancel(): ?array
    {
        $state = $this->state();
        if (($state['status'] ?? null) !== 'queued') {
            return null;
        }
        $state['status'] = 'cancelled';
        $state['finished_at'] = Time::now();
        Updater::writeState($this->varDir, $state);
        if (($state['source'] ?? '') === 'file') {
            Updater::removeTree($this->varDir . '/update/upload');
        }
        return $state;
    }
}
