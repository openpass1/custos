<?php

declare(strict_types=1);

namespace Custos\Http\Api;

use Custos\Http\Response;

/** An error answer of the agent API (A.11): {"error": "code", "message": "text"} plus optional fields. */
final class ApiError extends \RuntimeException
{
    public function __construct(
        public readonly int $status,
        public readonly string $error,
        string $message,
        public readonly array $extra = []
    ) {
        parent::__construct($message);
    }

    public static function badRequest(string $message): self
    {
        return new self(400, 'bad_request', $message);
    }

    public function response(): Response
    {
        return Response::json($this->status, ['error' => $this->error, 'message' => $this->getMessage()] + $this->extra);
    }
}
