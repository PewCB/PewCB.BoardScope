const assert=require('node:assert/strict'),fs=require('node:fs');
const controls=new Map(),handlers=new Map();
global.$=id=>{if(!controls.has(id))controls.set(id,{value:'',disabled:false,textContent:'',addEventListener(){}});return controls.get(id);};
global.state={features:{platformCenter:true},platform:{boardCenterX:100,boardCenterY:60},camera:{connected:true},motion:{connected:true,homed:true,state:'Idle'},engine:{job:{busy:false,phase:'idle'},manual:null}};
global.livePan={busy:false};global.on=(id,handler)=>handlers.set(id,handler);global.savedDefaults={scan:{name:'keep'}};
global.toast=()=>{};global.tab=()=>{};
const controller=require('../src/BoardScope/wwwroot/platform-controller.js');
controller.update();assert.equal($('boardCenterX').value,'100');assert.equal($('boardCenterY').value,'60');assert.equal(controller.canScan(state),true);assert.equal($('moveBoardCenter').disabled,false);
for(const patch of [{connected:false},{state:'Hold:0'},{state:'Alarm'}]){
 const original=state.motion;state.motion={...original,...patch};controller.update();assert.equal($('moveBoardCenter').disabled,true);assert.equal(controller.canScan(state),false);state.motion=original;
}
state.motion.homed=false;controller.update();assert.equal($('moveBoardCenter').disabled,false);assert.equal(controller.canScan(state),true);assert.match($('boardCenterStatus').textContent,/controller ready/);
$('boardCenterX').value='99.75';controller.update();assert.equal($('boardCenterX').value,'99.75');assert.equal($('moveBoardCenter').disabled,true);assert.equal(controller.canScan(state),false);assert.match($('boardCenterStatus').textContent,/Unsaved/);
$('boardCenterX').value='';assert.throws(()=>controller.settings(),/valid/);$('boardCenterX').value='NaN';assert.throws(()=>controller.settings(),/valid/);
controller.receive({boardCenterX:98,boardCenterY:61},true);state.platform={boardCenterX:98,boardCenterY:61};controller.update();assert.equal(controller.dirty(),false);
state.engine.job={busy:true,phase:'centering'};controller.update();assert.equal($('moveBoardCenter').disabled,true);assert.equal($('saveBoardCenter').disabled,true);assert.match($('moveBoardCenter').textContent,/Moving/);assert.equal(controller.canScan(state),false);
state.engine.job={busy:false,phase:'centered'};state.engine.manual={id:'legacy'};controller.update();assert.equal(controller.canScan(state),false);state.engine.manual=null;
state.features={};controller.update();assert.equal($('moveBoardCenter').disabled,true);assert.equal(controller.canScan(state),false);state.features.platformCenter=true;
const html=fs.readFileSync('src/BoardScope/wwwroot/index.html','utf8');
assert.match(html,/<button id="homeMotion"[^>]*>[^<]*<\/button><button id="moveBoardCenter"/);
assert.match(html,/<details><summary>Board center coordinates<\/summary>/);assert.ok(!html.includes('id="markReference"'));assert.ok(!html.includes('id="referencePoint"'));
assert.ok(html.indexOf('src="platform-controller.js')<html.indexOf('src="app.js'));
const engine=fs.readFileSync('src/BoardScope/ScanEngine.cs','utf8');assert.ok(!engine.includes('RequireHomed'));assert.ok(!engine.includes('motion.Homed'));assert.ok(engine.includes('await motion.IdlePositionAsync'));
(async()=>{
 controller.init();$('boardCenterX').value='100.25';$('boardCenterY').value='62';
 const calls=[];global.api=async(path,data)=>{calls.push({path,data});return data;};
 await handlers.get('saveBoardCenter')();assert.deepEqual(calls[0],{path:'/platform',data:{boardCenterX:100.25,boardCenterY:62}});assert.equal(savedDefaults.platform.boardCenterX,100.25);assert.equal(savedDefaults.scan.name,'keep');
 $('feed').value='900';await handlers.get('moveBoardCenter')();assert.deepEqual(calls[1],{path:'/motion/board-center',data:{feed:500}});
 console.log('PASS platform center drafts, homing/job guards, persistence handlers, center-move request and UI placement');
})().catch(e=>{console.error(e);process.exitCode=1;});
