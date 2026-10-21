function standaloneMosaicViewer(){
  const config=JSON.parse(document.getElementById('mosaic-config').textContent);
  const status=document.getElementById('status');
  const source={width:config.width,height:config.height,tileSize:config.tileSize,tileOverlap:config.overlap,
    minLevel:0,maxLevel:Math.ceil(Math.log2(Math.max(config.width,config.height))),
    getTileUrl(level,x,y){
      const url=new URL(config.mosaicUrl,document.baseURI);
      url.pathname=url.pathname.replace(/\.dzi$/i,`_files/${level}/${x}_${y}.${config.format}`);
      url.hash='';return url.href;
    }};
  // Inline pyramid metadata avoids fetching XML from a file:// page. Canvas
  // and ordinary image requests also allow viewing tiles across origins.
  const viewer=OpenSeadragon({id:'viewer',tileSources:source,drawer:'canvas',
    showNavigationControl:false,showNavigator:true,navigatorPosition:'BOTTOM_RIGHT',
    maxZoomPixelRatio:2,visibilityRatio:.5,constrainDuringPan:true,
    crossOriginPolicy:false,loadTilesWithAjax:false,minPixelRatio:1,maxImageCacheCount:128,imageLoaderLimit:6,
    gestureSettingsMouse:{clickToZoom:false}});
  installMosaicPrefetch(viewer,config);
  document.getElementById('zoom-in').onclick=()=>viewer.viewport.zoomBy(1.5);
  document.getElementById('zoom-out').onclick=()=>viewer.viewport.zoomBy(1/1.5);
  document.getElementById('fit').onclick=()=>viewer.viewport.goHome();
  document.getElementById('pixels').onclick=()=>{const image=viewer.world.getItemAt(0);if(image)viewer.viewport.zoomTo(image.imageToViewportZoom(1));};
  document.getElementById('fullscreen').onclick=()=>viewer.setFullScreen(!viewer.isFullPage());
  viewer.addHandler('tile-load-failed',()=>{status.textContent='Some tiles could not load. Check the mosaic address and that its image files are available.';status.hidden=false;});
  viewer.addHandler('open-failed',()=>{status.textContent='The mosaic could not open. Check its address and image files.';status.hidden=false;});
}
const MosaicExport={
  busy:false,pending:null,
  fileAddress(folder,projectId,dzi){
    if(typeof folder!=='string')throw Error('The local mosaic folder is unavailable. Refresh the app and try again.');
    const path=folder.replace(/\\/g,'/').replace(/\/$/,'');
    let prefix;
    if(/^[a-z]:(?:\/|$)/i.test(path))prefix='file:///'+path.split('/').map((part,i)=>i===0?part:encodeURIComponent(part)).join('/');
    else if(path.startsWith('//')){const [host,...parts]=path.slice(2).split('/');prefix='file://'+host+'/'+parts.map(encodeURIComponent).join('/');}
    else throw Error('The local mosaic folder is unavailable. Refresh the app and try again.');
    return this.address(prefix+'/'+encodeURIComponent(projectId)+'/'+dzi.split('/').map(encodeURIComponent).join('/'));
  },
  address(value){
    const reference=value.trim().replace(/\\/g,'/');
    let url;try{url=new URL(reference,'https://viewer.invalid/viewer.html');}catch{throw Error('Enter a relative path or an http://, https:// or file:/// address to a .dzi mosaic.');}
    if(!['http:','https:','file:'].includes(url.protocol)||!/\.dzi$/i.test(url.pathname))throw Error('Use a relative path or an http://, https:// or file:/// address ending in .dzi.');
    url.hash='';
    // Relative references must stay relative until the saved HTML is opened.
    return /^[a-z][a-z0-9+.-]*:/i.test(reference)?url.href:reference.split('#')[0];
  },
  filename(value){
    let name=value.trim();if(!name||/[<>:"/\\|?*\u0000-\u001f]/.test(name)||/[. ]$/.test(name))throw Error('Enter a filename without path separators or reserved filename characters.');
    if(!/\.html$/i.test(name))name+='.html';
    if(name.length>180||/^(con|prn|aux|nul|com[1-9]|lpt[1-9])\./i.test(name))throw Error('Choose another filename (up to 180 characters).');
    return name;
  },
  metadata(xml){
    const doc=new DOMParser().parseFromString(xml,'application/xml');
    const image=doc.getElementsByTagNameNS('*','Image')[0],size=image?.getElementsByTagNameNS('*','Size')[0];
    if(doc.getElementsByTagName('parsererror').length||!image||!size)throw Error('The saved map has an invalid DZI descriptor.');
    const result={width:Number(size.getAttribute('Width')),height:Number(size.getAttribute('Height')),tileSize:Number(image.getAttribute('TileSize')),overlap:Number(image.getAttribute('Overlap')),format:image.getAttribute('Format')};
    if(![result.width,result.height,result.tileSize].every(v=>Number.isSafeInteger(v)&&v>0)||!Number.isSafeInteger(result.overlap)||result.overlap<0||result.overlap>=result.tileSize||!['jpg','jpeg','png','webp'].includes(result.format))throw Error('The saved map has unsupported pyramid metadata.');
    return result;
  },
  build(config,vendor,license){
    config={...config,mosaicUrl:this.address(config.mosaicUrl)};
    const escape=value=>String(value).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    const json=JSON.stringify(config).replace(/[<>&\u2028\u2029]/g,c=>'\\u'+c.charCodeAt(0).toString(16).padStart(4,'0'));
    const script=value=>value.replace(/<\/script/gi,'<\\/script');
    const favicon=config.favicon?`<link rel="icon" type="image/svg+xml" href="${escape(config.favicon)}">`:'';
    let social='',fallback='';
    if(config.publicUrl){
      const canonical=new URL(config.publicUrl);if(!['http:','https:'].includes(canonical.protocol))throw Error('Enter an HTTP or HTTPS public page address.');
      const page=canonical.href,preview=new URL('preview.jpg',page).href;
      fallback=`<noscript><img src="./preview.jpg" width="1200" height="630" style="max-width:100%;height:auto" alt="${escape(config.title)} PCB microscope scan"></noscript>`;
      social=`<meta name="description" content="${escape(config.description)}"><meta name="robots" content="index, follow"><link rel="canonical" href="${escape(page)}">
<meta property="og:type" content="website"><meta property="og:site_name" content="PewCB"><meta property="og:title" content="${escape(config.seoTitle||config.title)}"><meta property="og:description" content="${escape(config.description)}"><meta property="og:url" content="${escape(page)}"><meta property="og:image" content="${escape(preview)}"><meta property="og:image:width" content="1200"><meta property="og:image:height" content="630"><meta property="og:image:alt" content="${escape(config.title)} PCB microscope scan">
<meta name="twitter:card" content="summary_large_image"><meta name="twitter:title" content="${escape(config.seoTitle||config.title)}"><meta name="twitter:description" content="${escape(config.description)}"><meta name="twitter:image" content="${escape(preview)}"><meta name="twitter:image:alt" content="${escape(config.title)} PCB microscope scan">`;
    }
    return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>${escape(config.seoTitle||config.title)}</title>${favicon}${social}
<style>*{box-sizing:border-box}html,body{height:100%;margin:0;font:14px system-ui,sans-serif;background:#121c28;color:#d3dfe7}body{display:flex;flex-direction:column}header{display:flex;align-items:center;gap:18px;padding:12px 16px;background:#192735;border-bottom:1px solid #344757;flex-wrap:wrap}h1{font-size:16px;margin:0;overflow-wrap:anywhere}.description{font-size:11px;color:#92a9b8;margin:4px 0 0}.tools{display:flex;gap:6px;margin-left:auto;flex-wrap:wrap}button{font:inherit;color:#d3dfe7;background:#203342;border:1px solid #405969;border-radius:6px;padding:7px 11px;cursor:pointer}button:hover{background:#2e4859}button:focus-visible{outline:2px solid #70d2b2;outline-offset:2px}#viewer{flex:1;min-height:0}#status{position:fixed;bottom:12px;left:12px;right:12px;background:#783b3bea;border:1px solid #b57c7c;border-radius:6px;padding:10px;font-size:12px;pointer-events:none}[hidden]{display:none!important}</style></head>
<body><header><div><h1>${escape(config.title)}</h1><p class="description">${escape(config.description)} · ${config.width.toLocaleString()} × ${config.height.toLocaleString()} px</p></div><nav class="tools" aria-label="Mosaic view"><button id="zoom-out" title="Zoom out">−</button><button id="zoom-in" title="Zoom in">+</button><button id="fit">Fit</button><button id="pixels" title="One image pixel per screen pixel">1:1</button><button id="fullscreen">Full screen</button></nav></header>${fallback}<div id="viewer" aria-label="Stitched mosaic"></div><p id="status" role="status" hidden></p>
<!-- Change mosaicUrl below to relocate the pyramid. Keep the .dzi and its matching _files folder together. -->
<script id="mosaic-config" type="application/json">${json}</script>
<script id="openseadragon-license" type="text/plain">${script(license)}</script>
<script>${script(vendor)}</script>
<script>${mosaicPrefetchCandidates.toString()}
${installMosaicPrefetch.toString()}
(${standaloneMosaicViewer.toString()})();</script></body></html>`;
  },
  init(){
    $('exportMosaic').onclick=()=>this.open();
    $('cancelMosaicExport').onclick=()=>{if(!this.busy)$('mosaicExportDialog').close();};
    $('mosaicExportDialog').addEventListener('cancel',e=>{if(this.busy)e.preventDefault();});
    $('mosaicExportForm').onsubmit=e=>{e.preventDefault();this.save();};
  },
  open(){
    const p=selectedProject;if(!p?.dzi||captureFollowing||this.busy)return;
    const map=StitchHistory.map(p),stitch=StitchHistory.alignment(p);
    const source=new URL(`/projects/${encodeURIComponent(p.id)}/${p.dzi}`,location.origin).href;
    this.pending={source,title:p.name,description:stitch?`${StitchHistory.methodName(stitch.method)} · ${StitchHistory.date(map?.created||stitch.created)} · ${map?.blendMode||p.blendMode}`:'Stitched mosaic'};
    $('mosaicExportName').value=this.filename(`${p.name.replace(/[<>:"/\\|?*\u0000-\u001f]/g,'-').slice(0,100).trim()||'Mosaic'}-viewer-${(map?.id||p.dzi.replace(/\.dzi$/i,'')).slice(-8)}`);
    $('mosaicExportUrl').value=this.fileAddress(state.dataFolder,p.id,p.dzi);$('mosaicExportError').hidden=true;$('mosaicExportDialog').showModal();$('saveMosaicExport').focus();
  },
  async text(url){const response=await fetch(url);if(!response.ok)throw Error(`Cannot read export source (${response.status}).`);return response.text();},
  async save(){
    if(!this.pending||this.busy)return;
    const target=this.pending;
    try{
      const filename=this.filename($('mosaicExportName').value),mosaicUrl=this.address($('mosaicExportUrl').value);
      this.busy=true;$('saveMosaicExport').disabled=$('cancelMosaicExport').disabled=$('exportMosaic').disabled=true;$('mosaicExportError').hidden=true;
      // Open the picker during the user's click, before asynchronous fetching.
      let handle=null;
      if(typeof window.showSaveFilePicker==='function'){
        try{handle=await window.showSaveFilePicker({suggestedName:filename,types:[{description:'Mosaic viewer HTML',accept:{'text/html':['.html']}}]});}
        catch(error){if(!['SecurityError','NotSupportedError'].includes(error.name))throw error;}
      }
      const [xml,vendor,license]=await Promise.all([this.text(target.source),this.text('vendor/openseadragon.min.js'),this.text('vendor/LICENSE.txt')]);
      const html=this.build({title:target.title,description:target.description,mosaicUrl,...this.metadata(xml)},vendor,license);
      const blob=new Blob([html],{type:'text/html;charset=utf-8'});
      if(handle){const writer=await handle.createWritable();try{await writer.write(blob);await writer.close();}catch(error){await writer.abort().catch(()=>{});throw error;}}
      else{
        const url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download=filename;document.body.appendChild(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),60000);
      }
      $('mosaicExportDialog').close();toast(handle?'Viewer HTML saved.':'Viewer HTML download started.');
    }catch(error){if(error.name!=='AbortError'){$('mosaicExportError').textContent=error.message;$('mosaicExportError').hidden=false;}}
    finally{this.busy=false;$('saveMosaicExport').disabled=$('cancelMosaicExport').disabled=false;StitchHistory.updateControls();}
  }
};
if(typeof module!=='undefined')module.exports={MosaicExport,standaloneMosaicViewer};
