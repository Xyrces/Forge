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
  const sprint = { id:'ux-fixture', name:'Review the dashboard change', goal:'## Goal\nKeep production clean.\n\n- Preserve development tooling.', status:'Active', startDate:'2026-11-17T00:00:00Z' };
  const states = ['Pending','AgentRunning','PROpen','PROpen','Merged'];
  const members = states.map((state,i) => ({ id:`fixture-${i}`, title:`Task ${i+1}: verify a long title remains readable inside its lane without clipping`, type:'ui', status:i===4?'Completed':i===0?'Pending':'InProgress', state, priority:2, assignee:i===1?'worker':null, prNumber:null, reworkAttempts:i===4?'2':null, boosted:false, blockerCount:0, blockedBy:[], situation:i===1?'Responding to a recorded review finding.':'Fixture evidence', situationTone:'muted', label:['Ready for work','Making a requested change','Review in progress','Waiting for human review','Merged'][i], nextActor:i===3?(nominatedHuman?'@'+nominatedHuman:'Human reviewer'):'Forge', nominatedHuman, humanReviewTaskHold:i===3, humanReviewRequired:i===3, awaitingHumanReview:i===3 }));
  const completion={receipt:{id:'synthetic-completion',projectId,sprintId:sprint.id,outcome:'PendingEvidence',bookkeepingState:'Completed',readyForDeployment:false,deploymentStatus:'Pending',deliveryNote:'Synthetic active-sprint fixture: one change merged; deployment and browser observation remain pending.'},batches:[],criteria:[{id:'code',text:'Keep production free of development-tool requests',evidenceKind:'MergedAndVerified'},{id:'browser',text:'Confirm the deployed dashboard opens without missing-script requests',evidenceKind:'OperatorObserved',proofReference:null}],nextCursor:null};
  let holdWrites = 0;
  const previousWrites = fixtureApi ? (await (await page.request.get(`${fixtureApi}/__writes`)).json()).length : 0;
  if (fixtureApi) {
    await page.request.put(`${fixtureApi}/__fixtures`, {data:{
      '/api/sprints/summary':{projectId,generatedAt:new Date().toISOString(),active:sprint,total:1,history:[],nextCursor:null},
      '/api/sprints/board':{projectId,generatedAt:new Date().toISOString(),sprint,total:5,done:1,members,nextCursor:null},
      '/api/sprints/ux-fixture/completion':completion,
      '/api/tasks/fixture-1/human-review':{required:false,syncPending:false,mode:'Automated'}
    }});
  } else {
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
  await page.setViewportSize({width:1280,height:900});
  await page.goto(`${baseUrl}/sprints`);
  await page.locator('.sprint-task').first().waitFor();
  await page.getByText('Awaiting required evidence',{exact:true}).waitFor();
  await page.getByRole('heading',{name:'Keep production free of development-tool requests',exact:true}).waitFor();
  await page.getByText('Awaiting explicit operator observation of the exact deployed version.',{exact:true}).waitFor();
  assert.equal(await page.getByRole('heading',{name:'Goal',exact:true}).count(),0);
  if(screenshotDir) await capture(page,{path:`${screenshotDir}/board-acceptance-1280.png`,fullPage:true});
  await page.locator('.sprint-acceptance').scrollIntoViewIfNeeded();
  if(screenshotDir) await capture(page,{path:`${screenshotDir}/board-criteria-1280.png`,fullPage:true});
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
  await page.reload();
  await picker.waitFor();
  assert.equal(await picker.inputValue(),'Completed / closed');
  assert.equal(await page.locator('.sprint-lane:visible .sprint-task__status').innerText(),'Merged');
}
