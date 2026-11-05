// Classic worker: MediaPipe's WASM loader can use importScripts here.
let tracker,backend="CPU";
self.onmessage = async ({data}) => {
 if(data.type === 'init') {
  try {
   const {HandLandmarker,FilesetResolver}=await import('https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@0.10.14/vision_bundle.mjs');
   const files=await FilesetResolver.forVisionTasks('https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@0.10.14/wasm');
   const options={baseOptions:{modelAssetPath:'https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task'},runningMode:'VIDEO',numHands:1,minHandDetectionConfidence:.5,minHandPresenceConfidence:.5,minTrackingConfidence:.5};
   try {
    tracker=await HandLandmarker.createFromOptions(files,{...options,baseOptions:{...options.baseOptions,delegate:'GPU'},canvas:new OffscreenCanvas(480,360)});
    backend='GPU';
   } catch {
    tracker=await HandLandmarker.createFromOptions(files,{...options,baseOptions:{...options.baseOptions,delegate:'CPU'}});
    backend='CPU';
   }
   postMessage({type:'ready',backend});
  } catch(e) { postMessage({type:'error',message:e.message}); }
  return;
 }
 if(data.type === 'frame') {
  const began=performance.now();
  try {
   const result=tracker.detectForVideo(data.bitmap,data.timestamp);
   postMessage({type:'result',timestamp:data.timestamp,inferenceMs:performance.now()-began,points:(result.landmarks[0]??[]).map(({x,y})=>({x,y}))});
  } catch(e) { postMessage({type:'error',message:e.message}); }
  finally { data.bitmap.close(); }
 }
};
