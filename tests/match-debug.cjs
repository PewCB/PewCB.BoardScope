const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const math=require('../src/BoardScope/wwwroot/match-debug-math.js');
const accepted={first:0,second:1,accepted:true,matches:30,inliers:20,errorPixels:.2,rotationDegrees:0,scale:1,reason:'Accepted'};
const rejected={...accepted,first:0,second:2,accepted:false,reason:'Too few features'};
const graph=math.graph(5,[accepted,rejected,{...accepted,first:3,second:4}]);
assert.deepEqual(graph.support,[1,1,0,1,1]);assert.deepEqual(graph.sizes,[2,1,2]);
assert.equal(graph.neighbors[0].length,2);assert.equal(graph.byKey.get(math.key(1,0)),accepted);
assert.equal(graph.components[0],graph.components[1]);assert.notEqual(graph.components[0],graph.components[2]);
const transform={a:0,b:2,x:100,y:10},point={x:10,y:20},placed=math.apply(transform,point);
assert.deepEqual(placed,{x:60,y:30});assert.deepEqual(math.local(transform,placed),point);
assert.equal(math.contains(transform,100,50,placed),true);assert.equal(math.contains(transform,100,50,{x:200,y:30}),false);
const match={first:{x:80,y:40},second:{x:20,y:40},status:'retained'};
assert.equal(math.residual(match,{a:1,b:0,x:0,y:0},{a:1,b:0,x:60,y:0}),0);
assert.equal(math.residual(match,{a:1,b:0,x:0,y:0},{a:1,b:0,x:63,y:4}),5);
assert.equal(math.visibleMatch(match,accepted,false),true);assert.equal(math.visibleMatch(match,rejected,false),false);
assert.equal(math.visibleMatch({...match,status:'stage-gate'},accepted,false),false);assert.equal(math.visibleMatch(match,rejected,true),true);
console.log('PASS accepted/rejected graph, disconnected groups, native feature placement, rotated bounds and displayed residuals');

const elements=new Map(),element=id=>{
  if(!elements.has(id))elements.set(id,{checked:true,hidden:false,disabled:false,value:'',textContent:'',innerHTML:'',style:{},classList:{toggle(){}},addEventListener(){}});
  return elements.get(id);
};
const project={id:'test',tiles:[{width:100,height:80},{width:100,height:80},{width:100,height:80}],registration:{pairs:[accepted,rejected],reportFile:'registration-test.json'}};
const items=[0,60,120].map(x=>({viewportToImageCoordinates:p=>({x:p.x-x,y:p.y}),setOpacity(value){this.opacity=value;}}));
const context={MatchDebugMath:math,$:element,selectedProject:project,loadedView:'tiles',pinnedTile:-1,highlightedTile:-1,tileItems:items,tileOutlines:[],state:{features:{matchDiagnostics:true},engine:{job:{busy:false}}},
  viewer:{world:{getItemCount:()=>3,setItemIndex(item){this.top=item;}}},requestAnimationFrame:()=>1,clearTimeout,setTimeout,AbortController,document:{addEventListener(){}},
  highlightTile(index){context.highlightedTile=index;context.debug.paint(index);}};
vm.runInNewContext(fs.readFileSync('src/BoardScope/wwwroot/match-debug.js','utf8')+';globalThis.debug=MatchDebug;',context);
const debug=context.debug;debug.reset(project);
const hoverFlags=[false,false,false];context.tileOutlines=hoverFlags.map((_,i)=>({style:{},classList:{toggle(name,on){if(name==='hovered')hoverFlags[i]=on;}}}));
debug.cache.set('0:1',{matches:[],featuresFirst:[],featuresSecond:[],totalFeaturesFirst:0,totalFeaturesSecond:0});
debug.pointer({x:70,y:40});assert.equal(context.highlightedTile,0);assert.equal(debug.pair,accepted);
assert.equal(items[0].opacity,1);assert.equal(items[1].opacity,1);assert.equal(items[2].opacity,.1);
// Moving the top image must not change hit selection at the same cursor position.
debug.onTop(1,false);debug.pointer({x:70,y:40});assert.equal(context.highlightedTile,0);
debug.click({quick:true});assert.equal(context.pinnedTile,0);
debug.pointer({x:175,y:40});assert.equal(context.highlightedTile,0,'pinned selection survives moving over another tile');
assert.deepEqual(hoverFlags,[false,false,true],'tile labels follow the pointer even when another tile is pinned');
debug.click({quick:false});assert.equal(context.pinnedTile,0,'dragging never changes the pin');
assert.ok(debug.tileInfo(0).includes('Accepted neighbors'));assert.ok(debug.tileInfo(0).includes('Rejected neighbors'));
context.highlightedTile=-1;debug.onTop(1);assert.equal(context.highlightedTile,0,'pair controls restore pinned highlighting after leaving the viewer');
assert.equal(items[0].opacity,1);assert.equal(items[1].opacity,1);
element('showNeighborDebug').checked=false;debug.paint(0);assert.equal(items[1].opacity,.12,'disabled diagnostics restores single-tile isolation');
debug.reset(null);assert.equal(debug.graph,null);assert.equal(debug.pair,null);assert.equal(element('matchDebugPanel').hidden,true);
console.log('PASS overlap selection is independent of layer order, both pair endpoints stay visible, pinning survives pointer movement, and project reset clears stale diagnostics');

