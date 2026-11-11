#!/usr/bin/env python3
"""Writes zabbix/custos_template.yaml, the Zabbix template "CUSTOS by HTTP" (monitor spec, section 10).

Run from the server folder after changing items or triggers:  python3 tools/make-zabbix-template.py
UUIDs are derived from item keys and names, so they stay the same between runs and Zabbix updates the template
in place on a new import.
"""
import pathlib
import hashlib
T = 'CUSTOS by HTTP'
def uid(name):
    h = list(hashlib.md5(('custos-zabbix-' + name).encode()).hexdigest())
    h[12] = '4'
    h[16] = '89ab'[int(h[16], 16) % 4]
    return ''.join(h)
def q(s):
    return "'" + s.replace("'", "''") + "'"
L = []
w = L.append
w("zabbix_export:")
w("  version: '7.0'")
w("  template_groups:")
w(f"    - uuid: {uid('group')}")
w("      name: Templates/Applications")
w("  templates:")
w(f"    - uuid: {uid('template')}")
w(f"      template: {q(T)}")
w(f"      name: {q(T)}")
w("      description: |")
for line in """Monitoring of CUSTOS backups through CUSTOS Monitor (the PHP site).
One HTTP request a minute (/api/zabbix/status) feeds every item; machines and tasks are discovered once an hour.
Set the host macros {$CUSTOS.URL} (site address without a trailing slash) and {$CUSTOS.TOKEN} (Settings → Zabbix and wall screen).
status_code of a task: 0 ok, 1 running or paused, 2 warning or anomaly, 3 missed while the machine was off, 4 no recent successful copy, 5 missed, 6 error, 9 disabled or not run yet.
space_days of a task: days until its storage is full while the site warns about it, otherwise -1.
The most serious problem of a task gives its code. A run in progress shows 1 only instead of 0 or 9, so an error or a missed run stays visible while the next run goes.
Format of Zabbix 7.0; newer versions (8.0) convert it on import.""".split("\n"):
    w("        " + line)
w("      vendor:")
w("        name: CUSTOS")
w("        version: 7.0-0")
w("      groups:")
w("        - name: Templates/Applications")
w("      items:")
def http_item(key, name, url, history, desc, status_codes=None, extra=None):
    w(f"        - uuid: {uid('item ' + key)}")
    w(f"          name: {q(name)}")
    w("          type: HTTP_AGENT")
    w(f"          key: {key}")
    w(f"          history: {history}")
    w("          value_type: TEXT")
    w("          trends: '0'")
    w(f"          description: {q(desc)}")
    w("          timeout: 15s")
    w(f"          url: {q(url)}")
    if status_codes:
        w(f"          status_codes: {q(status_codes)}")
    w("          headers:")
    w("            - name: X-Custos-Token")
    w("              value: '{$CUSTOS.TOKEN}'")
    w("          tags:")
    w("            - tag: component")
    w("              value: raw")
    if extra:
        extra()
def dep(key, name, master, path, value_type=None, units=None, desc=None, tags=(), on_fail=None, triggers=(), indent=8, proto=False):
    p = ' ' * indent
    w(f"{p}- uuid: {uid(('proto ' if proto else 'item ') + key)}")
    w(f"{p}  name: {q(name)}")
    w(f"{p}  type: DEPENDENT")
    w(f"{p}  key: {q(key)}")
    w(f"{p}  delay: '0'")
    if value_type:
        w(f"{p}  value_type: {value_type}")
    if value_type in ('CHAR', 'TEXT'):
        w(f"{p}  trends: '0'")
    if units:
        w(f"{p}  units: {units}")
    if desc:
        w(f"{p}  description: {q(desc)}")
    w(f"{p}  preprocessing:")
    w(f"{p}    - type: JSONPATH")
    w(f"{p}      parameters:")
    w(f"{p}        - {q(path)}")
    if on_fail is not None:
        w(f"{p}      error_handler: CUSTOM_VALUE")
        w(f"{p}      error_handler_params: {q(on_fail)}")
    if value_type is None or value_type == 'FLOAT':
        pass
    w(f"{p}  master_item:")
    w(f"{p}    key: {master}")
    if tags:
        w(f"{p}  tags:")
        for tag, value in tags:
            w(f"{p}    - tag: {tag}")
            w(f"{p}      value: {q(value)}")
    if triggers:
        w(f"{p}  {'trigger_prototypes' if proto else 'triggers'}:")
        for expr, tname, prio, tdesc in triggers:
            w(f"{p}    - uuid: {uid('trigger ' + tname)}")
            w(f"{p}      expression: {q(expr)}")
            w(f"{p}      name: {q(tname)}")
            w(f"{p}      priority: {prio}")
            w(f"{p}      description: {q(tdesc)}")

