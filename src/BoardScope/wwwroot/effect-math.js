// Same transform and rounding as ImageEffectTransform in ImageEffects.cs.
const EffectMath={
  keys:['brightness','contrast','shadows','highlights','saturation','temperature','tint'],
  settings(value={}){const result={};for(const key of this.keys){const n=value[key]??0;if(!Number.isFinite(n)||n< -100||n>100)throw Error('Image effects must be between −100 and 100.');result[key]=n;}return result;},
  transform(value){
    const s=this.settings(value),exposure=2**(s.brightness/50),t=s.temperature/100,tint=s.tint/100;
    const red=new Float64Array(256),green=new Float64Array(256),blue=new Float64Array(256),tone=new Float64Array(4097);
    const rg=exposure*2**(.35*t+.12*tint),gg=exposure*2**(-.24*tint),bg=exposure*2**(-.35*t+.12*tint),contrast=2**(s.contrast/100);
    for(let k=0;k<256;k++){red[k]=k/255*rg;green[k]=k/255*gg;blue[k]=k/255*bg;}
    for(let k=0;k<=4096;k++){const l=k/4096;tone[k]=(l-.5)*contrast+.5+.5*(s.shadows/100)*(1-l)**3+.5*(s.highlights/100)*l**3;}
    return {red,green,blue,tone,contrast,saturation:1+s.saturation/100};
  },
  apply(input,value,output=new Uint8ClampedArray(input.length)){
    const {red,green,blue,tone,contrast,saturation}=this.transform(value);
    for(let k=0;k<input.length;k+=4){
      const r=red[input[k]],g=green[input[k+1]],b=blue[input[k+2]],l=.2126*r+.7152*g+.0722*b,index=Math.max(0,Math.min(1,l))*4096,i=Math.floor(index);
      let target=tone[i]+(tone[Math.min(i+1,4096)]-tone[i])*(index-i);if(l>1)target+=contrast*(l-1);
      const offset=target-saturation*l;
      output[k]=Math.max(0,Math.min(255,Math.floor((saturation*r+offset)*255+.5)));
      output[k+1]=Math.max(0,Math.min(255,Math.floor((saturation*g+offset)*255+.5)));
      output[k+2]=Math.max(0,Math.min(255,Math.floor((saturation*b+offset)*255+.5)));
      output[k+3]=input[k+3];
    }
    return output;
  }
};
if(typeof module!=='undefined')module.exports=EffectMath;
