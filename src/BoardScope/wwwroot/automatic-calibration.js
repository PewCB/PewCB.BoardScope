let automaticCalibrationResult=null,calibrationCompareViewer,calibrationCompareItems=[],calibrationCompareLayout;
let automaticCalibrationLoading=false,automaticCalibrationApproved=false,calibrationBlinkTimer,calibrationTop=0,calibrationReviewGeneration=0;
function stopCalibrationBlink(){clearInterval(calibrationBlinkTimer);calibrationBlinkTimer=null;$('calibrationBlink').checked=false;}
function calibrationOnTop(index) {
  calibrationTop=index;const item=calibrationCompareItems[index];
  if(item && calibrationCompareViewer.world.getItemCount()===2)calibrationCompareViewer.world.setItemIndex(item,1);
  $('calibrationTop1').setAttribute('aria-pressed',String(index===0));$('calibrationTop2').setAttribute('aria-pressed',String(index===1));
}
function calibrationCompareFit() {
  const r=$('calibrationOverlap').checked?calibrationCompareLayout?.overlap:calibrationCompareLayout?.union;
  if(r)calibrationCompareViewer.viewport.fitBounds(new OpenSeadragon.Rect(r.x,r.y,r.width,r.height),true);
}
function clipCalibrationPair(fit=false) {
  calibrationCompareItems.forEach((item,i)=>{if(!item)return;const r=calibrationCompareLayout.clips[i];item.setClip($('calibrationOverlap').checked?new OpenSeadragon.Rect(r.x,r.y,r.width,r.height):null);});
  if(fit)calibrationCompareFit();
}
function refreshAutomaticCalibrationControls() {
  const ready=state?.features?.automaticCalibration && state?.camera.connected && state?.motion.connected && state.motion.state==='Idle' && !state.engine.job.busy && !state.engine.manual && !livePan?.busy;
  $('autoCalibrationStart').disabled=!ready;$('autoCalibrationDirection').disabled=!ready;
  const c=automaticCalibrationResult?.context;
  const valid=c && state?.camera.connected && state.camera.width===c.width && state.camera.height===c.height && $('cameraDevice').value===c.deviceId && CalibrationReview.sameOrientation(settings(),c.settings);
  const loaded=calibrationCompareItems.filter(Boolean).length===2;
  $('autoCalibrationApprove').disabled=!valid || !loaded || !!state?.engine.job.busy || !!livePan?.busy || automaticCalibrationApproved || !$('autoCalibrationChecked').checked;
  if(state?.engine.job.phase==='calibrating')$('autoCalibrationStatus').textContent=state.engine.job.message;
  else if(!automaticCalibrationResult)$('autoCalibrationStatus').textContent=state?.engine.job.error || (ready?'Ready: measure a 0.5 mm pair, then a pair with 50% overlap.':'Connect camera and stage to begin.');
  if(c && !valid && !automaticCalibrationApproved)$('autoCalibrationApproval').textContent='Camera orientation or axis settings changed. Run calibration again.';
  for(const button of activeActions)button.disabled=true;
}
async function showAutomaticCalibration(result) {
  stopCalibrationBlink();automaticCalibrationResult=result;
  automaticCalibrationApproved=!!state.calibration?.approved && Math.abs(Number($('fovWidth').value)-result.fine.widthMm)<.00001 && Math.abs(Number($('stageRotationDegrees').value)-result.fine.rotationDegrees)<.00001;
  $('calibrationPanel').hidden=false;$('autoCalibrationReview').hidden=false;$('autoCalibrationChecked').checked=false;
  const a=result.coarse,b=result.fine;
  $('autoCalibrationNumbers').textContent=`Frame ${b.widthMm.toFixed(5)} × ${b.heightMm.toFixed(5)} mm · stage → image ${b.rotationDegrees.toFixed(4)}°\nFirst pair: ${a.moveMm.toFixed(4)} mm · width ${a.widthMm.toFixed(5)} mm · angle ${a.rotationDegrees.toFixed(4)}°\nSecond pair: ${b.moveMm.toFixed(4)} mm · offset ${b.offsetX.toFixed(3)}, ${b.offsetY.toFixed(3)} px · phase response ${b.phaseResponse.toFixed(3)} · aligned correlation ${b.correlation.toFixed(3)} · ${b.verifiedRegions}/4 regions agree · residual ${b.errorPixels.toFixed(3)} px`;
  $('autoCalibrationStatus').textContent='Measurement complete. Check the second pair below before approving.';
  $('autoCalibrationApproval').textContent=automaticCalibrationApproved?'Already approved in this session.':'Measurements are applied only after your approval.';
  if(!calibrationCompareViewer)calibrationCompareViewer=OpenSeadragon({id:'calibrationCompare',showNavigationControl:false,backgroundColor:'#121c28',maxZoomPixelRatio:4,gestureSettingsMouse:{clickToZoom:false}});
  const generation=++calibrationReviewGeneration;calibrationCompareItems=[];calibrationCompareViewer.close();
  calibrationCompareLayout=CalibrationReview.layout(result.width,result.height,b.offsetX,b.offsetY);
  await Promise.all([0,1].map(i=>new Promise(resolve=>{
    const rect=i===0?calibrationCompareLayout.first:calibrationCompareLayout.second;
    calibrationCompareViewer.addSimpleImage({url:`/api/calibration/auto/${result.id}/image/${i}`,x:rect.x,y:rect.y,width:rect.width,
      success:event=>{if(generation!==calibrationReviewGeneration){calibrationCompareViewer.world.removeItem(event.item);resolve();return;}calibrationCompareItems[i]=event.item;resolve();},
      error:()=>{toast(`Cannot load calibration image ${i+1}. Run calibration again.`,true);resolve();}});
  })));
  if(generation!==calibrationReviewGeneration)return;
  clipCalibrationPair(true);calibrationOnTop(0);refreshAutomaticCalibrationControls();
}
async function syncAutomaticCalibration() {
  refreshAutomaticCalibrationControls();
  const c=state?.calibration;
  if(!c?.ready || automaticCalibrationResult?.id===c.id || automaticCalibrationLoading)return;
  automaticCalibrationLoading=true;
  try{await showAutomaticCalibration(await api('/calibration/auto/'+c.id));}catch(error){toast(error.message,true);}
  finally{automaticCalibrationLoading=false;}
}
function initializeAutomaticCalibration() {
  on('autoCalibrationStart',async()=>{
    cancelImagePan();stopCalibrationBlink();++calibrationReviewGeneration;automaticCalibrationResult=null;automaticCalibrationApproved=false;
    $('autoCalibrationReview').hidden=true;calibrationCompareViewer?.close();calibrationCompareItems=[];
    await api('/calibration/auto',{settings:settings(),direction:Number($('autoCalibrationDirection').value)});
  });
  $('calibrationTop1').onclick=()=>{stopCalibrationBlink();calibrationOnTop(0);};$('calibrationTop2').onclick=()=>{stopCalibrationBlink();calibrationOnTop(1);};
  $('calibrationBlink').onchange=()=>{clearInterval(calibrationBlinkTimer);if($('calibrationBlink').checked)calibrationBlinkTimer=setInterval(()=>{if(document.hidden || $('calibrationPanel').hidden){stopCalibrationBlink();return;}calibrationOnTop(1-calibrationTop);},350);};
  $('calibrationOverlap').onchange=()=>clipCalibrationPair(true);$('calibrationCompareFit').onclick=calibrationCompareFit;
  $('calibrationCompareActual').onclick=()=>{const item=calibrationCompareItems[0];if(item)calibrationCompareViewer.viewport.zoomTo(item.imageToViewportZoom(1));};
  $('autoCalibrationChecked').onchange=refreshAutomaticCalibrationControls;
  on('autoCalibrationApprove',async()=>{
    if(!automaticCalibrationResult || !$('autoCalibrationChecked').checked || calibrationCompareItems.filter(Boolean).length!==2)throw Error('Load and check both images before approving.');
    const result=await api(`/calibration/auto/${automaticCalibrationResult.id}/approve`,{settings:settings()});
    $('fovWidth').value=result.widthMm.toFixed(6);$('stageRotationDegrees').value=result.rotationDegrees.toFixed(6);
    automaticCalibrationApproved=true;deriveFrameHeight();captureGridEdited=true;await updatePlan();
    $('autoCalibrationApproval').textContent='Approved: frame size and stage → image angle applied. Save defaults to keep them.';stopCalibrationBlink();refreshAutomaticCalibrationControls();
    toast('Measured frame size and angle applied.');
  });
  configIds.forEach(id=>$(id).addEventListener('change',refreshAutomaticCalibrationControls));
}
