<?php

declare(strict_types=1);

namespace Custos\Http\Api;

use Custos\App;
use Custos\Domain\Install;
use Custos\Http\Request;
use Custos\Http\Response;
use Custos\Infra\Cidr;
use Custos\Infra\Log;

/**
 * The addresses of the install command for Linux (page "Install", Install):
 *   GET /install/linux/<token>                              the script the command runs
 *   GET /install/linux/<token>/<arch>                       the program it downloads, from the current package
 * No login: the server being installed has none. The token stands for the registration key, and the agents
 * allow-list applies, as to the agents. A wrong token answers 404, like an address that does not exist.
 */
final class InstallApi
{
    // Some nginx configurations treat .gz URLs as static files. Keep the old route for scripts already fetched.
    private const PATH = '#^/install/linux/([0-9a-fA-F]{32})(?:/(x64|arm64)|/custos-linux-(x64|arm64)\.tar\.gz)?$#';

    public function __construct(private readonly App $app)
    {
    }

    public static function handles(string $path): bool
    {
        return str_starts_with($path, '/install/linux/');
    }

    public function handle(Request $request): Response
    {
        try {
            $response = $this->dispatch($request);
        } catch (\Throwable $e) {
            Log::error("Install {$request->method} {$request->path()}: $e");
            $response = Response::text("Error: the site could not answer, see its log\n", 500);
        }
        $response->headers += ['X-Content-Type-Options' => 'nosniff', 'Cache-Control' => 'no-store'];
        return $response;
    }

    private function dispatch(Request $request): Response
    {
        if ($request->method !== 'GET' && $request->method !== 'HEAD') {
            return Response::text("Error: only GET\n", 405);
        }
        if (!preg_match(self::PATH, $request->path(), $m, PREG_UNMATCHED_AS_NULL)) {
            return Response::text("Error: not found\n", 404);
        }
        if (!Cidr::allows($request->ip, Cidr::fromStored($this->app->settings()->get('ip_allow_agents')))) {
            return Response::text("Error: the address {$request->ip} is not in the networks allowed for agents (site settings)\n", 403);
        }
        $install = $this->app->install();
        if (!$install->tokenMatches($m[1])) {
            return Response::text("Error: this install command is not valid any more: copy it again from the page Install of the site\n", 404);
        }
        $arch = $m[2] ?? $m[3] ?? null;
        if ($arch === null) {
            $response = Response::text($install->script());
            $response->headers['Content-Type'] = 'text/x-shellscript; charset=utf-8';
            return $response;
        }
        $archive = $install->archive($arch);
        if ($archive === null) {
            return Response::text("Error: the site has no CUSTOS for Linux ($arch)\n", 404);
        }
        [$zip, $name, $size] = $archive;
        Log::info("Install: {$request->ip} downloads $name");
        return Response::stream(static function ($out) use ($zip, $name): void {
            try {
                $zip->copy($name, $out);
            } catch (\RuntimeException $e) {
                // The headers are gone already: the download ends short, and curl or wget says so.
                Log::error("Install: $name: {$e->getMessage()}");
            }
        }, 'application/gzip', $name, $size);
    }
}