const html=fs.readFileSync('src/BoardScope/wwwroot/index.html','utf8'),ids=[...html.matchAll(/\bid="([^"]+)"/g)].map(m=>m[1]);
assert.equal(new Set(ids).size,ids.length,'unique HTML ids');
for(const file of ['match-debug.js','app.js'])for(const m of fs.readFileSync('src/BoardScope/wwwroot/'+file,'utf8').matchAll(/\$\('([^']+)'\)/g))assert.ok(ids.includes(m[1]),`missing ${m[1]}`);
assert.ok(html.indexOf('match-debug.js')<html.indexOf('app.js?'));
console.log('PASS diagnostic controls exist once and load before app integration');

(async()=>{
  const scheduled=[],pending=[],race={...context,pinnedTile:-1,highlightedTile:0,
    setTimeout(fn){scheduled.push(fn);return fn;},clearTimeout(fn){const index=scheduled.indexOf(fn);if(index>=0)scheduled.splice(index,1);},
    fetch(){return new Promise(resolve=>pending.push(resolve));},
    highlightTile(index){race.highlightedTile=index;race.debug.paint(index);}};
  element('showNeighborDebug').checked=true;
  vm.runInNewContext(fs.readFileSync('src/BoardScope/wwwroot/match-debug.js','utf8')+';globalThis.debug=MatchDebug;',race);
  race.debug.reset(project);race.debug.tile=0;race.debug.selectPair(accepted);const first=scheduled.shift()();
  race.debug.selectPair(rejected);const second=scheduled.shift()();
  const old={first:0,second:1,matches:[],featuresFirst:[],featuresSecond:[],totalFeaturesFirst:0,totalFeaturesSecond:0};
  pending.shift()({ok:true,json:async()=>old});await first;
  assert.equal(race.debug.data,null,'old pair response cannot overwrite the newly hovered pair');
  const current={...old,second:2};pending.shift()({ok:true,json:async()=>current});await second;
  assert.equal(race.debug.data,current);assert.equal(race.debug.cache.has('0:1'),false);
  race.debug.selectPair(accepted);const third=scheduled.shift()();race.debug.reset(null);
  pending.shift()({ok:true,json:async()=>old});await third;assert.equal(race.debug.data,null,'project switch rejects late diagnostics');
  console.log('PASS rapid hover and project changes cancel stale feature responses without corrupting the current overlay');
  const trialOptions={maxRotationDegrees:3,maxScalePercent:3,maxShiftMm:.25,workingSize:0,maxFeatures:12000,contrastThreshold:.003,enhanceLocalContrast:true};
  let trialResolve,trialCall;
  const trial={...context,pinnedTile:-1,highlightedTile:0,state:{features:{featureSensitivity:true},engine:{job:{busy:false}}},registrationSettings:()=>trialOptions,
    fetch(url,options){trialCall={url,options};return new Promise(resolve=>trialResolve=resolve);},
    highlightTile(index){trial.highlightedTile=index;trial.debug.paint(index);}};
  vm.runInNewContext(fs.readFileSync('src/BoardScope/wwwroot/match-debug.js','utf8')+';globalThis.debug=MatchDebug;',trial);
  trial.debug.reset(project);trial.debug.tile=0;trial.debug.pair=rejected;
  const task=trial.debug.trySettings();assert.equal(trialCall.options.method,'POST');
  assert.equal(JSON.parse(trialCall.options.body).options.contrastThreshold,.003);assert.equal(trial.debug.trialBusy,true);
  const result={...old,second:2,trial:true,reconstructed:true,analysisOptions:trialOptions,reconstructionReport:{...rejected,accepted:true,reason:'Accepted'}};
  trialResolve({ok:true,json:async()=>result});await task;
  assert.equal(trial.debug.displayReport().accepted,true,'preview features follow preview acceptance');
  assert.equal(trial.debug.graph.byKey.get('0:2').accepted,false,'saved neighbor graph is not modified by preview');
  assert.equal(trial.debug.cache.size,0,'preview does not replace cached saved feature observations');
  assert.equal(trial.debug.trialBusy,false);assert.ok(element('matchPairStatus').textContent.includes('PREVIEW ONLY'));
  console.log('PASS detection preview submits current settings, shows preview acceptance, and preserves the saved neighbor graph and feature cache');
  trial.state.features.phaseCorrelation=true;
  trial.registrationSettings=method=>({...trialOptions,method,phaseMinResponse:.15,phaseMinCorrelation:.3,phaseHighPass:true});
  const phaseTask=trial.debug.trySettings('phase');
  assert.equal(JSON.parse(trialCall.options.body).options.method,'phase');
  assert.equal(element('alternatePairMethod').disabled,true,'both previews are disabled while a request is running');
  const phaseReport={...rejected,method:'phase',accepted:true,reason:'Accepted phase correlation',phase:{response:.85,correlation:.94,deltaX:123.4,deltaY:-2.5,correctionX:3.4,correctionY:-2.5,width:400,height:300}};
  trialResolve({ok:true,json:async()=>({...result,analysisOptions:{...trialOptions,method:'phase',phaseHighPass:true},reconstructionReport:phaseReport,translationConstraint:{first:{x:80,y:40},second:{x:20,y:40},status:'phase-constraint'}})});await phaseTask;
  assert.ok(element('matchPairNumbers').textContent.includes('Response 0.850'));
  assert.ok(element('matchPairNumbers').textContent.includes('aligned correlation 0.940'));
  assert.ok(!element('matchPairNumbers').textContent.includes('inliers'));
  assert.equal(element('showUnmatchedFeatures').disabled,true);
  assert.equal(element('matchObservationLabel').textContent,'Show measured translation');
  assert.ok(element('matchPairStatus').textContent.includes('no feature points are detected'));
  assert.equal(trial.debug.graph.byKey.get('0:2').accepted,false);
  const phaseProject={...project,registration:{...project.registration,options:{method:'phase'}}};trial.selectedProject=phaseProject;trial.debug.updateButtons();
  assert.equal(element('alternatePairMethod').textContent,'Try feature matching on this pair');
  assert.equal(element('trialMatchFeatures').textContent,'Try current correlation settings');
  console.log('PASS alternative phase preview submits its method, displays correlation diagnostics without invented features, and leaves the saved graph intact');
  assert.equal(element('nccPairMethod').disabled,true,'NCC preview stays disabled against the older running backend');
  const oldCall=trialCall;await trial.debug.trySettings('ncc');assert.equal(trialCall,oldCall,'unsupported NCC cannot submit to the running old backend');
  trial.state.features.normalizedCrossCorrelation=true;
  const nccTask=trial.debug.trySettings('ncc');
  assert.equal(JSON.parse(trialCall.options.body).options.method,'ncc');
  assert.equal(element('nccPairMethod').disabled,true);
  const nccReport={...phaseReport,method:'ncc',phase:null,ncc:{peakCorrelation:.92,correlation:.94,secondCorrelation:.15,peakGap:.77,deltaX:123.4,deltaY:-2.5,correctionX:3.4,correctionY:-2.5,width:400,height:300,searchRadiusX:65,searchRadiusY:65}};
  const nccData={...result,analysisOptions:{...trialOptions,method:'ncc',nccHighPass:true,nccMinPeakGap:0},reconstructionReport:nccReport,translationConstraint:{first:{x:80,y:40},second:{x:20,y:40},status:'ncc-constraint'}};
  trialResolve({ok:true,json:async()=>nccData});await nccTask;
  assert.ok(element('matchPairNumbers').textContent.includes('NCC peak 0.920'));
  assert.ok(element('matchPairNumbers').textContent.includes('next peak 0.150'));
  assert.ok(!element('matchPairNumbers').textContent.includes('Response'));
  assert.ok(element('matchPairStatus').textContent.includes('competing-peak rejection off'));
  assert.equal(element('showUnmatchedFeatures').disabled,true);
  assert.equal(trial.debug.graph.byKey.get('0:2').accepted,false);
  trial.selectedProject={...project,registration:{...project.registration,options:{method:'ncc'}}};trial.debug.updateButtons();
  assert.equal(element('trialMatchFeatures').textContent,'Try current NCC settings');
  assert.equal(element('nccPairMethod').textContent,'Try feature matching on this pair');
  assert.equal(element('alternatePairMethod').textContent,'Try phase correlation on this pair');
  const labels=[],squares=[],canvasContext={setTransform(){},clearRect(){},setLineDash(){},beginPath(){},moveTo(){},lineTo(){},stroke(){},strokeRect(...args){squares.push(args);},fillText(...args){labels.push(args);}};
  trial.debug.canvas={width:0,height:0,getContext:()=>canvasContext};trial.viewLoading=false;trial.captureFollowing=false;trial.devicePixelRatio=2;
  element('osd').clientWidth=500;element('osd').clientHeight=300;element('mosaicView').hidden=false;
  trial.tileItems=items.map((item,i)=>({...item,imageToViewportCoordinates:(x,y)=>({x:x+i*60,y})}));
  trial.viewer={...trial.viewer,viewport:{pixelFromPoint:p=>p}};
  trial.debug.draw();assert.equal(labels[0][0],'Translation constraint');assert.deepEqual(squares[0],[76,36,8,8]);
  console.log('PASS NCC preview has distinct scores and controls, respects backend availability, preserves the saved graph and draws its native translation constraint');
  assert.equal(element('hybridPairMethod').disabled,true);
  const beforeHybrid=trialCall;await trial.debug.trySettings('hybrid');assert.equal(trialCall,beforeHybrid);
  trial.state.features.hybridMatching=true;trial.escapeHtml=s=>String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
  const hybridTask=trial.debug.trySettings('hybrid');assert.equal(JSON.parse(trialCall.options.body).options.method,'hybrid');
  const hybridReport={...nccReport,attemptLabel:'NCC fallback',attempts:[
    {label:'Phase first',options:{method:'phase'},report:{...phaseReport,accepted:false,reason:'Rejected <unsafe> low response'}},
    {label:'NCC fallback',options:{method:'ncc'},report:nccReport}
  ]};
  trialResolve({ok:true,json:async()=>({...nccData,reconstructionReport:hybridReport})});await hybridTask;
  assert.equal(element('matchAttemptHistory').hidden,false);assert.match(element('matchAttempts').innerHTML,/Phase first/);assert.match(element('matchAttempts').innerHTML,/NCC fallback/);
  assert.ok(element('matchAttempts').innerHTML.includes('&lt;unsafe&gt;'));assert.ok(!element('matchAttempts').innerHTML.includes('<unsafe>'));
  assert.match(element('matchPairNumbers').textContent,/NCC fallback.*NCC peak/);assert.equal(element('matchObservationLabel').textContent,'Show measured translation');
  assert.equal(trial.debug.graph.byKey.get('0:2').accepted,false);
  trial.selectedProject={...project,registration:{...project.registration,options:{method:'hybrid'}}};trial.debug.updateButtons();
  assert.equal(element('trialMatchFeatures').textContent,'Try hybrid matching on this pair');assert.equal(element('hybridPairMethod').hidden,true);assert.equal(trial.debug.methodName('hybrid'),'Hybrid matching');
  assert.ok(html.includes('value="hybrid"'));assert.ok(html.includes('id="hybridPositions"'));
  console.log('PASS hybrid preview is feature-gated, keeps saved neighbors unchanged, shows escaped attempt history and uses the winning method for scores/observations');
  assert.equal(element('parallaxPairMethod').disabled,true);
  const beforeParallax=trialCall;await trial.debug.trySettings('parallax');assert.equal(trialCall,beforeParallax);
  trial.state.features.parallaxMatching=true;
  const parallaxTask=trial.debug.trySettings('parallax');assert.equal(JSON.parse(trialCall.options.body).options.method,'parallax');
  const parallaxReport={...rejected,method:'parallax',accepted:true,reason:'Accepted local patches',parallax:{patchCount:9,validPatches:8,selectedHypothesis:1,globalRetries:2,globalChanges:1,hypotheses:[
    {id:0,label:'Raised <part>',deltaX:133,deltaY:1,patches:5,confidence:.8,spreadPixels:.3,globalErrorPixels:12},
    {id:1,label:'PCB plane',deltaX:121,deltaY:1,patches:3,confidence:.7,spreadPixels:.2,globalErrorPixels:.1}
  ]}};
  const parallaxData={...result,reconstructionReport:parallaxReport,matches:[match,{...match,status:'other-offset'}]};
  trialResolve({ok:true,json:async()=>parallaxData});await parallaxTask;
  assert.equal(element('parallaxHypotheses').hidden,false);assert.ok(element('parallaxOffsets').innerHTML.includes('Selected · PCB plane'));
  assert.ok(element('parallaxOffsets').innerHTML.includes('&lt;part&gt;'));assert.ok(!element('parallaxOffsets').innerHTML.includes('<part>'));
  assert.ok(element('matchPairNumbers').textContent.includes('2 global comparisons / 1 switches'));
  assert.equal(element('matchObservationLabel').textContent,'Show patch translations');assert.ok(element('matchPairStatus').textContent.includes('PREVIEW ONLY'));
  assert.equal(element('showUnmatchedFeatures').disabled,true);assert.equal(trial.debug.graph.byKey.get('0:2').accepted,false);
  trial.selectedProject={...project,registration:{...project.registration,options:{method:'parallax'}}};trial.debug.updateButtons();
  assert.equal(element('parallaxPairMethod').hidden,true);assert.equal(element('trialMatchFeatures').textContent,'Try parallax-aware matching on this pair');
  assert.ok(html.includes('value="parallax"'));assert.ok(html.includes('value="content"'));
  console.log('PASS parallax previews preserve saved graphs, expose escaped competing offsets and global switches, and distinguish patch observations from features');
})().catch(e=>{console.error(e);process.exitCode=1;});

const app=fs.readFileSync('src/BoardScope/wwwroot/app.js','utf8'),settingsFunction=app.slice(app.indexOf('function registrationSettings('),app.indexOf('function requestedBlend('));
const settingsContext={$:element};vm.runInNewContext(settingsFunction,settingsContext);
for(const [id,value] of Object.entries({regRotation:3,regScale:3,regShift:.25,regSize:0,regContrast:.003,regMaxFeatures:12000,phaseMinResponse:.15,phaseMinCorrelation:.3,nccMinCorrelation:.5,nccMinPeakGap:0}))element(id).value=String(value);
element('regLocalContrast').checked=true;
assert.equal(settingsContext.registrationSettings().contrastThreshold,.003);assert.equal(settingsContext.registrationSettings().enhanceLocalContrast,true);
element('phaseHighPass').checked=true;
assert.equal(settingsContext.registrationSettings().method,'sift');
assert.equal(settingsContext.registrationSettings('phase').method,'phase');
assert.equal(settingsContext.registrationSettings('phase').phaseMinResponse,.15);
assert.equal(settingsContext.registrationSettings('phase').phaseMinCorrelation,.3);
assert.equal(settingsContext.registrationSettings('phase').phaseHighPass,true);
element('nccHighPass').checked=true;
assert.equal(settingsContext.registrationSettings('ncc').method,'ncc');
assert.equal(settingsContext.registrationSettings('ncc').nccMinCorrelation,.5);
assert.equal(settingsContext.registrationSettings('ncc').nccMinPeakGap,0);
assert.equal(settingsContext.registrationSettings('ncc').nccHighPass,true);
assert.equal(settingsContext.registrationSettings('hybrid').method,'hybrid');
assert.equal(settingsContext.registrationSettings('parallax').method,'parallax');
element('nccMinPeakGap').value='-1';assert.throws(()=>settingsContext.registrationSettings('ncc'),/NCC/);element('nccMinPeakGap').value='0';
settingsContext.syncFeaturePreset();assert.equal(element('regSensitivity').value,'0.003');
element('regContrast').value='0.007';settingsContext.syncFeaturePreset();assert.equal(element('regSensitivity').value,'custom');
element('regContrast').value='0';assert.throws(()=>settingsContext.registrationSettings(),/contrast threshold/);
element('regContrast').value='0.01';element('regMaxFeatures').value='100.5';assert.throws(()=>settingsContext.registrationSettings(),/Feature limit/);
console.log('PASS detection settings serialize numeric sensitivity, contrast enhancement and feature limits, validate inputs, and distinguish custom presets');
