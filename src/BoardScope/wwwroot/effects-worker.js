importScripts('effect-math.js?v=1e90e5bfdbb8');
let original=null;
self.onmessage=({data})=>{
  try{
    if(data.type==='source'){original=new Uint8ClampedArray(data.pixels);self.postMessage({type:'ready',source:data.source});return;}
    if(!original)throw Error('Preview source is not loaded.');
    const output=EffectMath.apply(original,data.effects);
    self.postMessage({type:'frame',revision:data.revision,pixels:output.buffer},[output.buffer]);
  }catch(error){self.postMessage({type:'error',message:error.message});}
};
