const LightBackgroundUI={
  busy:false,previewId:'',
  init(){
    $('captureLightBackground').onclick=()=>this.capture();
    $('enableLightBackground').onchange=()=>this.enable();
    $('lightBackgroundPreviewKind').onchange=()=>this.preview();
  },
  async capture(){
    if(this.busy)return;this.busy=true;this.update();$('lightBackgroundStatus').textContent='Averaging 8 fresh frames and fitting the lighting…';
    try{
      const result=await api('/light-background/capture',{});state.lightBackground=result;this.previewId='';
      toast('Light background saved. Future stitches and reblends will use it.');
    }catch(error){toast(error.message,true);}
    finally{this.busy=false;await poll();this.update();}
  },
  async enable(){
    if(this.busy)return;this.busy=true;this.update();
    try{state.lightBackground=await api('/light-background',{enabled:$('enableLightBackground').checked});}
    catch(error){toast(error.message,true);}
    finally{this.busy=false;this.update();}
  },
  update(){
    const supported=!!state?.features?.lightBackground,blocked=this.busy||!!state?.engine.job.busy||!!state?.engine.manual,profile=state?.lightBackground?.profile;
    $('captureLightBackground').disabled=!supported||!state?.camera.connected||blocked;
    $('enableLightBackground').disabled=!supported||!profile||blocked;
    if(!this.busy)$('enableLightBackground').checked=!!state?.lightBackground?.enabled;
    $('lightBackgroundDetails').hidden=!profile;
    if(this.busy)return;
    $('lightBackgroundStatus').textContent=!supported?'Restart BoardScope to enable light correction.':!profile?'No light background saved.':`${state.lightBackground.enabled?'Enabled':'Disabled'} · ${profile.width} × ${profile.height} · ${new Date(profile.created).toLocaleString()} · gain ${profile.minGain.toFixed(2)}–${profile.maxGain.toFixed(2)}× · fit residual ${profile.fitErrorPercent.toFixed(2)}%.`;
    if(profile?.id!==this.previewId){this.previewId=profile?.id||'';this.preview();}
  },
  preview(){
    if(!this.previewId){$('lightBackgroundPreview').removeAttribute('src');return;}
    $('lightBackgroundPreview').src=`/api/light-background/${this.previewId}/preview?kind=${$('lightBackgroundPreviewKind').value}`;
  }
};
