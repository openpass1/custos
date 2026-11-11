<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Domain\Settings;
use Custos\Infra\Telegram;
use PHPUnit\Framework\TestCase;

final class TelegramTest extends TestCase
{
    public function testLongMessagesAreSplitAtLineEnds(): void
    {
        $this->assertSame(['short'], Telegram::split('short'));
        $this->assertSame(["aaaa\nbbbb", 'cccc'], Telegram::split("aaaa\nbbbb\ncccc", 10));
        $this->assertSame(['aaaaaaaaaa', "aaaaa\nbb"], Telegram::split(str_repeat('a', 15) . "\nbb", 10), 'a line longer than the limit is cut');
        $text = str_repeat("Строка уведомления\n", 400);
        foreach (Telegram::split($text) as $part) {
            $this->assertLessThanOrEqual(Telegram::MAX_LENGTH, mb_strlen($part));
        }
        $this->assertSame(trim($text), trim(implode("\n", Telegram::split($text))));
    }

    public function testListsOfChatsAndAddresses(): void
    {
        $this->assertSame(['-100123', '@chat', 'a@example.org'], Settings::splitList(" -100123,@chat;\n a@example.org \n\n"));
        $this->assertSame([], Settings::splitList(''));
    }
}
