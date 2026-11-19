import { capture } from './capture.mjs';
// Import into the Linux browser runner after opening an authenticated Forge UI.
// Routes are fixtures only; no service state is changed by this qualification.
import assert from 'node:assert/strict';
async function verifyActiveSprintLabel(page, width) {
  const label = page.locator('.app-active-sprint');
  await label.waitFor({state:'visible'});
  const text = await label.innerText();
  assert.equal(await label.getAttribute('title'), 'Active sprint: ' + text);
  const geometry = await label.evaluate(element => {
    const bounds = element.getBoundingClientRect();
    const range = document.createRange();
    range.selectNodeContents(element);
    return {
      left: bounds.left, right: bounds.right,
      top: bounds.top, bottom: bounds.bottom,
      lines: [...range.getClientRects()].map(line => ({
        left: line.left, right: line.right, top: line.top, bottom: line.bottom,
      })),
      clientHeight: element.clientHeight, scrollHeight: element.scrollHeight,
    };
  });
  assert.ok(geometry.left >= 0 && geometry.right <= width, 'Active sprint label fits viewport');
  assert.ok(geometry.scrollHeight <= geometry.clientHeight + 1, 'Active sprint text is not vertically clipped');
  for (const line of geometry.lines) {
    assert.ok(line.left >= geometry.left && line.right <= geometry.right + 1, 'Sprint text fits horizontally');
    assert.ok(line.top >= geometry.top && line.bottom <= geometry.bottom + 1, 'Each sprint name line fits vertically');
  }
}
export async function verifySprintBoard(page, baseUrl, { fixtureApi = null, screenshotDir = null, nominatedHuman = null } = {}) {
  const projectId = 'forge';
  const sprint = { id:'ux-fixture', name:'Review the dashboard change and qualify the protected pull request workflow across desktop and mobile', goal:'## Goal\nKeep production clean.\n\n- Preserve development tooling.', status:'Active', startDate:'2026-11-17T00:00:00Z' };
  const states = ['Pending','AgentRunning','PROpen','PROpen','Merged'];
  const members = states.map((state,i) => ({ id:`fixture-${i}`, title:`Task ${i+1}: verify a long title remains readable inside its lane without clipping`, type:'ui', status:i===4?'Completed':i===0?'Pending':'InProgress', state, priority:2, assignee:i===1?'worker':null, prNumber:null, reworkAttempts:i===4?'2':null, boosted:false, blockerCount:0, blockedBy:[], situation:i===1?'Responding to a recorded review finding.':'Fixture evidence', situationTone:'muted', label:['Ready for work','Making a requested change','Review in progress','Waiting for human review','Merged'][i], nextActor:i===3?(nominatedHuman?'@'+nominatedHuman:'Human reviewer'):'Forge', nominatedHuman, humanReviewTaskHold:i===3, humanReviewRequired:i===3, awaitingHumanReview:i===3 }));
  const completion={receipt:{id:'synthetic-completion',projectId,sprintId:sprint.id,outcome:'PendingEvidence',bookkeepingState:'Completed',readyForDeployment:false,deploymentStatus:'Pending',deliveryNote:'Synthetic active-sprint fixture: one change merged; deployment and browser observation remain pending.'},batches:[],criteria:[{id:'code',text:'Keep production free of development-tool requests',evidenceKind:'MergedAndVerified'},{id:'browser',text:'Confirm the deployed dashboard opens without missing-script requests',evidenceKind:'OperatorObserved',proofReference:null}],nextCursor:null};
  let holdWrites = 0;
  const previousWrites = fixtureApi ? (await (await page.request.get(`${fixtureApi}/__writes`)).json()).length : 0;
  if (fixtureApi) {
    await page.request.put(`${fixtureApi}/__fixtures`, {data:{
      '/api/sprints/active':sprint,
      '/api/sprints/summary':{projectId,generatedAt:new Date().toISOString(),active:sprint,total:1,history:[],nextCursor:null},
      '/api/sprints/board':{projectId,generatedAt:new Date().toISOString(),sprint,total:5,done:1,members,nextCursor:null},
      '/api/sprints/ux-fixture/completion':completion,
      '/api/tasks/fixture-1/human-review':{required:false,syncPending:false,mode:'Automated'}
    }});
  } else {
  await page.route('**/api/sprints/active**', route => route.fulfill({json:sprint}));
  await page.route('**/api/sprints/summary?**', route => route.fulfill({json:{projectId,generatedAt:new Date().toISOString(),active:sprint,total:1,history:[],nextCursor:null}}));
  await page.route('**/api/sprints/board?**', route => route.fulfill({json:{projectId,generatedAt:new Date().toISOString(),sprint,total:5,done:1,members,nextCursor:null}}));
  await page.route('**/api/sprints/ux-fixture/completion?**', route => route.fulfill({json:completion}));
  await page.route('**/api/tasks/fixture-1/human-review?**', route => {
    assert.equal(route.request().method(),'PUT');
    assert.deepEqual(route.request().postDataJSON(),{required:true});
    members[1].humanReviewRequired=true; holdWrites++;
    return route.fulfill({json:{required:true}});
  });
  }
  // Long recorded evidence must not push the board below the initial viewport.
  completion.criteria.push(...Array.from({length: 6}, (_, i) => ({
    id: `recorded-${i}`, text: `Recorded browser observation ${i + 1}`,
    evidenceKind: 'OperatorObserved', proofReference: 'Verified operator observation. '.repeat(70),
    operatorIdentity: 'Synthetic rehearsal operator',
  })));
  if (fixtureApi) {
    await page.request.put(`${fixtureApi}/__fixtures`, {data:{'/api/sprints/ux-fixture/completion':completion}});
  }
  await page.setViewportSize({width:2560,height:1274});
  await page.goto(`${baseUrl}/sprints`);
  await page.locator('.sprint-task').first().waitFor();
  const acceptanceToggle = page.locator('.sprint-acceptance__toggle');
  await acceptanceToggle.waitFor();
  assert.equal(await acceptanceToggle.getAttribute('aria-expanded'), 'false');
  await page.locator('.sprint-acceptance__details').waitFor({state:'hidden'});
  await page.locator('.app-main').evaluate(element => { element.scrollTop = 0; });
  await page.waitForFunction(() => !document.querySelector('.sprint-acceptance')?.textContent.includes('Loading completion evidence'));
  const glance = await page.evaluate(() => {
    const box = selector => {
      const rect = document.querySelector(selector).getBoundingClientRect();
      return {top:rect.top,bottom:rect.bottom,left:rect.left,right:rect.right};
    };
    return {
      main:box('.app-main'), header:box('.sprint-hero'), acceptance:box('.sprint-acceptance'),
      board:box('.sprint-board'), cards:[...document.querySelectorAll('.sprint-task')].map(element => {
        const rect = element.getBoundingClientRect();
        return {top:rect.top,bottom:rect.bottom,left:rect.left,right:rect.right};
      }),
    };
  });
  for (const [label, box] of Object.entries(glance).filter(([key]) => key !== 'cards')) {
    assert.ok(box.top >= 0 && box.bottom <= 1274, `${label} is visible without scrolling`);
  }
  for (const box of glance.cards) assert.ok(box.bottom <= 1274, 'Whole card visible at a glance');
  assert.ok(glance.header.bottom <= glance.acceptance.top && glance.acceptance.bottom <= glance.board.top);
  assert.ok(await page.locator('.app-nav').isVisible(), 'Desktop sidebar open at 2560');
  if(screenshotDir) await capture(page,{path:`${screenshotDir}/board-glance-2560.png`,fullPage:false});
  await acceptanceToggle.focus();
  await page.keyboard.press('Enter');
  await page.locator('.sprint-acceptance__details').waitFor({state:'visible'});
  assert.equal(await acceptanceToggle.getAttribute('aria-expanded'), 'true');
  await page.getByRole('heading',{name:'Keep production free of development-tool requests',exact:true}).waitFor();
  await page.getByText('Awaiting explicit operator observation of the exact deployed version.',{exact:true}).waitFor();
  await page.getByRole('button',{name:'Refresh',exact:true}).click();
  await page.waitForFunction(() => !document.querySelector('.page-header__meta')?.textContent.includes('updating'));
  assert.equal(await acceptanceToggle.getAttribute('aria-expanded'), 'true', 'Refresh keeps disclosure open');
  await acceptanceToggle.focus();
  await page.keyboard.press('Space');
  await page.locator('.sprint-acceptance__details').waitFor({state:'hidden'});
  assert.equal(await acceptanceToggle.getAttribute('aria-expanded'), 'false');
  if (fixtureApi) {
    await page.request.put(`${fixtureApi}/__fixtures`, {data:{
      '/api/completions':{items:[{...completion.receipt,sprintName:sprint.name}],nextCursor:null},
      '/api/sprints/board':{projectId,generatedAt:new Date().toISOString(),sprint,total:5,done:1,members,nextCursor:null},
    }});
    await page.getByRole('button',{name:'Refresh',exact:true}).click();
    await page.locator('summary').filter({hasText:'Outstanding sprint outcomes'}).click();
    await page.getByRole('button',{name:'Review '+sprint.name,exact:true}).click();
    await page.locator('.sprint-acceptance__details').waitFor({state:'visible'});
    assert.equal(await acceptanceToggle.getAttribute('aria-expanded'),'true',
      'Reviewing an outstanding outcome opens its acceptance evidence');
    await acceptanceToggle.click();
    await page.request.put(`${fixtureApi}/__fixtures`, {data:{'/api/completions':{items:[],nextCursor:null}}});
    await page.locator('summary').filter({hasText:'Outstanding sprint outcomes'}).click();
  }
  await page.setViewportSize({width:1280,height:900});
  assert.equal(await page.getByRole('heading',{name:'Goal',exact:true}).count(),0);
  if(screenshotDir) await capture(page,{path:`${screenshotDir}/board-acceptance-1280.png`,fullPage:true});
  await page.locator('.sprint-acceptance').scrollIntoViewIfNeeded();
  await acceptanceToggle.click();
  if(screenshotDir) await capture(page,{path:`${screenshotDir}/board-criteria-1280.png`,fullPage:true});
  await acceptanceToggle.click();
  const lanes=page.locator('.sprint-lane');
  assert.equal(await lanes.count(),5);
  for(let i=0;i<5;i++) {
    assert.ok(await lanes.nth(i).isVisible());
    const bounds=await lanes.nth(i).boundingBox();
    assert.ok(bounds.x>=0 && bounds.x+bounds.width<=1280,`Lane ${i} fits viewport`);
    const card=await lanes.nth(i).locator('.sprint-task').boundingBox();
    assert.ok(card.x>=bounds.x && card.x+card.width<=bounds.x+bounds.width+1,'Card fits lane');
  }
  assert.ok(await page.locator('.app-nav').isVisible(),'Desktop sidebar is open during bounds check');
  await verifyActiveSprintLabel(page, 1280);
  await lanes.first().scrollIntoViewIfNeeded();
  const tops=await lanes.evaluateAll(nodes=>nodes.map(n=>n.getBoundingClientRect().top));
  assert.ok(Math.max(...tops)-Math.min(...tops)<2,'All desktop lanes share a row');
  if(screenshotDir) await capture(page,{path:`${screenshotDir}/board-1280.png`,fullPage:true});
  const running=page.locator('.sprint-task').filter({hasText:'Task 2:'});
  await running.locator('summary').click();
  await running.getByRole('button',{name:nominatedHuman?'Require review by @'+nominatedHuman:'Require human review',exact:true}).click();
  await page.getByRole('status').filter({hasText:'Human review required'}).waitFor();
  if(fixtureApi) {
    const writes=await (await page.request.get(`${fixtureApi}/__writes`)).json();
    holdWrites=writes.slice(previousWrites).filter(w=>w.path==='/api/tasks/fixture-1/human-review' && w.body.required===true).length;
  }
  assert.equal(holdWrites,1);
  assert.ok(await page.getByRole('region',{name:'Running',exact:true}).getByText('Task 2:',{exact:false}).isVisible());
  await page.setViewportSize({width:390,height:844});
  await verifyActiveSprintLabel(page, 390);
  const picker=page.getByLabel('Task lane');
  for(const lane of ['Queued','Running','Review','Needs you','Completed / closed']) {
    await picker.selectOption({label:`${lane} (1)`});
    await page.getByRole('region',{name:lane,exact:true}).waitFor({state:'visible'});
    assert.equal(await page.locator('.sprint-lane:visible').count(),1);
    const visibleCard=page.getByRole('region',{name:lane,exact:true}).locator('.sprint-task');
    await visibleCard.waitFor({state:'visible'});
    await visibleCard.scrollIntoViewIfNeeded();
    const card=await visibleCard.boundingBox();
    assert.ok(card.x>=0 && card.x+card.width<=390,'Phone card fits');
    if(screenshotDir) await capture(page,{path:`${screenshotDir}/board-390-${lane.replace(/[^a-z]/gi,'-')}.png`,fullPage:true});
  }
  // Delay restoration interop to expose background refresh overwriting saved state.
  const scrollBeforeReload = await page.locator('.app-main').evaluate(e => e.scrollTop);
  await page.waitForFunction(expected => {
    const saved = JSON.parse(sessionStorage.getItem('forge:sprint-board:forge') || 'null');
    return saved?.lane === 'Completed / closed' && Math.abs(saved.scroll - expected) < 2;
  }, scrollBeforeReload);
  const delayRestore = async route => {
    const response = await route.fetch();
    const original = await response.text();
    assert.ok(original.includes('export function begin(p) {'), 'Delay targets actual restoration entry point');
    await route.fulfill({response, body:original.replace('export function begin(p) {',
      'export async function begin(p) { await new Promise(resolve => setTimeout(resolve, 500));')});
  };
  await page.route('**/sprint-board.js', delayRestore);
  try {
    await page.reload();
    await picker.waitFor();
    await page.waitForFunction(() => document.querySelector('#sprint-lane')?.value === 'Completed / closed');
    assert.equal(await picker.inputValue(),'Completed / closed');
    assert.equal(await page.locator('.sprint-lane:visible').count(),1);
    assert.equal(await page.locator('.sprint-lane:visible .sprint-task__status').innerText(),'Merged');
    await page.waitForFunction(expected => {
      const main = document.querySelector('.app-main');
      return main && Math.abs(main.scrollTop - Math.min(expected, main.scrollHeight - main.clientHeight)) < 2;
    }, scrollBeforeReload);
    const saved = await page.evaluate(() => JSON.parse(sessionStorage.getItem('forge:sprint-board:forge')));
    assert.equal(saved.lane,'Completed / closed');
    assert.ok(Math.abs(saved.scroll - scrollBeforeReload) < 2, 'Reload preserves saved inner scroll');
  } finally { await page.unroute('**/sprint-board.js', delayRestore); }
}
