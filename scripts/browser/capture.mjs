import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
export async function capture(page, options) {
  await page.evaluate(() => { window.__captureState=null; });
  await page.waitForFunction(() => {
    const text=document.body.innerText;
    const ready=!text.includes('Loading task…')&&!text.includes('Loading completion evidence…')&&(!location.pathname.startsWith('/tasks/')||document.querySelector('.task-outcome'));
    const prior=window.__captureState;
    if(!ready||!prior||prior.text!==text) { window.__captureState={text,at:performance.now()}; return false; }
    return performance.now()-prior.at>1500;
  });
  const before=await page.locator('body').innerText();
  assert.ok(!before.includes('Loading task…'));
  await page.screenshot(options);
  assert.equal(await page.locator('body').innerText(),before,'Captured content must remain stable through screenshot');
  await fs.writeFile(options.path+'.txt',before);
}
