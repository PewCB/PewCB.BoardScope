let calibrationFrames=[null,null],calibrationActive=0,calibrationValue=null;
function refreshCalibrationControls() {
  const ready=state?.camera.connected&&state?.motion.connected&&!state?.engine.job.busy&&!state?.engine.manual;
  $('calibrationSecond').disabled=!ready||!calibrationFrames[0]?.point;
  $('calibrationConfirm').disabled=!ready||!calibrationFrames[calibrationActive];
  $('calibrationApply').disabled=!ready||!calibrationValue;
  document.querySelectorAll('[data-nudge]').forEach(b=>b.disabled=!ready||!calibrationFrames[calibrationActive]);
  for(const button of activeActions)button.disabled=true;
}
function drawCalibration(i) {
  const f=calibrationFrames[i],canvas=$('calibrationCanvas'+i);if(!f){canvas.width=canvas.height=0;return;}
  canvas.width=f.width;canvas.height=f.height;
  const ctx=canvas.getContext('2d');ctx.drawImage(f.bitmap,0,0);
  const p=f.cursor,size=18,line=Math.max(1,f.width/1500);
  ctx.strokeStyle=f.point?'#80ff9b':'#ffe36e';ctx.lineWidth=line;
  ctx.beginPath();ctx.moveTo(p.x-size,p.y);ctx.lineTo(p.x+size,p.y);ctx.moveTo(p.x,p.y-size);ctx.lineTo(p.x,p.y+size);ctx.stroke();
  ctx.strokeStyle='#111';ctx.lineWidth=1;ctx.strokeRect(p.x-2,p.y-2,4,4);
  $('calibrationPosition'+i).textContent=`Stage X ${f.position.x.toFixed(3)}, Y ${f.position.y.toFixed(3)} mm · pixel ${p.x}, ${p.y} · ${f.point?'confirmed':'adjust crosshair, then confirm'}`;
  applyCalibrationZoom();
}
function applyCalibrationZoom(center=false) {
  calibrationFrames.forEach((f,i)=>{
    if(!f)return;
    const z=$('calibrationZoom').value,canvas=$('calibrationCanvas'+i);
    canvas.style.width=z==='fit'?'100%':`${f.width*Number(z)}px`;
    if(center&&z!=='fit') {
      const viewport=canvas.parentElement;
      viewport.scrollLeft=Math.max(0,f.cursor.x*Number(z)-viewport.clientWidth/2);
      viewport.scrollTop=Math.max(0,f.cursor.y*Number(z)-viewport.clientHeight/2);
    }
  });
}
function calibrationReport() {
  calibrationValue=null;
  try {
    calibrationValue=ScaleCalibration.calculate(calibrationFrames[0],calibrationFrames[1],Number($('rotation').value));
    const v=calibrationValue;
    $('calibrationResult').textContent=`Recorded move ${v.mm.toFixed(3)} mm / feature displacement ${v.pixels.toFixed(2)} px → ${(v.mmPerPixel*1000).toFixed(3)} µm/px · frame ${v.width.toFixed(4)} × ${v.height.toFixed(4)} mm. Best accuracy comes from a large displacement of the same feature.`;
  }catch(e){$('calibrationResult').textContent=e.message;}
  refreshCalibrationControls();
}
function selectCalibration(i) {calibrationActive=i;$('calibrationActive').textContent=`Crosshair: ${i===0?'first':'second'} snapshot`;refreshCalibrationControls();}
function moveCalibration(i,x,y) {
  const f=calibrationFrames[i];if(!f)return;
  f.cursor={x:Math.max(0,Math.min(f.width-1,Math.round(x))),y:Math.max(0,Math.min(f.height-1,Math.round(y)))};
  f.point=null;selectCalibration(i);drawCalibration(i);calibrationReport();
}
async function takeCalibration(i) {
  const f=await api('/calibration/frame',{});
  f.bitmap=new Image();f.bitmap.src=f.image;await f.bitmap.decode();
  f.cursor={x:Math.floor(f.width/2),y:Math.floor(f.height/2)};f.point=null;
  if(i===0){calibrationFrames=[null,null];$('calibrationPosition1').textContent='No snapshot';drawCalibration(1);}
  calibrationFrames[i]=f;selectCalibration(i);drawCalibration(i);calibrationReport();
}
on('showCalibration',()=>{$('calibrationPanel').hidden=false;tab('live');refreshCalibrationControls();$('calibrationPanel').scrollIntoView({behavior:'smooth',block:'nearest'});});
$('closeCalibration').onclick=()=>{stopCalibrationBlink();$('calibrationPanel').hidden=true;};
on('calibrationFirst',()=>takeCalibration(0));on('calibrationSecond',()=>takeCalibration(1));
$('calibrationZoom').onchange=()=>applyCalibrationZoom(true);
$('rotation').addEventListener('change',calibrationReport);
$('calibrationConfirm').onclick=()=>{
  const f=calibrationFrames[calibrationActive];if(!f)return;
  f.point={...f.cursor};drawCalibration(calibrationActive);calibrationReport();
  if(calibrationActive===0&&!calibrationFrames[1])$('calibrationResult').textContent='First feature confirmed. Jog a small distance, keeping it in view, then take the second snapshot.';
};
document.querySelectorAll('[data-nudge]').forEach(b=>b.onclick=()=>{
  const f=calibrationFrames[calibrationActive];if(!f)return;const [x,y]=b.dataset.nudge.split(',').map(Number);moveCalibration(calibrationActive,f.cursor.x+x,f.cursor.y+y);
});
[0,1].forEach(i=>{
  const canvas=$('calibrationCanvas'+i);
  const place=e=>{const r=canvas.getBoundingClientRect();moveCalibration(i,(e.clientX-r.left)*canvas.width/r.width,(e.clientY-r.top)*canvas.height/r.height);};
  canvas.addEventListener('pointerdown',e=>{if(!calibrationFrames[i])return;canvas.focus();canvas.setPointerCapture(e.pointerId);place(e);});
  canvas.addEventListener('pointermove',e=>{if(canvas.hasPointerCapture(e.pointerId))place(e);});
  canvas.addEventListener('pointerup',e=>{if(canvas.hasPointerCapture(e.pointerId))canvas.releasePointerCapture(e.pointerId);});
  canvas.addEventListener('focus',()=>selectCalibration(i));
  canvas.addEventListener('keydown',e=>{
    const f=calibrationFrames[i];if(!f)return;const d={ArrowLeft:[-1,0],ArrowRight:[1,0],ArrowUp:[0,-1],ArrowDown:[0,1]}[e.key];
    if(d){e.preventDefault();const step=e.shiftKey?10:1;moveCalibration(i,f.cursor.x+d[0]*step,f.cursor.y+d[1]*step);}
    else if(e.key==='Enter'){e.preventDefault();$('calibrationConfirm').click();}
  });
});
on('calibrationApply',async()=>{
  calibrationReport();if(!calibrationValue)throw Error('Confirm both features first.');
  $('fovWidth').value=calibrationValue.width.toFixed(6);deriveFrameHeight();await updatePlan();
  toast('Calculated frame width applied. Save defaults to keep it for future sessions.');
});
