const $ = id => document.getElementById(id);
let state, plan, viewer, selectedProject, projects = [], properties = [], previewBusy = false, toastTimer, polling = false, controlsLoaded = false;
let tileItems=[],tileBounds=[],tileOutlines=[],highlightedTile=-1,pinnedTile=-1,viewGeneration=0,loadedView='',viewLoading=false,lastReadyKey='';
let viewFrame=null,viewRegion=null,viewOpenHandlers=null;
const activeActions = new Set();
let savedDefaults=null;
let cameraControlsRevision=0,cameraControlWrites=0;
let projectCardsKey='',renamingProjectId=null,deletingProjectId=null,deletingProject=false;
const pencilIcon='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 5 4 4M4 20l4-1L20 7a2.8 2.8 0 0 0-4-4L4 15z" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/></svg>';
const numeric = ['boardWidth','boardHeight','regionOffsetX','regionOffsetY','fovWidth','fovHeight','overlap','feed','settleMs','discardFrames','rowApproachMm','stageXSign','stageYSign','stageRotationDegrees','maxTravelX','maxTravelY','rotation'];
const checks = ['swapStageAxes','flipX','flipY','autoStitch'];
const configIds = ['scanName','postScanMethod','postScanBlend', ...numeric, ...checks];
function referenceOffset(s){return {x:s.referencePoint==='center'?s.boardWidth/2:s.referencePoint?.endsWith('right')?s.boardWidth:0,y:s.referencePoint==='center'?s.boardHeight/2:s.referencePoint?.startsWith('bottom')?s.boardHeight:0};}
const escapeHtml = text => String(text ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function toast(message, error = false) { clearTimeout(toastTimer); $('toast').textContent = message; $('toast').classList.toggle('error', error); $('toast').hidden = false; toastTimer = setTimeout(() => $('toast').hidden = true, error ? 10000 : 4500); }
async function api(path, data, method = data === undefined ? 'GET' : 'POST') {
  const options = {method, headers: {'X-BoardScope':'1'}};
  if (data instanceof FormData) options.body = data;
  else if (data !== undefined) { options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(data); }
  const response = await fetch('/api' + path, options);
  const body = await response.text(); let result; try { result = body ? JSON.parse(body) : null; } catch { result = body; }
  if (!response.ok) throw Error(result?.error || `Request failed (${response.status})`);
  return result;
}
function deriveFrameHeight() {
  const option=$('cameraMode').selectedOptions[0],c=state?.camera;
  let w=c?.connected?c.width:Number(option?.dataset.width),h=c?.connected?c.height:Number(option?.dataset.height);
  if(!(w>0&&h>0)){w=1920;h=1080;}
  if(Number($('rotation').value)%180)[w,h]=[h,w];
  $('fovHeight').value=(Number($('fovWidth').value)*h/w).toFixed(4);
}
function settings() { deriveFrameHeight();const s = {name: $('scanName').value,testGrid:false,gridYSign:-1,referencePoint:'center',stitchMethod:$('postScanMethod').value,stitchBlendMode:$('postScanBlend').value,stitchOptions:savedDefaults?.scan.stitchOptions||null}; numeric.forEach(id => s[id] = Number($(id).value)); checks.forEach(id => s[id] = $(id).checked); return s; }
function persist() { localStorage.setItem('boardscope-settings', JSON.stringify(settings())); }
async function updatePlan() { updateJogLabels();try { plan = await api('/plan', settings()); drawGrid(); persist(); } catch(e) { $('gridStats').textContent = e.message; } }
function jogDelta(button) {
  const [x,y]=button.dataset.jog.split(',').map(Number);
  return JogMapping.map(x,y,Number($('jogStep').value),{stageXSign:Number($('stageXSign').value),stageYSign:Number($('stageYSign').value),stageRotationDegrees:Number($('stageRotationDegrees').value),swapStageAxes:$('swapStageAxes').checked});
}
function updateJogLabels() {
  document.querySelectorAll('[data-jog]').forEach(b=>{const p=jogDelta(b);b.title=`Configured jog → stage ΔX ${p.x} mm, ΔY ${p.y} mm`;});
}
function applyScanSettings(s) {
  numeric.forEach(id=>{if(s[id]!==undefined)$(id).value=s[id];});
  checks.forEach(id=>{if(s[id]!==undefined)$(id).checked=s[id];});
  $('scanName').value=s.name||'PCB inspection';
  $('postScanMethod').value=s.stitchMethod||'stage';$('postScanBlend').value=s.stitchBlendMode||'feather';
}
async function saveDefaults() {
  const option=$('cameraMode').selectedOptions[0];
  const list=state?.camera.connected?await api('/camera/properties'):[];
  const sameCamera=savedDefaults?.cameraDeviceId===$('cameraDevice').value;
  savedDefaults=await api('/defaults',{scan:settings(),platform:PlatformController.settings(),cameraDeviceId:$('cameraDevice').value,
    cameraMode:option?.dataset.width?{index:Number(option.value),width:Number(option.dataset.width),height:Number(option.dataset.height),fps:Number(option.dataset.fps),format:option.dataset.format}:null,
    cameraProperties:list.length?list.map(p=>({group:p.group,name:p.name,value:p.value,auto:p.auto})):(sameCamera?savedDefaults.cameraProperties:[]),
    port:$('port').value,baud:Number($('baud').value),statusInches:$('statusUnits').value==='in',jogStep:Number($('jogStep').value)});
  PlatformController.receive(savedDefaults.platform,true);
  $('defaultsStatus').textContent='Defaults saved. They will load next time you open the app.';
  toast('Current settings saved as defaults.');
}
async function action(button, work) { if(activeActions.has(button))return;if(livePan?.busy && button.id!=='hold'){toast('Wait for the drag movement to finish.');return;}activeActions.add(button);button.disabled = true; try { await work(); await poll(); } catch(e) { toast(e.message, true); } finally { activeActions.delete(button);button.disabled = false; if (state) applyState(); } }
function on(id, work) { $(id).addEventListener('click', () => action($(id), work)); }
async function refreshDevices() {
  const data = await api('/devices'); const oldCamera = $('cameraDevice').value || savedDefaults?.cameraDeviceId;
  $('cameraDevice').innerHTML = '<option value="">Select a camera</option>' + data.cameras.map(c => `<option value="${escapeHtml(c.id)}">${escapeHtml(c.name)}</option>`).join('');
  if (data.cameras.some(c => c.id === oldCamera)) $('cameraDevice').value = oldCamera;
  else if (data.cameras.length) $('cameraDevice').value = (data.cameras.find(c=>c.id.includes('usb#')) || data.cameras[0]).id;
  const oldPort = $('port').value || savedDefaults?.port;
  $('port').innerHTML = data.ports.map(p => `<option>${escapeHtml(p)}</option>`).join('') || '<option value="">No serial ports</option>';
  if (data.ports.includes(oldPort)) $('port').value = oldPort; else if (data.ports.includes('COM4')) $('port').value = 'COM4';
  await loadModes();
}
async function loadModes() {
  const id = $('cameraDevice').value;
  $('cameraMode').innerHTML = '<option value="">Choose a device first</option>';
  if (!id) return;
  const allModes = await api('/camera/modes?deviceId=' + encodeURIComponent(id));
  const modes = allModes.filter((m,i,a)=>a.findIndex(x=>x.width===m.width && x.height===m.height && x.fps===m.fps && x.format===m.format)===i);
  $('cameraMode').innerHTML = modes.map(m => `<option value="${m.index}" data-width="${m.width}" data-height="${m.height}" data-fps="${m.fps}" data-format="${escapeHtml(m.format)}">${m.width} × ${m.height} · ${m.fps.toFixed(1)} fps · ${escapeHtml(m.format)}</option>`).join('') || '<option value="">No supported modes found</option>';
  const saved=savedDefaults?.cameraDeviceId===id?savedDefaults.cameraMode:null;
  const preferred = modes.find(m=>saved&&m.width===saved.width&&m.height===saved.height&&m.format===saved.format&&Math.abs(m.fps-saved.fps)<.1)||modes.find(m=>m.format==='MJPG');if(preferred)$('cameraMode').value=preferred.index;
  await updatePlan();
}
function renderControls(list) {
  cameraControlsRevision++;
  properties = list; $('controlCount').textContent = list.length;
  $('properties').innerHTML = list.map((p,i) => `<div class="property"><div class="property-head"><label for="prop-${i}">${escapeHtml(p.name)}</label>${p.canAuto ? `<label class="check"><input id="auto-${i}" type="checkbox" ${p.auto ? 'checked':''}> Auto</label>` : ''}</div><div class="property-input"><input id="prop-${i}" type="range" min="${p.min}" max="${p.max}" step="${p.step}" value="${p.value}" ${p.auto || !p.canManual ? 'disabled':''}><input id="value-${i}" type="number" min="${p.min}" max="${p.max}" step="${p.step}" value="${p.value}" ${p.auto || !p.canManual ? 'disabled':''} aria-label="${escapeHtml(p.name)} value"></div><p class="hint">Driver range: ${p.min} to ${p.max} · step ${p.step} · default ${p.default}${p.group==='camera'&&p.name==='Exposure'?`<br>Windows log₂ seconds · nominal ${(1000*2**p.min).toFixed(2)}–${(1000*2**p.max).toFixed(2)} ms${p.auto?' · Auto controls exposure':''}`:''}</p></div>`).join('') || '<p class="hint">This camera does not expose adjustable UVC controls.</p>';
  list.forEach((p,i) => {
    const change = async(value, auto,freeze=false) => { cameraControlWrites++;cameraControlsRevision++;try { renderControls(await (freeze?CameraControlActions.freeze(api,p.group,p.name):api('/camera/property', {group:p.group,name:p.name,value:Number(value),auto}))); } catch(e) { toast(e.message,true); renderControls(await api('/camera/properties')); }finally{cameraControlWrites--;} };
    $('prop-'+i).addEventListener('input', () => $('value-'+i).value = $('prop-'+i).value);
    $('prop-'+i).addEventListener('change', () => change($('prop-'+i).value,false));
    $('value-'+i).addEventListener('change', () => change($('value-'+i).value,false));
    $('auto-'+i)?.addEventListener('change', () => change($('value-'+i).value,$('auto-'+i).checked,!$('auto-'+i).checked));
  });
}
async function refreshAutomaticControlValues(){
  if(cameraControlWrites||activeActions.has($('lockAuto'))||!properties.some(p=>p.auto))return;
  const revision=cameraControlsRevision,list=await api('/camera/properties');
  if(revision!==cameraControlsRevision||cameraControlWrites||activeActions.has($('lockAuto'))||!state?.camera.connected)return;
  if(list.length!==properties.length||list.some((p,i)=>p.group!==properties[i].group||p.name!==properties[i].name||p.auto!==properties[i].auto)){renderControls(list);return;}
  properties=list;
  list.forEach((p,i)=>{if(p.auto){$('prop-'+i).value=p.value;$('value-'+i).value=p.value;}});
}
async function poll() {
  if (polling) return; polling = true;
  try {
    state = await api('/state'); applyState();
    await syncCaptureProgress();
    await syncAutomaticCalibration();
    if (state.camera.connected && !controlsLoaded) { controlsLoaded = true; renderControls(await api('/camera/properties')); }
    else if(state.camera.connected)await refreshAutomaticControlValues();
    if (!state.camera.connected) controlsLoaded = false;
    if (!state.engine.job.busy || projects.some(p => p.id === state.engine.job.projectId && p.status !== 'ready')) await refreshProjects();
  } catch(e) { $('cameraBadge').textContent = 'Service unavailable'; } finally { polling = false; }
}
function applyState() {
  const c = state.camera, m = state.motion, e = state.engine, job = e.job, manual = e.manual;
  $('cameraDot').classList.toggle('online',c.connected); $('motionDot').classList.toggle('online',m.connected);
  document.querySelector('.live-indicator').classList.toggle('online',c.connected);
  $('cameraBadge').textContent = c.connected ? 'Camera connected' : 'Camera offline'; $('motionBadge').textContent = m.connected ? `${m.port} · ${m.state}` : 'Stage offline';
  $('cameraActual').textContent = c.connected ? `${c.width} × ${c.height} · ${c.fps.toFixed(1)} fps · ${c.format}` : '';
  $('liveDimensions').textContent = c.connected ? `${c.width} × ${c.height}` : ''; $('frameCounter').textContent = c.connected ? `${c.frames.toLocaleString()} frames` : '— frames';
  $('position').textContent = m.position ? `X ${m.position.x.toFixed(3)}   Y ${m.position.y.toFixed(3)}` : 'X —   Y —'; $('controllerState').textContent = m.state;
  $('serialLog').textContent = m.log.join('\n'); $('serialLog').scrollTop = $('serialLog').scrollHeight;
  PlatformController.update();
  $('dataFolder').textContent = state.dataFolder;
  $('jobStatus').hidden=!job.busy&&!job.error;
  $('capturePhase').textContent = job.phase.toUpperCase();
  $('captureMessage').textContent = job.message;
  $('captureHint').textContent = job.busy ? `${job.done} / ${job.total}` : '';
  $('progressFill').style.width = job.total ? `${Math.min(100, job.done/job.total*100)}%` : '0%';
  $('stopJob').hidden = !job.busy;
  $('stopJob').disabled = !!job.stopping;
  $('startAuto').disabled = !PlatformController.canScan(state);
  ['rebuildPositions','refinePositions','phasePositions','nccPositions','hybridPositions','parallaxPositions','reblendPositions'].forEach(id=>$(id).disabled=job.busy || !!manual || !selectedProject?.tiles.length);
  $('blendMode').disabled=job.busy || !!manual;
  if(!state.features?.similarityRefinement)$('refinePositions').disabled=true;
  if(!state.features?.phaseCorrelation)$('phasePositions').disabled=true;
  if(!state.features?.normalizedCrossCorrelation)$('nccPositions').disabled=true;
  if(!state.features?.hybridMatching)$('hybridPositions').disabled=true;
  if(!state.features?.parallaxMatching)$('parallaxPositions').disabled=true;
  ['regRotation','regScale','regShift','regSize','regSensitivity','regContrast','regMaxFeatures','regLocalContrast','phaseMinResponse','phaseMinCorrelation','phaseHighPass','nccMinCorrelation','nccMinPeakGap','nccHighPass'].forEach(id=>$(id).disabled=job.busy || !!manual);
  MatchDebug.updateButtons();
  if(manual){$('trialMatchFeatures').disabled=true;$('alternatePairMethod').disabled=true;$('nccPairMethod').disabled=true;$('hybridPairMethod').disabled=true;$('parallaxPairMethod').disabled=true;}
  document.querySelectorAll('[data-stitch],[data-refine],[data-phase],[data-ncc],[data-hybrid],[data-parallax]').forEach(b=>b.disabled=job.busy||!!manual);
  document.querySelectorAll('[data-rename]').forEach(b=>b.disabled=job.busy||!!manual);
  document.querySelectorAll('[data-delete]').forEach(b=>b.disabled=deletingProject||job.busy||!!manual);
  $('renameSelectedProject').hidden=!selectedProject;
  $('renameSelectedProject').disabled=job.busy||!!manual;
  if(!state.features?.similarityRefinement)document.querySelectorAll('[data-refine]').forEach(b=>b.disabled=true);
  if(!state.features?.phaseCorrelation)document.querySelectorAll('[data-phase]').forEach(b=>b.disabled=true);
  if(!state.features?.normalizedCrossCorrelation)document.querySelectorAll('[data-ncc]').forEach(b=>b.disabled=true);
  if(!state.features?.hybridMatching)document.querySelectorAll('[data-hybrid]').forEach(b=>b.disabled=true);
  if(!state.features?.parallaxMatching)document.querySelectorAll('[data-parallax]').forEach(b=>b.disabled=true);
  $('homeMotion').disabled = !m.connected || job.busy || !!manual;
  $('homeMotion').textContent = job.busy && job.phase==='homing' ? 'Homing…' : '⌂ Home stage';
  $('stopJob').textContent = job.stopping ? 'Stopping…' : job.phase==='homing' ? 'Stop homing' : 'Stop after current move';
  document.querySelectorAll('[data-jog]').forEach(b => b.disabled = !m.connected || job.busy);
  ['openCamera','cameraDevice','cameraMode','lockAuto','connectMotion','port','baud','statusUnits','resetMotion','unlockMotion'].forEach(id => $(id).disabled = job.busy || !!manual);
  configIds.forEach(id => $(id).disabled = job.busy || !!manual);
  $('saveDefaults').disabled=job.busy || !!manual;
  ['postScanMethod','postScanBlend'].forEach(id=>$(id).disabled=job.busy||!!manual||!state.features?.stitchHistory);
  StitchHistory.updateControls();
  if(typeof ImageEffectsUI!=='undefined')ImageEffectsUI.updateControls();
  if(typeof LightBackgroundUI!=='undefined')LightBackgroundUI.update();
  ['showCalibration','calibrationFirst'].forEach(id=>$(id).disabled=job.busy||!!manual||!c.connected||!m.connected);
  if(typeof refreshCalibrationControls==='function')refreshCalibrationControls();
  refreshAutomaticCalibrationControls();
  properties.forEach((p,i)=>{
    const blocked=job.busy||!!manual||!!livePan?.busy;
    ['prop-','value-'].forEach(prefix=>{const input=$(prefix+i);if(input)input.disabled=blocked||p.auto||!p.canManual;});
    const auto=$('auto-'+i);if(auto)auto.disabled=blocked;
  });
  for(const button of activeActions)button.disabled=true;
  $('connectMotion').textContent = m.connected ? 'Disconnect FluidNC' : 'Connect FluidNC'; $('openCamera').textContent = c.connected ? 'Reopen camera' : 'Open camera';
  if (!c.connected) { $('preview').hidden = true; $('previewEmpty').hidden = false; $('crosshair').hidden = true; }
  if(livePan?.busy) {
    ['startAuto','homeMotion','moveBoardCenter','saveBoardCenter','connectMotion','openCamera','cameraDevice','cameraMode','port','baud','statusUnits','showCalibration','calibrationFirst','calibrationSecond','autoCalibrationStart','resetMotion','unlockMotion','lockAuto','saveDefaults','rebuildPositions','refinePositions','phasePositions','nccPositions','hybridPositions','parallaxPositions','reblendPositions'].forEach(id=>$(id).disabled=true);
    document.querySelectorAll('[data-jog],#properties input').forEach(b=>b.disabled=true);
    configIds.forEach(id=>$(id).disabled=true);
  }
  refreshPanControls();fitLivePreview();updateCaptureMarker();
  drawGrid();
  if (!job.busy && job.phase === 'ready' && job.projectId) {
    const p = projects.find(p => p.id === job.projectId && p.dzi);
    if (p && `${p.id}/${p.dzi}` !== lastReadyKey) {lastReadyKey=`${p.id}/${p.dzi}`;openProject(p.id).catch(e=>toast(e.message,true));}
  }
}
async function preview() {
  if (!state?.camera.connected || previewBusy || $('liveView').hidden) return; previewBusy = true;
  try {
    const response = await fetch('/api/camera/frame'); if (!response.ok) return;
    const url = URL.createObjectURL(await response.blob()), old = $('preview').dataset.url;
    $('preview').src = url; $('preview').dataset.url = url; if(old) URL.revokeObjectURL(old);
    $('preview').hidden = false; $('previewEmpty').hidden = true; $('crosshair').hidden = false;
    const s = settings(); $('preview').style.transform = `scale(${s.flipX?-1:1},${s.flipY?-1:1}) rotate(${s.rotation}deg)`;
    fitLivePreview();refreshPanControls();
  } finally { previewBusy = false; }
}
function drawGrid() {
  const current=!captureGridEdited && captureProject && (state?.engine.manual?.id===captureProject.id || state?.engine.job.projectId===captureProject.id);
  const gridPlan=current?capturePlan:plan;if(!gridPlan)return;
  const canvas=$('grid'),ctx=canvas.getContext('2d'),viewport=$('gridViewport');
  const n=current?captureProject.tiles.length:(state?.engine.manual?.count??(state?.engine.job.phase==='capturing'?state.engine.job.done:0));
  const pad=34,available=Math.max(240,viewport.clientWidth),cell=Math.max(18,Math.min(48,(available-pad*2)/gridPlan.cols));
  const width=Math.max(available,pad*2+cell*gridPlan.cols),height=Math.max(240,pad*2+cell*gridPlan.rows),dpr=window.devicePixelRatio||1;
  canvas.style.width=`${width}px`;canvas.style.height=`${height}px`;canvas.width=Math.round(width*dpr);canvas.height=Math.round(height*dpr);ctx.scale(dpr,dpr);
  const ox=(width-gridPlan.cols*cell)/2,oy=(height-gridPlan.rows*cell)/2;
  ctx.textAlign='center';ctx.textBaseline='middle';ctx.font='11px Segoe UI';ctx.fillStyle='#6c7f8e';
  for(let col=0;col<gridPlan.cols;col++)if(cell>=24||col%2===0)ctx.fillText(col+1,ox+(col+.5)*cell,oy-14);
  for(let row=0;row<gridPlan.rows;row++)ctx.fillText(row+1,ox-17,oy+(row+.5)*cell);
  for(const p of gridPlan.points) {
    const x=ox+p.col*cell,y=oy+p.row*cell,next=p.index===n,captured=p.index<n;
    ctx.fillStyle=captured?'#208975':next?'#e3b044':'#edf2f5';ctx.strokeStyle=next?'#9f6600':'#c8d6df';ctx.lineWidth=next?2:1;
    ctx.fillRect(x+2,y+2,cell-4,cell-4);ctx.strokeRect(x+2,y+2,cell-4,cell-4);
    if(cell>=24){ctx.fillStyle=captured?'#fff':next?'#412e06':'#647c8c';ctx.font=`${Math.min(14,cell*.29)}px Segoe UI`;ctx.fillText(p.index+1,x+cell/2,y+cell/2);}
  }
  const next=gridPlan.points[n];
  $('gridStats').textContent=`${gridPlan.rows} rows × ${gridPlan.cols} columns`;
  $('gridCount').textContent=`${n} / ${gridPlan.points.length}`;
  $('gridNext').textContent=next?`Row ${next.row+1} · column ${next.col+1}`:'All tiles captured';
  $('grid').setAttribute('aria-label',`${n} of ${gridPlan.points.length} tiles captured. ${$('gridNext').textContent}`);
  $('gridDetails').textContent=`Left → right on every row. Steps ${gridPlan.stepX.toFixed(3)} × ${gridPlan.stepY.toFixed(3)} mm.`;
  $('showCaptureProgress').disabled=!captureProject;
}
async function refreshProjects() {
  projects = await api('/projects');
  if(!projects.length) {
    projectCardsKey='';
    $('projects').innerHTML='<div class="library-empty"><div class="folder-icon">▱</div><h3>Your scans live here</h3><p>Capture a board to start a saved scan.</p></div>';
    return;
  }
  const key=JSON.stringify(projects)+'/'+(selectedProject?.id||'');
  if(key===projectCardsKey)return;projectCardsKey=key;
  $('projects').innerHTML = projects.map(p => `<article class="project ${selectedProject?.id===p.id?'selected':''}"><div class="project-heading">${p.count||p.dzi?`<button class="project-preview" data-open="${p.id}" aria-label="Inspect ${escapeHtml(p.name)}"><img src="/api/projects/${p.id}/preview?v=${encodeURIComponent(p.dzi||p.count)}" alt="Board preview" loading="lazy"></button>`:'<span class="project-preview preview-empty">▱</span>'}<div class="project-title"><div class="project-name"><h3>${escapeHtml(p.name)}</h3><button class="rename-icon quiet" data-rename="${p.id}" title="Rename project" aria-label="Rename ${escapeHtml(p.name)}">${pencilIcon}</button></div><div class="project-date">${new Date(p.created).toLocaleString()}</div></div></div><div class="project-meta"><span>${escapeHtml(p.status.toUpperCase())}</span><span>${p.count} tiles</span></div>${p.dzi?`<p>${p.width.toLocaleString()} × ${p.height.toLocaleString()} pixels<br>${p.refined?`${MatchDebug.methodName(p.registrationMethod)} · ${p.acceptedMatches} accepted pairs`:'Stage placement'}</p>`:''}${p.error?`<p class="project-error">${escapeHtml(p.error)}</p>`:''}<div class="button-row">${p.count||p.dzi?`<button data-open="${p.id}">${p.count?'Inspect tiles':'View mosaic'}</button>`:''}${p.count?`<button data-parallax="${p.id}" class="primary">Restitch with parallax-aware matching</button><button data-hybrid="${p.id}" class="primary">Restitch with hybrid matching</button><button data-refine="${p.id}" class="secondary">Restitch with feature matching</button><button data-phase="${p.id}" class="secondary">Restitch with phase correlation</button><button data-ncc="${p.id}" class="secondary">Restitch with NCC</button><button data-stitch="${p.id}">Restitch from stage</button>`:''}<button data-folder="${p.id}" title="Open originals and position files">Folder</button><button data-delete="${p.id}" class="danger" title="Delete this scan from disk">Delete</button></div></article>`).join('');
  document.querySelectorAll('[data-rename]').forEach(b=>{b.onclick=()=>editProjectName(b.dataset.rename);b.disabled=!!state?.engine.job.busy||!!state?.engine.manual;});
  document.querySelectorAll('.project-preview img').forEach(img=>img.onerror=()=>{img.hidden=true;});
  document.querySelectorAll('[data-open]').forEach(b => b.onclick=()=>action(b,()=>openProject(b.dataset.open)));
  document.querySelectorAll('[data-stitch]').forEach(b => b.onclick=()=>action(b,()=>api(`/projects/${b.dataset.stitch}/stitch`,{blendMode:requestedBlend(b.dataset.stitch)})));
  document.querySelectorAll('[data-refine]').forEach(b=>b.onclick=()=>action(b,()=>api(`/projects/${b.dataset.refine}/stitch`,{refine:true,blendMode:requestedBlend(b.dataset.refine),registration:registrationSettings(),...(selectedProject?.id===b.dataset.refine?{placement:placementSettings()}:{})})));
  document.querySelectorAll('[data-phase]').forEach(b=>b.onclick=()=>action(b,()=>api(`/projects/${b.dataset.phase}/stitch`,{refine:true,blendMode:requestedBlend(b.dataset.phase),registration:registrationSettings('phase'),...(selectedProject?.id===b.dataset.phase?{placement:placementSettings()}:{})})));
  document.querySelectorAll('[data-parallax]').forEach(b=>b.onclick=()=>action(b,()=>api(`/projects/${b.dataset.parallax}/stitch`,{refine:true,blendMode:requestedBlend(b.dataset.parallax),registration:registrationSettings('parallax'),...(selectedProject?.id===b.dataset.parallax?{placement:placementSettings()}:{})})));
  document.querySelectorAll('[data-hybrid]').forEach(b=>b.onclick=()=>action(b,()=>api(`/projects/${b.dataset.hybrid}/stitch`,{refine:true,blendMode:requestedBlend(b.dataset.hybrid),registration:registrationSettings('hybrid'),...(selectedProject?.id===b.dataset.hybrid?{placement:placementSettings()}:{})})));
  document.querySelectorAll('[data-ncc]').forEach(b=>b.onclick=()=>action(b,()=>api(`/projects/${b.dataset.ncc}/stitch`,{refine:true,blendMode:requestedBlend(b.dataset.ncc),registration:registrationSettings('ncc'),...(selectedProject?.id===b.dataset.ncc?{placement:placementSettings()}:{})})));
  document.querySelectorAll('[data-stitch],[data-refine],[data-phase],[data-ncc],[data-hybrid],[data-parallax]').forEach(b=>b.disabled=!!state?.engine.job.busy||!!state?.engine.manual);
  if(!state?.features?.similarityRefinement)document.querySelectorAll('[data-refine]').forEach(b=>b.disabled=true);
  if(!state?.features?.phaseCorrelation)document.querySelectorAll('[data-phase]').forEach(b=>b.disabled=true);
  if(!state?.features?.normalizedCrossCorrelation)document.querySelectorAll('[data-ncc]').forEach(b=>b.disabled=true);
  if(!state?.features?.hybridMatching)document.querySelectorAll('[data-hybrid]').forEach(b=>b.disabled=true);
  if(!state?.features?.parallaxMatching)document.querySelectorAll('[data-parallax]').forEach(b=>b.disabled=true);
  document.querySelectorAll('[data-delete]').forEach(b=>{b.onclick=()=>askDeleteProject(b.dataset.delete);b.disabled=deletingProject||!!state?.engine.job.busy||!!state?.engine.manual;});
  document.querySelectorAll('[data-folder]').forEach(b => b.onclick=()=>action(b,()=>api(`/projects/${b.dataset.folder}/folder`,{})));
}
function askDeleteProject(id) {
  const p=projects.find(p=>p.id===id);if(!p)return;
  deletingProjectId=id;$('deleteProjectName').textContent=p.name;$('deleteError').hidden=true;
  $('deleteDialog').showModal();$('cancelDelete').focus();
}
function clearSelectedProject() {
  selectedProject=null;lastReadyKey='';++viewGeneration;viewLoading=false;loadedView='';
  cancelViewOpen();viewFrame=viewRegion=null;
  MatchDebug.reset(null);
  StitchHistory.cancelLoad();StitchHistory.render(null);
  if(typeof ImageEffectsUI!=='undefined'){ $('effectsDialog').close();ImageEffectsUI.cleanup();}
  viewer?.close();viewer?.clearOverlays();tileItems=[];tileBounds=[];tileOutlines=[];highlightedTile=pinnedTile=-1;
  $('tileInspector').hidden=true;$('tileInfo').hidden=true;
  $('renameSelectedProject').hidden=true;$('workspaceTitle').textContent='Your board, under the lens';
  $('mosaicEmpty').hidden=false;
  $('mosaicMode').disabled=true;$('tileLayout').disabled=true;$('registrationReport').hidden=true;
}
function editProjectName(id) {
  const p=projects.find(p=>p.id===id);if(!p)return;
  renamingProjectId=id;$('projectName').value=p.name;$('renameError').hidden=true;
  $('renameDialog').showModal();$('projectName').focus();$('projectName').select();
}
function tab(which) { const live=which==='live';if(!live)cancelImagePan();$('liveView').hidden=!live; $('mosaicView').hidden=live; $('liveTab').classList.toggle('active',live); $('mosaicTab').classList.toggle('active',!live);fitLivePreview();showCaptureProgress();StitchHistory.updateViewing();refreshPanControls(); }
async function openProject(id,provided=null) {
  if(!provided)StitchHistory.cancelLoad();
  let p = provided||await api('/projects/'+id); if(!p.dzi && !p.tiles.length&&!p.stitches?.length){if(provided){++viewGeneration;cancelViewOpen();viewFrame=viewRegion=null;viewLoading=false;selectedProject=p;StitchHistory.render(p);viewer?.close();$('mosaicEmpty').hidden=false;}return;}
  if(!provided&&state?.features?.stitchHistory&&p.stitchCatalogVersion<1&&!state.engine.job.busy&&!state.engine.manual)p=await api(`/projects/${id}/stitches/initialize`,{});
  captureFollowing=false;
  if(selectedProject?.id!==id) {pinnedTile=highlightedTile=-1;$('mosaicMode').value=p.tiles.length?'tiles':'stitched';$('blendMode').value='sharp';$('regSize').value='0';}
  if(p.registration&&selectedProject?.dzi!==p.dzi)$('tileLayout').value='refined';
  if(!p.registration&&$('tileLayout').value==='refined')$('tileLayout').value='stage';
  $('refinedLayout').disabled=!p.registration;
  $('stageMosaicOption').disabled=!p.stageMosaic||p.stageMosaic.dzi===p.dzi;
  $('previousMosaicOption').disabled=!p.previousMosaic||p.previousMosaic.dzi===p.dzi;
  $('previousMosaicOption').textContent=p.previousMosaic?.label||'Previous stitch';
  $('previousMosaicOption').hidden=$('stageMosaicOption').hidden=!!state?.features?.stitchHistory;
  if(state?.features?.stitchHistory){
    const alignment=StitchHistory.hasAlignment(p),entry=StitchHistory.alignment(p);
    $('savedLayout').disabled=!alignment;$('tilesModeOption').disabled=!!entry&&!alignment;
    if(entry&&!alignment)$('mosaicMode').value='stitched';else if(alignment)$('tileLayout').value='saved';
    if(['stageMosaic','previousMosaic'].includes($('mosaicMode').value))$('mosaicMode').value='stitched';
  }
  selectedProject=p; $('workspaceTitle').textContent=p.name;$('renameSelectedProject').hidden=false; tab('mosaic'); $('mosaicEmpty').hidden=true;
  MatchDebug.reset(p);
  if(!viewer) {
    viewer=OpenSeadragon({id:'osd',showNavigationControl:false,showNavigator:true,navigatorPosition:'BOTTOM_RIGHT',navigatorSizeRatio:.15,backgroundColor:'#121c28',maxZoomPixelRatio:2,visibilityRatio:.5,constrainDuringPan:true,gestureSettingsMouse:{clickToZoom:false}});
    viewer.addHandler('open',()=>{if(loadedView!=='tiles')footprints();});
    MatchDebug.init(viewer);
    new OpenSeadragon.MouseTracker({element:$('osd'),moveHandler:e=>{
      if(viewLoading || !viewer.world.getItemAt(0))return;
      const v=viewer.viewport.pointFromPixel(e.position);
      if(loadedView==='tiles') {
        MatchDebug.pointer(v);
      }
    },leaveHandler:()=>{tileOutlines.forEach(el=>el?.classList.remove('hovered'));highlightTile(pinnedTile);},clickHandler:e=>MatchDebug.click(e)});
  }
  const mapping=p.placement||p.settings;
  $('placeWidth').value=mapping.fovWidth;$('placeHeight').value=(mapping.fovWidth*(p.tiles[0]?.height||p.height)/(p.tiles[0]?.width||p.width)).toFixed(4);
  $('placeXSign').value=mapping.stageXSign;$('placeYSign').value=mapping.stageYSign;$('placeSwap').checked=mapping.swapStageAxes;
  $('placeRotation').value=mapping.stageRotationDegrees||0;
  $('tileInspector').hidden=!p.tiles.length;
  const options=p.registration?.options;
  if(options){$('regRotation').value=options.maxRotationDegrees;$('regScale').value=options.maxScalePercent;$('regShift').value=options.maxShiftMm;$('regSize').value=options.workingSize||0;}
  $('regContrast').value=options?.contrastThreshold??.04;$('regMaxFeatures').value=options?.maxFeatures??6000;$('regLocalContrast').checked=!!options?.enhanceLocalContrast;syncFeaturePreset();
  $('phaseMinResponse').value=options?.phaseMinResponse??.15;$('phaseMinCorrelation').value=options?.phaseMinCorrelation??.3;$('phaseHighPass').checked=options?.phaseHighPass??true;
  $('nccMinCorrelation').value=options?.nccMinCorrelation??.5;$('nccMinPeakGap').value=options?.nccMinPeakGap??0;$('nccHighPass').checked=options?.nccHighPass??true;
  $('registrationSummary').textContent=p.registration?`${p.registration.supportedTiles}/${p.tiles.length} tiles supported · ${p.registration.acceptedPairs} accepted / ${p.registration.rejectedPairs} rejected pairs · median ${p.registration.errorPixels.toFixed(2)} px · ${p.registration.limitedTiles} tiles at a correction limit`:'Refinement uses visual matches plus stage constraints. The existing mosaic stays available for comparison.';
  const shared=p.registration?.stageCorrection;
  if(shared)$('registrationSummary').textContent+=` · ${options?.method==='hybrid'?`${(p.registration.featurePairs||0).toLocaleString()} feature pairs + ${p.registration.translationPairs||0} pixel translation constraints`:['phase','ncc','parallax'].includes(options?.method)?`${p.registration.translationPairs} ${options.method.toUpperCase()} translation constraints`:`${(p.registration.featurePairs||0).toLocaleString()} feature pairs`} solved jointly · shared stage angle correction ${shared.rotationDegrees.toFixed(4)}° · pixels/mm correction ${((shared.scale-1)*100).toFixed(4)}% · estimated frame width ${(mapping.fovWidth/shared.scale).toFixed(4)} mm · tile pixels retain captured scale and orientation`;
  $('registrationReport').hidden=!p.registration;$('registrationReport').href=p.registration?`/projects/${p.id}/${p.registration.reportFile}`:'#';
  $('blendMode').value=['sharp','feather','content'].includes(p.blendMode)?p.blendMode:'sharp';$('historyBlendMode').value=$('blendMode').value;
  StitchHistory.render(p);
  if(typeof ImageEffectsUI!=='undefined')ImageEffectsUI.updateControls();
  if(typeof LightBackgroundUI!=='undefined')LightBackgroundUI.update();
  $('tileLayout').disabled=!p.tiles.length;$('mosaicMode').disabled=!p.tiles.length;
  await renderSelectedView();
  await refreshProjects();
}
function placementSettings() {
  const first=selectedProject.tiles[0];$('placeHeight').value=(Number($('placeWidth').value)*first.height/first.width).toFixed(4);
  const p={fovWidth:Number($('placeWidth').value),fovHeight:Number($('placeWidth').value)*first.height/first.width,stageXSign:Number($('placeXSign').value),stageYSign:Number($('placeYSign').value),stageRotationDegrees:Number($('placeRotation').value),swapStageAxes:$('placeSwap').checked,gridYSign:(selectedProject.placement||selectedProject.settings).gridYSign??-1};
  if(!Number.isFinite(p.fovWidth)||!Number.isFinite(p.fovHeight)||p.fovWidth<.05||p.fovHeight<.05||p.fovWidth>1000||p.fovHeight>1000)throw Error('Enter frame dimensions between 0.05 and 1000 mm.');
  if(!Number.isFinite(p.stageRotationDegrees)||Math.abs(p.stageRotationDegrees)>180)throw Error('Enter a stage → image angle between −180 and 180 degrees.');
  return p;
}
function tileTransform(t) {
  if($('tileLayout').value==='saved')return t.registration?.transform||{a:1,b:0,x:t.x,y:t.y};
  if($('tileLayout').value==='refined'&&t.registration)return t.registration.transform;
  if($('tileLayout').value==='recorded'||!selectedProject.reference)return {a:1,b:0,x:t.nominalX,y:t.nominalY};
  const p=placementSettings(),origin=selectedProject.cropToBoard?selectedProject.reference:{x:selectedProject.tiles[0].stageX,y:selectedProject.tiles[0].stageY};
  const position=JogMapping.stageToImage(t.stageX-origin.x,t.stageY-origin.y,p);
  const reference=selectedProject.cropToBoard?referenceOffset(selectedProject.settings):{x:0,y:0};
  return {a:1,b:0,x:(position.x+reference.x)*t.width/p.fovWidth-(selectedProject.cropToBoard?t.width/2:0),y:(position.y+reference.y)*t.height/p.fovHeight-(selectedProject.cropToBoard?t.height/2:0)};
}
function tilePosition(t){return tileTransform(t);}
function registrationSettings(method='sift'){
  const contrastThreshold=Number($('regContrast').value),maxFeatures=Number($('regMaxFeatures').value);
  if(!Number.isFinite(contrastThreshold)||contrastThreshold<.0001||contrastThreshold>.2)throw Error('Detection contrast threshold must be between 0.0001 and 0.2.');
  if(!Number.isInteger(maxFeatures)||maxFeatures<500||maxFeatures>20000)throw Error('Feature limit must be an integer between 500 and 20000.');
  const phaseMinResponse=Number($('phaseMinResponse').value),phaseMinCorrelation=Number($('phaseMinCorrelation').value);
  if(!Number.isFinite(phaseMinResponse)||phaseMinResponse<.01||phaseMinResponse>1||!Number.isFinite(phaseMinCorrelation)||phaseMinCorrelation<0||phaseMinCorrelation>1)throw Error('Phase response must be 0.01–1 and aligned correlation must be 0–1.');
  const nccMinCorrelation=Number($('nccMinCorrelation').value),nccMinPeakGap=Number($('nccMinPeakGap').value);
  if(!Number.isFinite(nccMinCorrelation)||nccMinCorrelation<.01||nccMinCorrelation>1||!Number.isFinite(nccMinPeakGap)||nccMinPeakGap<0||nccMinPeakGap>1)throw Error('NCC minimum correlation must be 0.01–1 and peak gap must be 0–1.');
  return {method,nccMinCorrelation,nccMinPeakGap,nccHighPass:$('nccHighPass').checked,phaseMinResponse,phaseMinCorrelation,phaseHighPass:$('phaseHighPass').checked,maxRotationDegrees:Number($('regRotation').value),maxScalePercent:Number($('regScale').value),maxShiftMm:Number($('regShift').value),workingSize:Number($('regSize').value),maxFeatures,contrastThreshold,enhanceLocalContrast:$('regLocalContrast').checked};
}
function syncFeaturePreset(){const value=Number($('regContrast').value);$('regSensitivity').value=[.04,.01,.003].includes(value)?String(value):'custom';}
function requestedBlend(id){return selectedProject?.id===id?$('blendMode').value:'sharp';}
function highlightTile(index) {
  highlightedTile=index;
  if(loadedView==='tiles') {
    tileItems.forEach((item,i)=>item?.setOpacity(index<0||i===index?1:.12));
    if(index>=0 && tileItems[index])viewer.world.setItemIndex(tileItems[index],viewer.world.getItemCount()-1);
    else if(index<0 && !viewLoading)tileItems.forEach((item,i)=>{if(item)viewer.world.setItemIndex(item,i);});
    tileOutlines.forEach((element,i)=>{element.classList.toggle('selected',i===index);element.style.opacity=index<0||i===index?'1':'.15';});
  }
  const t=selectedProject?.tiles[index];$('tileInfo').hidden=!t;
  if(!t){MatchDebug.paint(-1);return;}
  const first=selectedProject.tiles[0],pos=tilePosition(t),measured=!!selectedProject.reference;
  $('tileInfo').innerHTML=`<strong>Tile ${index+1} / ${selectedProject.tiles.length} · row ${t.row+1}, column ${t.col+1}</strong><span class="filename">${escapeHtml(t.file)}</span><dl><dt>${measured?'Gross stage':'Grid estimate'} mm</dt><dd>X ${t.stageX.toFixed(3)} · Y ${t.stageY.toFixed(3)}</dd><dt>From tile 1 mm</dt><dd>ΔX ${(t.stageX-first.stageX).toFixed(3)} · ΔY ${(t.stageY-first.stageY).toFixed(3)}</dd><dt>Original estimate px</dt><dd>${t.nominalX.toFixed(1)}, ${t.nominalY.toFixed(1)}</dd><dt>Displayed position px</dt><dd>${pos.x.toFixed(1)}, ${pos.y.toFixed(1)}</dd></dl>`;
  if(t.registration){const r=t.registration,g=SimilarityView.geometry(r.transform,t.width,t.height);$('tileInfo').innerHTML+=`<dl><dt>Centre correction px</dt><dd>${r.deltaX.toFixed(2)}, ${r.deltaY.toFixed(2)}</dd><dt>Rotation / scale</dt><dd>${g.degrees.toFixed(4)}° / ${(Math.hypot(r.transform.a,r.transform.b)*100).toFixed(4)}%</dd><dt>Support / error</dt><dd>${r.links} neighbors / ${r.errorPixels.toFixed(2)} px</dd><dt>Registration</dt><dd>${escapeHtml(r.status)}</dd></dl>`;}
  if(t.registration?.mechanicalDeltaX!=null)$('tileInfo').innerHTML+=`<dl><dt>Mechanical correction px</dt><dd>${t.registration.mechanicalDeltaX.toFixed(2)}, ${t.registration.mechanicalDeltaY.toFixed(2)}</dd></dl>`;
  $('tileInfo').innerHTML+=MatchDebug.tileInfo(index);
  MatchDebug.paint(index);
}
function addTileFrame(location,degrees,className,label) {
  // Overlay ignores Rect.degrees and clears its element's CSS transform. Keep
  // its location unrotated and rotate a child around the same centre as the image.
  const wrapper=document.createElement('div');wrapper.className='tile-frame-wrapper';
  const frame=document.createElement('div');frame.className=className;
  frame.style.transform=`rotate(${degrees}deg)`;
  if(label!==undefined){const span=document.createElement('span');span.textContent=label;frame.appendChild(span);}
  wrapper.appendChild(frame);
  viewer.addOverlay({element:wrapper,location,checkResize:false,rotationMode:OpenSeadragon.OverlayRotationMode.EXACT});
  return frame;
}
function cancelViewOpen(){
  if(!viewOpenHandlers)return;
  viewer.removeHandler('open',viewOpenHandlers.open);
  viewer.removeHandler('open-failed',viewOpenHandlers.failed);
  viewOpenHandlers=null;
}
function rememberViewRegion(projectId){
  if(!viewLoading&&viewFrame?.projectId===projectId&&viewer.world.getItemCount()>0)
    viewRegion={projectId,bounds:ViewNavigation.toPixels(viewer.viewport.getBounds(true),viewFrame)};
  if(viewRegion?.projectId!==projectId)viewRegion=null;
  return viewRegion?.bounds;
}
function restoreViewRegion(frame,region,home){
  const bounds=region?ViewNavigation.toViewport(region,frame):home;
  if(bounds)viewer.viewport.fitBounds(new OpenSeadragon.Rect(bounds.x,bounds.y,bounds.width,bounds.height),true);
  viewFrame=frame;viewLoading=false;
}
async function renderSelectedView() {
  if(!viewer || !selectedProject)return;
  StitchHistory.updateViewing();
  if(!selectedProject.tiles.length&&!selectedProject.dzi){++viewGeneration;cancelViewOpen();viewFrame=viewRegion=null;viewLoading=false;viewer.close();tileItems=[];tileOutlines=[];$('mosaicEmpty').hidden=false;return;}
  const p=selectedProject,mode=$('mosaicMode').value;
  if(mode!=='tiles'&&!(mode==='stageMosaic'?p.stageMosaic?.dzi:mode==='previousMosaic'?p.previousMosaic?.dzi:p.dzi)){$('mosaicMode').value='tiles';toast('Build this mosaic first.');return renderSelectedView();}
  const geometries=mode==='tiles'?p.tiles.map(t=>SimilarityView.geometry(tileTransform(t),t.width,t.height)):[];
  const region=rememberViewRegion(p.id);
  const generation=++viewGeneration;viewLoading=true;loadedView=mode;
  cancelViewOpen();
  MatchDebug.stopRequest();MatchDebug.pair=MatchDebug.data=null;MatchDebug.updatePanel();MatchDebug.scheduleDraw();
  viewer.close();viewer.clearOverlays();tileItems=[];tileBounds=[];tileOutlines=[];
  $('tileLayout').disabled=mode!=='tiles'||!p.tiles.length;
  $('showFootprints').disabled=mode==='tiles'||mode==='previousMosaic';
  if(mode!=='tiles') {
    const version=mode==='stageMosaic'?p.stageMosaic:mode==='previousMosaic'?p.previousMosaic:p;
    const frame={projectId:p.id,x:version.originX||0,y:version.originY||0,scale:version.width};
    const open=()=>{if(generation!==viewGeneration)return;cancelViewOpen();restoreViewRegion(frame,region);};
    const failed=()=>{if(generation!==viewGeneration)return;cancelViewOpen();viewFrame=null;viewLoading=false;};
    viewOpenHandlers={open,failed};viewer.addHandler('open',open);viewer.addHandler('open-failed',failed);
    $('tileInfo').hidden=true;viewer.open(`/projects/${p.id}/${version.dzi}`);
    return;
  }
  if(!p.tiles.length){viewLoading=false;return;}
  const minX=Math.floor(Math.min(...geometries.map(g=>g.left))),minY=Math.floor(Math.min(...geometries.map(g=>g.top)));
  const width=Math.ceil(Math.max(...geometries.map(g=>g.right))-minX),height=Math.ceil(Math.max(...geometries.map(g=>g.bottom))-minY);
  const scale=p.tiles[0].width;
  tileBounds=geometries.map(g=>new OpenSeadragon.Rect((g.left-minX)/scale,(g.top-minY)/scale,(g.right-g.left)/scale,(g.bottom-g.top)/scale));
  await Promise.all(p.tiles.map((t,i)=>new Promise(resolve=>{
    const g=geometries[i];
    const options={x:(g.x-minX)/scale,y:(g.y-minY)/scale,width:g.width/scale,opacity:highlightedTile<0||highlightedTile===i?1:.12,
      success:event=>{
        if(generation!==viewGeneration){viewer.world.removeItem(event.item);resolve();return;}
        tileItems[i]=event.item;event.item.setRotation(g.degrees,true);
        tileOutlines[i]=addTileFrame(event.item.getBoundsNoRotate(),g.degrees,'tile-outline',i+1);resolve();
      },error:()=>{if(generation===viewGeneration)toast(`Cannot load tile ${i+1}.`,true);resolve();}};
    if(t.sourceDzi)viewer.addTiledImage({...options,tileSource:`/projects/${p.id}/${t.sourceDzi}`});
    else viewer.addSimpleImage({...options,url:`/projects/${p.id}/${t.file}`});
  })));
  if(generation!==viewGeneration)return;
  restoreViewRegion({projectId:p.id,x:minX,y:minY,scale},region,{x:0,y:0,width:width/scale,height:height/scale});
  highlightTile(highlightedTile);
}
function footprints() {
  if(!viewer||loadedView==='tiles')return;viewer.clearOverlays();if(loadedView==='previousMosaic'||!$('showFootprints').checked||!selectedProject)return;
  if(state?.features?.stitchHistory&&StitchHistory.alignment()&&!StitchHistory.hasAlignment())return;
  const image=viewer.world.getItemAt(0);if(!image)return;const p=selectedProject,version=loadedView==='stageMosaic'?p.stageMosaic:p;
  for(const t of p.tiles){const transform=loadedView==='stageMosaic'?{a:1,b:0,x:t.baseX??t.x,y:t.baseY??t.y}:t.registration?.transform||{a:1,b:0,x:t.x,y:t.y};
    const g=SimilarityView.geometry(transform,t.width,t.height);
    const rect=image.imageToViewportRectangle(g.x-(version.originX||0),g.y-(version.originY||0),g.width,g.height);
    addTileFrame(rect,g.degrees,'footprint');}
}
on('refreshDevices',refreshDevices);on('refreshProjects',refreshProjects);
$('regSensitivity').addEventListener('change',()=>{if($('regSensitivity').value!=='custom')$('regContrast').value=$('regSensitivity').value;});
$('regContrast').addEventListener('input',syncFeaturePreset);
$('renameSelectedProject').onclick=()=>{if(selectedProject)editProjectName(selectedProject.id);};
$('cancelRename').onclick=()=>$('renameDialog').close();
$('cancelDelete').onclick=()=>$('deleteDialog').close();
$('deleteDialog').addEventListener('close',()=>{deletingProjectId=null;});
$('deleteDialog').addEventListener('cancel',e=>{if(deletingProject)e.preventDefault();});
$('deleteForm').onsubmit=async e=>{
  e.preventDefault();const id=deletingProjectId;if(!id||deletingProject)return;
  deletingProject=true;$('deleteProject').disabled=true;$('cancelDelete').disabled=true;
  $('deleteProject').textContent='Deleting…';$('deleteError').hidden=true;if(state)applyState();
  try {
    await api('/projects/'+id,undefined,'DELETE');
    if(selectedProject?.id===id)clearSelectedProject();
    $('deleteDialog').close();await poll();await refreshProjects();toast('Scan deleted from disk.');
  }catch(error){$('deleteError').textContent=error.message;$('deleteError').hidden=false;}
  finally {
    deletingProject=false;$('deleteProject').disabled=false;$('cancelDelete').disabled=false;
    $('deleteProject').textContent='Delete permanently';if(state)applyState();
  }
};
$('renameDialog').addEventListener('close',()=>{renamingProjectId=null;});
$('renameForm').onsubmit=async e=>{
  e.preventDefault();const id=renamingProjectId;if(!id)return;
  $('saveProjectName').disabled=true;$('renameError').hidden=true;
  try {
    const name=await api('/projects/'+id+'/name',{name:$('projectName').value});
    if(selectedProject?.id===id){selectedProject.name=name;selectedProject.settings.name=name;$('workspaceTitle').textContent=name;}
    $('renameDialog').close();await refreshProjects();toast('Project renamed.');
  }catch(error){$('renameError').textContent=error.message;$('renameError').hidden=false;}
  finally{$('saveProjectName').disabled=false;}
};
$('cameraDevice').addEventListener('change',()=>loadModes().catch(e=>toast(e.message,true)));
on('openCamera',async()=>{
  if(!$('cameraDevice').value || $('cameraMode').value==='')throw Error('Select a camera and capture mode.');
  await api('/camera/open',{deviceId:$('cameraDevice').value,modeIndex:Number($('cameraMode').value)});
  const available=await api('/camera/properties'),failures=[];
  if(savedDefaults?.cameraDeviceId===$('cameraDevice').value)for(const p of savedDefaults.cameraProperties) {
    const control=available.find(c=>c.group===p.group&&c.name===p.name);
    if(!control)continue;
    try{await api('/camera/property',p);}catch(e){failures.push(`${p.name}: ${e.message}`);}
  }
  controlsLoaded=false;tab('live');toast(failures.length?`Camera opened; some saved controls could not be restored: ${failures.join('; ')}`:'Camera opened. Saved camera controls restored.',!!failures.length);
});
on('lockAuto',async()=>{renderControls(await CameraControlActions.lockAll(api));toast('Auto controls locked using freshly read driver values. Check that the image stayed the same.');});
on('connectMotion',async()=>{if(state?.motion.connected)await api('/motion/disconnect',{});else await api('/motion/connect',{port:$('port').value,baud:Number($('baud').value),statusInches:$('statusUnits').value==='in'});});
on('homeMotion',async()=>{await api('/motion/home',{});tab('live');toast('Homing started.');});
document.querySelectorAll('[data-jog]').forEach(b=>b.addEventListener('click',()=>action(b,()=>api('/motion/jog',{...jogDelta(b),feed:Math.min(500,Number($('feed').value))}))));
$('jogStep').addEventListener('change',updateJogLabels);on('saveDefaults',saveDefaults);
on('hold',()=>{cancelImagePan();return api('/motion/hold',{});});on('resetMotion',()=>api('/motion/reset',{}));on('unlockMotion',()=>api('/motion/unlock',{}));
on('startAuto',async()=>{await api('/scan',{...settings(),testGrid:false});tab('live');});
on('stopJob',()=>api('/stop',{}));
$('mosaicMode').onchange=()=>renderSelectedView().catch(e=>toast(e.message,true));
$('tileLayout').onchange=()=>renderSelectedView().catch(e=>toast(e.message,true));
['placeWidth','placeXSign','placeYSign','placeSwap','placeRotation'].forEach(id=>$(id).addEventListener('change',()=>{
  try{placementSettings();if($('mosaicMode').value==='tiles' && $('tileLayout').value==='stage')renderSelectedView().catch(e=>toast(e.message,true));}catch(e){toast(e.message,true);}
}));
$('fovWidth').addEventListener('input',deriveFrameHeight);$('cameraMode').addEventListener('change',updatePlan);
$('placeWidth').addEventListener('input',()=>{const first=selectedProject?.tiles[0];if(first)$('placeHeight').value=(Number($('placeWidth').value)*first.height/first.width).toFixed(4);});

on('rebuildPositions',()=>api(`/projects/${selectedProject.id}/stitch`,{placement:placementSettings(),blendMode:requestedBlend(selectedProject.id)}));
on('refinePositions',()=>api(`/projects/${selectedProject.id}/stitch`,{placement:placementSettings(),refine:true,registration:registrationSettings(),blendMode:requestedBlend(selectedProject.id)}));
on('phasePositions',()=>api(`/projects/${selectedProject.id}/stitch`,{placement:placementSettings(),refine:true,registration:registrationSettings('phase'),blendMode:requestedBlend(selectedProject.id)}));
on('parallaxPositions',()=>api(`/projects/${selectedProject.id}/stitch`,{placement:placementSettings(),refine:true,registration:registrationSettings('parallax'),blendMode:requestedBlend(selectedProject.id)}));
on('hybridPositions',()=>api(`/projects/${selectedProject.id}/stitch`,{placement:placementSettings(),refine:true,registration:registrationSettings('hybrid'),blendMode:requestedBlend(selectedProject.id)}));
on('nccPositions',()=>api(`/projects/${selectedProject.id}/stitch`,{placement:placementSettings(),refine:true,registration:registrationSettings('ncc'),blendMode:requestedBlend(selectedProject.id)}));
on('reblendPositions',()=>api(`/projects/${selectedProject.id}/stitch`,{renderOnly:true,blendMode:requestedBlend(selectedProject.id),...(state?.features?.stitchHistory?{stitchId:selectedProject.activeStitchId}:{})}));
$('liveTab').onclick=()=>tab('live');$('mosaicTab').onclick=()=>tab('mosaic');
$('zoomIn').onclick=()=>displayViewer()?.viewport.zoomBy(1.5);$('zoomOut').onclick=()=>displayViewer()?.viewport.zoomBy(1/1.5);$('fitView').onclick=()=>{const v=displayViewer();if(v===captureViewer && captureProject)v.viewport.fitBounds(new OpenSeadragon.Rect(0,0,1,captureProject.settings.boardHeight/captureProject.settings.boardWidth));else v?.viewport.goHome();};$('fullView').onclick=()=>{const v=displayViewer();v?.setFullScreen(!v.isFullPage());};
$('actualSize').onclick=()=>{const v=displayViewer();if(v?.world.getItemAt(0))v.viewport.zoomTo(v.world.getItemAt(0).imageToViewportZoom(1));};$('showFootprints').onchange=footprints;
let planTimer;configIds.forEach(id=>$(id).addEventListener('change',()=>{if(!state?.engine.job.busy && !state?.engine.manual)captureGridEdited=true;clearTimeout(planTimer);planTimer=setTimeout(updatePlan,150);}));
initializeCaptureNavigation();initializeAutomaticCalibration();
(async()=>{
  try {
    savedDefaults=await api('/defaults');
    if(savedDefaults){applyScanSettings(savedDefaults.scan);PlatformController.receive(savedDefaults.platform,true);$('baud').value=savedDefaults.baud;$('statusUnits').value=savedDefaults.statusInches?'in':'mm';$('jogStep').value=savedDefaults.jogStep;$('port').innerHTML=`<option>${escapeHtml(savedDefaults.port)}</option>`;$('defaultsStatus').textContent='Saved defaults loaded.';}
    else {const draft=JSON.parse(localStorage.getItem('boardscope-settings')||'null');if(draft)applyScanSettings(draft);}
  }catch(e){toast(`Could not load defaults: ${e.message}`,true);}
  await updatePlan();await poll();try{await refreshDevices();}catch(e){toast(e.message,true);}await refreshProjects();setInterval(poll,1200);setInterval(()=>preview().catch(()=>{}),350);
})();

StitchHistory.init();
PlatformController.init();
MosaicExport.init();
PagesExportUI.init();

ImageEffectsUI.init();
LightBackgroundUI.init();
