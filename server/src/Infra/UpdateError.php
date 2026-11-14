<?php

declare(strict_types=1);

namespace Custos\Infra;

/**
 * Why a site update stopped: a key of lang/*.php with its parameters, so the site shows it in the reader's language
 * and bin/update.php in English. $output holds what a script of the new version printed (migrations, self-test).
 */
final class UpdateError extends \RuntimeException
{
    public function __construct(
        public readonly string $key,
        public readonly array $params = [],
        public readonly string $output = '',
        ?\Throwable $previous = null
    ) {
        parent::__construct($key . ($params === [] ? '' : ' ' . json_encode($params, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)), 0, $previous);
    }
}
