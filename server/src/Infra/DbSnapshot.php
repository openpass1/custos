<?php

declare(strict_types=1);

namespace Custos\Infra;

use PDO;

/**
 * A copy of some tables taken before a site update runs its migrations, so that a failed update can put the
 * database back. Only the tables the new migrations touch are copied (most updates have no migrations at all and
 * copy nothing); a migration whose statements are not understood makes it copy every table.
 *
 * File format (gzip): records "<length>\n<SQL statement>\n", which keeps any byte of the data intact. The statements
 * are DROP TABLE IF EXISTS, CREATE TABLE as SHOW CREATE TABLE prints it, and INSERT with the rows. The list of tables
 * that existed before goes into the update's backup.json: tables created later are dropped on restore.
 */
final class DbSnapshot
{
    /** INSERT statements are cut at about this size, far below max_allowed_packet. */
    private const INSERT_BYTES = 512 * 1024;
    private const ROWS_AT_ONCE = 1000;

    public function __construct(private readonly PDO $pdo)
    {
    }

    /** @return list<string> */
    public function tables(): array
    {
        return $this->pdo->query('SHOW TABLES')->fetchAll(PDO::FETCH_COLUMN);
    }

    /**
     * Tables that these SQL statements change or create; null when a statement is not understood (copy everything).
     *
     * @param list<string> $statements
     * @return list<string>|null
     */
    public static function touchedTables(array $statements): ?array
    {
        $name = '`?([A-Za-z0-9_$]+)`?';
        $tables = [];
        foreach ($statements as $sql) {
            $sql = trim((string) preg_replace('/\s+/', ' ', $sql));
            $found = [];
            if (preg_match("/^CREATE TABLE (?:IF NOT EXISTS )?$name/i", $sql, $m)
                || preg_match("/^ALTER (?:IGNORE )?TABLE $name/i", $sql, $m)
                || preg_match("/^(?:INSERT|REPLACE) (?:LOW_PRIORITY |DELAYED |HIGH_PRIORITY )?(?:IGNORE )?(?:INTO )?$name/i", $sql, $m)
                || preg_match("/^DELETE (?:LOW_PRIORITY |QUICK |IGNORE )*FROM $name/i", $sql, $m)
                || preg_match("/^TRUNCATE (?:TABLE )?$name/i", $sql, $m)
                || preg_match("/^(?:CREATE (?:UNIQUE |FULLTEXT |SPATIAL )?|DROP )INDEX $name ON $name/i", $sql, $m)) {
                $found[] = end($m);
            } elseif (preg_match("/^UPDATE (?:LOW_PRIORITY )?(?:IGNORE )?$name/i", $sql, $m)) {
                // A multi-table UPDATE may change every table it joins.
                $found[] = $m[1];
                preg_match_all("/\bJOIN $name/i", $sql, $joins);
                array_push($found, ...$joins[1]);
            } elseif (preg_match('/^DROP TABLE (?:IF EXISTS )?(.+)$/i', $sql, $m)) {
                foreach (explode(',', $m[1]) as $part) {
                    if (!preg_match("/^\s*$name\s*$/", $part, $t)) {
                        return null;
                    }
                    $found[] = $t[1];
                }
            } elseif (preg_match('/^RENAME TABLE (.+)$/i', $sql, $m)) {
                foreach (explode(',', $m[1]) as $pair) {
                    if (!preg_match("/^\s*$name TO $name\s*$/i", $pair, $t)) {
                        return null;
                    }
                    $found[] = $t[1];
                    $found[] = $t[2];
                }
            } else {
                return null;
            }
            foreach ($found as $table) {
                $tables[strtolower($table)] = $table;
            }
        }
        return array_values($tables);
    }

