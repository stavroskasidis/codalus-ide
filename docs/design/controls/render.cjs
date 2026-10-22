// Renders the previews of the control mockups and checks their contrast as rendered.
//   node docs/design/controls/render.cjs            previews (previews/*.png) and the contrast check in every theme
//   node docs/design/controls/render.cjs --check    only the contrast check
// Needs Playwright's Chromium (require('playwright'); PLAYWRIGHT_BROWSERS_PATH where it is installed). The check walks
// every visible text in every mockup and theme, takes its color and the color under it (the backgrounds of its
// ancestors, composited), and fails under 4.5:1 (7:1 in the High contrast themes). Icons, focus rings and check box
// outlines are graphics, held to 3:1. Disabled controls are exempt (WCAG 1.4.3), and so are the "Now" pictures of
// the current views on the before/after sheet (data-nocheck). A page with cues: true also has its non-text state cues
// held to 3:1 against what is behind them (WCAG 1.4.11): the rounded corners' selected tab, frames, pill and bar.
//   node docs/design/controls/render.cjs --only=DataTip   only the pages whose file starts with that
'use strict';
const path = require('path');
const fs = require('fs');
const { chromium } = require('playwright');

const here = __dirname;
const out = path.join(here, 'previews');
const checkOnly = process.argv.includes('--check');
const only = process.argv.find((a) => a.startsWith('--only='))?.slice(7);

