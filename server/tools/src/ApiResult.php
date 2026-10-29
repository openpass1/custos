<?php

declare(strict_types=1);

namespace Custos\Tools;

/** A server answer as the agent sees it. Only a signed answer may be acted upon (A.2, A.11). */
final class ApiResult
{
    /** @param array<string, string> $headers names in lower case */
    public function __construct(
        public readonly int $status,
        public readonly string $body,
        public readonly bool $signed,
        public readonly array $headers = [],
        public readonly string $transportError = ''
    ) {
    }

    public function data(): array
    {
        $data = json_decode($this->body, true);
        return is_array($data) ? $data : [];
    }

    public function error(): ?string
    {
        return $this->data()['error'] ?? null;
    }

    public function ok(): bool
    {
        return $this->signed && $this->status === 200;
    }

    public function describe(): string
    {
        if ($this->status === 0) {
            return 'no connection: ' . $this->transportError;
        }
        $data = $this->data();
        $text = "HTTP {$this->status}";
        if (isset($data['error'])) {
            $text .= " {$data['error']}: " . ($data['message'] ?? '');
        }
        return $this->signed ? $text : "$text (unsigned answer, treated as server unavailable)";
    }
}
