const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const actions=require('../src/BoardScope/wwwroot/camera-control-actions.js');
const prop=(name,value,auto=true)=>({group:'video',name,value,auto,canManual:true});
(async()=>{
  let reads=0,writes=[];
  const api=async(path,body)=>{
    if(path==='/camera/properties'){reads++;return [prop('WhiteBalance',170+reads)];}
    writes.push(body);return [prop('WhiteBalance',body.value,false)];
  };
  await actions.freeze(api,'video','WhiteBalance');assert.equal(writes[0].value,171);assert.equal(writes[0].auto,false);
  await actions.freeze(api,'video','WhiteBalance');assert.equal(writes[1].value,172);
  writes=[];reads=0;
  const bothApi=async(path,body)=>{
    if(path==='/camera/properties'){reads++;return [prop('WhiteBalance',190+reads),prop('Gain',10+reads),{...prop('ReadOnly',1),canManual:false}];}
    writes.push(body);return [prop('WhiteBalance',body.value,false)];
  };
  await actions.lockAll(bothApi);assert.deepEqual(writes.map(p=>[p.name,p.value]),[['WhiteBalance',192],['Gain',13]]);
  let sets=0;await actions.freeze(async(path,body)=>{if(body)sets++;return [prop('WhiteBalance',250,false)];},'video','WhiteBalance');assert.equal(sets,0);
  await assert.rejects(actions.freeze(async()=>{throw Error('Read failed');},'video','WhiteBalance'),/Read failed/);
  await assert.rejects(actions.freeze(async()=>[],'video','WhiteBalance'),/no longer exposes/);
  await assert.rejects(actions.freeze(async()=>[{...prop('WhiteBalance',190),canManual:false}],'video','WhiteBalance'),/manual mode/);
  console.log('PASS freezing auto reads a fresh value per control, preserves externally locked controls and never writes after a failed read');

  // Exercise the real polling helper without replacing controls being edited.
  const source=fs.readFileSync('src/BoardScope/wwwroot/app.js','utf8');
  const helper=source.slice(source.indexOf('async function refreshAutomaticControlValues(){'),source.indexOf('async function poll()'));
  const nodes=new Map(),$=id=>{if(!nodes.has(id))nodes.set(id,{value:0});return nodes.get(id);};let rendered=0;
  const ctx=vm.createContext({$,properties:[prop('WhiteBalance',190),prop('Gain',30,false)],state:{camera:{connected:true}},cameraControlsRevision:0,cameraControlWrites:0,activeActions:new Set(),api:async()=>[prop('WhiteBalance',211),prop('Gain',99,false)],renderControls:()=>rendered++});
  vm.runInContext(helper,ctx);await ctx.refreshAutomaticControlValues();assert.equal($('value-0').value,211);assert.equal(nodes.has('value-1'),false);assert.equal(rendered,0);
  ctx.cameraControlWrites=1;ctx.api=async()=>{throw Error('Should not read while editing');};await ctx.refreshAutomaticControlValues();ctx.cameraControlWrites=0;
  let release;ctx.api=()=>new Promise(resolve=>release=resolve);const refresh=ctx.refreshAutomaticControlValues();ctx.cameraControlsRevision++;release([prop('WhiteBalance',220),prop('Gain',30,false)]);await refresh;assert.equal($('value-0').value,211);
  console.log('PASS automatic values update without rebuilding manual sliders; pending refreshes cannot overwrite a user change');
})().catch(error=>{console.error(error);process.exitCode=1;});
