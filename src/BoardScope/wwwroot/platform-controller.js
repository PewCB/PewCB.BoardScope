const PlatformController={
  saved:{boardCenterX:100,boardCenterY:60},loaded:false,
  settings(){
    const boardCenterX=Number($('boardCenterX').value),boardCenterY=Number($('boardCenterY').value);
    if(!$('boardCenterX').value.trim()||!$('boardCenterY').value.trim()||![boardCenterX,boardCenterY].every(v=>Number.isFinite(v)&&Math.abs(v)<=100000))throw Error('Enter valid board-center machine coordinates in millimetres.');
    return {boardCenterX,boardCenterY};
  },
  receive(value,apply=false){
    if(!value)return;this.saved=value;
    if(!this.loaded||apply){$('boardCenterX').value=String(value.boardCenterX);$('boardCenterY').value=String(value.boardCenterY);this.loaded=true;}
  },
  dirty(){try{const p=this.settings();return p.boardCenterX!==this.saved.boardCenterX||p.boardCenterY!==this.saved.boardCenterY;}catch{return true;}},
  canScan(s){return !!(s?.features?.platformCenter&&s.camera.connected&&s.motion.connected&&s.motion.state==='Idle'&&!s.engine.job.busy&&!s.engine.manual&&!this.dirty());},
  update(){
    this.receive(state?.platform);
    const available=!!state?.features?.platformCenter,blocked=!!state?.engine.job.busy||!!state?.engine.manual||!!livePan?.busy;
    ['boardCenterX','boardCenterY','saveBoardCenter'].forEach(id=>$(id).disabled=blocked||!available);
    $('moveBoardCenter').disabled=blocked||!available||!state?.motion.connected||state.motion.state!=='Idle'||this.dirty();
    $('moveBoardCenter').textContent=state?.engine.job.phase==='centering'&&state.engine.job.busy?'Moving to center…':'Move to board center';
    $('boardCenterStatus').textContent=!available?'Board center controls will be available after restarting the app.':this.dirty()?'Unsaved center changes · save before moving or scanning.':`Board center: X ${this.saved.boardCenterX.toFixed(3)}, Y ${this.saved.boardCenterY.toFixed(3)} mm · ${!state?.motion.connected?'connect FluidNC':state.motion.state==='Idle'?'controller ready':`controller ${state.motion.state} · resolve before moving`}`;
  },
  init(){
    on('saveBoardCenter',async()=>{
      const platform=await api('/platform',this.settings());this.receive(platform,true);
      if(savedDefaults)savedDefaults={...savedDefaults,platform};toast('Board center saved.');
    });
    on('moveBoardCenter',async()=>{await api('/motion/board-center',{feed:Math.min(500,Number($('feed').value))});tab('live');});
    ['boardCenterX','boardCenterY'].forEach(id=>$(id).addEventListener('input',()=>{if(state)applyState();}));
  }
};
if(typeof module!=='undefined')module.exports=PlatformController;
