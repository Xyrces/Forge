const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

async function main() {
  const [base, out] = process.argv.slice(2);
  if (!base || !out) throw Error('URL and evidence directory required');
  fs.mkdirSync(out, { recursive: true });
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROMIUM_EXECUTABLE_PATH });
  try {
    const page = await browser.newPage({ viewport: { width: 2560, height: 1274 } });
    const errors = [];
    const writes = [];
    page.on('pageerror', error => errors.push(String(error)));
    await page.route('**/api/**', async route => {
      if (['GET', 'HEAD'].includes(route.request().method())) return route.continue();
      writes.push(route.request().url());
      return route.abort();
    });
    async function json(url) {
      const response = await page.request.get(base + url);
      assert(response.ok(), url + ' returned ' + response.status());
      return response.json();
    }
    const build = await json('/api/meta/buildinfo');
    const inbox = await json('/api/intake/inbox?projectId=forge');
    await page.goto(base + '/intake?project=forge');
    const list = page.getByRole('region', { name: 'Intake inbox', exact: true });
    await list.waitFor();
    if (inbox.items.length) {
      await list.getByText(inbox.items[0].title, { exact: true }).first().waitFor();
    } else {
      await list.getByRole('heading', { name: 'What would you like to build?' }).waitFor();
    }
    await page.screenshot({ path: path.join(out, 'intake-desktop.png'), fullPage: true });
    // Only visit already-linked conversations. Expanding an unlinked suggestion writes a session.
    const linked = inbox.items.filter(item => item.kind === 'proposal' && item.sessionId);
    const sample = linked.find(item => item.revision > 1) ?? linked[0];
    if (sample) {
      const query = new URLSearchParams({ project: 'forge', session: sample.sessionId, proposal: sample.id });
      await page.goto(base + '/intake?' + query);
      const brief = page.getByRole('region', { name: 'Working brief', exact: true });
      await brief.getByRole('heading', { name: sample.title, exact: true }).waitFor();
      if (sample.revision > 1) {
        await brief.getByRole('button', { name: 'Changes', exact: true }).click();
        await brief.getByText(`Revision ${sample.revision-1} compared with revision ${sample.revision}`, { exact: true }).waitFor();
      }
      await page.screenshot({ path: path.join(out, 'brief-desktop.png'), fullPage: true });
      await page.getByRole('button', { name: /Back to Intake/ }).click();
      await list.getByText(sample.title, { exact: true }).first().waitFor();
    }
    await page.setViewportSize({ width: 320, height: 900 });
    await page.screenshot({ path: path.join(out, 'intake-mobile.png'), fullPage: true });
    const sizes = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth }));
    assert(sizes.scroll <= sizes.width + 1);
    assert.deepEqual(errors, []);
    assert.deepEqual(writes, []);
    fs.writeFileSync(path.join(out, 'smoke.json'), JSON.stringify({
      revision: build.sourceRevision, inboxItems: inbox.items.length, inspectedRevision: sample?.revision,
      errors, writes, sizes
    }, null, 2));
  } finally {
    await browser.close();
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
