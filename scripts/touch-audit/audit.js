// Touch-target audit (WEB_BLAZOR.md 5: every interactive element >= 44 x 44 CSS px).
// Usage: node audit.js <outDir> [baseUrl]   (normally run through run.sh, which starts the site and a stand-in core)
// Needs the `playwright` npm package (set PLAYWRIGHT_MODULE to its path if it is not resolvable).
const fs = require('fs');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const OUT = process.argv[2];
const BASE = process.argv[3] || 'http://127.0.0.1:5281';
const MIN = 44;
const sizes = [['360x640', 360, 640, true], ['768x1024', 768, 1024, true], ['1280x800', 1280, 800, false]];
const pages = [
  ['login', '/login', false], ['character', '/character', true], ['password', '/account/password', true],
  ['creator', '/creator', true], ['map', '/creator/map', true], ['room', '/creator/rooms/castle.room0', true],
  ['players', '/creator/players', true], ['player-edit', '/creator/players/player', true],
  ['modules', '/creator/modules', true], ['terrain', '/creator/terrain', true],
  ['terrain-edit', '/creator/terrain/edit?kind=fragment', true], ['access-denied', '/access-denied', true],
];

// Runs in the page. Reports interactive elements whose hit area is under min in width or height.
// A checkbox/radio counts as its wrapping or `for` label, because the label is what a finger hits.
const measure = (min) => {
  const sel = 'a[href], button, input:not([type=hidden]), select, textarea, summary, [role=button], [role=tab], [role=link], [tabindex]:not([tabindex="-1"])';
  const small = [];
  for (const el of document.querySelectorAll(sel)) {
    if (el.classList.contains('mud-skip')) continue; // off-screen until focused; measured separately
    let r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    if (r.width === 0 || r.height === 0 || cs.visibility === 'hidden' || cs.display === 'none') continue;
    if (el.type === 'checkbox' || el.type === 'radio') {
      const l = el.closest('label') || (el.id && document.querySelector('label[for="' + el.id + '"]'));
      if (l) r = l.getBoundingClientRect();
    }
    if (r.height < min - 0.5 || r.width < min - 0.5) {
      small.push({ tag: el.tagName.toLowerCase(), cls: el.className && String(el.className).slice(0, 40), text: (el.innerText || el.value || el.getAttribute('aria-label') || el.getAttribute('href') || '').trim().slice(0, 30), w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10 });
    }
  }
  const de = document.documentElement;
  return { hscroll: de.scrollWidth > de.clientWidth + 1, small };
};

async function login(page, name) {
  await page.goto(BASE + '/login', { waitUntil: 'networkidle' });
  await page.fill('#login-name', name);
  await page.fill('#login-password', 'pw');
  await Promise.all([page.waitForURL(u => !u.pathname.startsWith('/login')), page.click('button[type=submit]')]);
}

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const browser = await chromium.launch();
  const results = [];
  for (const [label, w, h, touch] of sizes) {
    const ctx = await browser.newContext({ viewport: { width: w, height: h }, hasTouch: touch, isMobile: touch && w < 600, deviceScaleFactor: 1 });
    const page = await ctx.newPage();
    await login(page, 'Admin');
    for (const [name, path, auth] of pages) {
      if (name === 'login') { await ctx.clearCookies(); }
      else if (auth && !(await ctx.cookies()).length) { await login(page, 'Admin'); }
      await page.goto(BASE + path, { waitUntil: 'networkidle' });
      await page.waitForTimeout(700);
      await page.screenshot({ path: `${OUT}/${label}-${name}.png`, fullPage: true });
      const m = await page.evaluate(measure, MIN);
      // skip link: focus it to bring it on screen, then measure
      await page.evaluate(() => document.querySelector('.mud-skip')?.focus());
      const skip = await page.evaluate((min) => {
        const el = document.activeElement;
        if (!el || !el.classList.contains('mud-skip')) return null;
        const r = el.getBoundingClientRect();
        return { w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10, ok: r.width >= min - 0.5 && r.height >= min - 0.5 };
      }, MIN);
      if (name === 'creator') await page.screenshot({ path: `${OUT}/${label}-skip-link-focused.png` });
      if (skip && !skip.ok) m.small.push({ tag: 'a', cls: 'mud-skip (focused)', text: 'Skip to content', w: skip.w, h: skip.h });
      results.push({ size: label, page: name, hscroll: m.hscroll, small: m.small });
      if (name === 'login') await login(page, 'Admin');
    }
    await ctx.close();
  }
  await browser.close();
  fs.writeFileSync(`${OUT}/results.json`, JSON.stringify(results, null, 2));
  let bad = 0;
  for (const r of results) {
    if (r.small.length || r.hscroll) console.log(`${r.size} ${r.page}${r.hscroll ? ' [HSCROLL]' : ''}`);
    for (const s of r.small) { bad++; console.log(`   ${s.w}x${s.h} <${s.tag}${s.cls ? '.' + s.cls : ''}> "${s.text}"`); }
  }
  console.log(bad ? `FAIL: ${bad} element(s) under ${MIN}px` : `PASS: all interactive elements >= ${MIN}px`);
  process.exit(bad ? 1 : 0);
})().catch(e => { console.error(e); process.exit(2); });
