// Exports docs/diagrams/*.drawio to PNG through the draw.io embed page in headless Chromium (no desktop app needed).
// Usage: node docs/diagrams/export.mjs   (requires `playwright` with chromium; see docs/diagrams/README.md)
// The embed only talks to a parent window, so a tiny host page holds it in an iframe and relays the JSON protocol.
import { chromium } from 'playwright';
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const dir = process.env.DIAGRAMS_DIR ?? dirname(fileURLToPath(import.meta.url));
const files = readdirSync(dir).filter((f) => f.endsWith('.drawio'));
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
await page.setContent(`<!doctype html><html><body style="margin:0">
<iframe id="f" src="https://embed.diagrams.net/?embed=1&proto=json&spin=1&ui=min" style="width:1600px;height:1000px;border:0"></iframe>
<script>
  window.__events = [];
  window.__pending = {};
  window.addEventListener('message', (e) => {
    let msg; try { msg = JSON.parse(e.data); } catch { return; }
    window.__events.push(msg.event);
    const w = window.__pending[msg.event];
    if (w) { delete window.__pending[msg.event]; w(msg); }
  });
  window.__send = (m) => document.getElementById('f').contentWindow.postMessage(JSON.stringify(m), '*');
  window.__wait = (event, ms) => new Promise((res, rej) => { window.__pending[event] = res; setTimeout(() => rej(new Error('timeout waiting for ' + event)), ms); });
</script></body></html>`);
// The embed announces itself with "init" once it is ready.
await page.waitForFunction(() => window.__events.includes('init'), null, { timeout: 120000 });

for (const file of files) {
  const xml = readFileSync(join(dir, file), 'utf8');
  const png = await page.evaluate(async (xml) => {
    const loaded = window.__wait('load', 120000);
    window.__send({ action: 'load', xml, autosave: 0 });
    await loaded;
    const exported = window.__wait('export', 120000);
    window.__send({ action: 'export', format: 'png', scale: 2, border: 20, transparent: false });
    return (await exported).data;
  }, xml);
  const out = join(dir, file.replace(/\.drawio$/, '.png'));
  writeFileSync(out, Buffer.from(png.split(',')[1], 'base64'));
  console.log('exported', out);
}
await browser.close();
