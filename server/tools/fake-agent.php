<?php

// Fake CUSTOS agents for developing and checking the server without the Windows client (section 12).
// Run without arguments for help.

declare(strict_types=1);

if (PHP_SAPI !== 'cli') {
    exit("Run this script from the command line.\n");
}
require dirname(__DIR__) . '/vendor/autoload.php';

use Custos\Tools\FakeAgent;

const HELP = <<<'TEXT'
Usage: php tools/fake-agent.php --url=http://custos.lan --code=KEY#FINGERPRINT [options]

  --url=URL          server address, as typed into the agent (no trailing slash)
  --code=CODE        registration code from the site (Settings); needed for the first registration
  --count=N          number of agents, default 1 (FAKE-PC-01 ... FAKE-PC-NN)
  --name=PREFIX      host name prefix, default FAKE-PC
  --tz=ZONE          agent time zone (IANA), default UTC
  --state=DIR        where agents keep their keys and state, default var/fake-agents
  --once             one pass and exit: register, heartbeat, configuration, a short run of the first task,
                     inventory, command results; exit code 0 when every answer was signed and accepted
  --run-seconds=N    average run duration, default 30
  --run-now          start every task of every agent once right away (trigger "manual")
  --skip-schedule    heartbeats go on, but scheduled runs are skipped (for checking "missed")
  --offline[=MIN]    after the first sync the machine is "off" for MIN minutes (default 30), then catches up
                     (for checking "missed_offline")
  --pause[=SEC]      every run pauses halfway for SEC seconds (default 120) (for checking "paused")
  --result=STATUS    every run ends with this status: success, warning or error (default: random,
                     mostly success)
  --reset            forget the agents in the state folder and exit

Agents keep their identity between runs. The interval task "Desktop" runs every 15 minutes, the other
tasks at their scheduled times in the agent's time zone. Stop with Ctrl+C.
TEXT;

$options = [];
foreach (array_slice($argv, 1) as $arg) {
    if (preg_match('/^--([a-z-]+)(?:=(.*))?$/s', $arg, $m)) {
        $options[$m[1]] = $m[2] ?? true;
    } else {
        fwrite(STDERR, "Unknown argument: $arg\n\n" . HELP . "\n");
        exit(2);
    }
}
if ($options === [] || isset($options['help'])) {
    echo HELP, "\n";
    exit(0);
}

$stateDir = rtrim((string) ($options['state'] ?? dirname(__DIR__) . '/var/fake-agents'), '/');
if (isset($options['reset'])) {
    foreach (glob($stateDir . '/*.json') ?: [] as $file) {
        unlink($file);
    }
    echo "Forgot all fake agents in $stateDir\n";
    exit(0);
}

$url = rtrim((string) ($options['url'] ?? ''), '/');
if (!preg_match('#^https?://#', $url)) {
    fwrite(STDERR, "--url is required, for example --url=http://custos.lan\n");
    exit(2);
}
$timeZone = (string) ($options['tz'] ?? 'UTC');
try {
    new DateTimeZone($timeZone);
} catch (Exception) {
    fwrite(STDERR, "Unknown time zone $timeZone\n");
    exit(2);
}
$count = max(1, (int) ($options['count'] ?? 1));
$prefix = (string) ($options['name'] ?? 'FAKE-PC');
$agentOptions = [
    'url' => $url,
    'code' => is_string($options['code'] ?? null) ? $options['code'] : null,
    'time_zone' => $timeZone,
    'run_seconds' => max(2, (int) ($options['run-seconds'] ?? 30)),
    'skip_schedule' => isset($options['skip-schedule']),
    'offline_min' => isset($options['offline']) ? (is_string($options['offline']) ? max(1, (int) $options['offline']) : 30) : null,
    'pause_s' => isset($options['pause']) ? (is_string($options['pause']) ? max(1, (int) $options['pause']) : 120) : null,
    'result' => $options['result'] ?? null,
];
if ($agentOptions['result'] !== null && !in_array($agentOptions['result'], ['success', 'warning', 'error'], true)) {
    fwrite(STDERR, "--result must be success, warning or error\n");
    exit(2);
}
$log = static function (string $message): void {
    echo date('H:i:s'), ' ', $message, "\n";
};

$agents = [];
for ($i = 1; $i <= $count; $i++) {
    $hostname = sprintf('%s-%02d', $prefix, $i);
    $file = "$stateDir/" . strtolower($hostname) . '.json';
    if (!is_file($file)) {
        $log("$hostname: creating an RSA-3072 key...");
    }
    $agents[] = new FakeAgent($file, $agentOptions + ['hostname' => $hostname], $log);
}

if (isset($options['once'])) {
    $ok = true;
    foreach ($agents as $agent) {
        $ok = $agent->once() && $ok;
    }
    $log($ok ? 'done: every answer was signed and accepted' : 'done with problems, see the lines above');
    exit($ok ? 0 : 1);
}

if (isset($options['run-now'])) {
    foreach ($agents as $agent) {
        $agent->runAll();
    }
}
$log(sprintf('%d agent(s) running against %s, Ctrl+C to stop', count($agents), $url));
while (true) {
    foreach ($agents as $agent) {
        $agent->tick();
    }
    usleep(200000);
}
