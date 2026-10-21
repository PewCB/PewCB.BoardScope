const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const math=require('../src/BoardScope/wwwroot/effect-math.js');
const rgba=new Uint8ClampedArray([20,100,230,255,80,80,80,123,255,0,128,0]);
assert.deepEqual(math.apply(rgba,{}),rgba);
const gray=math.apply(rgba,{saturation:-100});assert.equal(gray[0],gray[1]);assert.equal(gray[1],gray[2]);assert.equal(gray[7],123);
assert.throws(()=>math.settings({tint:NaN}));assert.throws(()=>math.settings({brightness:101}));
if(process.argv[2])for(const vector of JSON.parse(fs.readFileSync(process.argv[2],'utf8')))for(const pixel of vector.pixels)assert.deepEqual([...math.apply(new Uint8ClampedArray(pixel.input),vector.settings)],pixel.output);
console.log('PASS neutral colors, saturation, alpha and numeric limits'+(process.argv[2]?', with byte-exact browser/native pixel agreement':''));
const posted=[],workerSelf={postMessage:data=>posted.push(data)},workerVM={self:workerSelf,importScripts(){},EffectMath:math,Uint8ClampedArray};
vm.runInNewContext(fs.readFileSync('src/BoardScope/wwwroot/effects-worker.js','utf8'),workerVM);
workerSelf.onmessage({data:{type:'source',source:1,pixels:rgba.slice().buffer}});
workerSelf.onmessage({data:{type:'render',revision:4,effects:{brightness:20}}});
assert.equal(posted[1].revision,4);assert.deepEqual([...new Uint8ClampedArray(posted[1].pixels)],[...math.apply(rgba,{brightness:20})]);
workerSelf.onmessage({data:{type:'render',revision:5,effects:{}}});assert.deepEqual([...new Uint8ClampedArray(posted[2].pixels)],[...rgba]);
console.log('PASS preview worker transforms the immutable source on each edit, transfers pixels and preserves revisions');

const elements=new Map(),element=id=>{
  if(!elements.has(id))elements.set(id,{value:'0',disabled:false,hidden:false,checked:false,textContent:'',events:{},
    addEventListener(type,fn){this.events[type]=fn;},showModal(){this.open=true;},close(){this.open=false;this.events.close?.();},
    getContext(){return canvasContext;}});
  return elements.get(id);
};
const canvasContext={paints:[],drawImage(){},getImageData(){return {data:rgba.slice()};},putImageData(data){this.paints.push([...data.data]);}};
const base={id:'base-map',dzi:'mosaic-base.dzi',width:640,height:480},entry={id:'alignment',maps:[base]},project={id:'board',name:'Test board',activeStitchId:entry.id,activeMapId:base.id,stitches:[entry]};
const workers=[],requests=[],calls=[],notices=[];let responseFactory=()=>Promise.resolve({ok:true,blob:async()=>({})});
class FakeWorker{
  constructor(){this.messages=[];workers.push(this);}
  postMessage(data){this.messages.push(data);}
  terminate(){this.terminated=true;}
  ready(){this.onmessage({data:{type:'ready'}});}
  finish(message=this.messages.at(-1)){this.onmessage({data:{type:'frame',revision:message.revision,pixels:math.apply(rgba,message.effects).buffer}});}
}
const context={$:element,EffectMath:math,selectedProject:project,state:{features:{imageEffects:true},engine:{job:{busy:false},manual:null}},captureFollowing:false,loadedView:'stitched',viewLoading:false,
  StitchHistory:{alignment:()=>context.selectedProject.stitches[0],map:()=>context.selectedProject.stitches[0].maps.find(m=>m.id===context.selectedProject.activeMapId)},
  viewer:{world:{getItemAt:()=>({viewportToImageRectangle:()=>({x:10,y:20,width:100,height:80})})},viewport:{getBounds:()=>({})}},
  Worker:FakeWorker,AbortController,URLSearchParams,Uint8ClampedArray,
  createImageBitmap:async()=>({width:3,height:1,close(){this.closed=true;}}),ImageData:class{constructor(data,width,height){this.data=data;this.width=width;this.height=height;}},
  fetch(url,options){requests.push({url,options});return responseFactory();},
  api:async(...args)=>{calls.push(args);},poll:async()=>{},renderSelectedView:async()=>{},toast:(...args)=>notices.push(args)};
