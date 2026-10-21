function mosaicPrefetchCandidates(config,bounds,level){
  const maxLevel=Math.ceil(Math.log2(Math.max(config.width,config.height))),result=[];
  level=Math.max(0,Math.min(maxLevel,level));
  const unit=config.tileSize*2**(maxLevel-level),cols=Math.ceil(config.width/unit),rows=Math.ceil(config.height/unit);
  const left=Math.max(0,bounds.x),top=Math.max(0,bounds.y),right=Math.min(config.width,bounds.x+bounds.width),bottom=Math.min(config.height,bounds.y+bounds.height);
  if(right<=left||bottom<=top)return result;
  const x0=Math.floor(left/unit),x1=Math.ceil(right/unit)-1,y0=Math.floor(top/unit),y1=Math.ceil(bottom/unit)-1;
  const cx=(x0+x1)/2,cy=(y0+y1)/2;
  function add(x,y){
    if(x<0||y<0||x>=cols||y>=rows)return;
    const dx=Math.max(x0-x,x-x1,0),dy=Math.max(y0-y,y-y1,0);
    result.push({level,x,y,rank:dx*dx+dy*dy,near:(x-cx)**2+(y-cy)**2});
  }
  for(let x=x0-1;x<=x1+1;x++){add(x,y0-1);add(x,y1+1);}
  for(let y=y0;y<=y1;y++){add(x0-1,y);add(x1+1,y);}
  result.sort((a,b)=>a.rank-b.rank||a.near-b.near);
  // A handful near the screen centre helps the next zoom-in without fetching
  // its entire visible area (which could be four times larger).
  if(level<maxLevel){
    result.splice(28);
    const nextUnit=unit/2,nx=Math.floor((left+right)/2/nextUnit),ny=Math.floor((top+bottom)/2/nextUnit);
    for(let y=ny-1;y<=ny;y++)for(let x=nx-1;x<=nx;x++)
      if(x>=0&&y>=0&&x<Math.ceil(config.width/nextUnit)&&y<Math.ceil(config.height/nextUnit))result.push({level:level+1,x,y});
  }
  return result.slice(0,32);
}
function installMosaicPrefetch(viewer,config){
  const warm=new Map(),seen=new Set();let queue=[],active=0,timer=null,stopped=false;
  function trim(){while(warm.size>32){const key=[...warm.keys()].find(k=>warm.get(k).ready);if(key===undefined)break;warm.delete(key);}}
  function idle(){const image=viewer.world.getItemAt(0);return image?.getFullyLoaded()&&viewer.imageLoader.jobsInProgress===0;}
  function schedule(){queue=[];clearTimeout(timer);if(!stopped)timer=setTimeout(plan,250);}
  function pump(){
    if(stopped||!idle())return;
    while(active<2&&queue.length){
      const url=queue.shift();if(warm.has(url)||seen.has(url))continue;
      const entry={image:new Image(),ready:false,waiters:new Set()};warm.set(url,entry);active++;
      entry.image.onload=()=>{active--;entry.ready=true;trim();for(const fn of entry.waiters)fn(true);entry.waiters.clear();pump();};
      entry.image.onerror=()=>{active--;warm.delete(url);for(const fn of entry.waiters)fn(false);entry.waiters.clear();pump();};
      entry.image.src=url;
    }
  }
  function plan(){
    if(stopped)return;
    if(!idle()){timer=setTimeout(plan,250);return;}
    const image=viewer.world.getItemAt(0),bounds=image.viewportToImageRectangle(viewer.viewport.getBounds(true));
    const maxLevel=Math.ceil(Math.log2(Math.max(config.width,config.height)));
    const ratio=image.viewportToImageZoom(viewer.viewport.getZoom(true))*(window.devicePixelRatio||1);
    const level=Math.max(0,Math.min(maxLevel,Math.ceil(maxLevel+Math.log2(Math.max(ratio,1e-12)))));
    queue=mosaicPrefetchCandidates(config,bounds,level).map(t=>image.source.getTileUrl(t.level,t.x,t.y));pump();
  }
  viewer.addHandler('open',()=>{
    const source=viewer.world.getItemAt(0).source;
    const start=source.downloadTileStart,abort=source.downloadTileAbort;
    source.downloadTileStart=function(context){
      const entry=warm.get(context.src);
      if(!entry)return start.call(this,context);
      // Reuse a decoded neighbor image directly, independent of HTTP caching.
      const done=ok=>{if(!context.userData.warmCancelled){context.userData.warmCancel=null;if(ok)context.finish(entry.image,null,'image');else start.call(this,context);}};
      context.userData.warmCancel=()=>{context.userData.warmCancelled=true;entry.waiters.delete(done);};
      if(entry.ready){warm.delete(context.src);warm.set(context.src,entry);queueMicrotask(()=>done(true));}else entry.waiters.add(done);
    };
    source.downloadTileAbort=function(context){if(context.userData.warmCancel)context.userData.warmCancel();else abort.call(this,context);};
    schedule();
  });
  viewer.addHandler('viewport-change',schedule);
  viewer.addHandler('tile-loaded',event=>{
    const url=event.tile.getUrl();seen.delete(url);seen.add(url);if(seen.size>128)seen.delete(seen.values().next().value);
  });
  viewer.addHandler('close',()=>{stopped=true;queue=[];clearTimeout(timer);warm.clear();});
}
if(typeof module!=='undefined')module.exports={mosaicPrefetchCandidates,installMosaicPrefetch};
