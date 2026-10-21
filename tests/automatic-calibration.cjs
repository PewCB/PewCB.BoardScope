const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const mapping=require('../src/BoardScope/wwwroot/jog-mapping.js');
const {CaptureNavigation:nav}=require('../src/BoardScope/wwwroot/capture-navigation.js');
const review=require('../src/BoardScope/wwwroot/calibration-review.js');
const close=(a,b)=>assert.ok(Math.abs(a-b)<1e-9,`${a} != ${b}`);
for(const stageRotationDegrees of [-35,12.5,95,178])for(const swapStageAxes of [false,true])for(const stageXSign of [-1,1])for(const stageYSign of [-1,1]) {
  const s={stageRotationDegrees,swapStageAxes,stageXSign,stageYSign,gridYSign:-1,fovWidth:12,fovHeight:6,boardWidth:54,boardHeight:36,referencePoint:'center'};
  const stage=mapping.imageToStage(3.4,2.1,s),pixel=mapping.stageToImage(stage.x,stage.y,s);close(pixel.x,3.4);close(pixel.y,2.1);
  const pan=nav.pan(100,50,600,300,s),movement=mapping.stageToImage(pan.x,pan.y,s);close(-movement.x,2);close(-movement.y,1);
  const jog=mapping.map(1,0,.5,s),jogImage=mapping.stageToImage(jog.x,jog.y,s);close(jogImage.x,.5);close(jogImage.y,0);
  const position=mapping.imageToStage(13-27,11-18,s);
  const rect=nav.tileRect({settings:s,reference:{x:100,y:80}},{stageX:100+position.x,stageY:80+position.y});close(rect.x*54,7);close(rect.y*54,8);
}
for(const [x,y] of [[700,8],[-700,-8],[0,400]]) {
  const layout=review.layout(1400,900,x,y);
  for(let i=0;i<2;i++){const pos=i?layout.second:layout.first,clip=layout.clips[i];close(pos.x+clip.x/1400,layout.overlap.x);close(pos.y+clip.y/1400,layout.overlap.y);close(clip.width/1400,layout.overlap.width);close(clip.height/1400,layout.overlap.height);}
}
assert.throws(()=>review.layout(1400,900,1400,0),/no overlap/);
const context={rotation:0,flipX:false,flipY:false,stageXSign:1,stageYSign:-1,swapStageAxes:false};
assert.equal(review.sameOrientation(context,{...context,stageRotationDegrees:0}),true);
assert.equal(review.sameOrientation(context,{...context,rotation:90}),false);
assert.equal(review.sameOrientation(context,{...context,stageRotationDegrees:12}),false);
const html=fs.readFileSync('src/BoardScope/wwwroot/index.html','utf8');
for(const gone of ['startManual','captureTile','finishManual','tileList','clearTile'])assert.ok(!html.includes(`id="${gone}"`));
const ids=[...html.matchAll(/\bid="([^"]+)"/g)].map(x=>x[1]);assert.equal(ids.length,new Set(ids).size);
for(const file of ['app.js','automatic-calibration.js','scale-helper.js'])for(const m of fs.readFileSync('src/BoardScope/wwwroot/'+file,'utf8').matchAll(/\$\('([^']+)'\)/g))if(!m[1].endsWith('-'))assert.ok(ids.includes(m[1]),`${file} missing ${m[1]}`);
console.log('PASS calibrated rotation is consistent across jog, drag and tile placement; overlap clips coincide; changed orientation invalidates approval; removed panels have no bindings');
const controls=new Map();const control=id=>{if(!controls.has(id))controls.set(id,{hidden:false,value:'',checked:false,disabled:false,setAttribute(name,value){this[name]=value;}});return controls.get(id);};
let current={...context};control('cameraDevice').value='mock-camera';control('calibrationOverlap').checked=true;
const worlds=[];
function OSD(){const items=[];const v={world:{getItemCount:()=>items.length,setItemIndex(item,index){items.splice(items.indexOf(item),1);items.splice(index,0,item);},removeItem(){}},
  viewport:{fitBounds(){},zoomTo(){}},close(){items.length=0;},addSimpleImage(o){const item={url:o.url,setClip(clip){this.clip=clip;}};items.push(item);o.success({item});}};worlds.push(items);return v;}
OSD.Rect=class Rect{constructor(x,y,width,height){Object.assign(this,{x,y,width,height});}};
const c=vm.createContext({console,CalibrationReview:review,OpenSeadragon:OSD,$:control,clearInterval,setInterval,document:{hidden:false},livePan:null,activeActions:new Set(),settings:()=>current,
  state:{features:{automaticCalibration:true},camera:{connected:true,width:1400,height:900},motion:{connected:true,state:'Idle'},engine:{job:{busy:false,phase:'calibrated'},manual:null},calibration:{approved:false}},toast(message){throw Error(message);}});
vm.runInContext(fs.readFileSync('src/BoardScope/wwwroot/automatic-calibration.js','utf8'),c);
const measurement={widthMm:14,heightMm:9,rotationDegrees:12,moveMm:7,offsetX:700,offsetY:0,phaseResponse:.95,correlation:.98,verifiedRegions:4,errorPixels:.2};
c.result={id:'test',width:1400,height:900,context:{deviceId:'mock-camera',width:1400,height:900,settings:{...current}},coarse:measurement,fine:measurement};
(async()=>{
  await vm.runInContext('showAutomaticCalibration(result)',c);
  assert.match(control('autoCalibrationNumbers').textContent,/phase response 0.950/);assert.match(control('autoCalibrationNumbers').textContent,/aligned correlation 0.980/);assert.ok(!control('autoCalibrationNumbers').textContent.includes('inliers'));
  assert.equal(worlds[0].length,2);assert.ok(worlds[0][1].url.endsWith('/image/0'));
  assert.equal(control('autoCalibrationApprove').disabled,true);
  vm.runInContext('calibrationOnTop(1)',c);assert.ok(worlds[0][1].url.endsWith('/image/1'));
  assert.ok(worlds[0].every(item=>item.clip));
  control('autoCalibrationChecked').checked=true;vm.runInContext('refreshAutomaticCalibrationControls()',c);assert.equal(control('autoCalibrationApprove').disabled,false);
  current={...current,rotation:90};vm.runInContext('refreshAutomaticCalibrationControls()',c);assert.equal(control('autoCalibrationApprove').disabled,true);
  console.log('PASS review uses two opaque original images, swaps their stacking order, clips the common overlap, and requires a visual check with unchanged orientation before approval');
})().catch(e=>{console.error(e);process.exitCode=1;});
