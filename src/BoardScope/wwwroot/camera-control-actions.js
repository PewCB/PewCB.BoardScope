const CameraControlActions={
  async freeze(api,group,name){
    const controls=await api('/camera/properties');
    const current=controls.find(p=>p.group===group&&p.name===name);
    if(!current)throw Error('Camera no longer exposes '+name+'.');
    if(!current.canManual)throw Error(name+' does not support manual mode.');
    if(!current.auto)return controls;
    return api('/camera/property',{group,name,value:current.value,auto:false});
  },
  async lockAll(api){
    let controls=await api('/camera/properties');
    const automatic=controls.filter(p=>p.auto&&p.canManual);
    for(const p of automatic)controls=await this.freeze(api,p.group,p.name);
    return controls;
  }
};
if(typeof module!=='undefined')module.exports=CameraControlActions;
