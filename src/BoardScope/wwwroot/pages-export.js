const PagesExportUI={
  busy:false,target:null,
  init(){
    $('exportPages').onclick=()=>this.open();
    $('cancelPagesExport').onclick=()=>{if(!this.busy)$('pagesExportDialog').close();};
    $('pagesExportDialog').addEventListener('cancel',e=>{if(this.busy)e.preventDefault();});
    $('pagesBoardName').addEventListener('input',()=>{if(!$('pagesPublicUrl').dataset.edited)$('pagesPublicUrl').value=this.defaultUrl($('pagesBoardName').value);});
    $('pagesPublicUrl').addEventListener('input',()=>{$('pagesPublicUrl').dataset.edited='yes';});
    $('pagesExportForm').onsubmit=e=>{e.preventDefault();this.save();};
  },
  defaultUrl(board){return 'https://pewcb.github.io/PewCB.GigaPixelScans/'+encodeURIComponent(board.trim())+'/';},
  parentFolder(){
    const saved=localStorage.getItem('boardscope-pages-parent');
    // Migrate the former default while preserving other chosen repositories.
    return !saved||saved.replace(/\\/g,'/').replace(/\/+$/,'').toLowerCase()==='c:/exo/pewcb.github.io/boardscans'
      ?'C:\\exo\\PewCB.GigaPixelScans':saved;
  },
  open(){
    const p=selectedProject;if(!p?.dzi||this.busy||!state?.features.pagesExport)return;
    this.target={projectId:p.id,dzi:p.dzi};
    $('pagesParentFolder').value=this.parentFolder();
    $('pagesBoardName').value=p.name;$('pagesPublicUrl').value=this.defaultUrl(p.name);delete $('pagesPublicUrl').dataset.edited;
    $('pagesDescription').value=`Explore the ${p.name} PCB in a high-resolution microscope scan. Pan across the board and zoom into components, solder joints and traces with PewCB.`;
    $('pagesReplace').checked=false;$('pagesExportError').hidden=true;$('pagesExportResult').hidden=true;$('pagesExportDialog').showModal();$('pagesBoardName').focus();
  },
  async save(){
    if(!this.target||this.busy)return;
    const target=this.target;
    try{
      const board=$('pagesBoardName').value.trim(),parent=$('pagesParentFolder').value.trim();
      const description=$('pagesDescription').value.trim(),replaceExisting=$('pagesReplace').checked;
      if(!board||/[<>:"/\\|?*\u0000-\u001f]/.test(board)||board.startsWith('.')||/[. ]$/.test(board))throw Error('Enter a board name that can be used as a folder name.');
      const url=new URL($('pagesPublicUrl').value.trim());if(!['http:','https:'].includes(url.protocol)||url.search||url.hash)throw Error('Enter the public board page URL without query parameters or a fragment.');
      url.pathname=url.pathname.replace(/\/index\.html$/i,'/');if(!url.pathname.endsWith('/'))url.pathname+='/';
      this.busy=true;$('savePagesExport').disabled=$('cancelPagesExport').disabled=true;$('savePagesExport').textContent='Saving board…';$('pagesExportError').hidden=true;$('pagesExportResult').hidden=true;StitchHistory.updateControls();
      const [xml,vendor,license,svg]=await Promise.all([MosaicExport.text(`/projects/${target.projectId}/${target.dzi}`),MosaicExport.text('vendor/openseadragon.min.js'),MosaicExport.text('vendor/LICENSE.txt'),MosaicExport.text('export-assets/pewcb-logo.svg')]);
      const html=MosaicExport.build({title:board,seoTitle:`${board} PCB microscope scan | PewCB`,description,publicUrl:url.href,favicon:'data:image/svg+xml,'+encodeURIComponent(svg),mosaicUrl:'./'+target.dzi,...MosaicExport.metadata(xml)},vendor,license);
      const result=await api(`/projects/${target.projectId}/export-pages`,{parentFolder:parent,boardName:board,dzi:target.dzi,html,replaceExisting,description,publicUrl:url.href});
      localStorage.setItem('boardscope-pages-parent',parent);
      $('pagesExportResult').textContent=`Saved ${result.files.toLocaleString()} files to ${result.folder}${result.backup?`. Previous folder retained at ${result.backup}`:''}.`;$('pagesExportResult').hidden=false;
      toast('GitHub Pages folder saved. Board README and main scan index updated.');
    }catch(error){$('pagesExportError').textContent=error.message;$('pagesExportError').hidden=false;}
    finally{this.busy=false;$('savePagesExport').disabled=$('cancelPagesExport').disabled=false;$('savePagesExport').textContent='Save board folder';StitchHistory.updateControls();}
  }
};
