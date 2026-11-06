const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const web = path.join(__dirname, '../src/HandPilot/Web');
const policy = fs.readFileSync(path.join(web, 'tracking-policy.js'), 'utf8');
const page = fs.readFileSync(path.join(web, 'index.html'), 'utf8').split('<script type="module">')[1].split('</script>')[0]
  .replace("import { FrameGate } from './tracking-policy.js';", '');
const workerSource = fs.readFileSync(path.join(web, 'tracker-worker.js'), 'utf8');
const hand = () => Array.from({length:21}, (_,i) => ({x:.3+i*.01,y:.4+i*.005}));
const flush = async () => { for(let i=0;i<16;i++) await Promise.resolve(); };
const deferred = () => {let resolve;const promise=new Promise(r=>resolve=r);return {promise,resolve};};

async function policyChecks(FrameGate) {
  const gate=new FrameGate(120);
  assert.equal(gate.begin(1),null,'Stopped sessions must not capture');
  gate.start('first');
  const first=gate.begin(10);
  assert.equal(gate.begin(11),null,'Busy tracker must drop new frames');
  const stale=gate.finish({...first,points:hand(),inferenceMs:100,backend:'GPU'},131);
  assert.deepEqual(stale.points,[],'Stale points must not reach the host');
  assert.deepEqual(stale.overlayPoints,[],'Stale dots must not be drawn');
  assert.equal(stale.processingAgeMs,121,'Bad frame telemetry must remain available');
  const second=gate.begin(140);
  assert.equal(second.sequence,2,'Processed frame sequences must increase');
  const fresh=gate.finish({...second,points:hand(),inferenceMs:10,backend:'GPU'},160);
  assert.equal(fresh.points.length,21);
  const invalid=gate.begin(170),bad=hand();bad[0].x=NaN;
  assert.equal(gate.finish({...invalid,points:bad},175).points.length,0,'Malformed landmarks must clear control');
  const old=gate.begin(180);
  gate.stop();gate.start('second');const current=gate.begin(200);
  assert.equal(gate.isCurrent(old),false,'Old snapshot promises must become obsolete');
  assert.equal(gate.finish({...old,points:hand()},205),null,'Old session result must be ignored');
  assert.equal(gate.begin(210),null,'Old result must not release current busy slot');
  assert.equal(gate.finish({...current,points:hand()},220).points.length,21);
  const future=gate.begin(300);
  assert.equal(gate.finish({...future,points:hand()},299).points.length,0,'Future timestamps are invalid');
}

