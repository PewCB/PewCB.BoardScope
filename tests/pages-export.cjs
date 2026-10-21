const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const elements=new Map(),element=id=>{if(!elements.has(id))elements.set(id,{value:'',hidden:false,disabled:false,dataset:{},addEventListener(){},showModal(){this.open=true;},focus(){}});return elements.get(id);};
const storage=new Map();let request,config;
const ctx={$:element,selectedProject:{id:'old-board',name:'CoreXY',dzi:'mosaic-old.dzi'},state:{features:{pagesExport:true}},
  localStorage:{getItem:k=>storage.get(k),setItem:(k,v)=>storage.set(k,v)},URL,encodeURIComponent,
  StitchHistory:{updateControls(){}},toast(){},MosaicExport:{text:async()=>'',metadata:()=>({width:640,height:360}),build:c=>{config=c;return '<!doctype html><title>CoreXY</title>';}},
  api:async(path,body)=>{request={path,body};return {files:12,folder:'C:\\output\\CoreXY',backup:null};}};
vm.runInNewContext(fs.readFileSync('src/BoardScope/wwwroot/pages-export.js','utf8')+';globalThis.pages=PagesExportUI;',ctx);
(async()=>{
  storage.set('boardscope-pages-parent','C:\\exo\\pewcb.github.io\\BoardScans');
  assert.equal(ctx.pages.parentFolder(),'C:\\exo\\PewCB.GigaPixelScans');
  storage.set('boardscope-pages-parent','D:\\custom-site');
  assert.equal(ctx.pages.parentFolder(),'D:\\custom-site');
  storage.clear();
  ctx.pages.open();assert.equal(element('pagesParentFolder').value,'C:\\exo\\PewCB.GigaPixelScans');assert.equal(element('pagesPublicUrl').value,'https://pewcb.github.io/PewCB.GigaPixelScans/CoreXY/');
  ctx.selectedProject={id:'new-board',name:'Other',dzi:'mosaic-new.dzi'};await ctx.pages.save();
  assert.equal(request.path,'/projects/old-board/export-pages');assert.equal(request.body.dzi,'mosaic-old.dzi');assert.equal(config.mosaicUrl,'./mosaic-old.dzi');assert.equal(config.publicUrl,'https://pewcb.github.io/PewCB.GigaPixelScans/CoreXY/');assert.equal(config.seoTitle,'CoreXY PCB microscope scan | PewCB');assert.ok(config.favicon.startsWith('data:image/svg+xml,'));
  assert.equal(request.body.publicUrl,config.publicUrl);assert.equal(request.body.description,config.description);
  assert.equal(element('pagesExportResult').hidden,false);assert.equal(storage.get('boardscope-pages-parent'),'C:\\exo\\PewCB.GigaPixelScans');
  console.log('PASS Pages saver defaults to the requested repository, preserves the selected historical map, sets relative links/branding and reports the saved folder');
  const prior=request;element('pagesBoardName').value='../other';await ctx.pages.save();assert.equal(request,prior);assert.equal(element('pagesExportError').hidden,false);
  const html=fs.readFileSync('src/BoardScope/wwwroot/index.html','utf8'),ids=[...html.matchAll(/\bid="([^"]+)"/g)].map(m=>m[1]);
  for(const m of fs.readFileSync('src/BoardScope/wwwroot/pages-export.js','utf8').matchAll(/\$\('([^']+)'\)/g))assert.ok(ids.includes(m[1]),`missing ${m[1]}`);
  console.log('PASS invalid folder names never submit and all Pages controls have matching HTML bindings');
})().catch(e=>{console.error(e);process.exitCode=1;});
