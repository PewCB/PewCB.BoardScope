let livePan, panPointer=null, captureViewer, captureProject=null, capturePlan=null;
let captureGeneration=0, captureFollowing=true, captureBusy=false, captureSeenKey='',captureBoard=null,captureGridEdited=false,captureNeedsFit=false;
const captureImages=new Map();

function fitLivePreview() {
  const c=state?.camera, frame=$('previewFrame');if(!c?.connected || !frame.clientWidth || !frame.clientHeight)return;
  const size=CaptureNavigation.fit(c.width,c.height,Number($('rotation').value),Math.max(1,frame.clientWidth-24),Math.max(1,frame.clientHeight-76));
  $('preview').style.width=`${size.width}px`;$('preview').style.height=`${size.height}px`;
}
function canPanImage() {
  return !!state?.camera.connected && !!state.motion.connected && !state.engine.job.busy && !$('liveView').hidden && !$('preview').hidden && activeActions.size===0;
}
function refreshPanControls() {
  const enabled=canPanImage() && (state.motion.state==='Idle' || livePan?.busy);
  $('previewFrame').classList.toggle('pan-enabled',enabled);
  $('previewFrame').classList.toggle('panning',!!panPointer || !!livePan?.inFlight);
  $('panHint').textContent=livePan?.inFlight?'Moving stage…':state?.engine.job.busy?'Drag is paused during jobs':enabled?'Drag image to pan stage · Esc cancels pending movement':'Connect camera and stage to drag';
  if(!enabled && panPointer)cancelImagePan();
}
function cancelImagePan() {
  const pointer=panPointer;panPointer=null;
  if(pointer && $('previewFrame').hasPointerCapture(pointer.id))$('previewFrame').releasePointerCapture(pointer.id);
  livePan?.cancel();$('panDisplacement').hidden=true;
}
function initializeCaptureNavigation() {
  livePan=new StagePanController(
    delta=>api('/motion/jog',{...delta,feed:Math.min(500,Number($('feed').value))}),
    ()=>{if(state)applyState();},error=>toast(error.message,true));
  const frame=$('previewFrame');$('preview').draggable=false;
  frame.addEventListener('pointerdown',event=>{
    if(event.button!==0 || !canPanImage() || state.motion.state!=='Idle' || livePan.busy)return;
    const rect=$('preview').getBoundingClientRect();
    if(event.clientX<rect.left || event.clientX>rect.right || event.clientY<rect.top || event.clientY>rect.bottom)return;
    event.preventDefault();
    panPointer={id:event.pointerId,x:event.clientX,y:event.clientY,rect,settings:settings(),started:false};
    frame.setPointerCapture(event.pointerId);
  });
  frame.addEventListener('pointermove',event=>{
    const p=panPointer;if(!p || p.id!==event.pointerId)return;
    const dx=event.clientX-p.x,dy=event.clientY-p.y;
    if(!p.started && Math.hypot(dx,dy)<4)return;
    if(!p.started){p.started=true;if(!livePan.start()){cancelImagePan();return;}}
    try {
      const delta=CaptureNavigation.pan(dx,dy,p.rect.width,p.rect.height,p.settings);
      $('panDisplacement').hidden=false;$('panDisplacement').textContent=`Stage ΔX ${delta.x.toFixed(3)} · ΔY ${delta.y.toFixed(3)} mm`;
      livePan.target(delta);
    } catch(error) {cancelImagePan();toast(error.message,true);}
  });
  frame.addEventListener('pointerup',event=>{
    if(panPointer?.id!==event.pointerId)return;
    const started=panPointer.started;panPointer=null;
    frame.releasePointerCapture(event.pointerId);$('panDisplacement').hidden=true;
    if(started)livePan.release();else refreshPanControls();
  });
  frame.addEventListener('pointercancel',cancelImagePan);
  frame.addEventListener('lostpointercapture',()=>{if(panPointer)cancelImagePan();});
  window.addEventListener('blur',cancelImagePan);
  document.addEventListener('visibilitychange',()=>{if(document.hidden)cancelImagePan();});
  document.addEventListener('keydown',event=>{if(event.key==='Escape')cancelImagePan();});
  new ResizeObserver(()=>{fitLivePreview();drawGrid();}).observe($('liveView'));
  new ResizeObserver(drawGrid).observe($('grid').parentElement);
  $('showCaptureProgress').onclick=()=>{captureFollowing=true;tab('mosaic');showCaptureProgress();};
}
function displayViewer() {return !$('captureOsd').hidden?captureViewer:viewer;}
function showCaptureProgress() {
  const show=captureFollowing && !!captureProject;
  $('captureOsd').hidden=!show;$('osd').hidden=show;
  document.querySelector('.tile-view-tools').hidden=show;
  $('capturePreviewLabel').hidden=!show;
  if(show){$('mosaicEmpty').hidden=true;$('tileInfo').hidden=true;$('tileInspector').hidden=true;$('renameSelectedProject').hidden=true;
    $('workspaceTitle').textContent=captureProject.name;$('showFootprints').disabled=true;
    fitCaptureProgress();
  } else {$('tileInspector').hidden=!selectedProject?.tiles.length;$('renameSelectedProject').hidden=!selectedProject;
    $('workspaceTitle').textContent=selectedProject?.name||'Your board, under the lens';}
}
function fitCaptureProgress() {
  const host=$('captureOsd');
  if(!captureNeedsFit || !captureBoard || !host.clientWidth || !host.clientHeight || host.hidden || $('mosaicView').hidden)return;
  captureViewer.viewport.resize(new OpenSeadragon.Point(host.clientWidth,host.clientHeight),true);
  captureViewer.viewport.fitBounds(new OpenSeadragon.Rect(0,0,1,captureProject.settings.boardHeight/captureProject.settings.boardWidth),true);
  captureNeedsFit=false;
}
async function syncCaptureProgress() {
  const e=state?.engine;if(!e || captureBusy)return;
  const active=e.manual || (['starting','capturing'].includes(e.job.phase) && e.job.projectId?{id:e.job.projectId,count:e.job.done}:null)
    || (captureProject?.id===e.job.projectId?{id:e.job.projectId,count:'settled'}:null);
  if(!active)return;
  const key=`${active.id}/${active.count}`;if(key===captureSeenKey)return;
  captureBusy=true;
  try {
    const p=await api('/projects/'+active.id);
    // A completed or replaced scan must not reopen itself after a delayed read.
    if((state.engine.manual?.id||state.engine.job.projectId)!==p.id)return;
    if(captureProject?.id!==p.id){
      const nextPlan=await api('/plan',p.settings);
      ++captureGeneration;captureFollowing=true;captureGridEdited=false;captureProject=p;captureImages.clear();captureBoard=null;captureNeedsFit=true;
      capturePlan=nextPlan;
      if(!captureViewer)captureViewer=OpenSeadragon({id:'captureOsd',showNavigationControl:false,showNavigator:true,navigatorPosition:'BOTTOM_RIGHT',backgroundColor:'#121c28',visibilityRatio:.7,constrainDuringPan:true,gestureSettingsMouse:{clickToZoom:false}});
      captureViewer.close();captureViewer.clearOverlays();
      const board=document.createElement('canvas');board.width=1000;board.height=Math.max(1,Math.round(1000*p.settings.boardHeight/p.settings.boardWidth));
      const ctx=board.getContext('2d');ctx.fillStyle='#1b2b39';ctx.fillRect(0,0,board.width,board.height);
      ctx.strokeStyle='#405568';ctx.lineWidth=1;
      for(const point of capturePlan.points){const s=p.settings;ctx.strokeRect((point.u-s.fovWidth/2)/s.boardWidth*board.width,(point.v-s.fovHeight/2)/s.boardHeight*board.height,s.fovWidth/s.boardWidth*board.width,s.fovHeight/s.boardHeight*board.height);}
      const generation=captureGeneration;
      captureViewer.addSimpleImage({url:board.toDataURL(),x:0,y:0,width:1,index:0,success:event=>{
        if(generation!==captureGeneration){captureViewer.world.removeItem(event.item);return;}
        captureBoard=event.item;orderCaptureImages();
        fitCaptureProgress();
      }});
    }
    captureProject=p;captureSeenKey=active.count==='settled'?key:`${p.id}/${p.tiles.length}`;
    const generation=captureGeneration;
    for(const [index,t] of p.tiles.entries()) {
      if(!t.previewFile || captureImages.has(t.file))continue;
      captureImages.set(t.file,null);
      const rect=CaptureNavigation.tileRect(p,t);
      captureViewer.addSimpleImage({url:`/projects/${p.id}/${t.previewFile}`,x:rect.x,y:rect.y,width:rect.width,index:index+1,
        success:event=>{if(generation!==captureGeneration){captureViewer.world.removeItem(event.item);return;}captureImages.set(t.file,event.item);orderCaptureImages();},
        error:()=>{if(generation===captureGeneration){captureImages.delete(t.file);captureSeenKey='';}}});
    }
    showCaptureProgress();updateCaptureMarker();drawGrid();
  }catch(error){console.debug('Capture preview will retry',error);}
  finally{captureBusy=false;}
}
function orderCaptureImages() {
  if(captureBoard)captureViewer.world.setItemIndex(captureBoard,0);
  let index=captureBoard?1:0;
  for(const tile of captureProject.tiles){const item=captureImages.get(tile.file);if(item)captureViewer.world.setItemIndex(item,index++);}
}
function updateCaptureMarker() {
  if(!captureViewer || !captureProject || !captureFollowing)return;
  captureViewer.clearOverlays();
  const s=captureProject.settings,n=captureProject.tiles.length,next=capturePlan?.points[n];
  if(next){const box=document.createElement('div');box.className='capture-next';box.textContent=`Next: ${next.row+1}, ${next.col+1}`;
    captureViewer.addOverlay({element:box,location:new OpenSeadragon.Rect((next.u-s.fovWidth/2)/s.boardWidth,(next.v-s.fovHeight/2)/s.boardWidth,s.fovWidth/s.boardWidth,s.fovHeight/s.boardWidth)});}
  if(state.motion.position && captureProject.reference){const rect=CaptureNavigation.tileRect(captureProject,{stageX:state.motion.position.x,stageY:state.motion.position.y});
    const box=document.createElement('div');box.className='capture-stage';
    captureViewer.addOverlay({element:box,location:new OpenSeadragon.Rect(rect.x,rect.y,rect.width,rect.height)});}
  $('capturePreviewLabel').textContent=`LIVE CAPTURE · ${n}/${captureProject.planned} · previews only`;
}
