import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {createRequire} from 'node:module';
import {capture} from './capture.mjs';

const require = createRequire(process.env.FORGE_BROWSER_NODE_PACKAGE || import.meta.url);
const {chromium} = require('playwright');
const output = process.argv[2];
if (!output) throw new Error('Pass an isolated browser receipt directory');
await fs.mkdir(output, {recursive:true});
const browser = await chromium.launch({headless:true,
  executablePath:process.env.FORGE_BROWSER_EXECUTABLE, args:['--no-sandbox']});
const page = await browser.newPage();
const base = 'http://127.0.0.1:5123', api = 'http://127.0.0.1:5124';
const receipt = {scope:'Synthetic merged and active review presentation; no production state', checks:[], errors:[]};
page.on('pageerror', error => receipt.errors.push(error.message));
const at = '2026-11-19T12:50:00Z';
const approved = 'Approved by @Xyrces. No further review action.';
const activeRequirement = 'Review by @Xyrces required before merge.';
const sprint = {id:'review-state-fixture', name:'Review completed and active work',
  goal:'Show the review decision appropriate to each task.', status:'Active', startDate:at};
const cases = [
  {id:'merged-human', title:'Merged with human approval', status:'Completed', state:'Merged', summary:approved},
  {id:'merged-legacy', title:'Merged with incomplete historical evidence', status:'Completed', state:'Merged', summary:null},
  {id:'active-human', title:'Active work awaiting nominated reviewer', status:'InProgress', state:'PROpen', summary:null},
];
const members = cases.map((item, index) => ({...item, type:'ui', priority:2, prNumber:String(159+index),
  blockedBy:[], blockerCount:0, label:index===2?'Waiting for human review':'Merged',
  situation:index===2?'Review the current revision.':'Merge observed. Release and acceptance are tracked with the sprint.',
  nextActor:index===2?'@Xyrces':null, humanReviewRequired:true, humanReviewTaskHold:true,
  nominatedHuman:'Xyrces', humanReviewSummary:item.summary,
  // Old metadata can retain this value after merge. Terminal cards must ignore it.
  awaitingHumanReview:true,
}));
const tasks = cases.map((item, index) => ({...item, type:'ui', createdAt:at, updatedAt:at,
  metadata:{humanReviewRequired:true, humanReviewTaskHold:true, awaitingHumanReview:true,
    humanReviewSyncPending:true, prNumber:String(159+index), state:item.state},
  situation:{label:members[index].label, text:members[index].situation, nextActor:members[index].nextActor,
    humanReviewRequired:true, nominatedHuman:'Xyrces', humanReviewSummary:item.summary},
  sprint:{id:sprint.id,name:sprint.name}, events:[],
}));
const fixture = {
  '/api/projects':[{id:'forge',name:'Isolated review presentation',repoUrl:'https://github.com/Xyrces/Forge.git'}],
  '/api/meta/buildinfo':{}, '/api/health/heartbeat':{status:'healthy',at},
  '/api/state':{tasks:[],agents:[],skills:[],sprints:[]}, '/api/now':{items:[]},
  '/api/gates':{}, '/api/sprints/active':sprint, '/api/sprints/building':{},
  '/api/queue':{ready:[],blocked:[]}, '/api/completions':{items:[],nextCursor:null},
  '/api/sprints/summary':{projectId:'forge',generatedAt:at,active:sprint,total:1,history:[],nextCursor:null},
  '/api/sprints/board':{projectId:'forge',generatedAt:at,sprint,total:3,done:2,members,nextCursor:null},
  '/api/sprints/review-state-fixture/followup-drafts':[],
  '/api/sprints/review-state-fixture/completion':{status:404,body:{}},
  '/api/tasks/list?view=active&search=&projectId=forge':{items:[tasks[2]],total:1,nextCursor:null},
  '/api/tasks/list?view=history&search=&projectId=forge':{items:tasks.slice(0,2),total:2,nextCursor:null},
};
for (const task of tasks) {
  fixture['/api/tasks/'+task.id] = task;
  fixture['/api/tasks/'+task.id+'/human-review'] = {required:true,syncPending:false,mode:'Both',nominatedHuman:'Xyrces'};
  fixture['/api/tasks/'+task.id+'/history'] = {events:[],nextCursor:null};
  fixture['/api/tasks/'+task.id+'/workers'] = {available:true,attempts:[]};
  fixture['/api/tasks/'+task.id+'/state'] = {};
}

