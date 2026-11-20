// Builds the colour schemes of the site in public/assets/app.css (between the "Colour schemes" marks): the same five
// schemes as the program window offers (client/src/Custos.Desktop/tools/make-palettes.mjs), each in light and dark.
//
// The colours come from Google's Material Color Utilities (Apache License 2.0), the same algorithm that made the base
// palette of app.css («Нейтральный серый»: scheme «tonal spot» of #2457c5 on grey surfaces).
//
//     npm install @material/material-color-utilities@0.3.0      (anywhere, once; 0.4 needs a bundler)
//     node tools/make-colors.mjs path/to/node_modules
//
// «neutral» is the base palette of app.css (:root and the two dark blocks, written by hand): here it gives only its
// sample for the menu. Every other scheme puts its colours over the base one while <html data-color="..."> names it
// (layout.php). Ok, warning, error and the heat-map hues stay the same in every scheme. The list of ids is also in
// src/Domain/Users.php (COLORS) and the names in lang/*.php (profile.color.*).
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const modules = process.argv[2];
if (!modules) {
  console.error('Usage: node tools/make-colors.mjs path/to/node_modules');
  process.exit(1);
}

const m = await import(pathToFileURL(join(resolve(modules), '@material/material-color-utilities/index.js')).href);
const { Hct, argbFromHex, hexFromArgb, DynamicScheme, TonalPalette, SchemeTonalSpot, SchemeFidelity, SchemeMonochrome, MaterialDynamicColors: C } = m;

const hct = (hex) => Hct.fromInt(argbFromHex(hex));
const SITE = '#2457c5';

// The schemes, as in the program window. A scheme whose accents come from `base` and whose surfaces and outlines come
// from grey or tinted neutral palettes.
function withSurfaces(base, hue, chroma, variantChroma, palettes = {}) {
  return (isDark) => {
    const b = base(isDark);
    return new DynamicScheme({
      sourceColorArgb: b.sourceColorArgb,
      variant: b.variant,
      contrastLevel: 0,
      isDark,
      primaryPalette: palettes.primary ?? b.primaryPalette,
      secondaryPalette: palettes.secondary ?? b.secondaryPalette,
      tertiaryPalette: palettes.tertiary ?? b.tertiaryPalette,
      neutralPalette: TonalPalette.fromHueAndChroma(hue ?? b.sourceColorHct.hue, chroma),
      neutralVariantPalette: TonalPalette.fromHueAndChroma(hue ?? b.sourceColorHct.hue, variantChroma),
    });
  };
}

const site = (dark) => new SchemeTonalSpot(hct(SITE), dark, 0);
const tone = (hue, chroma) => TonalPalette.fromHueAndChroma(hue, chroma);

const SCHEMES = [
  // The main one: the site's blue on grey surfaces.
  ['neutral', withSurfaces(site, null, 0, 2)],
  // Almost no colour: black and white accents.
  ['graphite', (dark) => new SchemeMonochrome(hct(SITE), dark, 0)],
  // The default accent of Windows 11, kept close to itself («fidelity»), on the grey surfaces of the main scheme. Its
  // own third colour would be orange (the avatar would look like a warning): the site's one instead.
  ['blue', withSurfaces((dark) => new SchemeFidelity(hct('#0067c0'), dark, 0), hct(SITE).hue, 0, 2, { tertiary: site(false).tertiaryPalette })],
  // Warm: the site's blue on warm grey surfaces, like paper under a lamp.
  ['warm', withSurfaces(site, 70, 5, 8)],
  // Sepia: brown ink on cream paper. Its containers (selected item, chips) are a quiet beige from a weak palette:
  // tone 90 of a stronger brown is the peach of the warning badge.
  ['sepia', withSurfaces(site, 80, 6, 10, { primary: tone(60, 24), secondary: tone(70, 10), tertiary: tone(95, 16) }),
    withSurfaces(site, 80, 6, 10, { primary: tone(70, 10), secondary: tone(75, 8) })],
];

// Roles a scheme may take from its second, quieter scheme.
const CONTAINERS = ['primaryContainer', 'onPrimaryContainer', 'secondaryContainer', 'onSecondaryContainer'];

// The --md-* names of app.css and their roles.
const ROLES = {
  'primary': 'primary', 'on-primary': 'onPrimary', 'primary-container': 'primaryContainer', 'on-primary-container': 'onPrimaryContainer',
  'secondary': 'secondary', 'secondary-container': 'secondaryContainer', 'on-secondary-container': 'onSecondaryContainer',
  'tertiary-container': 'tertiaryContainer', 'on-tertiary-container': 'onTertiaryContainer',
  'surface': 'surface', 'surface-bright': 'surfaceBright', 'on-surface': 'onSurface', 'on-surface-variant': 'onSurfaceVariant',
  'surface-container-lowest': 'surfaceContainerLowest', 'surface-container-low': 'surfaceContainerLow', 'surface-container': 'surfaceContainer',
  'surface-container-high': 'surfaceContainerHigh', 'surface-container-highest': 'surfaceContainerHighest',
  'outline': 'outline', 'outline-variant': 'outlineVariant', 'inverse-surface': 'inverseSurface', 'inverse-on-surface': 'inverseOnSurface',
  'inverse-primary': 'inversePrimary',
};

