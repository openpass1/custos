<?php

declare(strict_types=1);

namespace Custos\Infra;

use PHPMailer\PHPMailer\Exception as MailerException;
use PHPMailer\PHPMailer\PHPMailer;

/**
 * Plain-text e-mail over SMTP with PHPMailer (section 9).
 */
final class Mailer
{
    public const ENCRYPTIONS = ['none', 'starttls', 'ssl'];
    public const TIMEOUT_S = 20;

    /**
     * @param array{host: string, port: int, encryption: string, user: string, password: string, from: string} $smtp
     */
    public function __construct(private readonly array $smtp)
    {
    }

    /**
     * @param list<string> $to
     * @throws \RuntimeException with PHPMailer's explanation
     */
    public function send(array $to, string $subject, string $body, string $fromName = 'CUSTOS'): void
    {
        $mail = new PHPMailer(true);
        try {
            $mail->isSMTP();
            $mail->Host = $this->smtp['host'];
            $mail->Port = $this->smtp['port'];
            $mail->Timeout = self::TIMEOUT_S;
            $mail->CharSet = PHPMailer::CHARSET_UTF8;
            $mail->Encoding = PHPMailer::ENCODING_BASE64;
            match ($this->smtp['encryption']) {
                'ssl' => $mail->SMTPSecure = PHPMailer::ENCRYPTION_SMTPS,
                'starttls' => $mail->SMTPSecure = PHPMailer::ENCRYPTION_STARTTLS,
                default => $mail->SMTPAutoTLS = false,
            };
            if ($this->smtp['user'] !== '') {
                $mail->SMTPAuth = true;
                $mail->Username = $this->smtp['user'];
                $mail->Password = $this->smtp['password'];
            }
            $mail->setFrom($this->smtp['from'], $fromName);
            foreach ($to as $address) {
                $mail->addAddress($address);
            }
            $mail->Subject = $subject;
            $mail->Body = $body;
            $mail->isHTML(false);
            $mail->send();
        } catch (MailerException $e) {
            throw new \RuntimeException(trim($mail->ErrorInfo !== '' ? $mail->ErrorInfo : $e->getMessage()));
        }
    }
}
