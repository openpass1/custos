<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Domain\RegistrationKey;
use Custos\Infra\Crypto;
use Custos\Tests\TestEnv;
use Custos\Tools\AgentClient;
use PHPUnit\Framework\TestCase;

/** The server crypto (src/Infra) and the agent reference implementation (tools/src) must agree. */
final class CryptoTest extends TestCase
{
    public function testFingerprintFormatAndAgreement(): void
    {
        $key = TestEnv::agentKey(1);
        $fingerprint = Crypto::fingerprint($key);
        $this->assertMatchesRegularExpression('/^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$/', $fingerprint);
        $this->assertSame($fingerprint, AgentClient::fingerprint(AgentClient::publicPem($key)));
        $this->assertSame(strtoupper(substr(hash('sha256', Crypto::publicDer($key)), 0, 16)), str_replace('-', '', $fingerprint));
    }

    public function testSignatureRoundTrip(): void
    {
        $key = TestEnv::agentKey(1);
        $public = Crypto::loadPublicKey(Crypto::publicPem($key));
        $signature = Crypto::sign("200\n1\nabc\n" . hash('sha256', ''), $key);
        $this->assertTrue(Crypto::verify("200\n1\nabc\n" . hash('sha256', ''), $signature, $public));
        $this->assertFalse(Crypto::verify("404\n1\nabc\n" . hash('sha256', ''), $signature, $public));
        $this->assertFalse(Crypto::verify('x', 'not base64!', $public));
    }

    public function testSecretEncryptionMatchesAgentDecryption(): void
    {
        $key = TestEnv::agentKey(1);
        $encrypted = Crypto::encryptSecret('pässwörd', Crypto::loadPublicKey(Crypto::publicPem($key)));
        $raw = base64_decode($encrypted, true);
        $this->assertSame(384 + 12 + strlen('pässwörd') + 16, strlen($raw));
        $client = new AgentClient('http://unused', $key);
        $this->assertSame('pässwörd', $client->decryptSecret($encrypted));
        $raw[400] = chr(ord($raw[400]) ^ 1);
        $this->assertNull($client->decryptSecret(base64_encode($raw)), 'GCM tag must reject a changed ciphertext');
    }

    public function testRegistrationKey(): void
    {
        $key = RegistrationKey::generate();
        $this->assertMatchesRegularExpression('/^[A-Z2-7]{25}$/', $key);
        $formatted = RegistrationKey::format($key);
        $this->assertMatchesRegularExpression('/^([A-Z2-7]{5}-){4}[A-Z2-7]{5}$/', $formatted);
        $this->assertSame($key, RegistrationKey::normalize(' ' . strtolower($formatted)));
        $this->assertSame([$key, '3F2A-91C0-77DE-0B14'], AgentClient::parseCode(strtolower($formatted) . '#3F2A-91C0-77DE-0B14'));
        $this->assertSame([$key, null], AgentClient::parseCode($formatted));
    }

    public function testUuid(): void
    {
        $this->assertMatchesRegularExpression('/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/', Crypto::uuid4());
    }
}