function pageHarness(FrameGate,{rvfc=true,mediaPromise=null,playError=null}={}) {
  const sent=[],workers=[],callbacks=new Map(),timeouts=new Map(),intervals=new Map(),hostListeners={},deviceListeners={},trackListeners={};
  let now=0,nextId=0,session=0,captureOverride=null,enumerationOverride=null,permissionCalls=[];
  let devices=[{kind:'videoinput',deviceId:'built-in',label:'Built-in webcam'}];
  const drawing={clears:0,dots:0},ctx={clearRect(){drawing.clears++},beginPath(){},arc(){drawing.dots++},fill(){}};
  function element(){return {value:'',textContent:'',style:{},hidden:false,disabled:false,title:'',addEventListener(){},clientWidth:600,clientHeight:300};}
  const elements={};
  for(const id of ['start','stop-preview','video','canvas','message','camera','refresh','performance','viewport','stage','zone','calibration'])elements[id]=element();
  const video=elements.video;
  Object.assign(video,{videoWidth:480,videoHeight:360,readyState:2,currentTime:0,srcObject:null,plays:0,
    play:async()=>{video.plays++;if(playError)throw playError}});
  if(rvfc){video.requestVideoFrameCallback=f=>{const id=++nextId;callbacks.set(id,f);return id};video.cancelVideoFrameCallback=id=>callbacks.delete(id);}
  elements.canvas.getContext=()=>ctx;
  elements.camera.options=[];
  elements.camera.replaceChildren=(...options)=>{elements.camera.options=options;elements.camera.value='';};
  elements.camera.add=o=>elements.camera.options.push(o);
  elements.camera.selectedOptions=[{text:'Built-in webcam'}];
  const track={stops:0,label:'Built-in webcam',readyState:'live',stop(){this.stops++;this.readyState='ended'},
    getSettings:()=>({width:480,height:360,frameRate:30}),addEventListener:(name,f)=>trackListeners[name]=f};
  const stream={getTracks:()=>[track],getVideoTracks:()=>[track]};
  class Worker {
    constructor(){this.messages=[];this.terminated=false;workers.push(this);}
    postMessage(m){this.messages.push(m)}
    terminate(){this.terminated=true}
    emit(data){this.onmessage({data})}
  }
  const context={FrameGate,console,Number,Math,Promise,Array,Error,Worker,
    Option:function(text,value){this.text=text;this.value=value},
    document:{querySelector:s=>elements[s.slice(1)]},
    window:{chrome:{webview:{postMessage:m=>sent.push(m),addEventListener:(name,f)=>hostListeners[name]=f}},addEventListener:(name,f)=>hostListeners[name]=f},
    navigator:{mediaDevices:{enumerateDevices:async()=>enumerationOverride?await enumerationOverride:devices,
      getUserMedia:async constraints=>{permissionCalls.push(constraints);return mediaPromise?await mediaPromise:stream},
      addEventListener:(name,f)=>deviceListeners[name]=f}},
    performance:{now:()=>now,timeOrigin:1700000000000},crypto:{randomUUID:()=>`session-${++session}`},
    ResizeObserver:class{constructor(f){this.f=f}observe(){this.f()}},
    requestAnimationFrame:f=>{const id=++nextId;callbacks.set(id,f);return id},cancelAnimationFrame:id=>callbacks.delete(id),
    setTimeout:(f,delay)=>{const id=++nextId;timeouts.set(id,{f,delay});return id},clearTimeout:id=>timeouts.delete(id),
    setInterval:(f,delay)=>{const id=++nextId;intervals.set(id,{f,delay});return id},clearInterval:id=>intervals.delete(id),
    createImageBitmap:async()=>captureOverride?await captureOverride:{closed:false,close(){this.closed=true}}
  };
  vm.createContext(context);vm.runInContext(page,context);
  return {elements,sent,workers,callbacks,timeouts,intervals,hostListeners,deviceListeners,trackListeners,drawing,track,stream,permissionCalls,
    now:t=>now=t,capture:promise=>captureOverride=promise,devices:value=>devices=value,enumeration:promise=>enumerationOverride=promise,
    frame(t,presented=1){now=t;video.currentTime=t/1000;const [id,f]=callbacks.entries().next().value;callbacks.delete(id);f(t,{presentedFrames:presented});},
    fireTimeout(delay){const entry=[...timeouts.entries()].find(([,x])=>x.delay===delay);assert.ok(entry,`Expected ${delay}ms timeout`);timeouts.delete(entry[0]);entry[1].f()},
    async loading(){await flush();elements.camera.value='built-in';const completion=elements.start.onclick();await flush();return {completion,worker:workers.at(-1)}},
    async begin(backend='GPU'){const {completion,worker}=await this.loading();assert.ok(worker);worker.emit({type:'ready',backend});await completion;await flush();return worker},
    result(t,{points=hand(),backend='GPU',inferenceMs=20,worker=workers.at(-1)}={}){now=t;const frame=worker.messages.filter(x=>x.type==='frame').at(-1);assert.ok(frame);worker.emit({...frame,type:'result',points,inferenceMs,backend});return frame},
    async ended(){track.readyState='ended';trackListeners.ended();await flush()}
  };
}

