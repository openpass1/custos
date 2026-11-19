<?php

// Makes a test update package of CUSTOS (A.10) for checking "Updates" with the fake agent: a zip with manifest.json
// and a couple of dummy files. Real packages come from the client build.
//
//   php tools/make-update-package.php --version=1.0.1 [--out=custos-1.0.1.zip] [--min-protocol=1] [--edition=legacy]
//
// --edition=legacy makes a package of the edition for Windows 7 and 8.1 (custos-legacy-1.0.1.zip).

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
    fwrite(STDERR, "Usage: php tools/make-update-package.php --version=1.0.1 [--out=FILE] [--min-protocol=1] [--edition=legacy]\n");
    exit(2);
}
$edition = isset($options['edition']) ? (string) $options['edition'] : null;
$out = (string) ($options['out'] ?? ($edition === 'legacy' ? "custos-legacy-$version.zip" : "custos-$version.zip"));
$files = [
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
