<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Log;
use Custos\Infra\ServerKeys;
use Custos\Infra\ZipReader;

/**
 * Installing CUSTOS for Linux on a new server from the site (page "Install"): one command, run as root on the server,
 *
 *     curl -fsSL https://site/install/linux/<token> | sudo bash
 *
 * gets a small script from the site (script()) that downloads the program for the server's processor from the site
 * (the archive inside the current package for Linux, see Packages), checks it against the package's manifest, and
 * runs its install.sh with the connection line, so that the machine appears on the site for approval like any other.
 *
 * The token in the address is made from the registration key (A.3), so that the key itself never stands in an address
 * (web server logs, shell history), and a new key gives a new address: commands copied before stop working. Without a
 * registration key there is nothing to install with. These addresses need no login: the server being installed has
 * none. The agents allow-list applies to them, as to the agents themselves.
 */
final class Install
{
    /** The processors there is a program for, as the archives name them. */
    public const ARCHES = ['x64', 'arm64'];
    private const TOKEN_LENGTH = 32;

    public function __construct(
        private readonly Settings $settings,
        private readonly Packages $packages,
        private readonly ServerKeys $keys,
        private readonly string $siteUrl,
    ) {
    }

    /** The token in the install address for a registration key: 32 hex digits of an HMAC of it. */
    public static function token(string $registrationKey): string
    {
        return substr(hash_hmac('sha256', 'custos-install-linux', RegistrationKey::normalize($registrationKey)), 0, self::TOKEN_LENGTH);
    }

    /** The token of the current registration key, or null when there is no key. */
    public function currentToken(): ?string
    {
        $key = $this->settings->get('registration_key');
        return $key === '' ? null : self::token($key);
    }

    public function tokenMatches(string $token): bool
    {
        $current = $this->currentToken();
        return $current !== null && strlen($token) === self::TOKEN_LENGTH && hash_equals($current, strtolower($token));
    }

    /** The address of the install script, or null without a registration key or a site address (config url). */
    public function scriptUrl(): ?string
    {
        $token = $this->currentToken();
        return $token === null || $this->siteUrl === '' ? null : rtrim($this->siteUrl, '/') . '/install/linux/' . $token;
    }

    /**
     * The commands that install CUSTOS for Linux on a server: with curl (most systems) and with wget (Debian without
     * curl). Null when they cannot be made yet.
     *
     * @return ?array{curl: string, wget: string}
     */
    public function commands(): ?array
    {
        $url = $this->scriptUrl();
        if ($url === null) {
            return null;
        }
        return [
            'curl' => 'curl -fsSL ' . self::quote($url) . ' | sudo bash',
            'wget' => 'wget -qO- ' . self::quote($url) . ' | sudo bash',
        ];
    }

    /**
     * The package the command installs: the current version for Linux, otherwise the newest one the site has (so that a
     * first package works before anyone marks it current), or null when there is none.
     */
    public function package(): ?array
    {
        return $this->packages->current(Agents::LINUX) ?? ($this->packages->all(Agents::LINUX)[0] ?? null);
    }

    /**
     * SHA-256 of the archive of each processor, from the manifest of the package (build-linux.sh writes them).
     *
     * @return array<string, string> arch => sha256
     */
    public static function archiveSums(array $package): array
    {
        $manifest = json_decode((string) ($package['manifest'] ?? ''), true);
        $sums = [];
        foreach ((array) ($manifest['files'] ?? []) as $file) {
            $path = is_array($file) ? (string) ($file['path'] ?? '') : '';
            $sha = is_array($file) ? strtolower((string) ($file['sha256'] ?? '')) : '';
            foreach (self::ARCHES as $arch) {
                if ($path === self::archiveName($arch) && preg_match('/^[0-9a-f]{64}$/', $sha)) {
                    $sums[$arch] = $sha;
                }
            }
        }
        return $sums;
    }

    public static function archiveName(string $arch): string
    {
        return "custos-linux-$arch.tar.gz";
    }

    /**
     * The archive of the program for a processor, read from inside the stored package: [zip reader, entry name,
     * size], or null when the package or the archive is missing.
     *
     * @return ?array{0: ZipReader, 1: string, 2: int}
     */
    public function archive(string $arch): ?array
    {
        $package = $this->package();
        if ($package === null || !in_array($arch, self::ARCHES, true)) {
            return null;
        }
        $file = $this->packages->file(Agents::LINUX, $package['version']);
        if ($file === null) {
            return null;
        }
        try {
            $zip = ZipReader::open($file);
        } catch (\RuntimeException $e) {
            Log::error("Install: {$package['file_name']}: {$e->getMessage()}");
            return null;
        }
        $name = self::archiveName($arch);
        $size = $zip->size($name);
        return $size === null ? null : [$zip, $name, $size];
    }