async function pageChecks(FrameGate) {
  const h=pageHarness(FrameGate);await h.begin();
  const ready=h.sent.find(x=>x.ready===true);
  assert.equal(ready.cameraId,'built-in','The explicitly selected camera must be identified to host');
  assert.equal(h.permissionCalls[0].video.deviceId.exact,'built-in','Capture must request the selected device exactly');
  h.frame(100);await flush();
  const worker=h.workers[0],frame=worker.messages.find(x=>x.type==='frame');
  h.now(140);worker.emit({...frame,type:'result',points:hand(),inferenceMs:20,backend:'GPU'});
  assert.equal(h.drawing.dots,21);
  const result=h.sent.find(x=>x.sequence===1);
  assert.equal(result.capturedAtUnixMs,1700000000100);
  assert.equal(result.sessionId,ready.sessionId);
  assert.equal([...h.timeouts.values()].some(x=>x.delay===10000),false,'Successful inference must cancel its warmup watchdog');
  h.frame(200,2);await flush();
  const second=worker.messages.filter(x=>x.type==='frame').at(-1),before=h.drawing.clears;
  h.now(321);worker.emit({...second,type:'result',points:hand(),inferenceMs:110,backend:'GPU'});
  assert.equal(h.sent.filter(x=>x.sequence===2).at(-1).points.length,0);
  assert.equal(h.drawing.dots,21,'Stale result must not draw any new dots');
  assert.ok(h.drawing.clears>before,'Stale result must clear existing dots');
  h.now(1000);for(const {f} of h.intervals.values())f();
  const metrics=h.sent.find(x=>x.metrics)?.metrics;
  assert.equal(metrics.backend,'GPU');assert.equal(metrics.width,480);assert.equal(metrics.height,360);
  assert.ok(metrics.processingAgeMs>0,'Rejected stale data still contributes to diagnostics');
  h.hostListeners.message({data:{bounds:{left:.2,top:.3,right:.8,bottom:.7},calibration:{active:true,prompt:'Move to top left'}}});
  assert.equal(h.elements.zone.style.left,'20%');assert.ok(Math.abs(parseFloat(h.elements.zone.style.width)-60)<.001);
  assert.equal(h.elements.calibration.textContent,'Move to top left');assert.equal(h.elements.calibration.hidden,false);
  const delayed=deferred();h.capture(delayed.promise);h.frame(1100,3);await flush();
  h.elements.camera.onchange();const count=h.sent.length;
  worker.emit({...second,type:'result',points:hand()});worker.emit({type:'error',message:'obsolete'});
  assert.equal(h.sent.length,count,'Terminated worker events must not affect the new lifecycle');
  const bitmap={closed:false,close(){this.closed=true}};delayed.resolve(bitmap);await flush();
  assert.equal(bitmap.closed,true,'Obsolete snapshot must be disposed instead of sent');
  assert.equal(h.sent.filter(x=>x.ready===false).at(-1).sessionId,ready.sessionId);
  assert.equal(h.callbacks.size,0);assert.equal(h.intervals.size,0);

  const media=deferred(),cancelled=pageHarness(FrameGate,{mediaPromise:media.promise});
  await flush();cancelled.elements.camera.value='built-in';const startup=cancelled.elements.start.onclick();
  await flush();assert.equal(cancelled.workers.length,0,'Camera acquisition precedes tracker loading');cancelled.elements.camera.onchange();
  media.resolve(cancelled.stream);await startup;
  assert.equal(cancelled.track.stops,1,'Camera permission resolving after stop must release its stream');
  assert.equal(cancelled.sent.some(x=>x.ready===true),false,'Cancelled startup must never enable host control');

  const fallback=pageHarness(FrameGate,{rvfc:false});await fallback.begin();
  fallback.frame(50);await flush();const posted=fallback.workers[0].messages.length;
  fallback.frame(50);await flush();
  assert.equal(fallback.workers[0].messages.length,posted,'rAF fallback must not infer duplicate video frames');
  fallback.elements.camera.onchange();

  const stalled=pageHarness(FrameGate);await stalled.begin();
  stalled.frame(100);await flush();stalled.now(10100);stalled.fireTimeout(10000);await flush();
  previewAlive(stalled,'Hung inference must preserve the camera');
  assert.equal(stalled.workers[0].terminated,true,'Hung inference must terminate only its worker');
  assert.equal(stalled.workers[1].messages[0].forceCpu,true,'Hung GPU gets one CPU recovery attempt');
  assert.equal(stalled.sent.filter(x=>x.ready===false).at(-1).sessionId,'session-1');
  stalled.workers[1].emit({type:'ready',backend:'CPU'});await flush();
  stalled.frame(10200);await flush();stalled.result(10220,{backend:'CPU'});
  stalled.frame(10300);await flush();stalled.now(13300);stalled.fireTimeout(3000);await flush();
  previewAlive(stalled,'Repeated inference hang leaves preview active');assert.equal(stalled.workers.length,2);
  assert.equal(stalled.elements.start.disabled,false);assert.equal(stalled.elements.start.textContent,'Retry tracking');
}

function previewAlive(h,label){
  assert.equal(h.track.stops,0,label);assert.equal(h.track.readyState,'live',label);
  assert.equal(h.elements.video.srcObject,h.stream,label);
}

