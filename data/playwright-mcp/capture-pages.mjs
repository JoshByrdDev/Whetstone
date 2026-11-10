// Captures web pages as full-page screenshots for the board (PrototypeCaptureService).
//
// The fleet may not open a link off this machine, so a ticket whose visual reference is a hosted
// prototype would be built blind. The board can, for the hosts a project's config allows, and this
// is how: it reuses the browser library QA already installs here (`npm install` in this folder).
//
// Request on stdin, one JSON object:
//   { "items": [{ "id", "url", "out" }], "allowHosts": ["host[:port]", ...],
//     "width": 1440, "height": 900, "timeoutMs": 30000 }
// Answer on stdout, one JSON line per item, then {"done":true}:
//   { "id", "ok": true, "finalUrl", "title" }  or  { "id", "ok": false, "error" }
//
// A navigation of the page itself to a host not on allowHosts is aborted rather than followed: a
// prototype that bounces to a sign-in page on another host is a failed capture, and the board must
// not end up loading whatever a redirect chose. Sub-resources (scripts, fonts, images) load from
// wherever the page asks, or nothing would render.

import { chromium } from 'playwright-core';

const read = async () => {
  const chunks = [];
  for await (const c of process.stdin) chunks.push(c);
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
};

const say = (o) => process.stdout.write(JSON.stringify(o) + '\n');

function hostAllowed(u, allow) {
  let url;
  try { url = new URL(u); } catch { return false; }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return false;
  const host = url.hostname.toLowerCase();
  const port = url.port || (url.protocol === 'https:' ? '443' : '80');
  return allow.some((entry) => {
    const [h, p] = String(entry).toLowerCase().split(':');
    if (p && p !== port) return false;
    return host === h || host.endsWith('.' + h);
  });
}

// The installed Chrome first (what QA's browser uses by default), then Edge, which every Windows
// machine has, then Playwright's own build if someone installed one.
async function launch() {
  const errors = [];
  for (const opts of [{ channel: 'chrome' }, { channel: 'msedge' }, {}]) {
    try { return await chromium.launch({ headless: true, ...opts }); }
    catch (e) { errors.push(`${opts.channel ?? 'chromium'}: ${String(e.message).split('\n')[0]}`); }
  }
  throw new Error('no browser could be started - ' + errors.join('; '));
}

const req = await read();
const allow = req.allowHosts ?? [];
const timeout = req.timeoutMs ?? 30000;
let browser;
try {
  browser = await launch();
} catch (e) {
  for (const it of req.items ?? []) say({ id: it.id, ok: false, error: e.message });
  say({ done: true });
  process.exit(0);
}

for (const it of req.items ?? []) {
  if (!hostAllowed(it.url, allow)) {
    say({ id: it.id, ok: false, error: 'the link is not on the allowed hosts' });
    continue;
  }
  const context = await browser.newContext({
    viewport: { width: req.width ?? 1440, height: req.height ?? 900 },
    // Nothing of this machine's: no cookies, no storage, no downloads.
    acceptDownloads: false,
  });
  const page = await context.newPage();
  let refused = null;
  // A redirect the browser follows by itself never reaches a route handler, so before the page's
  // own navigation goes ahead its redirect chain is walked here, hop by hop with redirects off,
  // and every hop's host is checked. Only a chain that stays on allowed hosts is let through.
  await page.route('**/*', async (route) => {
    const r = route.request();
    if (!r.isNavigationRequest() || r.frame() !== page.mainFrame()) return route.continue();
    let next = r.url();
    try {
      for (let hop = 0; hop < 10; hop++) {
        if (!hostAllowed(next, allow)) {
          refused = next;
          return route.abort('blockedbyclient');
        }
        const res = await page.request.fetch(next, { method: r.method(), maxRedirects: 0, timeout });
        const location = res.headers()['location'];
        await res.dispose();
        if (res.status() < 300 || res.status() >= 400 || !location) break;
        next = new URL(location, next).toString();
      }
    } catch {
      // The browser's own request will meet the same failure and report it.
    }
    return route.continue();
  });
  try {
    let response;
    try {
      response = await page.goto(it.url, { waitUntil: 'load', timeout });
    } catch (e) {
      if (refused) throw new Error(`it redirected to ${new URL(refused).host}, which is not an allowed host - most likely a sign-in page`);
      if (/Timeout/i.test(e.name) || /Timeout/i.test(e.message)) throw new Error(`the page did not finish loading in ${Math.round(timeout / 1000)}s`);
      throw new Error(String(e.message).split('\n')[0]);
    }
    if (refused) throw new Error(`it redirected to ${new URL(refused).host}, which is not an allowed host - most likely a sign-in page`);
    if (response && response.status() >= 400) throw new Error(`the host answered HTTP ${response.status()}`);
    // A prototype is usually a client-rendered app: give it a moment to settle, but never wait on
    // a page that polls for ever.
    await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => {});
    await page.waitForTimeout(1000);
    if (refused) throw new Error(`it redirected to ${new URL(refused).host}, which is not an allowed host - most likely a sign-in page`);
    const password = await page.locator('input[type=password]').first().isVisible().catch(() => false);
    if (password) throw new Error('the page asks for a password - a sign-in wall, not the prototype');
    await page.screenshot({ path: it.out, fullPage: true, timeout });
    say({ id: it.id, ok: true, finalUrl: page.url(), title: await page.title().catch(() => '') });
  } catch (e) {
    say({ id: it.id, ok: false, error: e.message });
  } finally {
    await context.close().catch(() => {});
  }
}

say({ done: true });
// A page still waiting on a dead host can hold the browser's close for a minute; the answers are
// already out, so the browser gets a few seconds and then the process goes.
await Promise.race([browser.close().catch(() => {}), new Promise((r) => setTimeout(r, 5000))]);
process.exit(0);
