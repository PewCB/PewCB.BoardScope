const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const elements=new Map(),buttons=new Map();
const element=id=>{
  if(!elements.has(id))elements.set(id,{hidden:false,disabled:false,value:'',textContent:'',innerHTML:'',
    addEventListener(type,fn){this[type]=fn;},querySelectorAll(selector){return buttons.get(selector)||[];},
    showModal(){this.open=true;},close(){this.open=false;},focus(){}});
  return elements.get(id);
};
const map=(id,created,blendMode='sharp')=>({id,created,blendMode,width:640,height:480});
const p={id:'board',stitchCatalogVersion:1,activeStitchId:'ncc',activeMapId:'map-two',stitches:[
  {id:'stage',created:'2026-10-08T09:00:00Z',method:'stage',alignmentFile:'stage.json',supportedTiles:9,acceptedPairs:0,maps:[]},
  {id:'ncc',created:'2026-10-08T10:00:00Z',method:'ncc',alignmentFile:'ncc.json',supportedTiles:9,acceptedPairs:12,errorPixels:.2,maps:[map('map-one','2026-10-08T10:01:00Z'),map('map-two','2026-10-08T10:02:00Z','feather')]},
  {id:'older',created:'2026-10-08T08:00:00Z',method:'legacy',note:'Exact alignment unavailable',maps:[map('old-map','2026-10-08T08:01:00Z','unknown')]}
]};
const calls=[],opened=[],notifications=[];
let reply=p;
const context={$:element,selectedProject:p,captureFollowing:false,activeActions:new Set(),state:{features:{stitchHistory:true},engine:{job:{busy:false},manual:null}},
  MatchDebug:{methodName:m=>({ncc:'Normalized cross-correlation',phase:'Phase correlation',sift:'Feature matching'})[m]},
  escapeHtml:s=>String(s??'').replace(/</g,'&lt;'),api:async(...args)=>{calls.push(args);return reply;},
  openProject:async(id,value)=>{opened.push({id,value});context.selectedProject=value;},refreshProjects:async()=>{},toast:(...args)=>notifications.push(args),action:async(b,fn)=>fn()};
vm.runInNewContext(fs.readFileSync('src/BoardScope/wwwroot/stitch-history.js','utf8')+';globalThis.history=StitchHistory;',context);
const history=context.history;
element('mosaicMode').value='stitched';element('tileLayout').value='saved';
history.init();
const before=JSON.stringify(p);history.render();
assert.equal(JSON.stringify(p),before,'display order never mutates stored order');
assert.ok(element('stitchVersions').innerHTML.indexOf('Normalized cross-correlation')<element('stitchVersions').innerHTML.indexOf('Stage positions'));
assert.ok(element('stitchVersions').innerHTML.includes('Viewing map'));
assert.ok(element('stitchVersions').innerHTML.includes('tile alignment unavailable'));
assert.ok(element('stitchHistoryStatus').textContent.includes('3 rendered maps'));
assert.ok(element('stitchViewing').textContent.includes('feather'));
assert.ok(element('stitchViewing').textContent.includes('Normalized cross-correlation'));
element('mosaicMode').value='tiles';history.updateViewing();assert.ok(element('stitchViewing').textContent.startsWith('Saved alignment'));
element('mosaicView').hidden=true;history.updateViewing();assert.equal(element('stitchViewing').hidden,true);element('mosaicView').hidden=false;
const mutate=[{disabled:false},{disabled:false}],read=[{disabled:false}];
buttons.set('[data-stitch-blend],[data-delete-stitch],[data-delete-map]',mutate);buttons.set('[data-stitch-tiles],[data-stitch-map]',read);
context.state.engine.job.busy=true;history.updateControls();assert.equal(mutate[0].disabled,true);assert.equal(read[0].disabled,false);
context.state.engine.job.busy=false;context.state.features.stitchHistory=false;history.updateControls();assert.equal(read[0].disabled,true);
context.state.features.stitchHistory=true;context.captureFollowing=true;history.updateControls();assert.equal(element('stitchHistory').hidden,true);
context.captureFollowing=false;
console.log('PASS history identifies the viewed alignment/map, retains chronological data order, handles map-only entries and guards mutations during jobs');
(async()=>{
  await history.load('ncc','map-one','stitched');
  assert.equal(calls.at(-1)[0],'/projects/board/stitches/ncc?mapId=map-one');
  assert.equal(calls.at(-1).length,1,'historical loading is GET only');
  assert.equal(opened.at(-1).value,p);assert.equal(element('mosaicMode').value,'stitched');assert.equal(element('tileLayout').value,'saved');
  let resolve;context.api=(...args)=>{calls.push(args);return new Promise(r=>resolve=r);};
  const count=opened.length,late=history.load('stage',null,'tiles');history.cancelLoad();resolve(p);await late;assert.equal(opened.length,count);
  const switched=history.load('stage',null,'tiles');context.selectedProject={id:'another'};resolve(p);await switched;assert.equal(opened.length,count);
  context.selectedProject=p;context.api=async(...args)=>{calls.push(args);return p;};
  await history.load('stage',null,'tiles');assert.equal(element('mosaicMode').value,'tiles');
  console.log('PASS historical loading sets the exact saved layout and ignores responses after cancellation or project changes');
  history.askDelete('board','ncc','map-one');assert.equal(element('stitchDeleteDialog').open,true);assert.ok(element('stitchDeleteDescription').textContent.includes('alignment and its other maps stay available'));
  await element('stitchDeleteForm').onsubmit({preventDefault(){}});
  assert.deepEqual(calls.at(-1),['/projects/board/stitches/ncc/maps/map-one',undefined,'DELETE']);assert.equal(element('stitchDeleteDialog').open,false);
  history.askDelete('board','ncc');assert.ok(element('stitchDeleteDescription').textContent.includes('all 2 maps'));
  await element('stitchDeleteForm').onsubmit({preventDefault(){}});assert.equal(calls.at(-1)[0],'/projects/board/stitches/ncc');
  history.askDelete('board','ncc');const countBefore=opened.length;context.api=(...args)=>{calls.push(args);return new Promise(r=>resolve=r);};
  const deleting=element('stitchDeleteForm').onsubmit({preventDefault(){}});context.selectedProject={id:'another'};resolve(p);await deleting;assert.equal(opened.length,countBefore);
  console.log('PASS map/stitch deletion targets the requested artifact and a late response cannot replace another project');
  const html=fs.readFileSync('src/BoardScope/wwwroot/index.html','utf8'),ids=[...html.matchAll(/\bid="([^"]+)"/g)].map(m=>m[1]);
  for(const m of fs.readFileSync('src/BoardScope/wwwroot/stitch-history.js','utf8').matchAll(/\$\('([^']+)'\)/g))assert.ok(ids.includes(m[1]),`missing ${m[1]}`);
  assert.ok(html.indexOf('stitch-history.js')<html.indexOf('app.js?'));
})().catch(error=>{console.error(error);process.exitCode=1;});
