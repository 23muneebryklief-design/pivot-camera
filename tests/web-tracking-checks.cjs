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
const flush = async () => { for(let i=0;i<8;i++) await Promise.resolve(); };
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

function pageHarness(FrameGate,{rvfc=true,mediaPromise=null}={}) {
  const sent=[],workers=[],callbacks=new Map(),timeouts=new Map(),intervals=new Map(),hostListeners={};
  let now=0,nextId=0,session=0,captureOverride=null,permissionCalls=[];
  const drawing={clears:0,dots:0},ctx={clearRect(){drawing.clears++},beginPath(){},arc(){drawing.dots++},fill(){}};
  function element(){return {value:'',textContent:'',style:{},hidden:false,disabled:false,title:'',addEventListener(){},clientWidth:600,clientHeight:300};}
  const elements={};
  for(const id of ['start','video','canvas','message','camera','refresh','performance','viewport','stage','zone','calibration'])elements[id]=element();
  const video=elements.video;
  Object.assign(video,{videoWidth:480,videoHeight:360,readyState:2,currentTime:0,play:async()=>{}});
  if(rvfc){video.requestVideoFrameCallback=f=>{const id=++nextId;callbacks.set(id,f);return id};video.cancelVideoFrameCallback=id=>callbacks.delete(id);}
  elements.canvas.getContext=()=>ctx;
  elements.camera.options=[];
  elements.camera.replaceChildren=(...options)=>{elements.camera.options=options;elements.camera.value='';};
  elements.camera.add=o=>elements.camera.options.push(o);
  elements.camera.selectedOptions=[{text:'Built-in webcam'}];
  const track={stops:0,label:'Built-in webcam',stop(){this.stops++},getSettings:()=>({width:480,height:360,frameRate:30}),addEventListener(){}};
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
    navigator:{mediaDevices:{enumerateDevices:async()=>[{kind:'videoinput',deviceId:'built-in',label:'Built-in webcam'}],
      getUserMedia:async constraints=>{permissionCalls.push(constraints);return mediaPromise?await mediaPromise:stream},addEventListener(){}}},
    performance:{now:()=>now,timeOrigin:1700000000000},crypto:{randomUUID:()=>`session-${++session}`},
    ResizeObserver:class{constructor(f){this.f=f}observe(){this.f()}},
    requestAnimationFrame:f=>{const id=++nextId;callbacks.set(id,f);return id},cancelAnimationFrame:id=>callbacks.delete(id),
    setTimeout:(f,delay)=>{const id=++nextId;timeouts.set(id,{f,delay});return id},clearTimeout:id=>timeouts.delete(id),
    setInterval:(f,delay)=>{const id=++nextId;intervals.set(id,{f,delay});return id},clearInterval:id=>intervals.delete(id),
    createImageBitmap:async()=>captureOverride?await captureOverride:{closed:false,close(){this.closed=true}}
  };
  vm.createContext(context);vm.runInContext(page,context);
  return {elements,sent,workers,callbacks,timeouts,intervals,hostListeners,drawing,track,stream,permissionCalls,
    now:t=>now=t,capture:promise=>captureOverride=promise,
    frame(t,presented=1){now=t;video.currentTime=t/1000;const [id,f]=callbacks.entries().next().value;callbacks.delete(id);f(t,{presentedFrames:presented});},
    async begin(){await flush();elements.camera.value='built-in';const completion=elements.start.onclick();workers.at(-1).emit({type:'ready',backend:'GPU'});await flush();return completion;}
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
  assert.equal([...h.timeouts.values()].some(x=>x.delay===1000),false,'Successful inference must cancel its watchdog');
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
  cancelled.workers[0].emit({type:'ready',backend:'CPU'});await flush();cancelled.elements.camera.onchange();
  media.resolve(cancelled.stream);await startup;
  assert.equal(cancelled.track.stops,1,'Camera permission resolving after stop must release its stream');
  assert.equal(cancelled.sent.some(x=>x.ready===true),false,'Cancelled startup must never enable host control');

  const fallback=pageHarness(FrameGate,{rvfc:false});await fallback.begin();
  fallback.frame(50);await flush();const posted=fallback.workers[0].messages.length;
  fallback.frame(50);await flush();
  assert.equal(fallback.workers[0].messages.length,posted,'rAF fallback must not infer duplicate video frames');
  fallback.elements.camera.onchange();

  const stalled=pageHarness(FrameGate);await stalled.begin();
  stalled.frame(100);await flush();
  const watchdog=[...stalled.timeouts.values()].find(x=>x.delay===1000);
  assert.ok(watchdog,'Each in-flight frame needs a bounded timeout');
  stalled.now(1100);watchdog.f();
  assert.equal(stalled.track.stops,1,'Hung inference must release the camera');
  assert.equal(stalled.workers[0].terminated,true,'Hung inference must terminate the worker');
  assert.equal(stalled.callbacks.size,0,'Hung inference must stop scheduling frames');
  assert.equal(stalled.sent.at(-1).status,'Tracking stopped responding. Start preview to retry.');
  assert.equal(stalled.elements.start.disabled,false,'Hung inference must offer a restart');
  assert.equal(stalled.sent.filter(x=>x.ready===false).at(-1).sessionId,'session-1');
}

async function workerChecks(failGpu) {
  const delegates=[],messages=[];let closed=0,failDetection=false;
  const context={self:{},OffscreenCanvas:class{},performance:{now:()=>25},postMessage:m=>messages.push(m),
    fakeModule:{FilesetResolver:{forVisionTasks:async()=>({})},HandLandmarker:{createFromOptions:async(_,o)=>{
      delegates.push(o.baseOptions.delegate);
      if(failGpu&&o.baseOptions.delegate==='GPU')throw Error('GPU unavailable');
      return {detectForVideo:()=>{if(failDetection)throw Error('Detection failed');return {landmarks:[hand()]}}};
    }}}};
  vm.createContext(context);
  vm.runInContext(workerSource.replace("await import('https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@0.10.14/vision_bundle.mjs')",'fakeModule'),context);
  await context.self.onmessage({data:{type:'init'}});
  assert.deepEqual(delegates,failGpu?['GPU','CPU']:['GPU']);
  assert.equal(messages[0].backend,failGpu?'CPU':'GPU');
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
  await policyChecks(FrameGate);await pageChecks(FrameGate);await workerChecks(false);await workerChecks(true);
  console.log('Passed: stale/invalid/future frames and overlay clearing; sessions and cancellation; strict single-flight and hung-worker watchdog; selected camera and timestamps; metrics and calibration messages; rAF fallback; GPU/CPU fallback and bitmap cleanup.');
})().catch(error=>{console.error(error);process.exitCode=1});
