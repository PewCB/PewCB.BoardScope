const assert=require('node:assert/strict');
const jog=require('../src/BoardScope/wwwroot/jog-mapping.js');
const scale=require('../src/BoardScope/wwwroot/scale-calibration.js');
for(const swapStageAxes of [false,true])for(const stageXSign of [-1,1])for(const stageYSign of [-1,1]) {
  const s={swapStageAxes,stageXSign,stageYSign};
  assert.deepEqual(jog.map(1,0,5,s),{x:swapStageAxes?0:stageXSign*5,y:swapStageAxes?stageYSign*5:0});
  assert.deepEqual(jog.map(0,-1,.1,s),{x:swapStageAxes?-stageXSign*.1:0,y:swapStageAxes?0:-stageYSign*.1});
}
const a={width:3840,height:2160,deviceId:'camera',epoch:1,position:{x:5,y:8},point:{x:1500,y:1200}};
const b={...a,position:{x:4,y:8},point:{x:2300,y:1200}};
const v=scale.calculate(a,b);
assert.equal(v.width,4.8);assert.equal(v.height,2.7);assert.equal(scale.calculate(a,b,90).width,2.7);
assert.ok(Math.abs(scale.calculate(a,{...b,position:{x:4.4,y:8.8},point:{x:1980,y:560}}).width-4.8)<1e-10);
assert.throws(()=>scale.calculate(a,{...b,position:a.position}),/Jog/);
assert.throws(()=>scale.calculate(a,{...b,epoch:2}),/coordinates changed/);
assert.throws(()=>scale.calculate(a,{...b,deviceId:'different'}),/Camera/);
assert.throws(()=>scale.calculate(a,{...b,point:null}),/Confirm/);
assert.throws(()=>scale.calculate(a,{...b,point:{x:1501,y:1200}}),/too small/);
console.log('PASS jog signs/swap combinations and scale calculation, rotation, diagonal movement, invalid measurements');
const view=require('../src/BoardScope/wwwroot/similarity-view.js');
assert.deepEqual(view.geometry({a:1,b:0,x:10,y:20},100,50),{x:10,y:20,width:100,height:50,degrees:0,left:10,top:20,right:110,bottom:70});
const rotated=view.geometry({a:0,b:2,x:100,y:10},100,50);
assert.equal(rotated.left,0);assert.equal(rotated.right,100);assert.equal(rotated.bottom,210);assert.equal(rotated.degrees,90);assert.equal(rotated.width,200);
console.log('PASS similarity viewer geometry uses scaled rotated corners and the correct rotation centre');
