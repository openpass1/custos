<?php

declare(strict_types=1);

namespace Custos\Domain;

/** Data from an agent does not match the protocol. The API answers 400 bad_request with this message. */
final class InvalidInput extends \RuntimeException
{
}
