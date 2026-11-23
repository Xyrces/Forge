import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {createRequire} from 'node:module';
import {capture} from './capture.mjs';
const require=createRequire(process.env.FORGE_BROWSER_NODE_PACKAGE||import.meta.url);
const {chromium}=require('playwright');
const output=process.argv[2]; if(!output) throw new Error('Pass an isolated receipt directory');
await fs.mkdir(output,{recursive:true});
const browser=await chromium.launch({executablePath:process.env.FORGE_BROWSER_EXECUTABLE||undefined,headless:true,args:['--no-sandbox']});
const page=await browser.newPage({viewport:{width:2560,height:1274}});
const errors=[];page.on('pageerror',error=>errors.push(error.message));
const base=process.env.FORGE_REHEARSAL_UI_URL||'http://127.0.0.1:5123',api=process.env.FORGE_REHEARSAL_API_URL||'http://127.0.0.1:5124';
assert.ok(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname));
assert.ok(['localhost','127.0.0.1','[::1]'].includes(new URL(api).hostname));
const put=async data=>assert.ok((await page.request.put(api+'/__fixtures',{data})).ok());
const receipt={kind:'Actual Blazor worker environment and dependency controls, synthetic loopback API only',checks:[],errors};
const image='ghcr.io/example/project-worker@sha256:'+'a'.repeat(64);
const source='b'.repeat(40);
const environment={revision:'0',definitionRevision:'0',dockerfilePath:null,buildContext:'.',approvedImage:null,approvedSourceRevision:null,approvedDefinitionRevision:null,status:'stock'};
try {
 const initialWrites=(await (await page.request.get(api+'/__writes')).json()).length;
 await put({'/api/projects':[{id:'forge',name:'Offline worker environment fixture',roles:{},slots:[],defaultRoleCaps:{}}],
  '/api/projects/forge/review-policy':{mode:'Automated'},'/api/projects/forge/environments':{revision:1,sprintEnvironment:null,environments:[]},
  '/api/projects/forge/worker-environment':environment,'/api/now':{items:[]},'/api/state':{tasks:[],agents:[],skills:[],sprints:[]},
  '/api/meta/buildinfo':{},'/api/health/heartbeat':{status:'healthy'}});
 await page.goto(base+'/projects/forge/overview');
 const panel=page.locator('.worker-environment');
 await panel.getByText('Stock image',{exact:true}).waitFor();
 await panel.getByLabel('Dockerfile path',{exact:false}).fill('Dockerfile.worker');
 await panel.getByRole('button',{name:'Save definition',exact:true}).click();
 await panel.getByText('Definition saved. No build was started.',{exact:true}).waitFor();
 await panel.getByText('Build needed',{exact:true}).waitFor();
 assert.match(await panel.locator('pre').innerText(),/--project 'forge'.*--token-file/);
 const saved=(await (await page.request.get(api+'/__writes')).json()).at(-1);
 assert.equal(saved.method,'PUT');assert.equal(saved.body.expectedRevision,'0');assert.equal(saved.body.dockerfilePath,'Dockerfile.worker');
 assert.equal(saved.body.approvedImage,undefined);
 receipt.checks.push('Scoped definition save preserves revision; no image activation request');
 await put({'/api/projects/forge/worker-environment':{...environment,revision:'2',definitionRevision:'1',dockerfilePath:'Dockerfile.worker',approvedImage:image,approvedSourceRevision:source,approvedDefinitionRevision:'1',approvedDockerfilePath:'Dockerfile.worker',approvedBuildContext:'.',status:'active'}});
 await panel.getByLabel('Build context',{exact:false}).fill('worker');
 await panel.getByRole('button',{name:'Save definition',exact:true}).click();
 await panel.getByText('Worker environment changed. Reload before saving.',{exact:true}).waitFor();
 assert.equal(await panel.getByLabel('Build context',{exact:false}).inputValue(),'worker');
 await panel.getByRole('button',{name:'Reload saved environment',exact:true}).click();
 await panel.getByText('Qualified image active',{exact:true}).waitFor();
 assert.ok((await panel.innerText()).includes(image));assert.ok((await panel.innerText()).includes(source));
 receipt.checks.push('Concurrent definition rejected visibly; operator reload displays exact qualified digest and source');
 await panel.evaluate(el=>el.scrollIntoView({block:'start'}));
 await capture(page,{path:output+'/project-worker-environment-2560.png',fullPage:true});
 await page.setViewportSize({width:390,height:844});
 await panel.evaluate(el=>el.scrollIntoView({block:'start'}));
 await capture(page,{path:output+'/project-worker-environment-390.png',fullPage:true});
 await panel.locator('pre').scrollIntoViewIfNeeded();
 await capture(page,{path:output+'/project-worker-environment-command-390.png',fullPage:true});
 assert.ok(await panel.evaluate(el=>el.getBoundingClientRect().right<=innerWidth+1));
 assert.ok(await panel.locator('input').evaluateAll(inputs=>inputs.every(el=>el.getBoundingClientRect().right<=innerWidth+1)));
 receipt.checks.push('Desktop and mobile environment controls remain within viewport');
 const task={id:'worker-dependency-fixture',type:'task',title:'Compile the project',status:'Failed',priority:2,metadata:{},
  situation:{label:'Worker needs tools',text:'The compiler is unavailable in this worker environment.',nextActor:'Operator'},events:[]};
 await put({'/api/tasks/worker-dependency-fixture':task,
  '/api/tasks/worker-dependency-fixture/human-review':{required:false,syncPending:false,mode:'Automated'},
  '/api/tasks/worker-dependency-fixture/worker-dependencies':[
   {id:'request-make',tool:'make',reason:'make was not found during native verification.',suggestedChange:'Add make to the project worker Dockerfile.',status:'pending'},
   {id:'request-compiler',tool:'g++',reason:'The compiler is missing.',suggestedChange:'Add the C++ compiler to the project worker Dockerfile.',status:'pending'}]});
 await page.setViewportSize({width:2560,height:1274});
 await page.goto(base+'/tasks/worker-dependency-fixture?project=forge');
 const requests=page.locator('.worker-dependencies');
 await requests.getByRole('heading',{name:'Worker dependencies',exact:true}).waitFor();
 const make=requests.locator('article').filter({has:page.getByRole('heading',{name:'make',exact:true})});
 await make.getByRole('button',{name:'Acknowledge',exact:true}).click();
 await make.getByText('Acknowledged',{exact:true}).waitFor();
 const compiler=requests.locator('article').filter({has:page.getByRole('heading',{name:'g++',exact:true})});
 await compiler.getByRole('button',{name:'Dismiss',exact:true}).click();
 await compiler.getByText('Dismissed',{exact:true}).waitFor();
 const writes=(await (await page.request.get(api+'/__writes')).json()).slice(initialWrites);
 assert.equal(writes.filter(w=>w.method==='POST').length,2);
 assert.ok(writes.filter(w=>w.method==='POST').every(w=>w.path.includes('/worker-dependencies/')&&w.path.endsWith('?projectId=forge')));
 receipt.checks.push('Task operator acknowledges and dismisses scoped proposals without installing, retrying or creating work');
 await requests.evaluate(el=>el.scrollIntoView({block:'start'}));
 await capture(page,{path:output+'/task-worker-dependencies-2560.png',fullPage:true});
 await page.setViewportSize({width:390,height:844});
 await requests.evaluate(el=>el.scrollIntoView({block:'start'}));
 await capture(page,{path:output+'/task-worker-dependencies-390.png',fullPage:true});
 assert.ok(await requests.evaluate(el=>el.getBoundingClientRect().right<=innerWidth+1));
 assert.deepEqual(errors,[]);
} finally {
 await fs.writeFile(output+'/worker-environment.json',JSON.stringify(receipt,null,2));await browser.close();
}
