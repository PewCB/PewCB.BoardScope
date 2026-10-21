const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const ViewNavigation=require('../src/BoardScope/wwwroot/view-navigation.js');
const SimilarityView=require('../src/BoardScope/wwwroot/similarity-view.js');
function near(actual,expected){for(const key of ['x','y','width','height'])assert.ok(Math.abs(actual[key]-expected[key])<1e-9,`${key}: ${actual[key]} != ${expected[key]}`);}
for(const frame of [{x:-234,y:73,scale:1920},{x:0,y:0,scale:44000}]){
  const bounds={x:.13,y:-.02,width:.05,height:.03};near(ViewNavigation.toViewport(ViewNavigation.toPixels(bounds,frame),frame),bounds);
}
const elements=new Map(),element=id=>{if(!elements.has(id))elements.set(id,{value:'',hidden:false,disabled:false});return elements.get(id);};
const handlers=new Map(),fits=[],items=[];
let bounds={x:0,y:0,width:1,height:.75};
const viewer={
  viewport:{getBounds:()=>({...bounds}),fitBounds(b){bounds={...b};fits.push({...b});}},
  world:{getItemCount:()=>items.length,removeItem(item){const i=items.indexOf(item);if(i>=0)items.splice(i,1);}},
  addHandler(event,handler){if(!handlers.has(event))handlers.set(event,new Set());handlers.get(event).add(handler);},
  removeHandler(event,handler){handlers.get(event)?.delete(handler);},
  close(){items.length=0;},clearOverlays(){},
  open(url){this.url=url;bounds={x:0,y:0,width:1,height:.8};},
  finishOpen(){items.push({});for(const fn of [...(handlers.get('open')||[])])fn();},
  failOpen(){for(const fn of [...(handlers.get('open-failed')||[])])fn();},
  addSimpleImage(o){const item={setRotation(){},getBoundsNoRotate(){return {x:o.x,y:o.y,width:o.width,height:o.width*.8};}};items.push(item);o.success({item});},
  addTiledImage(o){this.addSimpleImage(o);}
};
const p={id:'board',width:500,height:400,originX:0,originY:0,dzi:'mosaic.dzi',tiles:[
  {file:'a.png',width:100,height:80,x:-20,y:-10},
  {file:'b.png',width:100,height:80,x:60,y:10}
]};
const context={ViewNavigation,SimilarityView,viewer,selectedProject:p,viewFrame:null,viewRegion:null,viewOpenHandlers:null,viewLoading:false,loadedView:'',viewGeneration:0,tileItems:[],tileBounds:[],tileOutlines:[],highlightedTile:-1,
  $:element,StitchHistory:{updateViewing(){}},MatchDebug:{stopRequest(){},updatePanel(){},scheduleDraw(){}},
  OpenSeadragon:{Rect:class Rect{constructor(x,y,width,height){Object.assign(this,{x,y,width,height});}}},
  tileTransform:t=>({a:1,b:0,x:t.x,y:t.y}),addTileFrame:()=>({}),highlightTile(){},toast(){}};
const app=fs.readFileSync('src/BoardScope/wwwroot/app.js','utf8');
vm.runInNewContext(app.slice(app.indexOf('function cancelViewOpen('),app.indexOf('function footprints(')),context);
(async()=>{
  element('mosaicMode').value='tiles';await context.renderSelectedView();
  near(bounds,{x:0,y:0,width:1.8,height:1});
  bounds={x:.7,y:.4,width:.25,height:.2};const region={x:50,y:30,width:25,height:20};
  element('mosaicMode').value='stitched';await context.renderSelectedView();
  assert.equal(context.viewLoading,true,'restoration waits for the mosaic to open');
  viewer.finishOpen();near(ViewNavigation.toPixels(bounds,context.viewFrame),region);assert.equal(context.viewLoading,false);
  element('mosaicMode').value='tiles';await context.renderSelectedView();near(bounds,{x:.7,y:.4,width:.25,height:.2});
  console.log('PASS tile/mosaic switching preserves board location and pixel zoom across different world scales and crop origins');
  element('mosaicMode').value='stitched';await context.renderSelectedView();
  const stale=[...handlers.get('open')][0];
  element('mosaicMode').value='tiles';await context.renderSelectedView();
  stale();near(ViewNavigation.toPixels(bounds,context.viewFrame),region);
  assert.equal(handlers.get('open').size,0,'superseded map listeners are removed');
  element('mosaicMode').value='stitched';await context.renderSelectedView();
  await context.renderSelectedView();assert.equal(handlers.get('open').size,1);
  viewer.finishOpen();near(ViewNavigation.toPixels(bounds,context.viewFrame),region);
  console.log('PASS rapid switches retain the last stable region and superseded map callbacks cannot reset it');
  bounds={x:.2,y:.1,width:.1,height:.08};
  element('mosaicMode').value='tiles';await context.renderSelectedView();
  const moved={x:100,y:50,width:50,height:40};near(ViewNavigation.toPixels(bounds,context.viewFrame),moved);
  element('mosaicMode').value='stitched';await context.renderSelectedView();viewer.failOpen();assert.equal(context.viewLoading,false);
  element('mosaicMode').value='tiles';await context.renderSelectedView();near(ViewNavigation.toPixels(bounds,context.viewFrame),moved);
  console.log('PASS subsequent pans are retained and a failed map load leaves the remembered view recoverable');
  // Loading another map of this project also accounts for its different origin.
  context.selectedProject={...p,originX:-100,originY:-50,width:800};
  element('mosaicMode').value='stitched';await context.renderSelectedView();viewer.finishOpen();near(ViewNavigation.toPixels(bounds,context.viewFrame),moved);
  context.selectedProject={...p,id:'another'};element('mosaicMode').value='tiles';await context.renderSelectedView();near(bounds,{x:0,y:0,width:1.8,height:1});
  console.log('PASS same-project maps keep the region while opening a different board uses its initial fit');
})().catch(error=>{console.error(error);process.exitCode=1;});