const ALL = ['Classic Blue', 'Classic Light', 'Classic Dark', 'Classic High Contrast', 'Harbor Light', 'Harbor Dark', 'Harbor High contrast'];
const VIEW_THEMES = ['Classic Light', 'Classic Dark', 'Harbor Light', 'Harbor Dark'];
const PAGES = [
  { file: 'Controls.dc.html', name: 'controls', width: 1280, height: 800, full: true, themes: ALL },
  { file: 'ToolWindowToolbars.dc.html', name: 'tool-window-toolbars', width: 1280, height: 800, full: true, themes: VIEW_THEMES },
  { file: 'FindResults.dc.html', name: 'find-results', width: 1280, height: 540, themes: VIEW_THEMES },
  { file: 'HttpResponse.dc.html', name: 'http-response', width: 1280, height: 640, themes: VIEW_THEMES },
  { file: 'WcfTestClient.dc.html', name: 'wcf-test-client', width: 1280, height: 800, themes: VIEW_THEMES },
  { file: 'WcfTestClient.dc.html', name: 'wcf-test-client-fault', query: 'mode=Fault', width: 1280, height: 800, themes: ['Harbor Light', 'Classic Dark'] },
  { file: 'ChecklistDialog.dc.html', name: 'checklist-dialog', width: 1280, height: 800, themes: VIEW_THEMES },
  { file: 'ChecklistDialog.dc.html', name: 'checklist-dialog-macos', query: 'os=macOS', width: 1280, height: 800, themes: ['Harbor Light'] },
  { file: 'AiChat.dc.html', name: 'ai-chat', width: 1280, height: 800, themes: VIEW_THEMES },
  { file: 'Options.dc.html', name: 'options', width: 1280, height: 800, themes: VIEW_THEMES },
  { file: 'WinFormsDesignerMenus.dc.html', name: 'winforms-designer-menus', width: 1280, height: 800, full: true, themes: [...VIEW_THEMES, 'Classic High Contrast'] },
  { file: 'PropertiesWindow.dc.html', name: 'properties-window', width: 1280, height: 800, full: true, themes: [...VIEW_THEMES, 'Classic High Contrast'] },
  { file: 'PropertiesWindow.dc.html', name: 'properties-window-color', query: 'state=Color', width: 1280, height: 800, themes: ['Classic Light', 'Harbor Dark'] },
  // The font editor keeps the Properties window inactive: Classic High Contrast shows the selected name's outline there.
  { file: 'PropertiesWindow.dc.html', name: 'properties-window-font', query: 'state=Font', width: 1280, height: 800, themes: ['Classic Dark', 'Harbor Light', 'Classic High Contrast'] },
  { file: 'PropertiesWindow.dc.html', name: 'properties-window-invalid', query: 'state=Invalid', width: 1280, height: 800, themes: ['Classic Light', 'Harbor Dark'] },
  // The window's other states, checked in every theme but not rendered: the sheet above shows each of them.
  { file: 'PropertiesWindow.dc.html', name: 'properties-window-alignment', query: 'state=Alignment', width: 1280, height: 800, themes: [] },
  { file: 'PropertiesWindow.dc.html', name: 'properties-window-strings', query: 'state=Strings', width: 1280, height: 800, themes: [] },
  { file: 'PropertiesWindow.dc.html', name: 'properties-window-resource', query: 'state=Resource', width: 1280, height: 800, themes: [] },
  { file: 'PropertiesWindow.dc.html', name: 'properties-window-file', query: 'state=File', width: 1280, height: 800, themes: [] },
  { file: 'PropertiesWindow.dc.html', name: 'properties-window-events', query: 'state=Events', width: 1280, height: 800, themes: [] },
  { file: 'PropertiesWindow.dc.html', name: 'properties-window-multiple', query: 'state=Multiple', width: 1280, height: 800, themes: [] },
  { file: 'PropertiesWindow.dc.html', name: 'properties-window-reference', query: 'state=Reference', width: 1280, height: 800, themes: [] },
  { file: 'GitChanges.dc.html', name: 'git-changes', width: 1280, height: 800, full: true, themes: [...VIEW_THEMES, 'Classic High Contrast'] },
  { file: 'GitChanges.dc.html', name: 'git-changes-folder', query: 'state=folder', width: 1280, height: 800, themes: ['Classic Light', 'Harbor Dark'] },
  { file: 'GitChanges.dc.html', name: 'git-changes-branch-menu', query: 'state=branch-menu', width: 1280, height: 800, themes: ['Classic Dark', 'Harbor Light'] },
  { file: 'GitChanges.dc.html', name: 'git-changes-delete-unmerged', query: 'state=delete-unmerged', width: 1280, height: 800, themes: ['Classic Light'] },
  { file: 'GitChanges.dc.html', name: 'git-changes-folder-macos', query: 'state=folder&os=macOS', width: 1280, height: 800, themes: ['Harbor Light'] },
  // A danger dialog's buttons on macOS: Cancel, the default, stays at the trailing edge.
  { file: 'GitChanges.dc.html', name: 'git-changes-delete-macos', query: 'state=delete&os=macOS', width: 1280, height: 800, themes: ['Harbor Light'] },
  // Update (question 21, the maintainer's request after D38's approval): the entry, and a branch that has diverged.
  { file: 'GitChanges.dc.html', name: 'git-changes-update-menu', query: 'state=update-menu', width: 1280, height: 800, themes: ['Classic Light', 'Harbor Dark'] },
  { file: 'GitChanges.dc.html', name: 'git-changes-update-diverged', query: 'state=update-diverged', width: 1280, height: 800, themes: ['Harbor Light'] },
  // The other states in the whole window, checked in every theme but not rendered: the sheet above shows each of them.
  ...['changes', 'staged-folder', 'deleted-folder', 'untracked-folder', 'filtered', 'file-menu', 'staged-file', 'deleted-file', 'repo-menu', 'conflict-file',
    'conflict-folder', 'discard', 'discard-filtered', 'stash', 'new-branch', 'new-branch-operation', 'rename', 'delete', 'delete-upstream',
    'delete-unmerged-upstream', 'delete-remote', 'compare', 'compare-loading', 'compare-same', 'branches', 'search', 'nomatch', 'loading', 'error', 'many',
    'branch-actions', 'current-menu', 'remote-menu', 'remote-twin', 'worktree', 'worktree-enter', 'detached', 'operation', 'unborn', 'noremote',
    'noremote-link', 'long-row', 'long-name', 'narrow', 'updating', 'updated', 'update-worktree']
    .map((state) => ({ file: 'GitChanges.dc.html', name: `git-changes-${state}`, query: `state=${state}`, width: 1280, height: 800, themes: [] })),
  { file: 'PackageManager.dc.html', name: 'package-manager', width: 1280, height: 800, full: true, themes: ALL },
  { file: 'DataTip.dc.html', name: 'data-tip', width: 1280, height: 800, full: true, themes: [...VIEW_THEMES, 'Classic High Contrast'] },
  // Single states, each its panel alone (a small viewport, the full page: the picture is the panel's size): the keyboard's
  // selection with the row's menu (in Classic High Contrast, whose overlays have 2 px borders), the Dictionary view and the
  // View drop-down, two pinned tips while the program runs, Ctrl's see-through tips.
  { file: 'DataTip.dc.html', name: 'data-tip-keyboard', query: 'state=keyboard', width: 400, height: 300, full: true, themes: ['Classic High Contrast'] },
  { file: 'DataTip.dc.html', name: 'data-tip-dictionary-view', query: 'state=dictionary-view', width: 400, height: 300, full: true, themes: ['Classic Dark', 'Harbor Light'] },
  { file: 'DataTip.dc.html', name: 'data-tip-collection-view', query: 'state=collection-view', width: 400, height: 300, full: true, themes: ['Classic Light'] },
  { file: 'DataTip.dc.html', name: 'data-tip-pinned-more', query: 'state=pinned-more', width: 400, height: 300, full: true, themes: ['Classic Light', 'Harbor Dark'] },
  { file: 'DataTip.dc.html', name: 'data-tip-ctrl', query: 'state=ctrl', width: 400, height: 300, full: true, themes: ['Classic Dark'] },
  { file: 'DiffDocument.dc.html', name: 'diff-document', width: 1280, height: 800, full: true, themes: [...VIEW_THEMES, 'Classic High Contrast'] },
  // Harbor's sheets draw the window as Harbor keeps it, then the Classic theme of the same kind for the rest (only Classic changes shape).
  { file: 'RoundedCorners.dc.html', name: 'rounded-corners', width: 1280, height: 800, full: true, cues: true, themes: ['Classic Blue', ...VIEW_THEMES, 'Classic High Contrast'] },
  { file: 'RoundedCorners.dc.html', name: 'rounded-corners-proposed', query: 'state=proposed', width: 400, height: 300, full: true, cues: true, themes: ['Classic Light', 'Classic Dark'] }
];

