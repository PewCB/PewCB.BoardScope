const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const {CaptureNavigation}=require('../src/BoardScope/wwwroot/capture-navigation.js');
const html=fs.readFileSync('src/BoardScope/wwwroot/index.html','utf8');
const ids=[...html.matchAll(/\bid="([^"]+)"/g)].map(x=>x[1]);
assert.equal(new Set(ids).size,ids.length,'Duplicate HTML ids');
for(const file of ['app.js','capture-live.js']) {
  for(const m of fs.readFileSync('src/BoardScope/wwwroot/'+file,'utf8').matchAll(/\$\('([^']+)'\)/g)) {
    if(!m[1].endsWith('-'))assert.ok(ids.includes(m[1]),`${file}: missing ${m[1]}`);
  }
}
console.log('PASS static UI element bindings and unique ids');
const elements=new Map();
const element=id=>{if(!elements.has(id))elements.set(id,{hidden:false,clientWidth:800,clientHeight:500,textContent:'',classList:{toggle(){}},style:{}});return elements.get(id);};
const s={boardWidth:54,boardHeight:36,fovWidth:12,fovHeight:6,referencePoint:'center',stageXSign:1,stageYSign:1,gridYSign:-1,swapStageAxes:false};
let project={id:'scan-test',name:'Board',settings:s,reference:{x:100,y:100},planned:3,tiles:[]};
const capturePlan={points:[{u:6,v:3,row:0,col:0},{u:12,v:3,row:0,col:1},{u:18,v:3,row:0,col:2}]};
const viewers=[];
function OSD(){
  const v={images:[],overlays:[],fits:0,close(){this.images=[];},clearOverlays(){this.overlays=[];},addOverlay(o){this.overlays.push(o);},
    viewport:{fitBounds(){v.fits++;},resize(){}},world:{removeItem(){},setItemIndex(){}},
    addSimpleImage(o){const item={};this.images.push(o);o.success?.({item});}};
  viewers.push(v);return v;
}
OSD.Rect=class Rect{constructor(x,y,width,height){Object.assign(this,{x,y,width,height});}};
OSD.Point=class Point{constructor(x,y){Object.assign(this,{x,y});}};
const state={engine:{job:{phase:'capturing',projectId:project.id,done:0}},motion:{position:{x:79,y:115}}};
const context=vm.createContext({console,CaptureNavigation,OpenSeadragon:OSD,state,selectedProject:null,viewer:null,
  $:element,drawGrid(){},api:async path=>path.startsWith('/projects/')?structuredClone(project):capturePlan,
  document:{querySelector(){return element('tile-view-tools');},createElement(type){return type==='canvas'?{getContext:()=>({fillRect(){},strokeRect(){}}),toDataURL:()=> 'data:image/png;base64,board'}:{className:'',textContent:''};}}});
vm.runInContext(fs.readFileSync('src/BoardScope/wwwroot/capture-live.js','utf8'),context);
async function sync(){await vm.runInContext('syncCaptureProgress()',context);}
(async()=>{
  await sync();assert.equal(viewers.length,1);assert.equal(viewers[0].fits,1);assert.equal(viewers[0].images.length,1);
  for(let i=0;i<2;i++){
    project.tiles.push({file:`captures/${i}.png`,previewFile:`previews/${i}.jpg`,stageX:79+i*6,stageY:115,width:3840,height:1920});
    state.engine.job.done=project.tiles.length;await sync();
  }
  assert.equal(viewers[0].images.length,3);assert.equal(viewers[0].fits,1);
  assert.equal(viewers[0].images[1].url,'/projects/scan-test/previews/0.jpg');
  assert.ok(Math.abs(viewers[0].images[2].x-6/54)<1e-9);
  assert.ok(Math.abs(viewers[0].images[2].y)<1e-9);
  await sync();assert.equal(viewers[0].images.length,3);
  // Capture may finish between polling ticks; the final preview must still arrive.
  project.tiles.push({file:'captures/2.png',previewFile:'previews/2.jpg',stageX:91,stageY:115,width:3840,height:1920});
  state.engine.job.phase='pyramid';state.engine.job.done=0;await sync();
  assert.equal(viewers[0].images.length,4);assert.equal(viewers[0].fits,1);
  assert.equal(element('captureOsd').hidden,false);assert.equal(element('tileInspector').hidden,true);
  vm.runInContext('captureFollowing=false;showCaptureProgress()',context);assert.equal(element('captureOsd').hidden,true);
  await sync();assert.equal(element('captureOsd').hidden,true);
  console.log('PASS live scan adds only new JPEG previews at measured stage positions, preserves the viewport, shows the whole board before tile 1, and respects opening another project');
})().catch(e=>{console.error(e);process.exitCode=1;});
