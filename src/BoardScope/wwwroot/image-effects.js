const ImageEffectsUI={
  context:null,worker:null,request:null,original:null,ready:false,rendering:false,pending:false,revision:0,generation:0,applying:false,region:null,
  init(){
    $('imageEffects').onclick=()=>this.open();
    $('closeEffects').onclick=()=>{if(!this.applying)$('effectsDialog').close();};
    $('effectsDialog').addEventListener('cancel',e=>{if(this.applying)e.preventDefault();});
    $('effectsDialog').addEventListener('close',()=>this.cleanup());
    for(const key of EffectMath.keys){
      $('effect-'+key).addEventListener('input',()=>{$('effect-value-'+key).value=$('effect-'+key).value;this.change();});
      $('effect-value-'+key).addEventListener('input',()=>{const n=Number($('effect-value-'+key).value);if(Number.isFinite(n)&&n>=-100&&n<=100){$('effect-'+key).value=n;this.change();}else this.updateControls();});
      $('effect-value-'+key).addEventListener('change',()=>{$('effect-value-'+key).value=$('effect-'+key).value;this.change();});
    }
    $('resetEffects').onclick=()=>{this.set({});this.change();};
    $('showOriginalEffects').onchange=()=>{if($('showOriginalEffects').checked)this.paint(this.original?.data);else this.change();};
    $('effectsArea').onchange=()=>this.load();
    $('applyEffects').onclick=()=>this.apply();this.updateControls();
  },
  settings(){const value={};for(const key of EffectMath.keys)value[key]=Number($('effect-value-'+key).value);return EffectMath.settings(value);},
  set(value){const s=EffectMath.settings(value);for(const key of EffectMath.keys)$('effect-'+key).value=$('effect-value-'+key).value=s[key];},
  key(p=selectedProject){return p?`${p.id}/${p.activeStitchId}/${p.activeMapId}`:'';},
  currentRegion(map){
    if(loadedView!=='stitched'||viewLoading||!viewer?.world.getItemAt(0))return null;
    const rect=viewer.world.getItemAt(0).viewportToImageRectangle(viewer.viewport.getBounds(true));
    const x=Math.max(0,Math.min(map.width,rect.x)),y=Math.max(0,Math.min(map.height,rect.y));
    const right=Math.max(x,Math.min(map.width,rect.x+rect.width)),bottom=Math.max(y,Math.min(map.height,rect.y+rect.height));
    return right>x&&bottom>y?{x,y,width:right-x,height:bottom-y}:null;
  },
  open(){
    if(!state?.features?.imageEffects){toast('Restart BoardScope to enable image effects.',true);return;}
    if(state.engine.job.busy||state.engine.manual||captureFollowing)return;
    const entry=StitchHistory.alignment(),map=StitchHistory.map();if(!entry||!map)return;
    this.cleanup();const source=map.effectSourceMapId?entry.maps.find(m=>m.id===map.effectSourceMapId):map;
    this.context={projectId:selectedProject.id,stitchId:entry.id,mapId:(source||map).id,key:this.key(),initial:EffectMath.settings(source?map.effects||{}:{})};
    this.set(this.context.initial);this.region=this.currentRegion(map);
    $('effectsCurrentView').disabled=!this.region;$('effectsArea').value=this.region?'current':'whole';$('showOriginalEffects').checked=false;
    $('effectsSource').textContent=source&&map.effects?'Editing the saved settings from this map’s original source.':!source?'The original source map was deleted. Adjustments start from the displayed map.':'Adjustments start from the selected saved map.';
    $('effectsHeading').textContent=`Image effects · ${selectedProject.name}`;$('effectsError').hidden=true;$('effectsDialog').showModal();this.load();
  },
  async load(){
    if(!this.context)return;this.stopPreview();const generation=++this.generation,context=this.context;
    this.ready=false;this.original=null;$('effectsCanvas').hidden=true;$('effectsStatus').textContent='Loading a small mosaic preview…';$('effectsError').hidden=true;this.updateControls();
    const query=new URLSearchParams({stitchId:context.stitchId,mapId:context.mapId});
    if($('effectsArea').value==='current'&&this.region)for(const [key,value] of Object.entries(this.region))query.set(key,String(value));
    const request=new AbortController();this.request=request;
    try{
      const response=await fetch(`/api/projects/${context.projectId}/effects-preview?${query}`,{headers:{'X-BoardScope':'1'},signal:request.signal});
      if(!response.ok){const error=await response.json();throw Error(error.error||'Cannot load mosaic preview.');}
      const bitmap=await createImageBitmap(await response.blob());
      if(generation!==this.generation||request.signal.aborted||this.context!==context){bitmap.close();return;}
      const canvas=$('effectsCanvas');canvas.width=bitmap.width;canvas.height=bitmap.height;const ctx=canvas.getContext('2d');ctx.drawImage(bitmap,0,0);bitmap.close();
      this.original=ctx.getImageData(0,0,canvas.width,canvas.height);canvas.hidden=false;
      const worker=new Worker('effects-worker.js?v=da67e151f579');this.worker=worker;
      worker.onmessage=({data})=>{
        if(worker!==this.worker||generation!==this.generation)return;
        if(data.type==='error'){this.fail(data.message);return;}
        if(data.type==='ready'){this.ready=true;this.change();this.updateControls();return;}
        if(data.type==='frame'){
          this.rendering=false;
          if(data.revision===this.revision&&!$('showOriginalEffects').checked)this.paint(new Uint8ClampedArray(data.pixels));
          if(this.pending)this.send();else $('effectsStatus').textContent=`${canvas.width} × ${canvas.height} preview · Apply creates a new full-resolution map.`;
          this.updateControls();
        }
      };
      worker.onerror=e=>this.fail(e.message||'Preview worker failed.');
      const pixels=this.original.data.slice();worker.postMessage({type:'source',source:generation,pixels:pixels.buffer},[pixels.buffer]);
    }catch(error){if(error.name!=='AbortError'&&generation===this.generation)this.fail(error.message);}
    finally{if(this.request===request)this.request=null;this.updateControls();}
  },
  paint(pixels){if(!pixels||!this.original)return;const canvas=$('effectsCanvas');canvas.getContext('2d').putImageData(new ImageData(pixels,canvas.width,canvas.height),0,0);},
  change(){
    this.revision++;this.pending=true;$('effectsError').hidden=true;
    if($('showOriginalEffects').checked)this.paint(this.original?.data);
    if(this.ready&&!this.rendering)this.send();this.updateControls();
  },
  send(){
    if(!this.worker||!this.ready)return;
    let effects;try{effects=this.settings();}catch(error){this.pending=false;this.rendering=false;$('effectsError').textContent=error.message;$('effectsError').hidden=false;this.updateControls();return;}
    this.pending=false;this.rendering=true;this.worker.postMessage({type:'render',revision:this.revision,effects});
  },
  fail(message){this.ready=false;this.rendering=false;this.pending=false;$('effectsError').textContent=message;$('effectsError').hidden=false;$('effectsStatus').textContent='Preview unavailable.';this.updateControls();},
  updateControls(){
    const busy=!!state?.engine.job.busy||!!state?.engine.manual;
    $('imageEffects').disabled=!state?.features?.imageEffects||busy||captureFollowing||!StitchHistory.map();
    if(!this.context)return;
    if(this.context.key!==this.key()&&!this.applying){$('effectsDialog').close();this.cleanup();return;}
    let dirty=false;try{dirty=JSON.stringify(this.settings())!==JSON.stringify(this.context.initial);}catch{}
    $('applyEffects').disabled=busy||this.applying||!this.ready||this.rendering||this.pending||!dirty;
    $('closeEffects').disabled=this.applying;$('effectsArea').disabled=this.applying;
    $('resetEffects').disabled=this.applying||!this.ready;
    for(const key of EffectMath.keys){$('effect-'+key).disabled=this.applying||!this.ready;$('effect-value-'+key).disabled=this.applying||!this.ready;}
  },
  async apply(){
    if(this.applying||!this.context||!this.ready||this.rendering||this.pending||state?.engine.job.busy||state?.engine.manual)return;
    const context=this.context;let effects;try{effects=this.settings();}catch(error){$('effectsError').textContent=error.message;$('effectsError').hidden=false;this.updateControls();return;}
    this.applying=true;this.updateControls();
    try{
      await api(`/projects/${context.projectId}/effects`,{stitchId:context.stitchId,mapId:context.mapId,effects});
      this.applying=false;$('effectsDialog').close();this.cleanup();
      if(selectedProject?.id===context.projectId){$('mosaicMode').value='stitched';await renderSelectedView();}
      await poll();toast('Applying effects. The adjusted map will appear in stitch history.');
    }catch(error){$('effectsError').textContent=error.message;$('effectsError').hidden=false;}
    finally{this.applying=false;this.updateControls();}
  },
  stopPreview(){this.request?.abort();this.request=null;this.worker?.terminate();this.worker=null;this.ready=false;this.rendering=false;this.pending=false;},
  cleanup(){this.generation++;this.stopPreview();this.original=null;this.context=null;this.region=null;}
};
