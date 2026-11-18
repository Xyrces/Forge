import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {createRequire} from 'node:module';
import {capture} from './capture.mjs';
import {verifySprintBoard} from './sprint-board.spec.mjs';
const require=createRequire(process.env.FORGE_BROWSER_NODE_PACKAGE||import.meta.url);
const {chromium}=require('playwright');
const output=process.argv[2];if(!output)throw new Error('Pass receipt directory');await fs.mkdir(output,{recursive:true});
const browser=await chromium.launch({executablePath:process.env.FORGE_BROWSER_EXECUTABLE||undefined,headless:true,args:['--no-sandbox']});
const page=await browser.newPage({viewport:{width:1280,height:900}});const errors=[];page.on('pageerror',e=>errors.push(e.message));
const base='http://127.0.0.1:5123',api='http://127.0.0.1:5124';
const put=async data=>assert.ok((await page.request.put(api+'/__fixtures',{data})).ok());
const receipt={kind:'offline synthetic nomination qualification; no production change',checks:[]};
try {
 await put({'/api/projects':[{id:'forge',name:'Forge offline nomination fixture',repoUrl:'https://github.com/Xyrces/Forge.git',defaultBranch:'main',root:'Offline fixture',roles:{},slots:[],defaultRoleCaps:{}}],'/api/projects/forge/review-policy':{mode:'Automated',nominatedHuman:null},'/api/now':{items:[]},'/api/state':{tasks:[],agents:[],skills:[],sprints:[]},'/api/meta/buildinfo':{},'/api/health/heartbeat':{status:'healthy'},'/api/sprints/active':{id:'ux-fixture',name:'Nomination fixture'},'/api/gates':{design:'open',groom:'open',sprint:'open',merge:'open'},'/api/queue':{ready:[],blocked:[]},'/api/sprints/building':{},'/api/sprints/ux-fixture/followup-drafts':[],'/api/sprints/outstanding-completions':{items:[],nextCursor:null}});
 await page.goto(base+'/projects/forge/overview');
 await page.getByLabel('Nominated human GitHub login (optional)').fill('Xyrces');
 await page.getByRole('button',{name:'Save review policy',exact:true}).click();
 await page.getByRole('status').filter({hasText:'Review policy saved'}).waitFor();
 const writes=await (await page.request.get(api+'/__writes')).json();
 assert.ok(writes.some(w=>w.path==='/api/projects/forge/review-policy'&&w.body.nominatedHuman==='Xyrces'));
 await page.locator('[aria-labelledby="project-review-policy-title"]').scrollIntoViewIfNeeded();
 await capture(page,{path:output+'/project-nomination.png',fullPage:true});
 await verifySprintBoard(page,base,{fixtureApi:api,screenshotDir:output,nominatedHuman:'Xyrces'});
 await put({'/api/tasks/nominated-fixture':{id:'nominated-fixture',title:'Synthetic task awaiting nominated reviewer',status:'InProgress',metadata:{humanReviewRequired:true},situation:{label:'Waiting for human review',text:'Review the current PR revision before merge.',nextActor:'@Xyrces',nominatedHuman:'Xyrces'},events:[]},'/api/tasks/nominated-fixture/human-review':{required:false,syncPending:false,mode:'Both',nominatedHuman:'Xyrces'}});
 await page.setViewportSize({width:390,height:900});await page.goto(base+'/tasks/nominated-fixture?project=forge');
 await page.getByText('Next: @Xyrces',{exact:true}).waitFor();
 await page.locator('details.task-actions summary').click();
 await page.getByRole('button',{name:'Require review by @Xyrces for this task',exact:true}).waitFor();
 await capture(page,{path:output+'/task-nomination-390.png',fullPage:true});
 assert.deepEqual(errors,[]);receipt.result='passed';receipt.checks=['Project UI saves nominated login','Board action is Require review by @Xyrces; engineering stays Running after hold','All desktop/mobile board lanes fit','Task next actor and action name show @Xyrces; extra hold cannot waive Both policy'];
} catch(e){receipt.result='failed';receipt.error=String(e.stack||e);process.exitCode=1;await page.screenshot({path:output+'/failure.png'}).catch(()=>{});}
finally {await fs.writeFile(output+'/receipt.json',JSON.stringify(receipt,null,2));console.log(JSON.stringify(receipt,null,2));await browser.close();}
