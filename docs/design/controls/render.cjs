// Renders the previews of the control mockups and checks their contrast as rendered.
//   node docs/design/controls/render.cjs            previews (previews/*.png) and the contrast check in every theme
//   node docs/design/controls/render.cjs --check    only the contrast check
// Needs Playwright's Chromium (require('playwright'); PLAYWRIGHT_BROWSERS_PATH where it is installed). The check walks
// every visible text in every mockup and theme, takes its color and the color under it (the backgrounds of its
// ancestors, composited), and fails under 4.5:1 (7:1 in the High contrast themes). Icons, focus rings and check box
// outlines are graphics, held to 3:1. Disabled controls are exempt (WCAG 1.4.3), and so are the "Now" pictures of
// the current views on the before/after sheet (data-nocheck).
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
  { file: 'Options.dc.html', name: 'options', width: 1280, height: 800, themes: VIEW_THEMES }
];

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

function checkInPage(minText) {
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
      const { checked, fails } = await page.evaluate(checkInPage, hc ? 7 : 4.5);
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
