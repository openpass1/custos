<?php

declare(strict_types=1);

namespace Custos\Http;

final class Response
{
    /** A file sent as the body instead of $body (agent update packages): read in chunks, never held in memory. */
    public ?string $file = null;

    /** @var ?\Closure(resource): void writes the body to the stream it gets (a file inside a package), instead of $body */
    public ?\Closure $stream = null;

    /** @param array<string, string> $headers */
    public function __construct(public int $status = 200, public string $body = '', public array $headers = [])
    {
    }

    public static function file(string $path, string $contentType, string $downloadName): self
    {
        $response = new self(200, '', [
            'Content-Type' => $contentType,
            'Content-Length' => (string) filesize($path),
            'Content-Disposition' => 'attachment; filename="' . str_replace('"', '', $downloadName) . '"',
        ]);
        $response->file = $path;
        return $response;
    }

    /**
     * A body written by $writer to the stream it gets, in pieces: a file inside a stored package (Install).
     *
     * @param \Closure(resource): void $writer
     */
    public static function stream(\Closure $writer, string $contentType, string $downloadName, int $length): self
    {
        $response = new self(200, '', [
            'Content-Type' => $contentType,
            'Content-Length' => (string) $length,
            'Content-Disposition' => 'attachment; filename="' . str_replace('"', '', $downloadName) . '"',
        ]);
        $response->stream = $writer;
        return $response;
    }

    /** The body, read from the file for a file response or written by the writer of a stream response. */
    public function content(): string
    {
        if ($this->stream !== null) {
            $memory = fopen('php://temp', 'w+b');
            ($this->stream)($memory);
            rewind($memory);
            $content = (string) stream_get_contents($memory);
            fclose($memory);
            return $content;
        }
        return $this->file === null ? $this->body : (string) file_get_contents($this->file);
    }

    public static function json(int $status, array|object $data): self
    {
        return new self(
            $status,
            json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR),
            ['Content-Type' => 'application/json; charset=utf-8']
        );
    }

    public static function html(string $html, int $status = 200): self
    {
        return new self($status, $html, ['Content-Type' => 'text/html; charset=utf-8']);
    }

    public static function text(string $text, int $status = 200): self
    {
        return new self($status, $text, ['Content-Type' => 'text/plain; charset=utf-8']);
    }

    public static function redirect(string $url): self
    {
        return new self(303, '', ['Location' => $url]);
    }

    public function header(string $name): ?string
    {
        foreach ($this->headers as $key => $value) {
            if (strcasecmp($key, $name) === 0) {
                return $value;
            }
        }
        return null;
    }

    public function send(): void
    {
        http_response_code($this->status);
        foreach ($this->headers as $name => $value) {
            header($name . ': ' . $value);
        }
        if ($this->file !== null || $this->stream !== null) {
            while (ob_get_level() > 0) {
                ob_end_clean();
            }
            if ($this->file !== null) {
                readfile($this->file);
            } else {
                $out = fopen('php://output', 'wb');
                ($this->stream)($out);
                fclose($out);
            }
            return;
        }
        echo $this->body;
    }
}