vm.runInNewContext(fs.readFileSync('src/BoardScope/wwwroot/image-effects.js','utf8')+';globalThis.effects=ImageEffectsUI;',context);
const ui=context.effects;ui.init();const tick=()=>new Promise(resolve=>setImmediate(resolve));
const html=fs.readFileSync('src/BoardScope/wwwroot/index.html','utf8');
for(const key of math.keys)for(const prefix of ['effect-','effect-value-'])assert.ok(html.includes(`id="${prefix+key}"`));
for(const match of fs.readFileSync('src/BoardScope/wwwroot/image-effects.js','utf8').matchAll(/\$\('([^']+)'\)/g))assert.ok(html.includes(`id="${match[1]}"`),match[1]);
(async()=>{
  ui.open();await tick();assert.equal(element('effectsDialog').open,true);assert.match(requests[0].url,/mapId=base-map/);assert.match(requests[0].url,/width=100/);
  const worker=workers.at(-1);worker.ready();assert.equal(element('applyEffects').disabled,true);
  const initial=worker.messages.at(-1);
  element('effect-brightness').value='30';element('effect-brightness').events.input();element('effect-temperature').value='15';element('effect-temperature').events.input();
  assert.equal(worker.messages.length,2,'only one render may be in flight');worker.finish(initial);
  assert.equal(canvasContext.paints.length,0,'superseded frames never paint');assert.equal(worker.messages.at(-1).effects.brightness,30);assert.equal(worker.messages.at(-1).effects.temperature,15);
  worker.finish();assert.equal(canvasContext.paints.length,1);assert.equal(element('applyEffects').disabled,false);
  element('showOriginalEffects').checked=true;element('showOriginalEffects').onchange();assert.deepEqual(canvasContext.paints.at(-1),[...rgba]);
  element('showOriginalEffects').checked=false;element('showOriginalEffects').onchange();worker.finish();
  await ui.apply();assert.equal(calls[0][0],'/projects/board/effects');assert.equal(calls[0][1].stitchId,'alignment');assert.equal(calls[0][1].mapId,'base-map');assert.equal(calls[0][1].effects.brightness,30);
  assert.equal(element('effectsDialog').open,false);assert.equal(worker.terminated,true);assert.equal(element('mosaicMode').value,'stitched');
  console.log('PASS sliders coalesce updates, discard stale frames, show the original and apply the exact selected map recipe');

  const adjusted={...base,id:'adjusted-map',effects:{brightness:20},effectSourceMapId:base.id};entry.maps.push(adjusted);project.activeMapId=adjusted.id;
  ui.open();await tick();let current=workers.at(-1);current.ready();current.finish();assert.equal(element('effect-brightness').value,20);assert.match(requests.at(-1).url,/mapId=base-map/);assert.equal(element('applyEffects').disabled,true);
  element('resetEffects').onclick();current.finish();assert.equal(element('effect-brightness').value,0);assert.equal(element('applyEffects').disabled,false);
  element('effectsDialog').close();entry.maps.splice(entry.maps.indexOf(base),1);
  ui.open();await tick();current=workers.at(-1);current.ready();current.finish();assert.equal(element('effect-brightness').value,0);assert.match(requests.at(-1).url,/mapId=adjusted-map/);assert.match(element('effectsSource').textContent,/deleted/);
  console.log('PASS saved recipes reopen from their original source, reset can remove effects, and deleted sources fall back to the current baked map');

  element('effectsArea').value='whole';const load=ui.load();await load;assert.equal(current.terminated,true);assert.ok(!requests.at(-1).url.includes('width='));
  const latest=workers.at(-1);current.onmessage({data:{type:'ready'}});assert.equal(ui.ready,false,'old workers cannot enable a newly loading source');latest.ready();latest.finish();
  let resolve;responseFactory=()=>new Promise(r=>resolve=r);const count=workers.length;const pending=ui.load();element('effectsDialog').close();resolve({ok:true,blob:async()=>({})});await pending;
  assert.equal(workers.length,count,'closing a loading preview prevents late worker creation');assert.equal(ui.context,null);
  context.state.features.imageEffects=false;ui.updateControls();assert.equal(element('imageEffects').disabled,true);context.state.features.imageEffects=true;context.state.engine.job.busy=true;ui.updateControls();assert.equal(element('imageEffects').disabled,true);
  console.log('PASS preview area changes and closing cancel stale work; running jobs and older backends disable effects');
})().catch(error=>{console.error(error);process.exitCode=1;});
