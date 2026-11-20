#!/usr/bin/env python3
"""Builds public/assets/icons.svg, the site's icon sprite, from Material Symbols Rounded (weight 400).

The icons are Google's Material Symbols (Apache License 2.0, see THIRD-PARTY-NOTICES.txt). They are not downloaded at
run time: this script copies the paths of the icons listed below into one SVG file that the site serves itself.

    npm install @material-symbols/svg-400        (anywhere, once)
    python3 tools/make-icons.py path/to/node_modules/@material-symbols/svg-400/rounded

A page uses an icon with icon('name') (src/helpers.php); 'name-fill' is the filled form where it is listed with fill.
"""
import re
import sys
from pathlib import Path

# Site name -> Material Symbols name. A trailing "+" also adds the filled form as "<site name>-fill".
ICONS = {
    # Actions
    'plus': 'add', 'play': 'play_arrow', 'stop': 'stop', 'pause': 'pause', 'pencil': 'edit', 'sliders': 'tune',
    'box': 'inventory_2', 'refresh': 'sync', 'copy': 'content_copy', 'clock': 'schedule', 'template': 'library_add',
    'power': 'power_settings_new', 'trash': 'delete', 'check': 'check', 'x': 'close', 'chevron': 'keyboard_arrow_down',
    'dots': 'more_vert', 'folder': 'folder', 'folder-open': 'folder_open', 'key': 'key', 'download': 'download',
    'upload': 'upload', 'alert': 'warning+', 'user': 'person', 'menu': 'menu', 'logout': 'logout', 'sun': 'light_mode',
    'moon': 'dark_mode', 'monitor': 'brightness_auto', 'globe': 'language', 'search': 'search', 'back': 'arrow_back',
    'forward': 'arrow_forward', 'right': 'chevron_right', 'left': 'chevron_left', 'expand': 'keyboard_arrow_down',
    'drop': 'arrow_drop_down', 'info': 'info+', 'error': 'error+', 'ok': 'check_circle+', 'done-all': 'done_all',
    'save': 'save', 'send': 'send', 'mail': 'mail', 'lock': 'lock', 'history': 'history', 'filter': 'filter_list',
    'up': 'arrow_upward', 'restore': 'history', 'visibility': 'visibility', 'open': 'open_in_new', 'link': 'link',
    'calendar': 'calendar_month', 'tag': 'sell', 'swap': 'swap_horiz', 'hub': 'hub', 'cancel': 'cancel',
    'skip': 'block', 'help': 'help',
    # Things
    'bell': 'notifications+', 'desktop': 'desktop_windows', 'server': 'dns', 'groups': 'workspaces', 'hdd': 'hard_drive',
    'cloud-upload': 'cloud_upload', 'cloud-off': 'cloud_off', 'new-machine': 'add_to_queue', 'trend-down': 'trending_down',
    'trend-up': 'trending_up', 'event-busy': 'event_busy', 'power-off': 'power_off', 'encryption': 'enhanced_encryption',
    'inbox': 'move_to_inbox', 'shield': 'verified_user', 'bolt': 'bolt', 'new': 'release_alert+', 'pending': 'pending',
    'task': 'assignment+', 'running': 'progress_activity', 'update': 'system_update_alt', 'zip': 'folder_zip',
    'database': 'database', 'person-add': 'person_add', 'notes': 'edit_note', 'chart': 'bar_chart', 'tv': 'tv',
    'snapshot': 'photo_camera',
    # Navigation (the filled form marks the open section)
    'nav-dashboard': 'space_dashboard+', 'nav-agents': 'computer+', 'nav-problems': 'report+', 'nav-runs': 'history',
    'nav-templates': 'library_books+', 'nav-updates': 'system_update_alt', 'nav-users': 'group+',
    'nav-audit': 'receipt_long+', 'nav-settings': 'settings+', 'nav-tasks': 'assignment+', 'nav-install': 'install_desktop+',
}


def path_of(source: Path, name: str) -> str:
    text = (source / (name + '.svg')).read_text(encoding='utf-8')
    paths = re.findall(r'<path d="([^"]+)"', text)
    if not paths:
        raise SystemExit(f'{name}: no path')
    return ''.join(f'<path d="{d}"/>' for d in paths)


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    source = Path(sys.argv[1])
    out = [
        '<svg xmlns="http://www.w3.org/2000/svg">',
        '<!-- CUSTOS Monitor icons: Material Symbols Rounded, weight 400 (c) Google, Apache License 2.0. '
        'Built by tools/make-icons.py. -->',
    ]
    for site, symbol in ICONS.items():
        fill = symbol.endswith('+')
        symbol = symbol.rstrip('+')
        out.append(f'<symbol id="i-{site}" viewBox="0 -960 960 960">{path_of(source, symbol)}</symbol>')
        if fill:
            out.append(f'<symbol id="i-{site}-fill" viewBox="0 -960 960 960">{path_of(source, symbol + "-fill")}</symbol>')
    out.append('</svg>')
    target = Path(__file__).resolve().parent.parent / 'public' / 'assets' / 'icons.svg'
    target.write_text('\n'.join(out) + '\n', encoding='utf-8')
    print(f'{target}: {len(ICONS)} icons')


if __name__ == '__main__':
    main()
