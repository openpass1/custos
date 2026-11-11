<?php

declare(strict_types=1);

namespace Custos\Infra;

use OpenSSLAsymmetricKey;

/**
 * The server key pair (A.2): config/keys/server.key (mode 600) and server.pub.
 */
final class ServerKeys
{
    private ?OpenSSLAsymmetricKey $private = null;

    public function __construct(private readonly string $dir)
    {
    }

    public function exists(): bool
    {
        return is_file($this->privatePath());
    }

    /** Creates the key pair unless it already exists. Returns false when it was there before. */
    public function create(): bool
    {
        if ($this->exists()) {
            return false;
        }
        if (!is_dir($this->dir) && !mkdir($this->dir, 0700, true) && !is_dir($this->dir)) {
            throw new \RuntimeException("Cannot create the directory {$this->dir}.");
        }
        @chmod($this->dir, 0700);
        $pair = Crypto::generateKeyPair();
        $old = umask(0077);
        try {
            if (@file_put_contents($this->privatePath(), $pair['private']) === false) {
                throw new \RuntimeException('Cannot write ' . $this->privatePath() . '.');
            }
        } finally {
            umask($old);
        }
        chmod($this->privatePath(), 0600);
        file_put_contents($this->dir . '/server.pub', $pair['public']);
        chmod($this->dir . '/server.pub', 0644);
        return true;
    }

    public function privateKey(): OpenSSLAsymmetricKey
    {
        if ($this->private === null) {
            $path = $this->privatePath();
            $pem = @file_get_contents($path);
            $key = $pem === false ? false : openssl_pkey_get_private($pem);
            if ($key === false) {
                throw new \RuntimeException(match (true) {
                    $pem !== false => "Server key $path is damaged.",
                    is_file($path) => "Server key $path cannot be read by this process. The server folder must belong to the site user, the one PHP runs as.",
                    default => "Server key $path is missing. Run bin/install.php as the site user.",
                });
            }
            $this->private = $key;
        }
        return $this->private;
    }

    public function publicPem(): string
    {
        return Crypto::publicPem($this->privateKey());
    }

    public function fingerprint(): string
    {
        return Crypto::fingerprint($this->privateKey());
    }

    private function privatePath(): string
    {
        return $this->dir . '/server.key';
    }
}
