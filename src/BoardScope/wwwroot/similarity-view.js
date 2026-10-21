const SimilarityView={
  geometry(t,width,height) {
    const scale=Math.hypot(t.a,t.b),degrees=Math.atan2(t.b,t.a)*180/Math.PI;
    const apply=(x,y)=>({x:t.x+t.a*x-t.b*y,y:t.y+t.b*x+t.a*y});
    const corners=[apply(0,0),apply(width,0),apply(0,height),apply(width,height)],centre=apply(width/2,height/2);
    return {x:centre.x-width*scale/2,y:centre.y-height*scale/2,width:width*scale,height:height*scale,degrees,
      left:Math.min(...corners.map(p=>p.x)),top:Math.min(...corners.map(p=>p.y)),right:Math.max(...corners.map(p=>p.x)),bottom:Math.max(...corners.map(p=>p.y))};
  }
};
if(typeof module!=='undefined')module.exports=SimilarityView;
