const assert=require('node:assert/strict');
const {CaptureNavigation:nav,StagePanController}=require('../src/BoardScope/wwwroot/capture-navigation.js');
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const close=(a,b)=>assert.ok(Math.abs(a-b)<1e-9,`${a} != ${b}`);
const base={boardWidth:54,boardHeight:36,fovWidth:12,fovHeight:6,referencePoint:'center',gridYSign:-1};
for(const swapStageAxes of [false,true])for(const stageXSign of [-1,1])for(const stageYSign of [-1,1]) {
  const s={...base,swapStageAxes,stageXSign,stageYSign};
  // This screen drag should move visible content right 2 mm and down 1 mm.
  const delta=nav.pan(100,50,600,300,s);
  const sx=delta.x*stageXSign,sy=delta.y*stageYSign;
  close(-(swapStageAxes?sy:sx),2);close((swapStageAxes?sx:sy),1);
  for(const referencePoint of ['center','top-left','top-right','bottom-left','bottom-right']) {
    const settings={...s,referencePoint};
    const refX=referencePoint==='center'?27:referencePoint.endsWith('right')?54:0;
    const refY=referencePoint==='center'?18:referencePoint.startsWith('bottom')?36:0;
    const u=13-refX,v=11-refY;
    const p={settings,reference:{x:90,y:80}};
    const t={stageX:90+stageXSign*(swapStageAxes?-v:u),stageY:80+stageYSign*(swapStageAxes?u:-v)};
    const rect=nav.tileRect(p,t);close(rect.x*54,7);close(rect.y*54,8);close(rect.width*54,12);close(rect.height*54,6);
  }
}
const limited=nav.pan(20000,10000,600,300,{...base,stageXSign:1,stageYSign:1});
close(limited.x,-10);close(limited.y,5);
assert.throws(()=>nav.pan(1,1,0,300,base),/Calibrate/);
const fit=nav.fit(3840,2160,90,800,500);close(fit.width,500);close(fit.height,281.25);
const nominal=nav.tileRect({settings:base},{nominalX:400,nominalY:200,width:1200,height:600});close(nominal.x*54,4);close(nominal.y*54,2);
console.log('PASS image drag sign/swap mapping, bounded distances, rotated fitting, all board references and nominal fallback');

(async()=>{
  const calls=[],resolvers=[];
  const controller=new StagePanController(delta=>{calls.push(delta);return new Promise(resolve=>resolvers.push(resolve));},()=>{},e=>{throw e;});
  assert.equal(controller.start(),true);controller.target({x:2,y:1});
  assert.equal(calls.length,1);close(calls[0].x,1);close(calls[0].y,.5);
  controller.target({x:3,y:1});controller.target({x:1,y:0});assert.equal(calls.length,1);
  resolvers.shift()();await tick();assert.equal(calls.length,2);close(calls[1].x,0);close(calls[1].y,-.5);
  controller.release();resolvers.shift()();await tick();assert.equal(controller.busy,false);
  controller.start();controller.target({x:4,y:0});controller.cancel();resolvers.shift()();await tick();assert.equal(calls.length,3);
  assert.equal(controller.busy,false);
  let failures=0;const rejected=new StagePanController(()=>Promise.reject(Error('Busy')),()=>{},()=>failures++);
  rejected.start();rejected.target({x:1,y:0});await tick();assert.equal(failures,1);assert.equal(rejected.busy,false);
  const click=new StagePanController(()=>{throw Error('Click moved hardware');},()=>{},()=>{});click.start();click.release();assert.equal(click.busy,false);
  console.log('PASS drag requests serialize, pointer updates replace pending targets, cancellation discards pending moves, clicks do not jog, failures clear the gesture');
})().catch(e=>{console.error(e);process.exitCode=1;});
