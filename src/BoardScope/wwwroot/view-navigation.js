const ViewNavigation={
  // Tile worlds use one source-frame width per unit; mosaics use their full
  // width. Store the visible rectangle in shared project pixels between loads.
  toPixels(bounds,frame){return {
    x:bounds.x*frame.scale+frame.x,y:bounds.y*frame.scale+frame.y,
    width:bounds.width*frame.scale,height:bounds.height*frame.scale
  };},
  toViewport(bounds,frame){return {
    x:(bounds.x-frame.x)/frame.scale,y:(bounds.y-frame.y)/frame.scale,
    width:bounds.width/frame.scale,height:bounds.height/frame.scale
  };}
};
if(typeof module!=='undefined')module.exports=ViewNavigation;
