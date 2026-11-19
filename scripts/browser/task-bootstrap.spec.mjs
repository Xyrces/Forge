import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {createRequire} from 'node:module';
const {chromium}=createRequire(process.env.FORGE_BROWSER_NODE_PACKAGE||import.meta.url)('playwright');
const output=process.argv[2];if(!output)throw new Error('Pass an isolated Linux proof directory');
await fs.mkdir(output,{recursive:true});
const base=process.env.FORGE_BROWSER_BASE_URL||'http://127.0.0.1:5123';
const api=process.env.FORGE_BROWSER_API_URL||'http://127.0.0.1:5124';
const browser=await chromium.launch({executablePath:process.env.FORGE_BROWSER_EXECUTABLE||undefined,headless:true,args:['--no-sandbox']});
const context=await browser.newContext();const page=await context.newPage();page.setDefaultTimeout(10000);
const errors=[],frames=[];page.on('pageerror',e=>errors.push(e.message));
page.on('websocket',socket=>socket.on('framesent',frame=>frames.push(Buffer.byteLength(frame.payload))));
const now=new Date().toISOString(),id='synthetic-review-retry',taskPath='/api/tasks/'+id;
const evidence='Historical evidence preserved. '.repeat(3000)+'END OF COMPLETE EVIDENCE';
const task={id,title:'Large task bootstrap regression',status:'Blocked',metadata:{blockedKind:'review-integration',reviewIntegrationAttempts:3,reviewIntegrationError:'Synthetic connection failure',reviewNotes:evidence,branch:'retained-branch'},situation:{label:'Review needs attention',text:'Synthetic retry test',nextActor:'You'},events:[]};
const common={
 '/api/projects':[ {id:'forge',name:'Forge',root:'/fixture',repoUrl:'https://github.com/Xyrces/Forge',slots:[],pending:0,inProgress:0,completed:0,failed:0} ],
 '/api/meta/buildinfo':{sourceRevision:'offline-bootstrap-fixture',framework:'.NET 10'},
 '/api/health/heartbeat':{status:'healthy',at:now},'/api/sprints/active':{},'/api/state':{tasks:[],agents:[],skills:[],sprints:[]},'/api/now':{items:[]},
 '/api/gates':{design:'open',groom:'open',sprint:'open',merge:'open',deploy:'open'},'/api/sprints/building':{},'/api/queue':{ready:[],blocked:[]},
 [taskPath]:task,[taskPath+'/human-review']:{required:true,syncPending:false,mode:'Both'},
 [taskPath+'/state']:{state:'Blocked',strikes:2,maxStrikes:3},[taskPath+'/workers']:{available:true,attempts:[]},'/api/agent-runs':{active:[],recent:[]},
 [taskPath+'/history']:{events:[{kind:'operator-note',at:now,actor:'fixture',detail:evidence}],nextCursor:null},
 [taskPath+'/review-history']:{reviews:[],nativeReviews:[{id:1,reviewer:'fixture-reviewer',role:'Human',headSha:'a'.repeat(40),verdict:'Approved',summary:evidence,submittedAt:now,comments:[]}],notes:[],limited:false},
 [taskPath+'/requeue?projectId=forge']:{status:503,body:{error:'Synthetic retry reached the endpoint'}}
};
const receipt={kind:'Offline actual Blazor direct task bootstrap; synthetic state only',checks:[],errors,frames};
try{
 assert.ok((await page.request.put(api+'/__fixtures',{data:common})).ok());
 const compact=structuredClone(task);compact.metadata.reviewNotes='Compact review note';
 assert.ok((await page.request.put(api+'/__fixtures',{data:{[taskPath]:compact}})).ok());
 const compactReadsBefore=(await (await page.request.get(api+'/__reads')).json()).length;
 await page.goto(base+'/tasks/'+id+'?project=forge');
 await page.getByRole('heading',{name:task.title,exact:true}).waitFor();
 await page.getByRole('button',{name:'Plan & checks',exact:true}).click();
 await page.waitForURL(url=>url.searchParams.get('section')==='Plan & checks');
 const compactReads=(await (await page.request.get(api+'/__reads')).json()).slice(compactReadsBefore).filter(url=>url.startsWith(taskPath+'?'));
 assert.equal(compactReads.length,1,'Compact snapshot retains one-read hydration');
 await page.locator('.task-actions > summary').click();
 await page.getByRole('button',{name:'Retry review',exact:true}).click();
 await page.getByText('Retry failed. Synthetic retry reached the endpoint',{exact:true}).waitFor();
 receipt.checks.push({compact:true,taskReads:compactReads.length,retryReachedBackend:true});
 // Search the real encoded bootstrap boundary, including escaping-heavy text.
 for(const [variant,unit] of [['ascii','x'],['escaped','"<>&\u00e9\u{1f680}']]){
  let low=0,high=20000;
  const setSize=async size=>{
   const bounded=structuredClone(task);bounded.metadata.reviewNotes=unit.repeat(size);
   assert.ok((await page.request.put(api+'/__fixtures',{data:{[taskPath]:bounded}})).ok());
   const response=await page.request.get(base+'/tasks/'+id+'?project=forge');
   const state=(await response.text()).match(/<!--Blazor-Server-Component-State:([\s\S]*?)-->/)?.[1];
   // Framework state remains even when the task snapshot is omitted.
   return state?.length>4096?state:undefined;
  };
  assert.equal(await setSize(high),undefined);
  while(low+1<high){const mid=Math.floor((low+high)/2);if(await setSize(mid))low=mid;else high=mid;}
  assert.equal(await setSize(high),undefined,'next larger snapshot must take the bounded fallback');
  const retained=await setSize(low);assert.ok(retained);
  const frameStart=frames.length;
  await page.goto(base+'/tasks/'+id+'?project=forge');
  await page.getByRole('heading',{name:task.title,exact:true}).waitFor();
  await page.locator('.task-actions > summary').click();
  await page.getByRole('button',{name:'Retry review',exact:true}).click();
  await page.getByText('Retry failed. Synthetic retry reached the endpoint',{exact:true}).waitFor();
  const maxFrame=Math.max(...frames.slice(frameStart));assert.ok(maxFrame<32768);
  assert.deepEqual(errors,[]);
  receipt.checks.push({boundary:variant,lastRetainedUnits:low,firstOmittedUnits:high,persistedCharacters:retained.length,maxClientFrame:maxFrame,retryReachedBackend:true});
 }
 assert.ok((await page.request.put(api+'/__fixtures',{data:{[taskPath]:task}})).ok());
 for(const width of [1280,390])for(const section of ['Overview','Review','History & runs']){
  await page.setViewportSize({width,height:900});
  const response=await page.goto(base+'/tasks/'+id+'?project=forge&section='+encodeURIComponent(section));
  const html=await response.text();
  await page.getByRole('heading',{name:task.title,exact:true}).waitFor();
  // A prerendered button is not evidence of an interactive circuit. Its click must
  // reach the fixture backend and its response must render on the current page.
  await page.locator('.task-actions > summary').click();
  const writesBefore=(await (await page.request.get(api+'/__writes')).json()).length;
  await page.getByRole('button',{name:'Retry review',exact:true}).click();
  await page.getByText('Retry failed. Synthetic retry reached the endpoint',{exact:true}).waitFor();
  const writes=(await (await page.request.get(api+'/__writes')).json()).slice(writesBefore);
  assert.deepEqual(writes,[{method:'POST',path:taskPath+'/requeue?projectId=forge'}]);
  for(const tab of ['Review','History & runs']){
   await page.getByRole('button',{name:tab,exact:true}).click();
   await page.getByRole('heading',{name:tab==='Review'?'GitHub reviews and inline discussions':'Task timeline',exact:true}).waitFor();
   await page.waitForFunction(()=>document.querySelector('.task-detail')?.textContent.includes('END OF COMPLETE EVIDENCE'));
   assert.match(await page.getByRole('button',{name:tab,exact:true}).getAttribute('class'),/btn--primary/);
   assert.equal(new URL(page.url()).searchParams.get('section'),tab);
  }
  const marker=html.match(/<!--Blazor-Server-Component-State:([\s\S]*?)-->/)?.[1];
  assert.ok(!marker||marker.length<16000,'bootstrap must stay bounded even for large task/review/history state');
  assert.deepEqual(errors,[]);
  await page.screenshot({path:`${output}/large-task-${width}-${section.replaceAll(/[^a-z]/gi,'-')}.png`});
  receipt.checks.push({width,section,htmlBytes:Buffer.byteLength(html),persistedCharacters:marker?.length||0,retryReachedBackend:true,fullEvidenceRetained:true});
 }
 assert.ok(Math.max(...frames)<32768,'client frames must fit the unchanged default receive limit');
 receipt.result='passed';
}catch(error){receipt.result='failed';receipt.error=String(error);throw error;}
finally{await fs.writeFile(output+'/task-bootstrap.json',JSON.stringify(receipt,null,2));await browser.close();}
