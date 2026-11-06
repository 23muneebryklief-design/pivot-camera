# HandPilot

A Windows webcam hand-control prototype. This build focuses on the movement milestone: comfortable reach to all four corners, stable pointing, and recovery after hand loss. **Clicking and scrolling are paused in this build** so gesture recognition cannot interrupt movement testing.

## Pull and run

Requires Windows 10/11 x64, the .NET 8 SDK (8.0.425 or a compatible patch), Microsoft Edge WebView2 Runtime, a webcam, and internet access to load tracking assets.

Close any running HandPilot window first. From your repository folder:

```powershell
git pull --ff-only origin main
dotnet run --project src/HandPilot -c Release
```

1. Select your webcam. Use **Find cameras** if names are missing, then **Start preview**. The selected device is requested explicitly.
2. Choose **Calibrate reach**. Movement stays paused during calibration.
3. Capture five comfortable positions: centre, upper left, upper right, lower right, lower left in the mirrored preview. Keep your wrist and every finger visible, hold still for at least half a second, and press **Capture position** with your other hand. Enter also captures when the desktop controls have focus.
4. Calibration saves automatically for that camera and display arrangement. It is required before starting movement.
5. Show your hand steadily and choose **Start movement**. Stabilization is automatic. Finger folding does not activate clicks, scrolling, or movement-blocking gestures in this build.
6. **Ctrl + Alt + Space** always pauses. Control resumes automatically after a brief hand-tracking loss, but an explicit pause requires **Start movement** again. Closing the app stops the camera and input.

## Repeatable live movement check

After calibration, choose **Check movement**. It opens a fullscreen guided check and starts movement if needed. Keep the physical mouse still during the check.

- Reach all four corners twice, holding each for 0.4 seconds within 24 pixels of the corner.
- Hold the cursor near the centre for two seconds within a 12-pixel span.
- Remove your hand completely. The cursor must hold within 3 pixels for half a second.
- Bring your hand back and return to the centre. Also watch the recovery transition: it should not teleport or remain stuck.

Corner, steady, and recovery holds count only with a fresh, fully visible hand and agreement between the engine's output and the real Windows cursor. Physical mouse movement alone cannot pass them. A step times out if it cannot be completed. Esc exits the check; Ctrl + Alt + Space pauses control. Movement pauses after completion or cancellation.

Completed pass/fail results save locally under `%LOCALAPPDATA%/HandPilot/MovementChecks/`. Reports include step times, pointing span, sampled processing-to-cursor ages, and the criteria used. **The automated live check does not certify recovery smoothness**; its report explicitly leaves that for visual review. Do not treat passing synthetic tests as a completed webcam acceptance test.

## Movement pipeline

- .NET 8 WPF host with native Windows `SendInput` output.
- WebView2 preview with MediaPipe Hand Landmarker in a dedicated JavaScript/WASM worker. One inference is in flight; camera frames are skipped while busy instead of queued.
- Camera capture requests 480x360 at up to 60 FPS when supported. Actual dimensions, camera FPS, tracking FPS, inference time, and processing age remain visible beneath the fitted preview.
- Camera-frame callbacks are used when available, with an animation-frame fallback. GPU initialization falls back to CPU if unavailable.
- Every result carries a session ID, sequence, and capture timestamp. Browser and native layers reject results older than 120 ms. Native input runs on a background timer and rechecks freshness at output; a 150 ms stream timeout holds control and releases pending input. Camera preview stays independent of tracking: the first inference has a 10-second warmup budget, later inferences have a 3-second watchdog, and a stalled/failed tracker gets one CPU recovery attempt. Further failure leaves the preview running with **Retry tracking**, without reopening the camera. Movement must be started again after a tracking-session restart.
- Always-on pixel-aware adaptive filtering suppresses small tremors and follows deliberate travel more quickly. Exact corner snapping avoids endlessly approaching an edge.
- Hand reacquisition requires at least three samples spanning 100 ms. A bounded 120 ms blend then returns from the previous cursor position to the absolute mapped target. Lost tracking never extrapolates movement.
- Calibration maps your comfortable palm workspace to the full Windows virtual desktop. Profiles are stored atomically per camera and display arrangement; corrupt or incompatible profiles require recalibration.

The freshness cutoff prevents old input; it cannot make a slow detector faster. If results repeatedly exceed the budget, the app holds the cursor and reports delayed tracking. Timing starts before the browser frame snapshot and excludes camera sensor/driver buffering and display latency. Reacquisition deliberately adds confirmation and blending time; it is distinct from steady-state tracking latency.

## Checks

```powershell
dotnet run --project tests/HandPilot.Checks
dotnet run --project tests/HandPilot.CalibrationChecks
dotnet run --project tests/HandPilot.SessionChecks
node tests/web-tracking-checks.cjs
dotnet build src/HandPilot -c Release
```

The .NET checks cover mapping, jitter, 30/60 FPS response, bounded recovery, invalid/stale frames, calibration, profile persistence, input cancellation, and live-check criteria. Node checks cover freshness, overlay expiry, session cancellation, single-flight processing, worker watchdog, explicit camera selection, telemetry, and GPU fallback. These are deterministic tests; physical webcam acceptance remains necessary on each device.

## Local data and limits

Camera frames are processed locally; the app does not record or upload them. MediaPipe 0.10.14 is loaded from jsDelivr and the model from Google; those services receive asset requests. Hand tracking startup therefore still requires internet access; camera-only preview can stay on while tracking is unavailable. Use **Stop preview** to release the camera explicitly. WebView data lives under `%LOCALAPPDATA%/HandPilot/WebView2`, reach profiles in `%LOCALAPPDATA%/HandPilot/calibration.json`, and movement reports in the folder above. These local data files are not part of the repository.

This remains a movement-test prototype, not a production release. Clicking/dragging/scrolling code is retained internally for the later interaction milestone but is disabled in the current desktop flow. Accuracy depends on lighting, camera placement, and hand visibility. Multi-monitor arrangements, especially gaps and mixed scaling, need live verification. Windows secure desktop/UAC and higher-integrity applications cannot be controlled. There is no installer or right-click flow yet.

References: [MediaPipe Hand Landmarker](https://ai.google.dev/edge/mediapipe/solutions/vision/hand_landmarker/web_js), [Microsoft WebView2](https://learn.microsoft.com/microsoft-edge/webview2/).

No license has been selected yet.