def status_triggers():
    w("          triggers:")
    name = 'CUSTOS: no data from the monitoring site for 10 minutes'
    w(f"            - uuid: {uid('trigger ' + name)}")
    w(f"              expression: {q('nodata(/' + T + '/custos.status,10m)=1')}")
    w(f"              name: {q(name)}")
    w("              priority: HIGH")
    w(f"              description: {q('The site does not answer, or the token or the address allow-list does not let Zabbix in.')}")

http_item('custos.status', 'CUSTOS: status data', '{$CUSTOS.URL}/api/zabbix/status', '1h',
          'Every status in one answer; the other items take their values from it.', extra=status_triggers)
http_item('custos.health', 'CUSTOS: health data', '{$CUSTOS.URL}/api/health', '1h',
          'Database and cron state. The site answers 503 when either is bad, so 503 is accepted too.', status_codes='200,503')
dep('custos.health.status', 'CUSTOS: health', 'custos.health', '$.status', value_type='CHAR',
    desc='ok, or fail when the database does not answer or cron has not run for 5 minutes (missed runs are not checked then).',
    tags=[('component', 'health')],
    triggers=[('last(/' + T + '/custos.health.status)<>"ok"', 'CUSTOS: monitoring site is unhealthy (database or cron)', 'HIGH',
               'Open /api/health on the site: db shows the database, cron_lag_s how long ago cron ran.')])
dep('custos.health.cron_lag', 'CUSTOS: seconds since the last cron pass', 'custos.health', '$.cron_lag_s', units='s', on_fail='-1',
    value_type='FLOAT', tags=[('component', 'health')])
for k, n in [('agents', 'machines'), ('online', 'machines online'), ('tasks', 'watched tasks'), ('problems', 'open problems'),
             ('error', 'tasks with errors'), ('missed', 'tasks with missed runs'), ('warning', 'tasks with warnings'), ('running', 'tasks running')]:
    dep(f'custos.summary.{k}', f'CUSTOS: {n}', 'custos.status', f'$.summary.{k}', tags=[('component', 'summary')])

w("      discovery_rules:")
def rule(key, name, url, protos):
    w(f"        - uuid: {uid('rule ' + key)}")
    w(f"          name: {q(name)}")
    w("          type: HTTP_AGENT")
    w(f"          key: {key}")
    w("          delay: 1h")
    w("          timeout: 15s")
    w(f"          url: {q(url)}")
    w("          headers:")
    w("            - name: X-Custos-Token")
    w("              value: '{$CUSTOS.TOKEN}'")
    w("          item_prototypes:")
    protos()
def agent_protos():
    tags = [('machine', '{#AGENT}'), ('group', '{#GROUP}')]
    dep('custos.agent.online[{#AGENT_ID}]', 'Machine {#AGENT}: online', 'custos.status', "$.agents['{#AGENT_ID}'].online",
        desc='1 when the agent sent a heartbeat recently (offline_after_min on the site).', tags=tags, indent=12, proto=True)
    dep('custos.agent.last_seen[{#AGENT_ID}]', 'Machine {#AGENT}: since the last contact', 'custos.status', "$.agents['{#AGENT_ID}'].last_seen_s",
        units='s', tags=tags, indent=12, proto=True,
        triggers=[('last(/' + T + '/custos.agent.last_seen[{#AGENT_ID}])>{$CUSTOS.OFFLINE.MAX}',
                   'CUSTOS: {#AGENT} has been offline for more than {$CUSTOS.OFFLINE.MAX}', 'WARNING',
                   'Useful for servers that should always be on. Workstations that are switched off at night can get a larger {$CUSTOS.OFFLINE.MAX} on their host.')])
    dep('custos.agent.version[{#AGENT_ID}]', 'Machine {#AGENT}: CUSTOS version', 'custos.status', "$.agents['{#AGENT_ID}'].version",
        value_type='CHAR', tags=tags, indent=12, proto=True)