// New Project (0.6.0): each page in the view themes, its main states in Harbor Light and Classic Dark, the others in
// Classic Light (the default), and every state in the contrast check.
const PAIR = ['Harbor Light', 'Classic Dark'];
const NEW_PROJECT = {
  choose: { data: VIEW_THEMES, search: PAIR, nomatch: PAIR, install: PAIR, loading: ['Classic Light'], installing: [], installfailed: ['Classic Light'], menu: ['Classic Light'], fromstart: [] },
  configure: { new: VIEW_THEMES, solution: PAIR, error: PAIR, blank: PAIR, folder: [], add: ['Classic Light'], trusted: [], recent: ['Classic Light'], exists: [], longpath: ['Classic Light'] },
  additional: { blazor: VIEW_THEMES, wpf: PAIR, xunit: PAIR, keys: PAIR, untrusted: ['Classic Light'], globaljson: ['Classic Light'], library: ['Classic Light'], xunit10: [], creating: ['Classic Light'], failed: ['Classic Light'] }
};
const FIRST = { choose: 'data', configure: 'new', additional: 'blazor' };
for (const [page, states] of Object.entries(NEW_PROJECT)) {
  for (const [state, themes] of Object.entries(states)) {
    const name = state === FIRST[page] ? `new-project-${page}` : `new-project-${page}-${state}`;
    const query = state === 'keys' ? `page=${page}&keys=on` : `page=${page}&state=${state}`;
    PAGES.push({ file: 'NewProject.dc.html', name, query, width: 1280, height: 800, themes });
  }
}

const slug = (s) => s.toLowerCase().replace(/\s+/g, '-');

