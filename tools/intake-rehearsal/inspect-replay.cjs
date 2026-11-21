const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

async function inspect() {
  const [base, output] = process.argv.slice(2);
  if (!base || !output) throw Error('Pass the disposable replay URL and evidence directory');
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROMIUM_EXECUTABLE_PATH });
  try {
    const page = await browser.newPage({ colorScheme: 'dark', viewport: { width: 2560, height: 1274 } });
    const errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    const response = await page.request.get(base + '/__qualification/replay');
    assert(response.ok());
    const proposal = await response.json();
    assert(proposal?.sessionId, 'The fixture must be started with --replay');
    const query = new URLSearchParams({ project: 'forge', session: proposal.sessionId, proposal: proposal.id });
    await page.goto(base + '/intake?' + query);
    const brief = page.getByRole('region', { name: 'Working brief', exact: true });
    await brief.getByRole('button', { name: 'Changes', exact: true }).click();
    await brief.getByText('Revision 1 compared with revision 2', { exact: true }).waitFor();
    const layouts = [];
    for (const width of [2560, 1884, 1280, 390]) {
      await page.setViewportSize({ width, height: 1274 });
      await page.screenshot({ path: path.join(output, 'changes-' + width + '.png') });
      const bounds = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth }));
      assert(bounds.scroll <= bounds.width + 1, 'Page overflow at ' + width);
      layouts.push(bounds);
    }
    const workspace = page.getByRole('navigation', { name: 'Intake workspace', exact: true });
    await workspace.getByRole('button', { name: 'Conversation', exact: true }).click();
    await page.locator('#intake-message').waitFor({ state: 'visible' });
    const opening = page.getByRole('button', { name: /Opening request/ });
    await opening.click();
    await page.locator('.intake-message--user').first().waitFor();
    await page.screenshot({ path: path.join(output, 'conversation-390.png') });
    await workspace.getByRole('button', { name: 'Working brief', exact: true }).click();
    await brief.waitFor({ state: 'visible' });
    await page.setViewportSize({ width: 2560, height: 1274 });
    await brief.getByRole('button', { name: 'Current brief', exact: true }).click();
    await brief.getByRole('heading', { name: 'What changed', exact: true }).waitFor({ state: 'hidden' });
    await page.screenshot({ path: path.join(output, 'current-2560.png') });
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'inspection.json'), JSON.stringify({ layouts, errors }, null, 2));
  } finally {
    await browser.close();
  }
}
inspect().catch(error => { console.error(error); process.exitCode = 1; });
