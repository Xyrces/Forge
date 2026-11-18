import { capture } from './capture.mjs';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { createRequire } from 'node:module';

const require = createRequire(process.env.FORGE_BROWSER_NODE_PACKAGE || import.meta.url);
let chromium;
try { ({ chromium } = require('playwright')); }
catch { throw new Error('Install playwright in this checkout or set FORGE_BROWSER_NODE_PACKAGE to an external package.json. See scripts/browser/README.md.'); }
const output = process.argv[2];
if (!output) throw new Error('Pass an isolated Linux receipt directory');
await fs.mkdir(output,{recursive:true});
const base='http://127.0.0.1:5123', api='http://127.0.0.1:5124';
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
 '/api/health/heartbeat':{status:'healthy',at:now}, '/api/sprints/active':{id:'ux-fixture',name:'Offline browser qualification'},
 '/api/state':{tasks:[],agents:[],skills:[],sprints:[]}, '/api/now':{items:[]},
 '/api/gates':{design:'open',groom:'open',sprint:'open',merge:'open',deploy:'open'},
 '/api/sprints/building':{}, '/api/queue':{ready:[],blocked:[]},
 '/api/sprints/ux-fixture/followup-drafts':[], '/api/sprints/outstanding-completions':{items:[],nextCursor:null}
};
const receipt={kind:'offline actual Blazor components; fixture API only; no live database, scheduler or paid models',checks:[],errors,requests};
try {
 await put({...common,'/api/projects':[{id:'forge',name:'Forge synthetic first project',repoUrl:'https://github.com/Xyrces/Forge.git',slots:[]}],
  '/__project_registration':{status:503,body:{error:'Synthetic project storage provisioning failed. Retry registration.'}}});
 await page.goto(base+'/projects');
 await page.locator('.project-card').filter({hasText:'Forge synthetic first project'}).waitFor();
 await page.getByRole('button',{name:'Add Project',exact:true}).click();
 const id=page.getByPlaceholder('forge',{exact:true}), name=page.getByPlaceholder('Forge',{exact:true});
 const repo=page.getByPlaceholder('https://github.com/Xyrces/Forge.git',{exact:true});
 await id.fill('second-project'); await name.fill('Synthetic second project');
 await repo.fill('https://github.com/example/synthetic-second.git');
 await page.getByPlaceholder('main',{exact:true}).fill('main');
 const before=(await (await page.request.get(api+'/__writes')).json()).length;
 await page.getByRole('button',{name:'Register',exact:true}).click();
 await page.getByText('Synthetic project storage provisioning failed. Retry registration.',{exact:false}).waitFor();
 assert.equal(await id.inputValue(),'second-project');
 assert.equal(await name.inputValue(),'Synthetic second project');
 assert.equal(await repo.inputValue(),'https://github.com/example/synthetic-second.git');
 assert.equal(await page.locator('.project-card').count(),1);
 await capture(page,{path:`${output}/project-registration-retry-1280.png`,fullPage:true});
 await put({'/__project_registration':{status:201,body:{}}});
 await page.getByRole('button',{name:'Register',exact:true}).click();
 await page.locator('.project-card').filter({hasText:'Synthetic second project'}).waitFor();
 assert.equal(await page.locator('.project-card').count(),2);
 const writes=(await (await page.request.get(api+'/__writes')).json()).slice(before);
 assert.equal(writes.length,2);
 assert.deepEqual(writes[0],writes[1]);
 assert.equal(writes[0].path,'/api/projects/');
 assert.equal(writes[0].body.id,'second-project');
 assert.equal(writes[0].body.defaultBranch,'main');
 assert.equal((await (await page.request.get(api+'/api/projects')).json()).length,2);
 for(const width of [1280,390]) {
  await page.setViewportSize({width,height:900});
  await capture(page,{path:`${output}/project-registration-success-${width}.png`,fullPage:true});
 }
 receipt.checks.push('Actual Add Project form: retryable synthetic503 reason visible; fields retained; identical retry succeeds; exactly two projects listed, no duplicate registration');
 assert.deepEqual(errors,[]);receipt.result='passed';
} catch(error) {
 receipt.result='failed';receipt.failure=String(error.stack||error);process.exitCode=1;
 await capture(page,{path:`${output}/failure.png`,fullPage:true}).catch(()=>{});
} finally {
 await fs.writeFile(`${output}/receipt.json`,JSON.stringify(receipt,null,2));
 await browser.close();console.log(JSON.stringify(receipt,null,2));
}
