const MatchDebug={
  graph:null,tile:-1,pair:null,data:null,canvas:null,request:null,timer:null,generation:0,cache:new Map(),frame:0,top:0,trialBusy:false,
  enabled(){return !!selectedProject?.registration&&loadedView==='tiles'&&$('showNeighborDebug').checked;},
  init(osd) {
    if(this.canvas)return;
    this.canvas=document.createElement('canvas');this.canvas.className='match-overlay';this.canvas.setAttribute('aria-hidden','true');$('osd').appendChild(this.canvas);
    osd.addHandler('viewport-change',()=>this.scheduleDraw());osd.addHandler('animation',()=>this.scheduleDraw());osd.addHandler('resize',()=>this.scheduleDraw());
    for(const id of ['showMatchFeatures','showUnmatchedFeatures','showRejectedMatches'])$(id).addEventListener('change',()=>this.scheduleDraw());
    $('showNeighborDebug').addEventListener('change',()=>{this.stopRequest();this.pair=this.data=null;highlightTile(highlightedTile);this.updatePanel();});
    $('matchPairSelect').addEventListener('change',()=>{
      const pair=this.graph?.byKey.get($('matchPairSelect').value);if(!pair)return;
      pinnedTile=this.tile;highlightTile(pinnedTile);this.selectPair(pair);this.updatePanel();
    });
    $('unpinMatchTile').onclick=()=>{pinnedTile=-1;this.pair=this.data=null;this.stopRequest();highlightTile(-1);this.updatePanel();};
    $('fitMatchPair').onclick=()=>{
      if(!this.pair)return;const a=tileItems[this.pair.first],b=tileItems[this.pair.second];if(!a||!b)return;
      pinnedTile=this.tile;highlightTile(pinnedTile);this.updatePanel();
      const r=a.getBounds(),s=b.getBounds(),left=Math.min(r.x,s.x),top=Math.min(r.y,s.y),right=Math.max(r.x+r.width,s.x+s.width),bottom=Math.max(r.y+r.height,s.y+s.height);
      viewer.viewport.fitBounds(new OpenSeadragon.Rect(left,top,right-left,bottom-top));
    };
    $('matchFirstTop').onclick=()=>this.onTop(0);$('matchSecondTop').onclick=()=>this.onTop(1);
    $('reloadMatchFeatures').onclick=()=>{const pair=this.pair;if(!pair)return;this.cache.delete(MatchDebugMath.key(pair.first,pair.second));this.pair=null;this.selectPair(pair);};
    $('trialMatchFeatures').onclick=()=>this.trySettings();
    $('alternatePairMethod').onclick=()=>this.trySettings(selectedProject.registration.options?.method==='phase'?'sift':'phase');
    $('nccPairMethod').onclick=()=>this.trySettings(selectedProject.registration.options?.method==='ncc'?'sift':'ncc');
    $('hybridPairMethod').onclick=()=>this.trySettings('hybrid');
    $('parallaxPairMethod').onclick=()=>this.trySettings('parallax');
    document.addEventListener('keydown',e=>{if(e.key==='Escape'&&pinnedTile>=0&&!['INPUT','SELECT','TEXTAREA'].includes(e.target.tagName))$('unpinMatchTile').click();});
  },
  reset(project) {
    this.stopRequest();this.generation++;this.tile=-1;this.pair=this.data=null;this.cache.clear();pinnedTile=-1;
    this.graph=project?.registration?MatchDebugMath.graph(project.tiles.length,project.registration.pairs):null;
    $('matchDebugPanel').hidden=!this.graph;
    if(this.graph){const zero=this.graph.support.filter(n=>n===0).length,one=this.graph.support.filter(n=>n===1).length;
      $('matchGraphSummary').textContent=`${zero} tiles with 0 neighbors · ${one} with 1 · ${this.graph.sizes.length} connected groups. A group shares no accepted visual links with another group.`;}
    this.updatePanel();this.scheduleDraw();
  },
  stopRequest(){clearTimeout(this.timer);this.timer=null;this.request?.abort();this.request=null;this.trialBusy=false;},
  displayReport(){return this.data?.trial&&this.data.reconstructionReport?this.data.reconstructionReport:this.pair;},
  async trySettings(method=selectedProject?.registration?.options?.method||'sift') {
    if(!this.pair||this.trialBusy)return;
    if(!this.methodAvailable(method)){$('matchPairStatus').textContent='Restart the app to enable matching previews.';return;}
    if(state?.engine.job.busy){$('matchPairStatus').textContent='Finish the current job before testing detection settings.';return;}
    let options;try{options=registrationSettings(method);}catch(e){$('matchPairStatus').textContent=e.message;return;}
    const pair=this.pair,project=selectedProject,generation=this.generation;
    this.stopRequest();this.trialBusy=true;const request=new AbortController();this.request=request;
    pinnedTile=this.tile;highlightTile(pinnedTile);this.updatePanel();$('matchPairStatus').textContent=method!=='sift'?`Correlating this overlap with ${this.methodName(method)}…`:'Rechecking this pair with current detection settings…';
    try {
      const response=await fetch(`/api/projects/${project.id}/matches/${pair.first}/${pair.second}/recheck`,{method:'POST',headers:{'X-BoardScope':'1','Content-Type':'application/json'},body:JSON.stringify({version:project.registration.reportFile,options,stitchId:project.activeStitchId||null}),signal:request.signal});
      const data=await response.json();if(!response.ok)throw Error(data.error||'Cannot recheck this pair.');
      if(request.signal.aborted||generation!==this.generation||this.pair!==pair)return;
      this.data=data;this.updatePanel();this.scheduleDraw();
    }catch(e){if(e.name!=='AbortError'&&generation===this.generation&&this.pair===pair)$('matchPairStatus').textContent=e.message;}
    finally{if(this.request===request){this.request=null;this.trialBusy=false;this.updateButtons();}}
  },
  pointer(v) {
    let found=-1,best=Infinity;
    const containing=new Map();
    tileItems.forEach((item,i)=>{if(!item)return;const p=item.viewportToImageCoordinates(v),t=selectedProject.tiles[i];
      if(p.x>=0&&p.y>=0&&p.x<t.width&&p.y<t.height){const distance=MatchDebugMath.distance(p,t.width,t.height);containing.set(i,distance);if(distance<best){best=distance;found=i;}}});
    const index=pinnedTile>=0?pinnedTile:found;
    tileOutlines.forEach((el,i)=>el?.classList.toggle('hovered',i===found));
    if(this.enabled()&&index>=0&&this.tile!==index){this.tile=index;this.pair=this.data=null;this.stopRequest();this.updatePanel();}
    highlightTile(index);
    if(!this.enabled())return;
    if(index<0||!containing.has(index))return;
    const possible=(this.graph?.neighbors[index]||[]).filter(n=>containing.has(n.index)).sort((a,b)=>containing.get(a.index)-containing.get(b.index));
    if(possible.length)this.selectPair(possible[0].pair);
    else if(pinnedTile<0&&this.pair){this.pair=this.data=null;this.stopRequest();this.updatePanel();this.paint(index);}
  },
  click(event) {
    if(!event.quick||!this.enabled()||highlightedTile<0)return;
    event.preventDefaultAction=true;pinnedTile=pinnedTile===highlightedTile?-1:highlightedTile;
    this.tile=highlightedTile;this.updatePanel();this.paint(highlightedTile);
  },
  tileInfo(index) {
    if(!this.graph||index<0)return '';
    const list=this.graph.neighbors[index],good=list.filter(n=>n.pair.accepted),bad=list.filter(n=>!n.pair.accepted);
    return `<dl><dt>Accepted neighbors</dt><dd>${good.length?good.map(n=>n.index+1).join(', '):'None'}</dd><dt>Rejected neighbors</dt><dd>${bad.length?bad.map(n=>n.index+1).join(', '):'None'}</dd><dt>Connected group</dt><dd>${this.graph.components[index]+1} · ${this.graph.sizes[this.graph.components[index]]} tiles</dd></dl>`;
  },
  paint(index) {
    const enabled=this.enabled(),active=enabled&&this.pair&&(pinnedTile>=0||index>=0)?this.pair:null;
    const list=enabled&&index>=0?this.graph.neighbors[index]:[],accepted=new Set(list.filter(n=>n.pair.accepted).map(n=>n.index)),rejected=new Set(list.filter(n=>!n.pair.accepted).map(n=>n.index));
    tileItems.forEach((item,i)=>{if(!item)return;item.setOpacity(active?(i===active.first||i===active.second?1:.1):index<0||i===index?1:accepted.has(i)?0.55:rejected.has(i)?0.25:0.12);});
    tileOutlines.forEach((el,i)=>{
      el.classList.toggle('neighbor-accepted',accepted.has(i));el.classList.toggle('neighbor-rejected',rejected.has(i));el.classList.toggle('pair-endpoint',!!active&&(i===active.first||i===active.second));
      el.classList.toggle('support-zero',enabled&&this.graph.support[i]===0);el.classList.toggle('support-one',enabled&&this.graph.support[i]===1);
      el.style.opacity=index<0||i===index||accepted.has(i)||rejected.has(i)?'1':'.15';
    });
    if(active)this.onTop(this.top,false);
    this.scheduleDraw();
  },
  onTop(which,pin=true){if(!this.pair)return;this.top=which;if(pin){pinnedTile=this.tile;highlightTile(pinnedTile);this.updatePanel();}
    const index=which===0?this.pair.first:this.pair.second,item=tileItems[index];if(item)viewer.world.setItemIndex(item,viewer.world.getItemCount()-1);
    $('matchFirstTop').classList.toggle('active',which===0);$('matchSecondTop').classList.toggle('active',which===1);this.scheduleDraw();
  },
  selectPair(pair) {
    const key=MatchDebugMath.key(pair.first,pair.second);if(this.pair&&MatchDebugMath.key(this.pair.first,this.pair.second)===key)return;
    this.stopRequest();this.pair=pair;this.data=null;this.top=0;this.updatePanel();this.paint(highlightedTile);
    const cached=this.cache.get(key);if(cached){this.data=cached;this.updatePanel();this.scheduleDraw();return;}
    const project=selectedProject,generation=this.generation;
    this.timer=setTimeout(async()=>{
      if(!state?.features?.matchDiagnostics){$('matchPairStatus').textContent='Restart the app to enable feature inspection.';return;}
      if(state?.engine.job.busy){$('matchPairStatus').textContent='Finish the running job before loading feature diagnostics.';return;}
      const request=new AbortController();this.request=request;
      try {
        const response=await fetch(`/api/projects/${project.id}/matches/${pair.first}/${pair.second}?version=${encodeURIComponent(project.registration.reportFile)}${project.activeStitchId?`&stitchId=${encodeURIComponent(project.activeStitchId)}`:''}`,{headers:{'X-BoardScope':'1'},signal:request.signal});
        const data=await response.json();if(!response.ok)throw Error(data.error||'Cannot load feature diagnostics.');
        if(generation!==this.generation||request.signal.aborted)return;
        while(this.cache.size>=8)this.cache.delete(this.cache.keys().next().value);this.cache.set(key,data);
        if(this.pair===pair){this.data=data;this.updatePanel();this.scheduleDraw();}
      }catch(e){if(e.name!=='AbortError'&&generation===this.generation&&this.pair===pair)$('matchPairStatus').textContent=e.message;}
      finally{if(this.request===request)this.request=null;}
    },200);
  },
  updateButtons() {
    const method=selectedProject?.registration?.options?.method||'sift',phase=method==='phase',busy=this.trialBusy||!!state?.engine.job.busy||!this.pair;
    $('trialMatchFeatures').disabled=busy||!this.methodAvailable(method);
    $('alternatePairMethod').disabled=busy||!this.methodAvailable(phase?'sift':'phase');
    $('nccPairMethod').disabled=busy||!this.methodAvailable(method==='ncc'?'sift':'ncc');
    $('hybridPairMethod').disabled=busy||!this.methodAvailable('hybrid');$('hybridPairMethod').hidden=method==='hybrid';
    $('parallaxPairMethod').disabled=busy||!this.methodAvailable('parallax');$('parallaxPairMethod').hidden=method==='parallax';
    $('trialMatchFeatures').textContent=method==='parallax'?'Try parallax-aware matching on this pair':method==='hybrid'?'Try hybrid matching on this pair':method==='sift'?'Try current detection settings':method==='ncc'?'Try current NCC settings':'Try current correlation settings';
    $('alternatePairMethod').textContent=phase?'Try feature matching on this pair':'Try phase correlation on this pair';
    $('nccPairMethod').textContent=method==='ncc'?'Try feature matching on this pair':'Try NCC on this pair';
  },
  methodName(method){return method==='parallax'?'Parallax-aware matching':method==='hybrid'?'Hybrid matching':method==='ncc'?'Normalized cross-correlation':method==='phase'?'Phase correlation':'Feature matching';},
  methodAvailable(method){return !!(method==='parallax'?state?.features?.parallaxMatching:method==='hybrid'?state?.features?.hybridMatching:method==='ncc'?state?.features?.normalizedCrossCorrelation:method==='phase'?state?.features?.phaseCorrelation:state?.features?.featureSensitivity);},
  pairNumbers(p) {
    if(p.method==='parallax')return p.parallax?`${p.parallax.validPatches}/${p.parallax.patchCount} patches matched · ${p.parallax.hypotheses.length} offset candidates · chosen ${p.parallax.selectedHypothesis==null?'none':p.parallax.selectedHypothesis+1} · ${p.parallax.globalRetries} global comparisons / ${p.parallax.globalChanges||0} switches${p.globalErrorPixels!=null?` · solved error ${p.globalErrorPixels.toFixed(2)} px`:''}`:'No coherent translation';
    if(p.method==='ncc')return p.ncc?`NCC peak ${p.ncc.peakCorrelation.toFixed(3)} · aligned correlation ${p.ncc.correlation.toFixed(3)} · next peak ${p.ncc.secondCorrelation?.toFixed(3)??'—'} · gap ${p.ncc.peakGap?.toFixed(3)??'—'} · placement Δ ${p.ncc.deltaX.toFixed(2)}, ${p.ncc.deltaY.toFixed(2)} px · correction ${p.ncc.correctionX.toFixed(2)}, ${p.ncc.correctionY.toFixed(2)} px${p.globalErrorPixels!=null?` · solved error ${p.globalErrorPixels.toFixed(2)} px`:''}`:'No measurable translation';
    if(p.method==='phase')return p.phase?`Response ${p.phase.response.toFixed(3)} · aligned correlation ${p.phase.correlation?.toFixed(3)??'not measured'} · placement Δ ${p.phase.deltaX.toFixed(2)}, ${p.phase.deltaY.toFixed(2)} px · correction ${p.phase.correctionX.toFixed(2)}, ${p.phase.correctionY.toFixed(2)} px${p.globalErrorPixels!=null?` · solved error ${p.globalErrorPixels.toFixed(2)} px`:''}`:'No measurable translation';
    return `${p.inliers}/${p.matches} inliers · pair error ${p.errorPixels.toFixed(2)} px${p.globalErrorPixels!=null?` · solved error ${p.globalErrorPixels.toFixed(2)} px`:''} · relative angle ${p.rotationDegrees.toFixed(4)}° · relative scale ${p.scale.toFixed(5)}×`;
  },
  updatePanel() {
    this.updateButtons();if(!this.graph)return;
    const list=this.graph.neighbors[this.tile]||[];
    $('matchTileHeading').textContent=this.tile<0?'Hover a tile to inspect its neighbors':`Tile ${this.tile+1} · ${this.graph.support[this.tile]} accepted / ${list.length} checked neighbors${pinnedTile>=0?' · pinned':''}`;
    $('unpinMatchTile').disabled=pinnedTile<0;
    $('matchPairSelect').innerHTML='<option value="">Choose a checked neighbor…</option>'+list.map(n=>`<option value="${MatchDebugMath.key(n.pair.first,n.pair.second)}">Tile ${n.index+1} · ${n.pair.accepted?'accepted':'rejected'} · ${n.pair.method==='parallax'?`${n.pair.parallax?.validPatches??0} patches`:n.pair.method==='ncc'?`NCC ${n.pair.ncc?.correlation.toFixed(3)??'—'}`:n.pair.method==='phase'?`response ${n.pair.phase?.response.toFixed(3)??'—'}`:`${n.pair.inliers}/${n.pair.matches} matches`}</option>`).join('');
    $('matchPairSelect').disabled=!list.length||loadedView!=='tiles'||!$('showNeighborDebug').checked;
    $('matchNoNeighbors').hidden=this.tile<0||list.length>0;
    $('matchPairDetails').hidden=!this.pair;
    if(!this.pair)return;
    const p=this.pair;$('matchPairSelect').value=MatchDebugMath.key(p.first,p.second);
    $('matchPairHeading').textContent=`Tiles ${p.first+1} ↔ ${p.second+1} · ${p.accepted?'accepted':'rejected'}`;
    $('matchPairReason').textContent=this.data?.trial?`Preview: ${this.displayReport().reason} · Saved result: ${p.reason}`:p.reason;
    const shown=this.displayReport(),phase=shown.method==='phase'||shown.method==='ncc';
    $('matchPairNumbers').textContent=`${this.data?.trial?'Preview: ':''}${shown.attemptLabel?shown.attemptLabel+' · ':''}${this.pairNumbers(shown)}`;
    const attempts=shown.attempts||[];$('matchAttemptHistory').hidden=!attempts.length;
    $('matchAttempts').innerHTML='<ol>'+attempts.map(a=>`<li><strong>${escapeHtml(a.label)} · ${a.report.accepted?'pair matched':'rejected'}</strong><span>${escapeHtml(a.report.reason)}</span><small>${escapeHtml(this.pairNumbers(a.report))}</small></li>`).join('')+'</ol>';
    $('parallaxHypotheses').hidden=!shown.parallax;
    $('parallaxOffsets').innerHTML=shown.parallax?'<ol>'+shown.parallax.hypotheses.map(h=>`<li><strong>${h.id===shown.parallax.selectedHypothesis?'Selected · ':''}${escapeHtml(h.label)}</strong><span>Placement Δ ${h.deltaX.toFixed(2)}, ${h.deltaY.toFixed(2)} px · confidence ${h.confidence.toFixed(3)} · spread ${h.spreadPixels.toFixed(2)} px${h.globalErrorPixels!=null?` · layout error ${h.globalErrorPixels.toFixed(2)} px`:''}</span></li>`).join('')+'</ol>':'';
    $('showRejectedMatches').disabled=phase;$('showUnmatchedFeatures').disabled=phase;
    $('matchObservationLabel').textContent=phase?'Show measured translation':'Show feature pairs';
    $('matchObservationLegend').textContent=phase?'Arrow endpoints show the measured translation constraint between the two tiles, not detected feature points. Line length shows disagreement in the displayed layout.':'Cyan circle = first tile; amber cross = second. Green lines are retained matches; red lines were excluded. Line length shows disagreement in the displayed layout.';
    $('matchFirstTop').textContent=`Tile ${p.first+1} on top`;$('matchSecondTop').textContent=`Tile ${p.second+1} on top`;
    if(!this.data){$('matchPairStatus').textContent='Loading overlap observations…';return;}
    if(shown.method==='parallax'){
      $('showUnmatchedFeatures').disabled=true;$('matchObservationLabel').textContent='Show patch translations';
      $('matchObservationLegend').textContent='These are patch translation measurements, not detected feature points. Green belongs to the selected offset; red indicates other offsets. Whole-overlap fallbacks show one central constraint.';
      $('matchPairStatus').textContent=`${this.data.trial?'PREVIEW ONLY: saved alignment is unchanged. ':''}Independent patches vote once even when several resolutions or methods agree. Global comparisons exclude this pair and require another connecting path. ${this.data.reconstructed?'Local reconstruction does not rerun global selection; saved acceptance above is unchanged. ':''}Competing offsets are listed below.`;return;
    }
    if(phase){const ncc=shown.method==='ncc',m=ncc?shown.ncc:shown.phase;
      $('matchPairStatus').textContent=`${this.data.trial?'PREVIEW ONLY: saved neighbor status and stitching are unchanged. ':''}${this.methodName(shown.method)} measures one translation from an overlap ${ncc?'template':'strip'}; no feature points are detected. ${this.data.reconstructed?'Recheck: '+(this.data.reconstructionReport?.reason||'')+'. Saved acceptance above is unchanged. ':''}${ncc?'Template':'Overlap'} ${m?.width??'—'} × ${m?.height??'—'} px. Smooth illumination removal ${(ncc?this.data.analysisOptions?.nccHighPass:this.data.analysisOptions?.phaseHighPass)?'on':'off'}.${ncc&&m?` Search ±${m.searchRadiusX.toFixed(1)} / ±${m.searchRadiusY.toFixed(1)} px; competing-peak rejection ${(this.data.analysisOptions?.nccMinPeakGap??0)>0?'on':'off'}.`:''}`;return;}
    const d=this.data,counts=new Map();for(const m of d.matches)counts.set(m.status,(counts.get(m.status)||0)+1);
    const reconstruction=d.reconstructed?` Reconstructed from saved images/cache using current matching rules; neighbor acceptance above is from the original saved solve.${d.reconstructionReport?` Recheck: ${d.reconstructionReport.reason}, ${d.reconstructionReport.inliers}/${d.reconstructionReport.matches} inliers.`:''}`:'';
    const detection=d.analysisOptions?` Threshold ${d.analysisOptions.contrastThreshold}, local contrast ${d.analysisOptions.enhanceLocalContrast?'on':'off'}, limit ${d.analysisOptions.maxFeatures}.`:'';
    $('matchPairStatus').textContent=`${d.trial?'PREVIEW ONLY: saved neighbor status and stitching are unchanged. ':''}Detected in search region: ${d.featuresFirst.length}/${d.totalFeaturesFirst} features in tile ${p.first+1}, ${d.featuresSecond.length}/${d.totalFeaturesSecond} in tile ${p.second+1}. ${Array.from(counts,([status,count])=>`${count} ${status}`).join(' · ')||'No mutual descriptor matches.'}${detection}${reconstruction}`;
  },
  scheduleDraw(){if(this.frame)return;this.frame=requestAnimationFrame(()=>{this.frame=0;this.draw();});},
  draw() {
    if(!this.canvas)return;const w=$('osd').clientWidth,h=$('osd').clientHeight,dpr=devicePixelRatio||1;
    if(this.canvas.width!==Math.round(w*dpr)||this.canvas.height!==Math.round(h*dpr)){this.canvas.width=Math.round(w*dpr);this.canvas.height=Math.round(h*dpr);}
    const ctx=this.canvas.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,w,h);
    if(!this.enabled()||viewLoading||$('mosaicView').hidden||captureFollowing||highlightedTile<0)return;
    const pixel=(i,p)=>{const item=tileItems[i];if(!item)return null;return viewer.viewport.pixelFromPoint(item.imageToViewportCoordinates(p.x,p.y),true);};
    const center=i=>pixel(i,{x:selectedProject.tiles[i].width/2,y:selectedProject.tiles[i].height/2});
    const origin=center(highlightedTile);if(origin)for(const n of this.graph.neighbors[highlightedTile]){const end=center(n.index);if(!end)continue;
      ctx.strokeStyle=n.pair.accepted?'#6ce1b5':'#ff7c85';ctx.globalAlpha=.65;ctx.lineWidth=1.5;ctx.setLineDash(n.pair.accepted?[]:[5,5]);ctx.beginPath();ctx.moveTo(origin.x,origin.y);ctx.lineTo(end.x,end.y);ctx.stroke();}
    ctx.setLineDash([]);ctx.globalAlpha=1;
    if(!this.pair||!this.data||!$('showMatchFeatures').checked)return;
    const p=this.pair,d=this.data,report=this.displayReport();
    if(report.method==='phase'||report.method==='ncc'){
      const m=d.translationConstraint;if(!m)return;
      const a=pixel(p.first,m.first),b=pixel(p.second,m.second);if(!a||!b)return;
      ctx.strokeStyle=report.accepted?'#69e7b4':'#ff7683';ctx.lineWidth=2;ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();
      const angle=Math.atan2(b.y-a.y,b.x-a.x);ctx.beginPath();ctx.moveTo(b.x-8*Math.cos(angle-.5),b.y-8*Math.sin(angle-.5));ctx.lineTo(b.x,b.y);ctx.lineTo(b.x-8*Math.cos(angle+.5),b.y-8*Math.sin(angle+.5));ctx.stroke();
      ctx.strokeStyle='#56d9ff';ctx.strokeRect(a.x-4,a.y-4,8,8);ctx.fillStyle='#ffcd68';ctx.fillText('Translation constraint',b.x+10,b.y-8);return;
    }
    if($('showUnmatchedFeatures').checked)for(const [index,points,color] of [[p.first,d.featuresFirst,'#56d9ff'],[p.second,d.featuresSecond,'#ffc866']]){
      ctx.fillStyle=color;ctx.globalAlpha=.45;for(const point of points){const q=pixel(index,point);if(q&&q.x>=-3&&q.y>=-3&&q.x<=w+3&&q.y<=h+3)ctx.fillRect(q.x-1,q.y-1,2,2);}}
    ctx.globalAlpha=1;
    for(const match of d.matches){if(!MatchDebugMath.visibleMatch(match,report,$('showRejectedMatches').checked))continue;
      const a=pixel(p.first,match.first),b=pixel(p.second,match.second);if(!a||!b)continue;
      if(Math.max(a.x,b.x)<-5||Math.min(a.x,b.x)>w+5||Math.max(a.y,b.y)<-5||Math.min(a.y,b.y)>h+5)continue;
      const used=report.accepted&&match.status==='retained';ctx.strokeStyle=used?'#69e7b4':'#ff7683';ctx.globalAlpha=.7;ctx.lineWidth=1;ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();
      ctx.globalAlpha=1;ctx.strokeStyle=used?'#53dfff':'#ff7683';ctx.beginPath();ctx.arc(a.x,a.y,3,0,Math.PI*2);ctx.stroke();
      ctx.strokeStyle=used?'#ffcd68':'#ff7683';ctx.beginPath();ctx.moveTo(b.x-3,b.y);ctx.lineTo(b.x+3,b.y);ctx.moveTo(b.x,b.y-3);ctx.lineTo(b.x,b.y+3);ctx.stroke();
    }
  }
};