// The colours of app.css made from a scheme: the same tones as the base palette has them.
const ITEM_DARK = hct('#252525').tone;          // a list item on a card, dark theme (light: white)
const MISSED_OFFLINE = { light: hct('#adabac').tone, dark: hct('#5f5e5f').tone };   // heat map: missed while offline

function colours(id, make, containers, dark) {
  const scheme = make(dark);
  const quiet = containers ? containers(dark) : scheme;
  const out = {};
  for (const [name, role] of Object.entries(ROLES)) {
    out['--md-' + name] = hexFromArgb(C[role].getArgb(CONTAINERS.includes(role) ? quiet : scheme));
  }
  // In the light theme the search field in focus and the list items are white, as in the base palette.
  if (!dark) {
    out['--md-surface-bright'] = '#ffffff';
  }
  out['--item-bg'] = dark ? hexFromArgb(scheme.neutralPalette.tone(ITEM_DARK)) : '#ffffff';
  out['--log-bg'] = dark ? out['--md-surface-container-lowest'] : out['--md-inverse-surface'];
  out['--log-text'] = dark ? out['--md-on-surface'] : out['--md-inverse-on-surface'];
  out['--heat-none'] = out['--md-surface-container-highest'];
  out['--heat-missed_offline'] = hexFromArgb(scheme.neutralVariantPalette.tone(dark ? MISSED_OFFLINE.dark : MISSED_OFFLINE.light));
  out['--select-arrow'] = "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 -960 960 960'%3E%3Cpath fill='"
    + out['--md-on-surface-variant'].replace('#', '%23') + "' d='M480-360 280-560h400L480-360Z'/%3E%3C/svg%3E\")";
  return out;
}

// Declarations a few to a line, so that a scheme stays short to read.
function block(selector, values, indent) {
  const lines = [];
  let line = '';
  for (const [name, value] of Object.entries(values)) {
    const one = `${name}: ${value};`;
    if (line !== '' && (indent + 4 + line.length + 1 + one.length > 120 || name === '--select-arrow')) {
      lines.push(line);
      line = '';
    }
    line += (line === '' ? '' : ' ') + one;
  }
  lines.push(line);
  const pad = ' '.repeat(indent);
  return `${pad}${selector} {\n${lines.map((l) => pad + '    ' + l).join('\n')}\n${pad}}\n`;
}

let css = '';
const samples = { light: '', dark: '' };
for (const [id, make, containers] of SCHEMES) {
  const light = colours(id, make, containers, false);
  const dark = colours(id, make, containers, true);
  samples.light += `.swatch-${id} { --swatch-fg: ${light['--md-primary']}; --swatch-bg: ${light['--md-surface-container']}; }\n`;
  samples.dark += `.swatch-${id} { --swatch-fg: ${dark['--md-primary']}; --swatch-bg: ${dark['--md-surface-container']}; }\n`;
  if (id === 'neutral') {
    continue;
  }
  const at = `:root[data-color="${id}"]`;
  css += `/* ${id} */\n`;
  css += block(at, light, 0);
  css += `@media (prefers-color-scheme: dark) {\n${block(at + ':not([data-theme="light"])', dark, 4)}}\n`;
  css += block(at + '[data-theme="dark"]', dark, 0);
}

// The samples of the menu (layout.php): the main colour of a scheme on its background, in the theme shown now.
const darkSamples = (prefix) => samples.dark.replace(/^\.swatch/gm, prefix + ' .swatch');
css += '/* The samples in the user menu */\n' + samples.light
  + '@media (prefers-color-scheme: dark) {\n' + darkSamples(':root:not([data-theme="light"])').replace(/^(?=.)/gm, '    ') + '}\n'
  + darkSamples(':root[data-theme="dark"]');

const file = join(dirname(fileURLToPath(import.meta.url)), '..', 'public', 'assets', 'app.css');
const BEGIN = '/* Colour schemes (made by tools/make-colors.mjs, do not edit by hand) */\n';
const END = '/* End of the colour schemes */\n';
const text = readFileSync(file, 'utf8');
const from = text.indexOf(BEGIN);
const to = text.indexOf(END);
if (from < 0 || to < from) {
  console.error('app.css has no "Colour schemes" marks.');
  process.exit(1);
}
writeFileSync(file, text.slice(0, from + BEGIN.length) + css + text.slice(to));
console.log(`${SCHEMES.length} schemes -> ${file}`);
