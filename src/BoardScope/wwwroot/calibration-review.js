const CalibrationReview={
  layout(width,height,x,y) {
    const left=Math.min(0,x),top=Math.min(0,y),right=Math.max(width,width+x),bottom=Math.max(height,height+y);
    const overlap={x:Math.max(0,x),y:Math.max(0,y),right:Math.min(width,width+x),bottom:Math.min(height,height+y)};
    if(overlap.right<=overlap.x || overlap.bottom<=overlap.y)throw Error('The calibration pair has no overlap.');
    const rect=(x,y,w,h)=>({x,y,width:w,height:h});
    return {first:rect(-left/width,-top/width,1,height/width),second:rect((x-left)/width,(y-top)/width,1,height/width),
      union:rect(0,0,(right-left)/width,(bottom-top)/width),overlap:rect((overlap.x-left)/width,(overlap.y-top)/width,(overlap.right-overlap.x)/width,(overlap.bottom-overlap.y)/width),
      clips:[rect(overlap.x,overlap.y,overlap.right-overlap.x,overlap.bottom-overlap.y),rect(overlap.x-x,overlap.y-y,overlap.right-overlap.x,overlap.bottom-overlap.y)]};
  },
  sameOrientation(current,previous) {
    return ['rotation','flipX','flipY','stageXSign','stageYSign','swapStageAxes'].every(key=>current[key]===previous[key]) && (current.stageRotationDegrees||0)===(previous.stageRotationDegrees||0);
  }
};
if(typeof module!=='undefined')module.exports=CalibrationReview;
