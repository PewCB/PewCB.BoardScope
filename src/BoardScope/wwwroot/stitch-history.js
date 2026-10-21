const StitchHistory={
  generation:0,loading:false,pendingDelete:null,
  available(){return !!state?.features?.stitchHistory;},
  alignment(p=selectedProject){return p?.stitches?.find(s=>s.id===p.activeStitchId);},
  hasAlignment(p=selectedProject){return !!this.alignment(p)?.alignmentFile;},
  map(p=selectedProject){return this.alignment(p)?.maps.find(m=>m.id===p.activeMapId);},
  methodName(method){return method==='stage'?'Stage positions':method==='import'?'Imported image':method==='legacy'?'Older map':MatchDebug.methodName(method);},
  date(value){return new Date(value).toLocaleString();},
  cancelLoad(){this.generation++;this.loading=false;},
  init(){
    $('historyBlendMode').onchange=()=>{$('blendMode').value=$('historyBlendMode').value;};
    $('blendMode').addEventListener('change',()=>{$('historyBlendMode').value=$('blendMode').value;});
    $('cancelStitchDelete').onclick=()=>{$('stitchDeleteDialog').close();this.pendingDelete=null;};
    $('stitchDeleteForm').onsubmit=async e=>{
      e.preventDefault();const target=this.pendingDelete;if(!target)return;
      const button=$('confirmStitchDelete');button.disabled=true;$('stitchDeleteError').hidden=true;
      try{
        const path=`/projects/${target.projectId}/stitches/${target.stitchId}${target.mapId?`/maps/${target.mapId}`:''}`;
        const p=await api(path,undefined,'DELETE');this.pendingDelete=null;$('stitchDeleteDialog').close();
        if(selectedProject?.id===target.projectId)await openProject(p.id,p);await refreshProjects();
      }catch(error){$('stitchDeleteError').textContent=error.message;$('stitchDeleteError').hidden=false;}
      finally{button.disabled=false;}
    };
  },
  render(p=selectedProject){
    $('stitchHistory').hidden=!p;
    if(!p){$('stitchVersions').innerHTML='';$('stitchViewing').hidden=true;return;}
    const stitches=p.stitches||[];
    $('stitchHistoryStatus').textContent=!this.available()?'History will be available after the next backend restart.':p.stitchCatalogVersion<1?'Older stitches will be indexed when the running job finishes.':`${stitches.length} alignments / entries · ${stitches.reduce((n,s)=>n+s.maps.length,0)} rendered maps. Reblending adds a map and preserves the alignment.`;
    $('stitchVersions').innerHTML=[...stitches].sort((a,b)=>new Date(b.created)-new Date(a.created)).map(s=>{
      const current=s.id===p.activeStitchId;
      return `<details class="stitch-entry ${current?'viewed':''}" ${current?'open':''}><summary><strong>${escapeHtml(this.methodName(s.method))}</strong><time>${escapeHtml(this.date(s.created))}</time>${current?'<b class="viewed-badge">VIEWING</b>':''}</summary>${s.note?`<p class="hint">${escapeHtml(s.note)}</p>`:''}${s.alignmentFile?`<p class="hint">${s.supportedTiles} tiles supported · ${s.acceptedPairs} pairs${s.errorPixels!=null?` · ${s.errorPixels.toFixed(2)} px`:''}</p><div class="button-row"><button data-stitch-tiles="${s.id}" class="quiet">Inspect tiles</button><button data-stitch-blend="${s.id}" class="secondary">Add blended map</button></div>`:'<p class="hint">Rendered map only · tile alignment unavailable.</p>'}<div class="stitch-maps">${[...s.maps].reverse().map(m=>`<div class="stitch-map ${current&&m.id===p.activeMapId?'viewed':''}"><div><time>${escapeHtml(this.date(m.created))}</time><span>${m.blendMode==='unknown'?'Older blend':escapeHtml(m.blendMode)}${m.effects?' · Image effects':''}${m.lightBackground?' · Light corrected':''} · ${m.width.toLocaleString()} × ${m.height.toLocaleString()}</span></div><div class="button-row"><button data-stitch-map="${s.id}" data-map-id="${m.id}" class="quiet">${current&&m.id===p.activeMapId?'Viewing map':'Load map'}</button><button data-delete-map="${s.id}" data-map-id="${m.id}" class="danger">Delete map</button></div></div>`).join('')}</div><button data-delete-stitch="${s.id}" class="danger full">Delete stitch & its maps</button></details>`;
    }).join('')||'<p class="hint">No saved stitches yet. Each completed stitch will appear here.</p>';
    $('stitchVersions').querySelectorAll('[data-stitch-tiles]').forEach(b=>b.onclick=()=>this.load(b.dataset.stitchTiles,null,'tiles'));
    $('stitchVersions').querySelectorAll('[data-stitch-map]').forEach(b=>b.onclick=()=>this.load(b.dataset.stitchMap,b.dataset.mapId,'stitched'));
    $('stitchVersions').querySelectorAll('[data-stitch-blend]').forEach(b=>b.onclick=()=>action(b,()=>api(`/projects/${p.id}/stitch`,{renderOnly:true,stitchId:b.dataset.stitchBlend,blendMode:$('historyBlendMode').value})));
    $('stitchVersions').querySelectorAll('[data-delete-stitch]').forEach(b=>b.onclick=()=>this.askDelete(p.id,b.dataset.deleteStitch));
    $('stitchVersions').querySelectorAll('[data-delete-map]').forEach(b=>b.onclick=()=>this.askDelete(p.id,b.dataset.deleteMap,b.dataset.mapId));
    this.updateControls();this.updateViewing();
  },
  updateControls(){
    $('stitchHistory').hidden=!selectedProject||captureFollowing;this.updateViewing();
    const busy=!!state?.engine.job.busy||!!state?.engine.manual,blocked=!this.available()||busy||this.loading;
    $('stitchVersions').querySelectorAll('[data-stitch-blend],[data-delete-stitch],[data-delete-map]').forEach(b=>b.disabled=blocked||activeActions.has(b));
    $('stitchVersions').querySelectorAll('[data-stitch-tiles],[data-stitch-map]').forEach(b=>b.disabled=!this.available()||this.loading);
    $('historyBlendMode').disabled=blocked;
    $('exportMosaic').disabled=!selectedProject?.dzi||captureFollowing||(typeof MosaicExport!=='undefined'&&MosaicExport.busy);
    $('exportPages').disabled=!state?.features.pagesExport||!selectedProject?.dzi||captureFollowing||busy||(typeof PagesExportUI!=='undefined'&&PagesExportUI.busy);
    if(this.available())$('reblendPositions').disabled=blocked||!this.hasAlignment();
  },
  updateViewing(){
    const p=selectedProject,s=this.alignment(p),m=this.map(p),mode=$('mosaicMode').value;
    $('stitchViewing').hidden=!p||!s||captureFollowing||$('mosaicView').hidden;
    if(!s)return;
    const name=`${this.methodName(s.method)} · ${this.date(s.created)}`;
    $('stitchViewing').textContent=mode==='tiles'?`${$('tileLayout').value==='saved'?'Saved alignment':'Layout preview'}: ${name}`:`Map: ${m?`${this.date(m.created)} · ${m.blendMode}${m.effects?' · Image effects':''}${m.lightBackground?' · Light corrected':''}`:'none'} | Stitch: ${name}`;
  },
  async load(stitchId,mapId,mode){
    if(!this.available()||!selectedProject)return;
    const projectId=selectedProject.id,generation=++this.generation;this.loading=true;this.updateControls();
    try{
      const p=await api(`/projects/${projectId}/stitches/${stitchId}${mapId?`?mapId=${encodeURIComponent(mapId)}`:''}`);
      if(generation!==this.generation||selectedProject?.id!==projectId)return;
      this.loading=false;$('mosaicMode').value=mode;$('tileLayout').value='saved';
      await openProject(projectId,p);
    }catch(error){if(generation===this.generation)toast(error.message,true);}
    finally{if(generation===this.generation){this.loading=false;this.updateControls();}}
  },
  askDelete(projectId,stitchId,mapId=null){
    const s=selectedProject?.stitches.find(s=>s.id===stitchId);if(!s)return;
    this.pendingDelete={projectId,stitchId,mapId};
    $('stitchDeleteDescription').textContent=mapId?`Delete the ${this.date(s.maps.find(m=>m.id===mapId).created)} rendered map? The alignment and its other maps stay available.`:`Delete ${this.methodName(s.method)} from ${this.date(s.created)}, its alignment/diagnostics and all ${s.maps.length} maps?`;
    $('stitchDeleteError').hidden=true;$('stitchDeleteDialog').showModal();$('cancelStitchDelete').focus();
  }
};
