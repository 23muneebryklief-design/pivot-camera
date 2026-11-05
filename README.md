# HandPilot

A Windows desktop prototype for controlling your PC with webcam hand gestures.

## Run

Requires Windows 10/11 x64, the .NET 8 SDK, Microsoft Edge WebView2 Runtime, a webcam, and internet access to download the tracker assets.

```powershell
dotnet run --project src/HandPilot
```

1. Press **Find cameras** and allow camera access. Select your built-in webcam from the **Camera** dropdown, then press **Start camera preview**. The selected device is requested explicitly, so a connected phone is not silently substituted. Switching cameras pauses mouse control; restart preview and enable control again.
2. Show one hand in good lighting. The preview draws dots on detected joints.
3. Press **Enable mouse control**.
4. Press **Ctrl + Alt + Space** at any time to pause. Closing the window stops the camera.

## Gestures

| Gesture | Action |
| --- | --- |
| Move your palm | Move cursor across the Windows virtual desktop |
| Close hand into a fist | Left button down |
| Open hand | Left button up (close then open to click) |
| Move while holding a fist | Drag |
| Index and middle fingers up, ring finger folded | Scroll by moving fingers vertically |

The pointer starts at the centre when you enable control. Your first tracked palm position becomes the neutral position; moving from there moves the pointer. Re-enabling control or reacquiring a lost hand resets that neutral position. The pointer is mirrored with time-based adaptive smoothing: small motions are filtered more strongly, while larger movements respond faster. Show an open hand after enabling or reacquiring tracking to arm fist clicking. Lost tracking releases the mouse button. A 500 ms watchdog also releases it if frames stop arriving. Control starts disabled. The global shortcut only pauses; use the app button to enable it again.

## Architecture

- C# / .NET 8 WPF desktop host and gesture state machine.
- Windows SendInput for desktop mouse input.
- WebView2 camera preview, with MediaPipe Hand Landmarker running locally in a dedicated JavaScript/WASM worker. Only one frame is processed at a time; newer frames are skipped while busy to avoid a backlog. Capture requests 480�360 at up to 30 FPS; inference frames are resized to 480 pixels wide. The preview reports tracking FPS and inference time.
- MediaPipe 0.10.14 runtime loaded from jsDelivr; Google hosts the model. These services receive asset download requests, not camera frames from this application.
- WebView profile is stored in `%LOCALAPPDATA%/HandPilot/WebView2`.

This initial version uses a JavaScript tracking component; it is not an entirely C# inference implementation. Moving inference to C# / ONNX is a future option. The application does not record or upload camera frames. Initial setup and tracker loading require internet access.

## Checks

```powershell
dotnet run --project tests/HandPilot.Checks
dotnet build src/HandPilot
```

## Prototype limits

Hand tracking accuracy depends on lighting and camera position. Gestures need tuning through real webcam trials. Input cannot control Windows secure desktop/UAC prompts or applications at a higher integrity level. No right-click, calibration UI, or installer yet. Keep a physical mouse available while trying the prototype.

## References

- [MediaPipe Hand Landmarker](https://ai.google.dev/edge/mediapipe/solutions/vision/hand_landmarker/web_js)
- [Microsoft WebView2](https://learn.microsoft.com/microsoft-edge/webview2/)

No license has been selected yet.
