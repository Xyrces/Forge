import { capture } from './capture.mjs';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { createRequire } from 'node:module';
import { verifySprintBoard } from './sprint-board.spec.mjs';
const require = createRequire(process.env.FORGE_BROWSER_NODE_PACKAGE || import.meta.url);
let chromium;
try { ({ chromium } = require('playwright')); }
catch { throw new Error('Install playwright in this checkout or set FORGE_BROWSER_NODE_PACKAGE to an external package.json. See scripts/browser/README.md.'); }
const output = process.argv[2];
if (!output) throw new Error('Pass an isolated Linux receipt directory');
await fs.mkdir(output,{recursive:true});
const base='http://127.0.0.1:5123', api='http://127.0.0.1:5124';
const actualTask=JSON.parse(await fs.readFile(new URL('./fixtures/pr150-task.json',import.meta.url),'utf8'));
const historical=JSON.parse(await fs.readFile(new URL('./fixtures/pr150-history.json',import.meta.url),'utf8'));
const browser=await chromium.launch({executablePath:process.env.FORGE_BROWSER_EXECUTABLE || undefined,headless:true,args:['--no-sandbox']});
const context=await browser.newContext({viewport:{width:1280,height:900},timezoneId:'America/New_York'});
const page=await context.newPage();
const errors=[]; page.on('pageerror',e=>errors.push(e.message));
const requests=[]; page.on('requestfailed',r=>requests.push({url:r.url(),error:r.failure()?.errorText}));
const put=async fixtures=>{const r=await page.request.put(api+'/__fixtures',{data:fixtures});assert.ok(r.ok());};
const now='2026-11-18T02:20:00Z';
const common={
 '/api/projects':[{id:'forge',name:'Forge · offline rehearsal',repoUrl:'https://github.com/Xyrces/Forge.git'}],
 '/api/meta/buildinfo':{informationalVersion:'Offline UI rehearsal',framework:'.NET 10'},
 '/api/health/heartbeat':{status:'healthy',at:now}, '/api/sprints/active':{id:'ux-fixture',name:'Qualify the deployed Forge sprint board and protected pull request workflow across desktop and mobile'},
 '/api/state':{tasks:[],agents:[],skills:[],sprints:[]}, '/api/now':{items:[]},
 '/api/gates':{design:'open',groom:'open',sprint:'open',merge:'open',deploy:'open'},
 '/api/sprints/building':{}, '/api/queue':{ready:[],blocked:[]},
 '/api/sprints/ux-fixture/followup-drafts':[], '/api/sprints/outstanding-completions':{items:[],nextCursor:null}
};
const receipt={kind:'offline actual Blazor components; fixture API only; no live database, scheduler or paid models',checks:[],errors,requests};
try {
 await put(common);
 if (!process.argv.includes('--tasks-only')) {
  await verifySprintBoard(page,base,{fixtureApi:api,screenshotDir:output});
  receipt.checks.push('1280 sidebar + all five lane/card bounds; 390 all five lanes; mid-work hold; refreshed lane persistence');
 }
 if (!process.argv.includes('--retry-only')) {
 const binding=JSON.parse(actualTask.metadata.sprintOutcome), publication=JSON.parse(actualTask.metadata.trustedPublication), merge=JSON.parse(actualTask.metadata.observedMerge);
 const task={...actualTask,delivery:{criteria:binding.criteria.filter(c=>binding.criterionIds.includes(c.id)).map(c=>({text:c.text,evidenceKind:c.evidenceKind})),verifiedHead:publication.PublishedHeadSha,verifiedAt:publication.Verification.CompletedAt,mergeCommit:merge.MergeSha,mergeObservedAt:merge.ObservedAt},situation:{label:'Merged',text:'Merge observed. Release and acceptance are tracked with the sprint.',nextActor:null,humanReviewRequired:false},events:historical.reviews.map(r=>({kind:'review',at:r.submittedAt,actor:`PR comment by ${r.reviewer}`,detail:r.summary}))};
 await put({
  '/api/tasks/client-2':task,
  '/api/tasks/story-3':{id:'story-3',title:'Synthetic navigation target',status:'Pending',metadata:{},situation:{label:'Queued',text:'Synthetic parent fixture'},events:[]},
  '/api/tasks/story-3/human-review':{required:false,syncPending:false,mode:'Automated'},
  '/api/tasks/client-2/state':{state:'Merged',waitingOn:'done',strikes:1,maxStrikes:3},
  '/api/tasks/client-2/human-review':{required:false,syncPending:false,mode:'Automated'},
  '/api/tasks/client-2/history':{events:[...task.events].reverse(),nextCursor:null},
  '/api/tasks/client-2/workers':{available:true,attempts:[]},
  '/api/tasks/client-2/review-history':historical,
  '/api/agent-runs':{active:[],recent:[]}
 });
 for(const width of [1280,390]) {
  await page.setViewportSize({width,height:900});
  const readsBefore=(await (await page.request.get(api+'/__reads')).json()).length;
  await page.goto(base+'/tasks/client-2?project=forge');
  await page.getByRole('heading',{name:task.title,exact:true}).waitFor();
  // Oversized history intentionally reloads after circuit start. Establish interactive
  // section navigation before asserting the settled overview, not its prerender copy.
  await page.getByRole('button',{name:'Plan & checks',exact:true}).click();
  await page.waitForURL(url=>url.searchParams.get('section')==='Plan & checks');
  await page.getByRole('button',{name:'Overview',exact:true}).click();
  await page.waitForURL(url=>url.searchParams.get('section')==='Overview');
  await page.getByText('Merge observed.',{exact:false}).waitFor();
  assert.equal(await page.getByText('1/3 strikes',{exact:false}).count(),0);
  assert.equal(await page.getByRole('link',{name:'Open PR #150 ↗'}).getAttribute('href'),'https://github.com/Xyrces/Forge/pull/150');
  await page.getByRole('heading',{name:'What this task contributes',exact:true}).waitFor();
  assert.equal(await page.locator('.task-criteria li').count(),binding.criterionIds.length);
  assert.equal(await page.getByRole('link',{name:publication.PublishedHeadSha.slice(0,7),exact:true}).getAttribute('href'),'https://github.com/Xyrces/Forge/commit/'+publication.PublishedHeadSha);
  assert.equal(await page.getByRole('link',{name:merge.MergeSha.slice(0,7),exact:true}).getAttribute('href'),'https://github.com/Xyrces/Forge/commit/'+merge.MergeSha);
  await capture(page,{path:`${output}/task-overview-${width}.png`,fullPage:true});
  const overviewReads=(await (await page.request.get(api+'/__reads')).json()).slice(readsBefore);
  assert.equal(overviewReads.filter(url=>url.startsWith('/api/agent-runs')||/\/(history|workers|state|review-history)\?/.test(url)).length,0,'Overview must not fetch activity/history/review sources');
  const taskReads=overviewReads.filter(url=>url.startsWith('/api/tasks/client-2?'));
  assert.ok(taskReads.every(url=>url.includes('includeEvents=false')));
  assert.equal(taskReads.length,2,'Large historical task uses one prerender read and one interactive read instead of an oversized circuit bootstrap');
  (receipt.overviewReads??=[]).push({width,requests:overviewReads});
  await page.locator('.task-criteria').scrollIntoViewIfNeeded();
  await capture(page,{path:`${output}/task-criteria-${width}.png`,fullPage:true});


  await page.getByRole('button',{name:'Review',exact:true}).click();
  await page.getByLabel('Review decision').selectOption('1');
  await page.getByText('Changes requested',{exact:true}).waitFor();
  assert.equal(await page.getByRole('link',{name:'Original PR comment ↗'}).getAttribute('href'),historical.reviews[1].url);
  assert.ok(await page.locator('pre').filter({hasText:'IWorkflowDispatcher? dispatcher = null'}).count());
  await page.locator('.task-finding').first().scrollIntoViewIfNeeded();
  const findingBounds=await page.locator('.task-finding h3').evaluateAll(nodes=>nodes.map(n=>{const range=document.createRange();range.selectNodeContents(n);return [...range.getClientRects()].map(r=>({left:r.left,right:r.right}));}).flat());
  assert.ok(findingBounds.every(r=>r.left>=0 && r.right<=width),'Finding path text must wrap within viewport');
  await capture(page,{path:`${output}/review-reported-${width}.png`,fullPage:true});
  await page.getByRole('button',{name:'Later reviewed revision',exact:true}).first().click();
  await page.locator('a[href*="263bbfe"][href*="#L"]').first().waitFor();
  assert.ok((await page.getByRole('link',{name:'Open verified source ↗'}).first().getAttribute('href')).includes('263bbfe'));
  await page.locator('.task-finding').first().scrollIntoViewIfNeeded();
  await capture(page,{path:`${output}/review-later-${width}.png`,fullPage:true});
  await page.getByLabel('Review decision').selectOption('0');
  await page.getByText('Approved',{exact:true}).waitFor();
  assert.equal(await page.getByRole('link',{name:'Original PR comment ↗'}).getAttribute('href'),historical.reviews[2].url);
  await page.getByRole('button',{name:'Plan & checks',exact:true}).click();
  await page.getByText('Allowed with warning',{exact:true}).waitFor();
  assert.equal(await page.getByText('Passed',{exact:true}).count(),2);
  await page.locator('.task-checks').first().scrollIntoViewIfNeeded();
  await capture(page,{path:`${output}/plan-${width}.png`,fullPage:true});
  await page.getByRole('button',{name:'History & runs',exact:true}).click();
  await page.getByRole('heading',{name:'Task timeline'}).waitFor();
  await page.locator('.task-timeline>li').nth(2).waitFor();
  assert.equal(await page.locator('.task-timeline>li').count(),3);
  const historyReads=(await (await page.request.get(api+'/__reads')).json()).slice(readsBefore);
  assert.equal(historyReads.filter(url=>url.startsWith('/api/tasks/client-2/history?')).length,1,'Timeline loads exactly once when opened');
  assert.equal(historyReads.filter(url=>url.startsWith('/api/agent-runs?')&&!url.includes('taskId=client-2')).length,0,'Run query is task-scoped');

  await page.locator('.task-timeline').first().scrollIntoViewIfNeeded();
  await capture(page,{path:`${output}/timeline-${width}.png`,fullPage:true});
  const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>window.innerWidth);
  assert.equal(overflow,false,`Task ${width} document fits`);
  await page.getByRole('button',{name:'Overview',exact:true}).click();
  await page.getByRole('link',{name:'Open parent work',exact:true}).click();
  await page.waitForURL('**/tasks/story-3?project=forge');
  await page.getByRole('heading',{name:'Synthetic navigation target',exact:true}).waitFor();
  await page.getByText('Queued',{exact:true}).waitFor();
  assert.equal(await page.locator('.task-criteria li').count(),0,'Navigation clears prior delivery evidence');
  assert.equal(await page.getByRole('heading',{name:'Task timeline',exact:true}).count(),0,'Navigation returns to overview');

 }
 receipt.checks.push('Overview performs no activity or legacy history reads; history loaded once and task-scoped; task navigation clears prior delivery evidence');
 receipt.checks.push('PR150 original 3 comments, exact SHA excerpts, later-revision toggle, honest plan warning, timeline and main link at 1280/390');
 const synthetic={...task,delivery:null,id:'synthetic-rework',title:'Synthetic fixture: revise a review finding',status:'InProgress',metadata:{humanReviewWaitSeconds:'125',humanReviewWaitStartedAt:'2026-11-18T02:00:00Z',humanReviewWaitEndedAt:'2026-11-18T02:02:05Z',reviewGateError:'Synthetic policy synchronization interruption',reviewIntegrationError:'Synthetic publication retry awaiting reconciliation'},situation:{label:'Making a requested change',text:'Worker is revising the same PR.',nextActor:'UI developer'},events:[]};
 const run={id:'synthetic-run',taskId:synthetic.id,taskTitle:synthetic.title,role:'clientdev',projectId:'forge',scopeProjectId:'forge',status:'succeeded',startedAt:'2026-11-18T02:03:00Z',finishedAt:'2026-11-18T02:04:00Z',durationMs:60000,messageCount:4,toolCallCount:2,hasTranscript:false};
 await put({
  '/api/tasks/synthetic-rework':synthetic,
  '/api/tasks/synthetic-rework/state':{state:'ReworkRunning',waitingOn:'worker',strikes:1,maxStrikes:3},
  '/api/tasks/synthetic-rework/human-review':{required:false,syncPending:false,mode:'Automated'},
  '/api/tasks/synthetic-rework/history':{events:[
   {at:now,kind:'review',actor:'Synthetic reviewer',detail:JSON.stringify({verdict:'RequestChanges',summary:'Keep the correction within scope'})},
   {at:'2026-11-18T01:58:59Z',kind:'sprint_activated',actor:'orchestrator',detail:'sprint-fixture-history'}
  ],nextCursor:null},
  '/api/tasks/synthetic-rework/workers': {
   available: true,
   attempts: [
    {
     id: 'synthetic-attempt', role: 'clientdev', state: 'Succeeded', createdAt: now, updatedAt: now,
     outcome: 'Worker completed; delivery tracked separately', verificationRecorded: true,
     phases: {
      eligibleAt: '2026-11-18T02:02:30Z', workerStartedAt: '2026-11-18T02:03:00Z',
      modelStartedAt: '2026-11-18T02:03:00Z', modelCompletedAt: '2026-11-18T02:04:00Z',
      verificationStartedAt: '2026-11-18T02:04:00Z', verificationCompletedAt: '2026-11-18T02:04:20Z',
      queueWakeup: {cause: 'task-status-returned-to-pending', eventAt: '2026-11-18T02:02:29.750Z', observedAdmissionAt: '2026-11-18T02:02:30Z'}
     }
    },
    {
     id: 'synthetic-timeout', role: 'clientdev', state: 'Failed', createdAt: now, updatedAt: now,
     outcome: 'Worker failed', verificationRecorded: false,
     phases: {
      modelStartedAt: '2026-11-18T02:00:00Z', modelCompletedAt: '2026-11-18T02:00:17Z', modelOutcome: 'timed-out'
     }
    },
    {
     id: 'synthetic-partial', role: 'clientdev', state: 'Failed', createdAt: now, updatedAt: now,
     outcome: 'Worker interrupted', verificationRecorded: false,
     phases: {modelStartedAt: '2026-11-18T01:59:00Z', modelCompletedAt: null, modelOutcome: 'running'}
    }
   ]
  },
  '/api/agent-runs':{active:[],recent:[run]}, '/api/agent-runs/synthetic-run':{view:run,transcript:[]}
 });
 for(const width of [1280,390]) {
  await page.setViewportSize({width,height:900});
  await page.goto(base+'/tasks/synthetic-rework?project=forge');
  await page.getByText('Making a requested change',{exact:true}).waitFor();
  await page.getByText('Review gate unavailable.',{exact:true}).waitFor();
  await page.getByText('Review publication needs attention.',{exact:true}).waitFor();
  await page.getByRole('button',{name:'History & runs',exact:true}).click();
  const completedAttempt = page.locator('.task-run-list article').filter({hasText: 'Worker completed; delivery tracked separately'});
  await completedAttempt.getByText('Dispatch admission to worker start', {exact: true}).waitFor();
  await completedAttempt.getByText('Requeue to dispatch admission', {exact: true}).waitFor();
  assert.match(await completedAttempt.innerText(), /250 ms/);
  const timedOut = page.locator('.task-run-list article').filter({hasText: 'Worker failed'});
  await timedOut.getByText('Timed out after 17s', {exact: true}).waitFor();
  await timedOut.getByText('Start time not recorded', {exact: true}).waitFor();
  const interrupted = page.locator('.task-run-list article').filter({hasText: 'Worker interrupted'});
  await interrupted.getByText('Started · finish time not recorded', {exact: true}).waitFor();
  const activation = page.locator('.task-timeline li').filter({hasText: 'Sprint activated'});
  const sprint = activation.getByRole('link', {name: 'Open sprint', exact: true});
  assert.equal(await sprint.getAttribute('href'), '/sprints?project=forge&sprint=sprint-fixture-history');
  await page.getByText('125s',{exact:false}).waitFor();
  await page.locator('.task-run-list').first().scrollIntoViewIfNeeded();
  await capture(page,{path:`${output}/synthetic-rework-${width}.png`,fullPage:true});
  await page.getByRole('link',{name:'UI developer',exact:true}).click();
  await page.waitForURL('**/runs/synthetic-run?project=forge');
  await page.getByText('No transcript recorded.',{exact:true}).waitFor();
  await page.getByRole('heading',{name:synthetic.title,exact:true}).waitFor();
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>window.innerWidth),false,`Run ${width} document fits`);
  await capture(page,{path:`${output}/synthetic-run-${width}.png`,fullPage:true});
 }
 synthetic.status='Completed';synthetic.situation={label:'Completion recorded',text:'No merge receipt recorded for this fixture.'};synthetic.metadata={};
 await put({'/api/tasks/synthetic-rework':synthetic});
 await page.goto(base+'/tasks/synthetic-rework?project=forge');
 await page.getByText('Completion recorded',{exact:true}).waitFor();
 assert.equal(await page.getByText('Merged',{exact:true}).count(),0);
 synthetic.delivery={criteria:[{text:'Synthetic independently verified behavior',evidenceKind:'ImplementationVerified'}],existingSource:'c'.repeat(40),existingVerifiedAt:now,existingReviewedAt:now};
 synthetic.situation={label:'Existing implementation verified',text:'Independent verification and review established the approved behavior at a recorded revision.'};
 synthetic.metadata={lastError:'cancelled earlier worker'};
 await put({'/api/tasks/synthetic-rework':synthetic});
 for(const width of [1280,390]) {
  await page.setViewportSize({width,height:900});
  await page.goto(base+'/tasks/synthetic-rework?project=forge');
  await page.getByRole('heading',{name:'Existing implementation verified',exact:true}).waitFor();
  await page.getByText('Independent review',{exact:true}).waitFor();
  assert.equal(await page.getByText('Merged revision',{exact:true}).count(),0);
  await capture(page,{path:`${output}/synthetic-existing-${width}.png`,fullPage:true});
 }
 receipt.checks.push('Separate synthetic rework and completion states; measured worker/model/verification/human wait, independent integration errors, linked run detail at 1280/390');
 }
 // Explicitly synthetic recovery fixture. The real endpoint/state transition has separate backend tests.
 const retryId = 'synthetic-review-retry';
 const retryPath = '/api/tasks/' + retryId;
 const retryRoute = retryPath + '/requeue?projectId=forge';
 for (const width of [1280,390]) {
  const preserved = {reworkAttempts:2,reviewDraft:'synthetic saved draft',reviewPublicationIntent:'synthetic pending intent',humanReviewTaskHold:true,branch:'synthetic-retained-branch'};
  await put({
   [retryPath]:{id:retryId,title:'Synthetic review integration recovery',status:'Blocked',metadata:{...preserved,blockedKind:'review-integration',reviewIntegrationAttempts:3,reviewIntegrationError:'Review connection needs attention.'},situation:{label:'Review needs attention',text:'Fix the connection, then retry review.',nextActor:'You'},events:[]},
   [retryPath+'/state']:{state:'Blocked',strikes:2,maxStrikes:3},
   [retryPath+'/human-review']:{required:true,syncPending:false,mode:'Both'},
   [retryRoute]:{status:503,body:{error:'Synthetic temporary review service outage'}}
  });
  await page.setViewportSize({width,height:900});
  await page.goto(base+'/tasks/'+retryId+'?project=forge');
  await page.getByRole('heading',{name:'Synthetic review integration recovery',exact:true}).waitFor();
  await page.locator('.task-actions > summary').click();
  const writesBefore = (await (await page.request.get(api+'/__writes')).json()).length;
  await page.getByRole('button',{name:'Retry review',exact:true}).click();
  await page.getByText('Retry failed. Synthetic temporary review service outage',{exact:true}).waitFor();
  await put({[retryRoute]:{status:200,body:{}}});
  await page.getByRole('button',{name:'Retry review',exact:true}).click();
  await page.getByText('Review reconciliation queued; engineering work retained.',{exact:true}).waitFor();
  assert.equal(await page.getByRole('button',{name:'Retry review',exact:true}).count(),0);
  const writes = (await (await page.request.get(api+'/__writes')).json()).slice(writesBefore);
  assert.deepEqual(writes,[{method:'POST',path:retryRoute},{method:'POST',path:retryRoute}]);
  const saved = await (await page.request.get(api+retryPath)).json();
  assert.equal(saved.status,'InProgress');
  for(const [key,value] of Object.entries(preserved)) assert.equal(saved.metadata[key],value);
  assert.equal((await (await page.request.get(api+retryPath+'/state')).json()).strikes,2);
  await capture(page,{path:`${output}/synthetic-review-retry-${width}.png`,fullPage:true});
 }
 receipt.checks.push('Synthetic Retry review at 1280/390: failed action stays visible; project-scoped requeue resumes review with retained strikes/draft/intent/hold/branch, never reset-strikes');
 assert.deepEqual(errors,[]);
 receipt.result='passed';
} catch(error) {
 receipt.result='failed';receipt.failure=String(error.stack||error);
 await capture(page,{path:`${output}/failure.png`,fullPage:true}).catch(()=>{});
 await fs.writeFile(`${output}/failure.html`,await page.content()).catch(()=>{});
 process.exitCode=1;
} finally {
 await fs.writeFile(`${output}/receipt.json`,JSON.stringify(receipt,null,2));
 await browser.close();
 console.log(JSON.stringify(receipt,null,2));
}
