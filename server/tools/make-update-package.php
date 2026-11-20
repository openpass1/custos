<?php

// Makes a test update package of CUSTOS (A.10) for checking "Updates" with the fake agent: a zip with manifest.json
// and a couple of dummy files. Real packages come from the client build.
//
//   php tools/make-update-package.php --version=1.0.1 [--out=custos-1.0.1.zip] [--min-protocol=1] [--edition=legacy|macos]
//
// --edition=legacy makes a package of the edition for Windows 7 and 8.1 (custos-legacy-1.0.1.zip), --edition=macos one
// of CUSTOS for macOS (custos-macos-1.0.1.zip), --edition=linux one of CUSTOS for Linux (custos-linux-1.0.1.zip) with
// custos-linux-x64.tar.gz and custos-linux-arm64.tar.gz: real archives whose install.sh only writes its arguments to
// the file named by CUSTOS_FAKE_INSTALL_LOG (the tests of the install command run it).

declare(strict_types=1);

if (PHP_SAPI !== 'cli') {
    exit("Run this script from the command line.\n");
}

$options = [];
foreach (array_slice($argv, 1) as $arg) {
    if (preg_match('/^--([a-z-]+)=(.*)$/s', $arg, $m)) {
        $options[$m[1]] = $m[2];
    }
}
$version = (string) ($options['version'] ?? '');
if (!preg_match('/^\d{1,5}(\.\d{1,5}){1,3}([-+][0-9A-Za-z.-]{1,20})?$/', $version)) {
    fwrite(STDERR, "Usage: php tools/make-update-package.php --version=1.0.1 [--out=FILE] [--min-protocol=1] [--edition=legacy|macos|linux]\n");
    exit(2);
}
$edition = isset($options['edition']) ? (string) $options['edition'] : null;
$out = (string) ($options['out'] ?? ($edition === null ? "custos-$version.zip" : "custos-$edition-$version.zip"));
$files = $edition === 'linux' ? [
    'custos-linux-x64.tar.gz' => fakeLinuxArchive($version, 'x64'),
    'custos-linux-arm64.tar.gz' => fakeLinuxArchive($version, 'arm64'),
] : [
    'Custos.Service.exe' => "MZ fake CUSTOS service $version\n" . random_bytes(2048),
    'Custos.Gui.exe' => "MZ fake CUSTOS window $version\n" . random_bytes(2048),
];
$manifest = [
    'version' => $version,
    ...($edition === null ? [] : ['edition' => $edition]),
    'min_protocol' => (int) ($options['min-protocol'] ?? 1),
    'files' => array_map(fn($name) => ['path' => $name, 'sha256' => hash('sha256', $files[$name])], array_keys($files)),
];
$entries = ['manifest.json' => json_encode($manifest, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES)] + $files;

// A plain zip without compression, written by hand so that the script needs no PHP extension.
$zip = '';
$directory = '';
foreach ($entries as $name => $data) {
    $crc = crc32($data);
    $size = strlen($data);
    $header = pack('vvvvvVVVvv', 20, 0, 0, 0, 0x21, $crc, $size, $size, strlen($name), 0);
    $directory .= "PK\x01\x02" . pack('v', 20) . $header . pack('vvvVV', 0, 0, 0, 0, strlen($zip)) . $name;
    $zip .= "PK\x03\x04" . $header . $name . $data;
}
$zip .= $directory . "PK\x05\x06" . pack('vvvvVVv', 0, 0, count($entries), count($entries), strlen($directory), strlen($zip), 0);
if (file_put_contents($out, $zip) === false) {
    fwrite(STDERR, "Cannot write $out\n");
    exit(1);
}
echo "$out: CUSTOS $version, ", strlen($zip), " bytes, SHA-256 ", hash('sha256', $zip), "\n";

/** A tar.gz like the archive of CUSTOS for Linux, with ./install.sh and ./custos, written by hand (ustar). */
function fakeLinuxArchive(string $version, string $arch): string
{
    $files = [
        './install.sh' => "#!/bin/bash\n# Fake install.sh of CUSTOS for Linux $version ($arch): writes its arguments, one per line.\n"
            . "printf '%s\\n' \"\$@\" > \"\${CUSTOS_FAKE_INSTALL_LOG:-/dev/null}\"\necho \"fake CUSTOS $version ($arch) installed\"\n",
        './custos' => "#!/bin/sh\necho $version\n",
    ];
    $tar = '';
    foreach ($files as $name => $data) {
        $header = pack('a100a8a8a8a12a12a8a1a100a6a2a32a32a8a8a155a12', $name, '0000755', '0000000', '0000000',
            sprintf('%011o', strlen($data)), sprintf('%011o', time()), str_repeat(' ', 8), '0', '', "ustar", '00', 'root', 'root', '', '', '', '');
        $header = substr_replace($header, sprintf('%06o', array_sum(array_map('ord', str_split($header)))) . "\0 ", 148, 8);
        $tar .= $header . $data . str_repeat("\0", (512 - strlen($data) % 512) % 512);
    }
    return (string) gzencode($tar . str_repeat("\0", 1024));
}
