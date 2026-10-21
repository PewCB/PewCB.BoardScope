const MatchDebugMath={
  key(first,second){return `${Math.min(first,second)}:${Math.max(first,second)}`;},
  graph(count,pairs=[]) {
    const neighbors=Array.from({length:count},()=>[]),byKey=new Map();
    for(const pair of pairs){if(pair.first<0||pair.second<0||pair.first>=count||pair.second>=count||pair.first===pair.second)continue;
      byKey.set(this.key(pair.first,pair.second),pair);
      neighbors[pair.first].push({index:pair.second,pair});neighbors[pair.second].push({index:pair.first,pair});}
    const support=neighbors.map(list=>list.filter(n=>n.pair.accepted).length),components=Array(count).fill(-1),sizes=[];
    for(let i=0;i<count;i++)if(components[i]<0){const group=sizes.length,queue=[i];components[i]=group;let size=0;
      while(queue.length){const current=queue.pop();size++;for(const n of neighbors[current])if(n.pair.accepted&&components[n.index]<0){components[n.index]=group;queue.push(n.index);}}
      sizes.push(size);}
    return {neighbors,byKey,support,components,sizes};
  },
  apply(t,p){return {x:t.x+t.a*p.x-t.b*p.y,y:t.y+t.b*p.x+t.a*p.y};},
  local(t,p){const dx=p.x-t.x,dy=p.y-t.y,d=t.a*t.a+t.b*t.b;return {x:(t.a*dx+t.b*dy)/d,y:(t.a*dy-t.b*dx)/d};},
  contains(t,width,height,p){const q=this.local(t,p);return q.x>=0&&q.y>=0&&q.x<width&&q.y<height;},
  // Stable selection depends on geometry, never on hover-induced layer order.
  distance(local,width,height){return Math.hypot((local.x-width/2)/width,(local.y-height/2)/height);},
  visibleMatch(match,report,showRejected){return (report.accepted&&match.status==='retained')||showRejected;},
  residual(match,first,second){const p=this.apply(first,match.first),q=this.apply(second,match.second);return Math.hypot(p.x-q.x,p.y-q.y);}
};
if(typeof module!=='undefined')module.exports=MatchDebugMath;