def task_protos():
    tags = [('machine', '{#AGENT}'), ('task', '{#TASK}')]
    key = "$.tasks['{#TASK_KEY}']"
    dep('custos.task.code[{#TASK_KEY}]', '{#AGENT} / {#TASK}: status code', 'custos.status', key + '.status_code',
        desc='0 ok, 1 running, 2 warning or anomaly, 3 missed while off, 4 no recent success, 5 missed, 6 error, 9 disabled or not run yet.',
        tags=tags, indent=12, proto=True,
        triggers=[
            ('last(/' + T + '/custos.task.code[{#TASK_KEY}])=6', 'CUSTOS: {#AGENT} / {#TASK}: backup failed', 'HIGH',
             'The last run ended with an error, or a stored set failed its check. Details on the task page of the site.'),
            ('last(/' + T + '/custos.task.code[{#TASK_KEY}])=5', 'CUSTOS: {#AGENT} / {#TASK}: backup did not start on schedule', 'AVERAGE',
             'The machine was on, but the scheduled run did not happen.'),
            ('last(/' + T + '/custos.task.code[{#TASK_KEY}])=4', 'CUSTOS: {#AGENT} / {#TASK}: no recent successful copy', 'WARNING',
             'The latest successful copy is older than stale_days on the site (7 days by default).'),
        ])
    dep('custos.task.status[{#TASK_KEY}]', '{#AGENT} / {#TASK}: status', 'custos.status', key + '.status', value_type='CHAR',
        tags=tags, indent=12, proto=True)
    dep('custos.task.success_age[{#TASK_KEY}]', '{#AGENT} / {#TASK}: since the last successful copy', 'custos.status',
        key + '.last_success_age_s', value_type='FLOAT', units='s', desc='-1 when there has been no successful copy yet.',
        tags=tags, indent=12, proto=True)
    dep('custos.task.last_status[{#TASK_KEY}]', '{#AGENT} / {#TASK}: last run result', 'custos.status', key + '.last_run_status',
        value_type='CHAR', tags=tags, indent=12, proto=True)
    dep('custos.task.written[{#TASK_KEY}]', '{#AGENT} / {#TASK}: written by the last run', 'custos.status', key + '.last_bytes_written',
        units='B', tags=tags, indent=12, proto=True)
    dep('custos.task.anomaly[{#TASK_KEY}]', '{#AGENT} / {#TASK}: size anomaly', 'custos.status', key + '.anomaly',
        desc='1 when the source size or file count of the last run left the usual range.', tags=tags, indent=12, proto=True)
    dep('custos.task.space_days[{#TASK_KEY}]', '{#AGENT} / {#TASK}: days until the storage is full', 'custos.status',
        key + '.space_days', value_type='FLOAT', on_fail='-1',
        desc='Days until a destination of the task fills up while the site warns about it (space_warn_days, 14 by default), -1 otherwise.',
        tags=tags, indent=12, proto=True,
        triggers=[
            ('last(/' + T + '/custos.task.space_days[{#TASK_KEY}])>=0', 'CUSTOS: {#AGENT} / {#TASK}: storage will be full soon', 'WARNING',
             'By the free space trend of the last 30 days, a destination of the task fills up within space_warn_days. '
             'The item value is the number of days left. Clears when the storage stops filling up or the forecast doubles.'),
        ])
rule('custos.agents.discovery', 'CUSTOS: machines', '{$CUSTOS.URL}/api/zabbix/discovery/agents', agent_protos)
rule('custos.tasks.discovery', 'CUSTOS: tasks', '{$CUSTOS.URL}/api/zabbix/discovery/tasks', task_protos)
w("      tags:")
w("        - tag: class")
w("          value: software")
w("        - tag: target")
w("          value: custos")
w("      macros:")
w("        - macro: '{$CUSTOS.OFFLINE.MAX}'")
w("          value: 24h")
w(f"          description: {q('A machine offline for longer than this raises a warning.')}")
w("        - macro: '{$CUSTOS.TOKEN}'")
w("          type: SECRET_TEXT")
w(f"          description: {q('Zabbix token from the site: Settings → Zabbix and wall screen.')}")
w("        - macro: '{$CUSTOS.URL}'")
w("          value: 'http://custos.lan'")
w(f"          description: {q('Address of the monitoring site without a trailing slash, as in config.php.')}")
out = pathlib.Path(__file__).resolve().parent.parent / "zabbix" / "custos_template.yaml"
out.write_text("\n".join(L) + "\n", encoding="utf-8")
print(f"Written {out}")
