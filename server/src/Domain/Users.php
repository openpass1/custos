<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Site users. There are no roles: every user is an administrator (section 4).
 */
final class Users
{
    public const MIN_PASSWORD = 8;

    public function __construct(private readonly Db $db)
    {
    }

    public static function hash(string $password): string
    {
        return password_hash($password, defined('PASSWORD_ARGON2ID') ? PASSWORD_ARGON2ID : PASSWORD_BCRYPT);
    }

    public static function validLogin(string $login): bool
    {
        return (bool) preg_match('/^[\p{L}\p{N}._@-]{1,64}$/u', $login);
    }

    public function find(int $id): ?array
    {
        return $this->db->one('SELECT * FROM users WHERE id = ?', [$id]);
    }

    public function findByLogin(string $login): ?array
    {
        return $this->db->one('SELECT * FROM users WHERE login = ?', [$login]);
    }

    public function all(): array
    {
        return $this->db->all('SELECT * FROM users ORDER BY login');
    }

    public function count(): int
    {
        return (int) $this->db->value('SELECT COUNT(*) FROM users');
    }

    public function countActive(): int
    {
        return (int) $this->db->value('SELECT COUNT(*) FROM users WHERE disabled = 0');
    }

    /** @throws \InvalidArgumentException with a language key */
    public function create(string $login, string $password): int
    {
        if (!self::validLogin($login)) {
            throw new \InvalidArgumentException('users.error.login');
        }
        if (mb_strlen($password) < self::MIN_PASSWORD) {
            throw new \InvalidArgumentException('users.error.password');
        }
        if ($this->findByLogin($login) !== null) {
            throw new \InvalidArgumentException('users.error.exists');
        }
        $this->db->exec(
            'INSERT INTO users (login, password_hash, created_at) VALUES (?, ?, ?)',
            [$login, self::hash($password), Time::now()]
        );
        return $this->db->lastInsertId();
    }

    /** @throws \InvalidArgumentException with a language key */
    public function setPassword(int $id, string $password): void
    {
        if (mb_strlen($password) < self::MIN_PASSWORD) {
            throw new \InvalidArgumentException('users.error.password');
        }
        $this->db->exec('UPDATE users SET password_hash = ? WHERE id = ?', [self::hash($password), $id]);
    }

    public function setDisabled(int $id, bool $disabled): void
    {
        $this->db->exec('UPDATE users SET disabled = ? WHERE id = ?', [(int) $disabled, $id]);
    }

    /**
     * Checks a login and password. Spends the same time on unknown logins, and upgrades old hashes.
     * Returns the user row, disabled users included; the caller decides what to do with them.
     */
    public function verify(string $login, string $password): ?array
    {
        $user = $this->findByLogin($login);
        if ($user === null) {
            password_verify($password, self::dummyHash());
            return null;
        }
        if (!password_verify($password, $user['password_hash'])) {
            return null;
        }
        if (password_needs_rehash($user['password_hash'], defined('PASSWORD_ARGON2ID') ? PASSWORD_ARGON2ID : PASSWORD_BCRYPT)) {
            $this->db->exec('UPDATE users SET password_hash = ? WHERE id = ?', [self::hash($password), $user['id']]);
        }
        return $user;
    }

    public function touchLogin(int $id, string $ip): void
    {
        $this->db->exec('UPDATE users SET last_login_at = ?, last_login_ip = ? WHERE id = ?', [Time::now(), $ip, $id]);
    }

    private static function dummyHash(): string
    {
        static $hash = null;
        return $hash ??= self::hash(bin2hex(random_bytes(8)));
    }
}
