<?php

declare(strict_types=1);

namespace Custos\Infra;

/**
 * Sends a text message to one Telegram chat through the Bot API, optionally through an HTTP or SOCKS5 proxy
 * (section 9: api.telegram.org may be unreachable from the server).
 */
final class Telegram
{
    /** Telegram refuses longer messages. */
    public const MAX_LENGTH = 4096;
    public const TIMEOUT_S = 20;

    /**
     * @param string $apiUrl https://api.telegram.org, or a local stand-in in tests
     * @param string $proxy "" or http://host:port, socks5://host:port, socks5h://user:password@host:port
     */
    public function __construct(
        private readonly string $token,
        private readonly string $proxy = '',
        private readonly string $apiUrl = 'https://api.telegram.org'
    ) {
    }

    /** @throws \RuntimeException with a text the administrator can act on */
    public function send(string $chatId, string $text): void
    {
        foreach (self::split($text) as $part) {
            $this->call('sendMessage', ['chat_id' => $chatId, 'text' => $part, 'disable_web_page_preview' => 'true']);
        }
    }

    private function call(string $method, array $fields): void
    {
        $curl = curl_init(rtrim($this->apiUrl, '/') . '/bot' . $this->token . '/' . $method);
        $options = [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => http_build_query($fields),
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_CONNECTTIMEOUT => 10,
            CURLOPT_TIMEOUT => self::TIMEOUT_S,
            CURLOPT_PROTOCOLS => CURLPROTO_HTTP | CURLPROTO_HTTPS,
        ];
        if ($this->proxy !== '') {
            $options[CURLOPT_PROXY] = $this->proxy;
        }
        curl_setopt_array($curl, $options);
        $body = curl_exec($curl);
        $status = (int) curl_getinfo($curl, CURLINFO_RESPONSE_CODE);
        $error = curl_error($curl);
        if ($body === false) {
            // The token is part of the URL; make sure it never ends up in an error text.
            throw new \RuntimeException('Telegram is not reachable: ' . str_replace($this->token, '***', $error));
        }
        $answer = json_decode((string) $body, true);
        if ($status !== 200 || !is_array($answer) || ($answer['ok'] ?? false) !== true) {
            $description = is_array($answer) ? (string) ($answer['description'] ?? '') : '';
            throw new \RuntimeException(trim("Telegram answered HTTP $status " . str_replace($this->token, '***', $description)));
        }
    }

    /** @return list<string> the text cut into messages Telegram accepts, at line ends where possible */
    public static function split(string $text, int $limit = self::MAX_LENGTH): array
    {
        $parts = [];
        $current = '';
        foreach (explode("\n", $text) as $line) {
            while (mb_strlen($line) > $limit) {
                if ($current !== '') {
                    $parts[] = $current;
                    $current = '';
                }
                $parts[] = mb_substr($line, 0, $limit);
                $line = mb_substr($line, $limit);
            }
            $candidate = $current === '' ? $line : $current . "\n" . $line;
            if (mb_strlen($candidate) > $limit) {
                $parts[] = $current;
                $current = $line;
            } else {
                $current = $candidate;
            }
        }
        if (trim($current) !== '') {
            $parts[] = $current;
        }
        return $parts;
    }
}