async function verifyTerminal(scope, expected) {
  await scope.getByText(expected,{exact:true}).waitFor();
  assert.equal(await scope.getByText(/required before merge|Synchronizing with GitHub/).count(),0);
  assert.equal(await scope.getByRole('button',{name:/Require.*review|Release.*hold/}).count(),0);
}

try {
  assert.ok((await page.request.put(api+'/__fixtures',{data:fixture})).ok());
  const initialWrites = await (await page.request.get(api+'/__writes')).json();
  for (const width of [1280,390]) {
    await page.setViewportSize({width,height:900});
    await page.goto(base+'/sprints?project=forge&sprint='+sprint.id);
    await page.locator('.sprint-task').first().waitFor();
    if(width===390) await page.getByLabel('Task lane').selectOption('Completed / closed');
    const merged = page.locator('.sprint-task').filter({hasText:cases[0].title});
    await verifyTerminal(merged,approved);
    assert.equal(await merged.getByRole('link',{name:'Open PR #159 ↗',exact:true}).count(),1);
    assert.equal(await merged.getByRole('link',{name:'Review PR #159 ↗',exact:true}).count(),0);
    assert.equal(await merged.locator('.sprint-task__actions').count(),0);
    await verifyTerminal(page.locator('.sprint-task').filter({hasText:cases[1].title}),'No further review action.');
    if(width===390) await page.getByLabel('Task lane').selectOption('Needs you');
    const active = page.locator('.sprint-task').filter({hasText:cases[2].title});
    await active.getByText(activeRequirement,{exact:true}).waitFor();
    await active.locator('summary').click();
    assert.ok(await active.getByRole('button',{name:'Release additional review hold',exact:true}).isEnabled());
    assert.equal(await active.getByRole('link',{name:'Review PR #161 ↗',exact:true}).count(),1);
    receipt.checks.push({width,surface:'Board',mergedDecision:approved,activeHoldAction:true});

    for (const task of tasks) {
      await page.goto(base+'/tasks/'+task.id+'?project=forge');
      await page.getByRole('heading',{name:task.title,exact:true}).waitFor();
      if(task.status==='Completed') {
        await verifyTerminal(page.locator('.task-detail'),task.summary ?? 'No further review action.');
      } else {
        await page.getByText(activeRequirement+' Synchronizing with GitHub; merge remains held.',{exact:true}).waitFor();
        await page.locator('details.task-actions summary').click();
        assert.ok(await page.getByRole('button',{name:'Release task review hold',exact:true}).isEnabled());
      }
      await capture(page,{path:`${output}/${task.id}-${width}.png`,fullPage:true});
    }
    receipt.checks.push({width,surface:'Task detail',mergedDecision:true,missingHistoryNeutral:true,activeHoldAction:true});
    await page.goto(base+'/tasks?project=forge');
    await page.getByRole('button',{name:'History',exact:true}).click();
    await verifyTerminal(page.locator('.task-run-list article').filter({hasText:cases[0].title}),approved);
    await verifyTerminal(page.locator('.task-run-list article').filter({hasText:cases[1].title}),'No further review action.');
    await page.getByRole('button',{name:'Active work',exact:true}).click();
    await page.locator('.task-run-list').getByText(activeRequirement,{exact:true}).waitFor();
    receipt.checks.push({width,surface:'Task list',mergedDecision:true,missingHistoryNeutral:true,activeRequirement:true});
  }
  assert.deepEqual(await (await page.request.get(api+'/__writes')).json(),initialWrites,'Rendering performs no action writes');
  assert.deepEqual(receipt.errors,[]);
  receipt.result='passed';
} catch(error) {
  receipt.result='failed'; receipt.error=String(error.stack || error); process.exitCode=1;
  await page.screenshot({path:output+'/failure.png'});
} finally {
  await fs.writeFile(output+'/receipt.json',JSON.stringify(receipt,null,2));
  console.log(JSON.stringify(receipt,null,2));
  await browser.close();
}