    /**
     * The script the command runs: everything happens in main(), called on the last line, so that a download cut off
     * half-way runs nothing. Without a package for Linux on the site the script only says so.
     */
    public function script(): string
    {
        $package = $this->package();
        $token = (string) $this->currentToken();
        $site = rtrim($this->siteUrl, '/');
        if ($package === null) {
            return self::refusal(
                "the monitoring site $site has no CUSTOS for Linux yet: upload its package (custos-linux-….zip) under Updates, or download it there from GitHub",
                "на сайте мониторинга $site ещё нет CUSTOS для Linux: загрузите его пакет (custos-linux-….zip) в разделе «Обновления» или скачайте его там с GitHub"
            );
        }
        $sums = self::archiveSums($package);
        $line = RegistrationKey::connection($site, RegistrationKey::code($this->settings->get('registration_key'), $this->keys->fingerprint()));
        $values = [
            'site' => $site,
            'token' => $token,
            'version' => (string) $package['version'],
            'line' => $line,
            'sha_x64' => $sums['x64'] ?? '',
            'sha_arm64' => $sums['arm64'] ?? '',
        ];
        $assign = '';
        foreach ($values as $name => $value) {
            $assign .= "  local $name=" . self::quote($value) . "\n";
        }
        return str_replace('{{values}}', rtrim($assign), self::SCRIPT);
    }

    /** A word for bash in single quotes. */
    public static function quote(string $value): string
    {
        return "'" . str_replace("'", "'\\''", $value) . "'";
    }

    /** A script that only explains why it cannot install, in English and Russian like install.sh. */
    private static function refusal(string $english, string $russian): string
    {
        return "#!/bin/bash\n# CUSTOS for Linux from the monitoring site: nothing to install yet.\n"
            . "case \"\${LC_ALL:-\${LC_MESSAGES:-\${LANG:-}}}\" in ru*) echo " . self::quote("Ошибка: $russian") . " >&2 ;; "
            . "*) echo " . self::quote("Error: $english") . " >&2 ;; esac\nexit 1\n";
    }

    private const SCRIPT = <<<'BASH'
        #!/bin/bash
        # CUSTOS for Linux from a monitoring site: downloads the program for this processor from the site, checks it,
        # installs it with its install.sh (the program in /opt/custos, the command custos, the systemd service custos)
        # and connects it to the site, where the machine waits for approval. Run as root:
        #
        #   curl -fsSL <address of this script> | sudo bash
        #
        # Everything happens in main(), on the last line: a download cut off half-way runs nothing.
        set -euo pipefail

        main() {
        {{values}}
          local ru=false
          case "${LC_ALL:-${LC_MESSAGES:-${LANG:-}}}" in ru*) ru=true ;; esac
          say() { if $ru; then echo "$2"; else echo "$1"; fi; }
          fail() { if $ru; then echo "Ошибка: $2" >&2; else echo "Error: $1" >&2; fi; exit 1; }

          [ "$(id -u)" = 0 ] || fail "run it as root: ... | sudo bash" "запустите от root: ... | sudo bash"
          local arch sha
          case "$(uname -m)" in
            x86_64|amd64) arch=x64; sha=$sha_x64 ;;
            aarch64|arm64) arch=arm64; sha=$sha_arm64 ;;
            *) fail "the processor $(uname -m) is not supported (x86_64 and aarch64 are)" "процессор $(uname -m) не поддерживается (только x86_64 и aarch64)" ;;
          esac

          # tar and gzip unpack the program; a minimal system may lack them.
          if ! command -v tar > /dev/null || ! command -v gzip > /dev/null; then
            say "Installing tar and gzip" "Устанавливаю tar и gzip"
            if command -v apt-get > /dev/null; then
              apt-get update -qq > /dev/null || true
              DEBIAN_FRONTEND=noninteractive apt-get install -y -qq tar gzip > /dev/null
            elif command -v dnf > /dev/null; then dnf -y -q install tar gzip > /dev/null
            elif command -v yum > /dev/null; then yum -y -q install tar gzip > /dev/null
            elif command -v zypper > /dev/null; then zypper -n -q install tar gzip > /dev/null
            fi
          fi

          local work
          work=$(mktemp -d)
          # The folder is named now: the trap runs after main(), when its local variables are gone.
          trap "rm -rf -- '$work'" EXIT
          local url="$site/install/linux/$token/custos-linux-$arch.tar.gz"
          say "Downloading CUSTOS $version for Linux ($arch) from $site" "Скачиваю CUSTOS $version для Linux ($arch) с $site"
          if command -v curl > /dev/null; then
            curl -fSL --progress-bar --retry 3 -o "$work/custos.tar.gz" "$url" || fail "could not download $url" "не удалось скачать $url"
          elif command -v wget > /dev/null; then
            wget -q -O "$work/custos.tar.gz" "$url" || fail "could not download $url" "не удалось скачать $url"
          else
            fail "neither curl nor wget is installed" "не установлены ни curl, ни wget"
          fi
          if [ -n "$sha" ] && [ "$(sha256sum "$work/custos.tar.gz" | cut -d' ' -f1)" != "$sha" ]; then
            fail "the downloaded program is damaged (its SHA-256 differs from the package): try again" \
              "скачанная программа повреждена (SHA-256 не совпадает с пакетом), попробуйте ещё раз"
          fi

          mkdir "$work/setup"
          tar -xzf "$work/custos.tar.gz" -C "$work/setup" ./install.sh 2> /dev/null || tar -xzf "$work/custos.tar.gz" -C "$work/setup" install.sh
          bash "$work/setup/install.sh" --archive "$work/custos.tar.gz" "$line" < /dev/null
          say "A new machine waits for approval on the site: $site/agents" "Новая машина ждёт одобрения на сайте: $site/agents"
        }

        main "$@"
        BASH;
}
