// Jog Y+ means up. The scan planner reverses that component for grid rows down.
const JogMapping = {
  map(x, y, step, settings) {
    return this.imageToStage(x*step,-y*step,{...settings,gridYSign:-1});
  },
  imageToStage(u,v,s) {
    const angle=(s.stageRotationDegrees||0)*Math.PI/180,c=Math.cos(angle),sn=Math.sin(angle);
    const x=c*u+sn*v,y=-sn*u+c*v;
    return {x:s.stageXSign*(s.swapStageAxes?y*(s.gridYSign??-1):x)||0,y:s.stageYSign*(s.swapStageAxes?x:y*(s.gridYSign??-1))||0};
  },
  stageToImage(dx,dy,s) {
    const x=dx*s.stageXSign,y=dy*s.stageYSign,u=s.swapStageAxes?y:x,v=(s.swapStageAxes?x:y)*(s.gridYSign??-1);
    const angle=(s.stageRotationDegrees||0)*Math.PI/180,c=Math.cos(angle),sn=Math.sin(angle);
    return {x:c*u-sn*v,y:sn*u+c*v};
  }
};
if (typeof module !== 'undefined') module.exports = JogMapping;
