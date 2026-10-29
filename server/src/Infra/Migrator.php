<?php

declare(strict_types=1);

namespace Custos\Infra;

/**
 * Applies migrations/NNN_name.sql in order and records them in schema_migrations.
 * A migration file is a list of statements separated by ";" at the end of a line; "--" lines are comments.
 */
final class Migrator
{
    public function __construct(private readonly Db $db, private readonly string $dir)
    {
    }

    /** @return list<string> file names not applied yet */
    public function pending(): array
    {
        $this->ensureTable();
        $applied = array_flip($this->db->column('SELECT version FROM schema_migrations'));
        $files = array_map('basename', glob($this->dir . '/*.sql') ?: []);
        sort($files, SORT_STRING);
        return array_values(array_filter($files, fn(string $file) => !isset($applied[$file])));
    }

    /**
     * @param callable(string): void $report called with each applied file name
     * @return int number of applied migrations
     */
    public function migrate(callable $report): int
    {
        $count = 0;
        foreach ($this->pending() as $file) {
            foreach (self::statements((string) file_get_contents($this->dir . '/' . $file)) as $sql) {
                $this->db->pdo->exec($sql);
            }
            $this->db->exec('INSERT INTO schema_migrations (version, applied_at) VALUES (?, ?)', [$file, Time::now()]);
            $report($file);
            $count++;
        }
        return $count;
    }

    /** @return list<string> */
    public static function statements(string $sql): array
    {
        $lines = array_filter(
            preg_split('/\R/', $sql),
            fn(string $line) => !str_starts_with(ltrim($line), '--')
        );
        $statements = preg_split('/;\s*(?:\R|$)/', implode("\n", $lines));
        return array_values(array_filter(array_map('trim', $statements), fn(string $s) => $s !== ''));
    }

    private function ensureTable(): void
    {
        $this->db->pdo->exec(
            'CREATE TABLE IF NOT EXISTS schema_migrations (
                version VARCHAR(255) NOT NULL,
                applied_at DATETIME NOT NULL,
                PRIMARY KEY (version)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci'
        );
    }
}