async function previewRegressionChecks(FrameGate){
  const first=pageHarness(FrameGate),loading=await first.loading();
  previewAlive(first,'Camera plays before tracker assets load');assert.equal(first.elements.video.plays,1);
  assert.equal(first.sent.some(x=>x.ready===true),false,'Preview alone cannot enable movement');
  loading.worker.emit({type:'error',message:'Assets unavailable'});await loading.completion;
  previewAlive(first,'Tracker initialization failure preserves preview');assert.equal(first.elements.start.textContent,'Retry tracking');
  assert.equal(first.elements.start.disabled,false);assert.equal(loading.worker.terminated,true);
  const calls=first.permissionCalls.length;await first.begin();assert.equal(first.permissionCalls.length,calls,'Retry tracking reuses the camera');

  const warm=pageHarness(FrameGate);await warm.begin();warm.frame(100);await flush();
  assert.ok([...warm.timeouts.values()].some(x=>x.delay===10000),'First GPU inference receives a warmup budget');
  assert.equal([...warm.timeouts.values()].some(x=>x.delay===1000),false,'No one-second camera shutdown watchdog remains');
  warm.result(1700,{inferenceMs:1590});previewAlive(warm,'Slow GPU warmup cannot stop preview');
  assert.equal(warm.sent.filter(x=>x.sequence===1).at(-1).points.length,0,'Late warmup data still cannot move the cursor');
  for(let i=0;i<3;i++){warm.frame(1800+i*100,i+2);await flush();warm.result(1820+i*100,{points:[]})}
  previewAlive(warm,'Repeated missing-hand detections keep preview active');assert.equal(warm.workers.length,1);

  const recover=pageHarness(FrameGate),gpu=await recover.begin();recover.frame(100);await flush();
  gpu.emit({type:'error',message:'GPU device lost'});await flush();previewAlive(recover,'Runtime GPU failure preserves preview');
  assert.equal(gpu.terminated,true);assert.equal(recover.workers.length,2);
  const cpu=recover.workers[1];assert.equal(cpu.messages[0].forceCpu,true);
  const sentBefore=recover.sent.length;gpu.emit({type:'ready',backend:'GPU'});gpu.emit({type:'error',message:'old'});assert.equal(recover.sent.length,sentBefore);
  cpu.emit({type:'ready',backend:'CPU'});await flush();assert.equal(recover.sent.filter(x=>x.ready===true).at(-1).sessionId,'session-2');
  cpu.emit({type:'error',message:'CPU failed too'});await flush();previewAlive(recover,'CPU failure preserves preview for manual retry');
  assert.equal(recover.workers.length,2,'Automatic retry is bounded');assert.equal(recover.callbacks.size,0);assert.equal(recover.intervals.size,0);
  assert.equal(recover.elements.start.textContent,'Retry tracking');assert.equal(recover.elements.start.hidden,false);assert.equal(recover.elements.start.disabled,false);
  const mediaCalls=recover.permissionCalls.length;await recover.begin();assert.equal(recover.permissionCalls.length,mediaCalls);

  const snapshot=pageHarness(FrameGate);await snapshot.begin();const pending=deferred();snapshot.capture(pending.promise);
  snapshot.frame(100);await flush();snapshot.now(10100);snapshot.fireTimeout(10000);await flush();previewAlive(snapshot,'Stuck snapshot cannot stop preview');
  const bitmap={closed:false,close(){this.closed=true}};pending.resolve(bitmap);await flush();assert.equal(bitmap.closed,true,'Late snapshot must be disposed');
  snapshot.elements['stop-preview'].onclick();assert.equal(snapshot.track.stops,1);assert.equal(snapshot.workers.at(-1).terminated,true);

  const stopped=pageHarness(FrameGate);await stopped.begin();stopped.elements['stop-preview'].onclick();
  assert.equal(stopped.track.stops,1);assert.equal(stopped.elements.video.srcObject,null);assert.equal(stopped.callbacks.size,0);assert.equal(stopped.intervals.size,0);
  const ended=pageHarness(FrameGate);await ended.begin();await ended.ended();
  assert.equal(ended.track.stops,1);assert.equal(ended.elements.video.srcObject,null);assert.match(ended.elements.message.textContent,/Camera disconnected/);
  const hidden=pageHarness(FrameGate);await hidden.begin();hidden.hostListeners.pagehide();assert.equal(hidden.track.stops,1);

  const enumerated=pageHarness(FrameGate);await enumerated.begin();enumerated.devices([]);await enumerated.deviceListeners.devicechange();
  previewAlive(enumerated,'Transient enumeration omission preserves a live camera');assert.equal(enumerated.elements.camera.value,'built-in');
  assert.ok(enumerated.elements.camera.options.some(x=>x.value==='built-in'));
  const reply=deferred();enumerated.enumeration(reply.promise);const deviceEvent=enumerated.deviceListeners.devicechange();
  enumerated.elements.camera.value='';enumerated.elements.camera.onchange();reply.resolve([]);await deviceEvent;
  assert.equal(enumerated.track.stops,1,'Late enumeration cannot revive or restop a cancelled camera');

  const duringLoad=pageHarness(FrameGate),pendingLoad=await duringLoad.loading();duringLoad.elements['stop-preview'].onclick();await pendingLoad.completion;
  assert.equal(duringLoad.track.stops,1);assert.equal(pendingLoad.worker.terminated,true);assert.equal(duringLoad.sent.some(x=>x.ready===true),false);
  const duringRecovery=pageHarness(FrameGate),old=await duringRecovery.begin();old.emit({type:'error',message:'GPU failed'});await flush();
  const pendingCpu=duringRecovery.workers.at(-1);duringRecovery.elements['stop-preview'].onclick();const beforeLate=duringRecovery.sent.length;
  pendingCpu.emit({type:'ready',backend:'CPU'});await flush();assert.equal(duringRecovery.sent.length,beforeLate);
  assert.equal(duringRecovery.track.stops,1);assert.equal(pendingCpu.terminated,true);

  const playFailure=pageHarness(FrameGate,{playError:new Error('Preview playback failed')});
  const failedPlayback=await playFailure.loading();await failedPlayback.completion;
  assert.equal(playFailure.track.stops,1,'A real preview playback failure must release camera');
  assert.equal(playFailure.elements.video.srcObject,null);assert.equal(playFailure.elements.start.disabled,false);
  assert.equal(playFailure.elements.start.textContent,'Start preview');assert.equal(playFailure.workers.length,0);
}