// Connecting an agent (0.6.0, controls.md#connect-an-agent): the dialog's states, AI Chat's states and Options' AI page.
// The main states in the view themes and Classic High Contrast, the others in one or two; every state in the check.
const FIVE = ['Classic Light', 'Classic Dark', 'Classic High Contrast', 'Harbor Light', 'Harbor Dark'];
const TWO = ['Harbor Light', 'Classic Dark'];
const AGENT_CONNECT = {
  connect: FIVE, apikey: FIVE, ok: FIVE, progress: TWO, signin: TWO, missing: TWO, disconnect: TWO,
  codex: ['Harbor Light'], gemini: ['Classic Dark'], unconfirmed: ['Classic Light'], failed: ['Harbor Dark'],
  'apikey-memory': ['Classic Light'], installing: ['Classic Light'], offline: ['Classic Light'], remote: ['Classic Light']
};
for (const [state, themes] of Object.entries(AGENT_CONNECT)) {
  PAGES.push({ file: 'AgentConnect.dc.html', name: `agent-connect-${state}`, query: `state=${state}`, width: 1280, height: 800, themes });
}
PAGES.push({ file: 'AgentConnect.dc.html', name: 'agent-connect-missing-windows', query: 'state=missing&os=Windows', width: 1280, height: 800, themes: ['Classic Light'] });
PAGES.push({ file: 'AgentConnect.dc.html', name: 'agent-connect-missing-macos', query: 'state=missing&os=macOS', width: 1280, height: 800, themes: ['Harbor Light'] });
const AI_CHAT_CONNECT = {
  off: FIVE, picker: FIVE, connected: TWO, 'no-provider': ['Harbor Dark'], more: ['Classic Light'], manage: ['Harbor Light'],
  'manage-claude': ['Classic Dark'], commands: ['Classic Light'], remote: ['Classic Light']
};
for (const [state, themes] of Object.entries(AI_CHAT_CONNECT)) {
  PAGES.push({ file: 'AiChat.dc.html', name: `ai-chat-${state}`, query: `state=${state}`, width: 1280, height: 800, themes });
}
PAGES.push({ file: 'Options.dc.html', name: 'options-ai', query: 'page=ai', width: 1280, height: 800, themes: VIEW_THEMES });