    /**
     * Writes the tables that exist among $tables into $file.
     *
     * @param list<string> $tables
     * @return list<string> the tables written
     */
    public function dump(array $tables, string $file): array
    {
        $existing = [];
        foreach ($this->tables() as $table) {
            $existing[strtolower($table)] = $table;
        }
        $out = gzopen($file, 'wb6');
        if ($out === false) {
            throw new \RuntimeException("cannot write $file");
        }
        $written = [];
        try {
            foreach ($tables as $wanted) {
                $table = $existing[strtolower($wanted)] ?? null;
                if ($table === null) {
                    continue;
                }
                $create = (string) $this->pdo->query('SHOW CREATE TABLE ' . self::quoteName($table))->fetch(PDO::FETCH_NUM)[1];
                self::write($out, 'DROP TABLE IF EXISTS ' . self::quoteName($table));
                self::write($out, $create);
                $this->dumpRows($out, $table);
                $written[] = $table;
            }
        } finally {
            gzclose($out);
        }
        return $written;
    }

    /**
     * Puts the copied tables back and drops tables that did not exist when the copy was taken.
     *
     * @param list<string> $tablesBefore every table of the database at the time of the copy
     */
    public function restore(string $file, array $tablesBefore): void
    {
        $before = array_flip(array_map('strtolower', $tablesBefore));
        $this->pdo->exec('SET FOREIGN_KEY_CHECKS = 0');
        try {
            foreach ($this->tables() as $table) {
                if (!isset($before[strtolower($table)])) {
                    $this->pdo->exec('DROP TABLE ' . self::quoteName($table));
                }
            }
            $in = gzopen($file, 'rb');
            if ($in === false) {
                throw new \RuntimeException("cannot read $file");
            }
            try {
                while (($line = gzgets($in)) !== false) {
                    $length = (int) $line;
                    if ($length <= 0) {
                        continue;
                    }
                    $sql = '';
                    while (strlen($sql) < $length) {
                        $chunk = gzread($in, $length - strlen($sql));
                        if ($chunk === false || $chunk === '') {
                            throw new \RuntimeException("$file is cut short");
                        }
                        $sql .= $chunk;
                    }
                    gzgetc($in);
                    $this->pdo->exec($sql);
                }
            } finally {
                gzclose($in);
            }
        } finally {
            $this->pdo->exec('SET FOREIGN_KEY_CHECKS = 1');
        }
    }

    /** @param resource $out */
    private function dumpRows($out, string $table): void
    {
        $columns = null;
        $values = [];
        $bytes = 0;
        $flush = function () use ($out, $table, &$columns, &$values, &$bytes): void {
            if ($values !== []) {
                self::write($out, 'INSERT INTO ' . self::quoteName($table) . ' (' . $columns . ') VALUES ' . implode(',', $values));
            }
            $values = [];
            $bytes = 0;
        };
        // Unbuffered reading would block other queries on this connection; pages by offset keep memory flat.
        for ($offset = 0; ; $offset += self::ROWS_AT_ONCE) {
            $rows = $this->pdo->query('SELECT * FROM ' . self::quoteName($table) . ' LIMIT ' . self::ROWS_AT_ONCE . ' OFFSET ' . $offset)
                ->fetchAll(PDO::FETCH_ASSOC);
            foreach ($rows as $row) {
                $columns ??= implode(', ', array_map(self::quoteName(...), array_keys($row)));
                $tuple = '(' . implode(', ', array_map($this->literal(...), $row)) . ')';
                $values[] = $tuple;
                $bytes += strlen($tuple);
                if ($bytes >= self::INSERT_BYTES) {
                    $flush();
                }
            }
            if (count($rows) < self::ROWS_AT_ONCE) {
                break;
            }
        }
        $flush();
    }

    private function literal(mixed $value): string
    {
        return match (true) {
            $value === null => 'NULL',
            is_int($value) => (string) $value,
            is_float($value) => var_export($value, true),
            is_bool($value) => $value ? '1' : '0',
            // Bytes that are not UTF-8 (compressed logs) go as a hex literal, which MySQL never re-encodes.
            !mb_check_encoding((string) $value, 'UTF-8') => $value === '' ? "''" : "X'" . bin2hex((string) $value) . "'",
            default => (string) $this->pdo->quote((string) $value),
        };
    }

    /** @param resource $out */
    private static function write($out, string $sql): void
    {
        if (gzwrite($out, strlen($sql) . "\n" . $sql . "\n") === false) {
            throw new \RuntimeException('cannot write the database copy');
        }
    }

    private static function quoteName(string $name): string
    {
        return '`' . str_replace('`', '``', $name) . '`';
    }
}