async function workerChecks(failGpu,forceCpu=false) {
  const delegates=[],messages=[];let closed=0,failDetection=false;
  const context={self:{},OffscreenCanvas:class{},performance:{now:()=>25},postMessage:m=>messages.push(m),
    fakeModule:{FilesetResolver:{forVisionTasks:async()=>({})},HandLandmarker:{createFromOptions:async(_,o)=>{
      delegates.push(o.baseOptions.delegate);
      if(failGpu&&o.baseOptions.delegate==='GPU')throw Error('GPU unavailable');
      return {detectForVideo:()=>{if(failDetection)throw Error('Detection failed');return {landmarks:[hand()]}}};
    }}}};
  vm.createContext(context);
  vm.runInContext(workerSource.replace("await import('https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@0.10.14/vision_bundle.mjs')",'fakeModule'),context);
  await context.self.onmessage({data:{type:'init',forceCpu}});
  assert.deepEqual(delegates,forceCpu?['CPU']:failGpu?['GPU','CPU']:['GPU']);
  assert.equal(messages[0].backend,forceCpu||failGpu?'CPU':'GPU');
  await context.self.onmessage({data:{type:'frame',sessionId:'test',sequence:7,timestamp:100,bitmap:{close(){closed++}}}});
  const result=messages.at(-1);
  assert.equal(result.sessionId,'test');assert.equal(result.sequence,7);assert.equal(result.timestamp,100);
  assert.equal(result.points.length,21);assert.equal(closed,1);
  failDetection=true;
  await context.self.onmessage({data:{type:'frame',bitmap:{close(){closed++}}}});
  assert.equal(messages.at(-1).type,'error');assert.equal(closed,2,'Worker errors must also release the image bitmap');
}

(async()=>{
  const {FrameGate}=await import('data:text/javascript;base64,'+Buffer.from(policy).toString('base64'));
  await policyChecks(FrameGate);await pageChecks(FrameGate);await previewRegressionChecks(FrameGate);
  await workerChecks(false);await workerChecks(true);await workerChecks(false,true);
  console.log('Passed: freshness/overlay/session gates; camera-first startup; slow GPU warmup; bounded CPU recovery; preview retention and manual retry; worker/snapshot watchdogs; device/end/stop cleanup; lifecycle cancellation; telemetry; forced CPU and fallback.');
})().catch(error=>{console.error(error);process.exitCode=1});