function checkInPage({ minText, cues }) {
  const parse = (c) => {
    const m = /rgba?\(([^)]+)\)/.exec(c);
    if (!m) return null;
    const p = m[1].split(/[ ,/]+/).filter(Boolean).map(Number);
    return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
  };
  const over = (top, under) => {
    const a = top.a + under.a * (1 - top.a);
    if (a === 0) return { r: 0, g: 0, b: 0, a: 0 };
    const mix = (k) => (top[k] * top.a + under[k] * under.a * (1 - top.a)) / a;
    return { r: mix('r'), g: mix('g'), b: mix('b'), a };
  };
  const lum = (c) => {
    const f = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
    return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
  };
  const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
  const hex = (c) => '#' + [c.r, c.g, c.b].map((v) => Math.round(v).toString(16).padStart(2, '0')).join('').toUpperCase();
  const bgUnder = (el) => {
    const layers = [];
    for (let e = el; e && e.nodeType === 1; e = e.parentElement) {
      const c = parse(getComputedStyle(e).backgroundColor);
      if (c && c.a > 0) { layers.push(c); if (c.a >= 1) break; }
    }
    let acc = { r: 255, g: 255, b: 255, a: 1 };
    for (let i = layers.length - 1; i >= 0; i--) acc = over(layers[i], acc);
    return acc;
  };
  const opacityOf = (el) => { let o = 1; for (let e = el; e && e.nodeType === 1; e = e.parentElement) o *= Number(getComputedStyle(e).opacity); return o; };
  const exempt = (el) => el.closest('.is-disabled, [aria-disabled="true"], [data-nocheck], #cx-sprite');
  const visible = (el) => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden'; };
  const fails = [];
  let checked = 0;
  const test = (el, fgColor, kind, min, text) => {
    const bg = bgUnder(el);
    let fg = parse(fgColor);
    if (!fg) return;
    fg = { ...fg, a: fg.a * opacityOf(el) };
    const shown = over(fg, bg);
    const r = ratio(shown, bg);
    checked++;
    if (r + 1e-6 < min) fails.push({ kind, text: text.slice(0, 60), fg: hex(shown), bg: hex(bg), ratio: Math.round(r * 100) / 100, min });
  };
  for (const el of document.querySelectorAll('body *')) {
    if (exempt(el) || !visible(el)) continue;
    if (el.closest('svg')) continue;
    const own = [...el.childNodes].filter((n) => n.nodeType === 3 && n.nodeValue.trim()).map((n) => n.nodeValue.trim()).join(' ');
    if (own) test(el, getComputedStyle(el).color, 'text', minText, own);
    if (el.tagName === 'INPUT' && !el.value && el.placeholder) test(el, getComputedStyle(el, '::placeholder').color, 'placeholder', minText, el.placeholder);
  }
  for (const svg of document.querySelectorAll('svg.i')) {
    if (exempt(svg) || !visible(svg) || svg.closest('[aria-hidden="true"]:not(svg)')) continue;
    const use = svg.querySelector('use');
    const name = use ? use.getAttribute('href') : '';
    if (/cs-file|json-file|warning|error|info/.test(name)) continue; // multi-color glyphs drawn from the state tokens
    test(svg, getComputedStyle(svg).color, 'icon', 3, name);
  }
  for (const el of document.querySelectorAll('.is-focus')) {
    if (exempt(el) || !visible(el)) continue;
    const target = el.matches('.chk, .rad, .sw') ? el.querySelector('.box, .dot, .track') : el;
    test(el.parentElement, getComputedStyle(target).outlineColor, 'focus ring', 3, el.textContent.trim() || el.getAttribute('aria-label') || 'focus');
  }
  for (const el of document.querySelectorAll('.chk:not(.on):not(.mixed) .box, .rad:not(.on) .dot, .sw:not(.on) .track')) {
    if (exempt(el) || !visible(el)) continue;
    test(el.parentElement, getComputedStyle(el).borderTopColor, 'outline', 3, el.parentElement.textContent.trim());
  }
  if (cues) {
    // Each cue against what is behind it; of several colors (a tab found by its fill or by its outline) the best counts.
    const cue = (el, under, colors, kind, text) => {
      if (exempt(el) || !visible(el)) return;
      const bg = bgUnder(under);
      let best = null;
      for (const c of colors.map(parse).filter(Boolean)) {
        const shown = over(c, bg);
        const r = ratio(shown, bg);
        if (!best || r > best.r) best = { r, shown };
      }
      if (!best) return;
      checked++;
      if (best.r + 1e-6 < 3) fails.push({ kind, text: (text || '').slice(0, 60), fg: hex(best.shown), bg: hex(bg), ratio: Math.round(best.r * 100) / 100, min: 3 });
    };
    const cs = (el, pseudo) => getComputedStyle(el, pseudo);
    for (const el of document.querySelectorAll('.v-new .dt.on')) cue(el, el.parentElement, [cs(el).backgroundColor, cs(el).borderTopColor], 'selected tab', el.textContent.trim());
    for (const el of document.querySelectorAll('.v-new.outline .well:not(.idle) > .wd')) cue(el, el.parentElement.parentElement, [cs(el).borderLeftColor], 'document frame', 'active document');
    for (const el of document.querySelectorAll('.v-new.outline:not(.capfill) .pn.focus')) cue(el, el.parentElement, [cs(el).borderLeftColor], 'tool window frame', el.getAttribute('aria-label'));
    for (const el of document.querySelectorAll('.v-new .trow .pill')) cue(el, el.parentElement, [cs(el).backgroundColor], 'selection pill', el.parentElement.textContent.trim());
    for (const el of document.querySelectorAll('.v-new .atab.on')) cue(el, el, [cs(el, '::after').backgroundColor], 'auto-hide bar', el.textContent.trim());
  }
  return { checked, fails };
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  if (!checkOnly) fs.mkdirSync(out, { recursive: true });
  let failures = 0;
  let total = 0;
  for (const p of PAGES) {
    if (only && !p.file.startsWith(only)) continue;
    for (const theme of ALL) {
      await page.setViewportSize({ width: p.width, height: p.height });
      const url = 'file://' + path.join(here, p.file) + '?theme=' + encodeURIComponent(theme) + (p.query ? '&' + p.query : '');
      await page.goto(url);
      await page.waitForSelector('html[data-rendered="true"]');
      await page.evaluate(() => document.fonts.ready);
      const hc = theme.toLowerCase().includes('high contrast');
      const { checked, fails } = await page.evaluate(checkInPage, { minText: hc ? 7 : 4.5, cues: !!p.cues });
      total += checked;
      for (const f of fails) {
        failures++;
        console.log(`${p.name} · ${theme} · ${f.kind} "${f.text}" ${f.fg} on ${f.bg}: ${f.ratio} < ${f.min}`);
      }
      if (!checkOnly && p.themes.includes(theme)) {
        const file = path.join(out, `${p.name}-${slug(theme)}.png`);
        await page.screenshot({ path: file, fullPage: !!p.full });
      }
    }
  }
  await browser.close();
  console.log(`${total} pairs checked, ${failures} below the minimum.`);
  process.exit(failures ? 1 : 0);
})();
