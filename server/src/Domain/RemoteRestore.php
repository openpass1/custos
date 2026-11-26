<?php

declare(strict_types=1);

namespace Custos\Domain;

/** Structured command results for restoring Linux backup files. */
final class RemoteRestore
{
    public const LIST = 'restore.list';
    public const START = 'restore.start';
    public const STATUS = 'restore.status';
    public const STOP = 'restore.stop';
    public const COMMANDS = [self::LIST, self::START, self::STATUS, self::STOP];

    private const STATES = ['running', 'success', 'warning', 'error', 'aborted', 'interrupted'];
    private const ERRORS = ['bad_request', 'not_allowed', 'task_missing', 'set_missing', 'busy', 'output_unavailable',
                            'job_missing', 'scan_failed', 'internal_error'];

    /** Read only the fields this feature displays. Errors and job details never carry arbitrary paths or logs. */
    public static function result(string $kind, ?string $jobId, string $status, Input $in): array
    {
        $restore = $in->object('restore', $status !== 'done');
        if ($status !== 'done') {
            return ['error' => $restore?->enum('error', self::ERRORS, true) ?? 'internal_error'];
        }

        if ($kind === self::LIST) {
            if (!$restore->has('sets')) {
                throw new InvalidInput('restore.sets is required');
            }
            $items = $restore->objects('sets');
            if (count($items) > 100) {
                throw new InvalidInput('restore.sets may contain at most 100 items');
            }
            $sets = [];
            foreach ($items as $item) {
                $name = $item->string('name', 120);
                if (str_contains($name, '/') || str_contains($name, '\\') || preg_match('/[\x00-\x1f\x7f]/', $name)) {
                    throw new InvalidInput('restore.sets.name must be a display name');
                }
                $set = [
                    'set_run_id' => $item->uuid('set_run_id'),
                    'destination_id' => $item->uuid('destination_id'),
                    'name' => $name,
                    'started_at' => $item->time('started_at'),
                    'backup_type' => $item->enum('backup_type', Reports::BACKUP_TYPES),
                    'storage' => $item->enum('storage', ['folder', 'zip', '7z']),
                    'parts' => $item->int('parts', min: 1),
                    'bytes' => $item->int('bytes'),
                ];
                $sets[$set['set_run_id'] . ':' . $set['destination_id']] = $set;
            }
            return ['sets' => $sets, 'truncated' => $restore->bool('truncated'), 'partial' => $restore->bool('partial')];
        }

        $job = $restore->object('job');
        $id = $job->uuid('job_id');
        if ($id !== $jobId) {
            throw new InvalidInput('restore.job.job_id does not match the command');
        }
        $result = ['job_id' => $id, 'state' => $job->enum('state', self::STATES)];
        foreach (['files_done', 'files_total', 'bytes_done', 'bytes_total', 'files_restored', 'files_skipped', 'files_failed', 'warnings'] as $key) {
            $value = $job->int($key, true);
            if ($value !== null) {
                $result[$key] = $value;
            }
        }
        return ['job' => $result];
    }
}
