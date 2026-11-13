using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace HandPilot;

public partial class MainWindow : Window
{
    private readonly MovementSession movement = new(new WindowsMouse(), ReadCursor);
    private readonly CalibrationProfileStore profiles = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandPilot", "calibration.json"));
    private readonly DispatcherTimer uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private System.Threading.Timer? controlTimer;
    private HwndSource? source;
    private ReachCalibration? calibration;
    private string sessionId = "", cameraId = "", displayKey = "";
    private CameraBounds bounds = CameraBounds.Default;
    private bool cameraReady, calibrated, closing;
    private int displayCheckTicks;
    private PointerMode mode = PointerMode.Trackpad;
    private bool ReachReady => calibrated || mode == PointerMode.Trackpad;

    private static HandPoint? ReadCursor()
    {
        try { return DesktopGeometry.Pointer(); }
        catch (InvalidOperationException) { return null; }
    }

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // Dark title bar to match the window (Windows 10 20H1+ / 11). Ignored on older systems.
            int dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        Loaded += Initialize;
        uiTimer.Tick += (_, _) => RefreshState();
        Closed += (_, _) =>
        {
            closing = true;
            uiTimer.Stop(); controlTimer?.Dispose(); movement.Dispose();
            if (source != null) { UnregisterHotKey(source.Handle, 1); source.RemoveHook(Hook); }
            CameraView.Dispose();
        };
    }
    private static double NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private async void Initialize(object sender, RoutedEventArgs e)
    {
        try
        {
            displayKey = DesktopGeometry.Key();
            movement.Configure(bounds, DesktopGeometry.Width, DesktopGeometry.Height);
            movement.SetMode(mode);
            movement.SetSensitivity(SpeedSlider.Value);
            UpdateModeText();
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(Hook);
            RegisterPauseShortcut();
            controlTimer = new System.Threading.Timer(_ => movement.Tick(NowMs), null, 0, 8);
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandPilot", "WebView2"));
            if (closing) return;
            await CameraView.EnsureCoreWebView2Async(env);
            if (closing) return;
            var core = CameraView.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("handpilot.example", Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => { if (args.Uri != "https://handpilot.example/index.html") args.Cancel = true; };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) =>
            {
                if (args.PermissionKind != CoreWebView2PermissionKind.Camera || !args.Uri.StartsWith("https://handpilot.example/", StringComparison.Ordinal)) args.State = CoreWebView2PermissionState.Deny;
            };
            core.WebMessageReceived += Receive;
            core.NavigationCompleted += (_, _) => UpdatePreview();
            core.Navigate("https://handpilot.example/index.html");
            uiTimer.Start();
        }
        catch (Exception ex) { ActionMessage.Text = "Could not start: " + ex.Message; }
    }

    private void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != "https://handpilot.example/index.html" || closing) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var status))
            {
                if (!cameraReady) ActionMessage.Text = status.GetString();
                return;
            }
            if (root.TryGetProperty("ready", out var ready))
            {
                string incomingId = root.TryGetProperty("sessionId", out var sid) ? sid.GetString() ?? "" : "";
                if (!ready.GetBoolean() && incomingId.Length > 0 && incomingId != sessionId) return;
                movement.Pause();
                calibration = null;
                CalibrationPanel.Visibility = Visibility.Collapsed;
                cameraReady = ready.GetBoolean();
                sessionId = cameraReady ? incomingId : "";
                movement.StartStream(sessionId);
                if (cameraReady)
                {
                    if (sessionId.Length == 0) throw new InvalidOperationException("Camera did not provide a tracking session.");
                    cameraId = root.TryGetProperty("cameraId", out var device) ? device.GetString() ?? "" : "";
                    LoadReach();
                }
                else
                {
                    calibrated = false;
                    ActionMessage.Text = "Choose your camera, then start the preview.";
                }
                UpdatePreview(); RefreshState();
                return;
            }
            if (!root.TryGetProperty("points", out var points)) return;
            string frameSession = root.GetProperty("sessionId").GetString() ?? "";
            var frame = new TrackingSample(frameSession, root.GetProperty("sequence").GetInt64(),
                root.GetProperty("capturedAtUnixMs").GetDouble(),
                points.GetArrayLength() == 21 ? points.EnumerateArray().Select(p => new HandPoint(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble())).ToArray() : [],
                root.TryGetProperty("inferenceMs", out var infer) ? infer.GetDouble() : 0,
                root.TryGetProperty("backend", out var backend) ? backend.GetString() ?? "" : "",
                root.TryGetProperty("aspect", out var aspect) && aspect.ValueKind == JsonValueKind.Number ? aspect.GetDouble() : 0);
            if (movement.Submit(frame) && calibration != null)
            {
                double age = NowMs - frame.CapturedAtUnixMs;
                calibration.AddSample(age >= 0 && age <= MovementSession.MaximumResultAgeMs ? frame.Points : [], frame.CapturedAtUnixMs / 1000);
                // Evaluate on arrival, before a UI-timer tick can age an eligible frame out.
                AdvanceCalibration(NowMs / 1000);
            }
        }
        catch (Exception ex)
        {
            movement.Pause();
            calibration = null;
            CalibrationPanel.Visibility = Visibility.Collapsed;
            UpdatePreview();
            ActionMessage.Text = "Movement paused: " + ex.Message;
        }
    }

    private void LoadReach()
    {
        calibrated = profiles.TryLoad(cameraId, displayKey, out var saved);
        bounds = calibrated ? saved : CameraBounds.Default;
        movement.Configure(bounds, DesktopGeometry.Width, DesktopGeometry.Height);
        UpdateModeText();
    }

    /// <summary>Prefer Ctrl+Alt+Space; fall back if another app owns it. Never block startup over a shortcut.</summary>
    private void RegisterPauseShortcut()
    {
        const uint NoRepeat = 0x4000, Control = 0x0002, Alt = 0x0001, Shift = 0x0004;
        (uint Modifiers, uint Key, string[] Labels)[] candidates =
        [
            (Control | Alt, 0x20, ["Ctrl", "Alt", "Space"]),
            (Control | Alt, 0x50, ["Ctrl", "Alt", "P"]),
            (Control | Shift | Alt, 0x20, ["Ctrl", "Shift", "Alt", "Space"])
        ];
        foreach (var candidate in candidates)
        {
            if (!RegisterHotKey(source!.Handle, 1, NoRepeat | candidate.Modifiers, candidate.Key)) continue;
            PauseKeys.ItemsSource = candidate.Labels;
            PauseHint.Text = "pauses at any time";
            return;
        }
        PauseKeys.ItemsSource = Array.Empty<string>();
        PauseHint.Text = "No pause shortcut available. Use Pause movement.";
    }

    private void UpdateModeText()
    {
        bool trackpad = mode == PointerMode.Trackpad;
        SpeedPanel.Visibility = GesturePanel.Visibility = trackpad ? Visibility.Visible : Visibility.Collapsed;
        DirectPanel.Visibility = trackpad ? Visibility.Collapsed : Visibility.Visible;
        CalibrateButton.Visibility = trackpad ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(TrialButton, trackpad ? 0 : 2);
        Grid.SetColumnSpan(TrialButton, trackpad ? 3 : 1);
        ReachStatus.Text = !cameraReady ? "Choose your camera and start the preview." :
            trackpad ? "Show your open hand, then start movement." :
            calibrated ? "Saved reach is ready for this camera and display layout." :
            "Run Quick setup: hold your open hand still for about a second.";
        ActionMessage.Text = trackpad
            ? "Hold your hand upright, palm facing the camera, about an arm's length away."
            : "Hand position maps to the screen. Clicking is off in Direct mode.";
    }

    private void ModeChosen(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var chosen = DirectOption.IsChecked == true ? PointerMode.Direct : PointerMode.Trackpad;
        if (chosen == mode) return;
        mode = chosen;
        movement.SetMode(mode);
        UpdateModeText(); UpdatePreview(); RefreshState();
    }

    private void SpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedValue != null) SpeedValue.Text = $"{e.NewValue:0.0}×";
        movement.SetSensitivity(e.NewValue);
    }

    private void RefreshState()
    {
        if (closing) return;
        if (calibration != null || ++displayCheckTicks >= 10)
        {
            displayCheckTicks = 0;
            string current = DesktopGeometry.Key();
            if (current != displayKey)
            {
                displayKey = current;
                movement.Pause(); calibration = null; CalibrationPanel.Visibility = Visibility.Collapsed;
                LoadReach(); UpdatePreview();
                ActionMessage.Text = "Display arrangement changed. Movement was paused; check your saved reach before restarting.";
            }
        }
        if (calibration != null)
        {
            AdvanceCalibration(NowMs / 1000);
        }
        var state = movement.Snapshot(NowMs);
        TrackingStatus.Text = !cameraReady ? "Camera not started" : state.Condition switch
        {
            TrackingCondition.Tracking when state.Enabled && state.Mode == PointerMode.Trackpad => state.Gesture switch
            {
                HandGesture.Clutched => "Hand closed · reposition freely",
                HandGesture.Pinching => "Pinch · click",
                HandGesture.Dragging => "Pinch held · dragging",
                _ => "Trackpad active · pinch to click"
            },
            TrackingCondition.Tracking => state.Enabled ? "Movement active" : "Hand tracked · ready",
            TrackingCondition.Acquiring => "Finding a steady hand…",
            TrackingCondition.Delayed => "Tracking delayed · cursor held",
            TrackingCondition.NoHand => "Hand out of view · cursor held",
            TrackingCondition.Error => "Movement paused · input error",
            _ => "Show your hand to the camera"
        };
        if (state.Error != null) ActionMessage.Text = state.Error;
        string tone = !cameraReady ? "Idle" : state.Condition switch
        {
            TrackingCondition.Tracking => state.Enabled ? "Success" : "Accent",
            TrackingCondition.Acquiring or TrackingCondition.Delayed or TrackingCondition.NoHand => "Warning",
            TrackingCondition.Error => "Danger",
            _ => "Idle"
        };
        var brush = (System.Windows.Media.Brush)FindResource(tone);
        StatusDot.Fill = StatusHalo.Fill = brush;
        ControlButton.Content = state.Enabled ? "Pause movement" : "Start movement";
        ControlButton.IsEnabled = state.Enabled || cameraReady && ReachReady && calibration == null && state.Condition == TrackingCondition.Tracking;
        CalibrateButton.IsEnabled = cameraReady && calibration == null;
        TrackpadOption.IsEnabled = DirectOption.IsEnabled = calibration == null;
        TrialButton.IsEnabled = cameraReady && ReachReady && calibration == null && state.Condition == TrackingCondition.Tracking;
    }

    private void ToggleControl(object sender, RoutedEventArgs e)
    {
        if (movement.Snapshot(NowMs).Enabled) movement.Pause();
        else if (!movement.Enable(DesktopGeometry.Pointer())) ActionMessage.Text = "Show your hand steadily before starting movement.";
        RefreshState();
    }

    private void BeginCalibration(object sender, RoutedEventArgs e)
    {
        if (!cameraReady) return;
        movement.Pause();
        calibration = new ReachCalibration();
        CalibrationPanel.Visibility = Visibility.Visible;
        CalibrationPrompt.Text = calibration.Prompt;
        CalibrationProgress.Value = 0;
        CalibrationHint.Text = "Show your open hand in a comfortable position. Setup saves automatically.";
        ActionMessage.Text = "Movement is paused during setup. No corners to reach and no capture buttons to press.";
        UpdatePreview(); RefreshState();
    }

    private void FinishCalibration(CameraBounds result)
    {
        if (calibration == null || !cameraReady || !result.IsValid) return;
        bounds = result;
        movement.Configure(bounds, DesktopGeometry.Width, DesktopGeometry.Height);
        calibrated = true;
        calibration = null;
        CalibrationPanel.Visibility = Visibility.Collapsed;
        try
        {
            profiles.Save(cameraId, displayKey, bounds);
            ReachStatus.Text = "Quick setup saved. Reach is estimated from your comfortable hand position.";
            ActionMessage.Text = "Start movement, then use Check movement to validate your webcam.";
        }
        catch (Exception ex) { ReachStatus.Text = "Reach set up for this session."; ActionMessage.Text = "Could not save setup: " + ex.Message; }
        UpdatePreview();
    }

    private void AdvanceCalibration(double nowSeconds)
    {
        if (calibration == null) return;
        bool complete = calibration.TryCapture(nowSeconds, out var hint);
        CalibrationProgress.Value = calibration.Progress;
        CalibrationHint.Text = hint;
        if (complete && calibration.Result is CameraBounds result)
        {
            string currentDisplay = DesktopGeometry.Key();
            if (currentDisplay != displayKey)
            {
                displayKey = currentDisplay;
                calibration = null;
                CalibrationPanel.Visibility = Visibility.Collapsed;
                LoadReach();
                ActionMessage.Text = "Display arrangement changed. Run Quick setup again for this arrangement.";
                UpdatePreview();
            }
            else FinishCalibration(result);
        }
        else UpdatePreview();
    }

    private void CancelCalibration(object sender, RoutedEventArgs e)
    {
        calibration = null;
        CalibrationPanel.Visibility = Visibility.Collapsed;
        ActionMessage.Text = calibrated ? "Setup cancelled. Your previous reach is still available." : "Setup cancelled. Use Quick setup before starting movement.";
        UpdatePreview(); RefreshState();
    }

    private void UpdatePreview()
    {
        CameraView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            mode = mode.ToString(),
            bounds = new { left = bounds.Left, top = bounds.Top, right = bounds.Right, bottom = bounds.Bottom },
            calibration = new { active = calibration != null, prompt = calibration == null ? "" : CalibrationHint.Text }
        }));
    }

    private void RunMovementTrial(object sender, RoutedEventArgs e)
    {
        if (!ReachReady || calibration != null) return;
        if (!movement.Snapshot(NowMs).Enabled && !movement.Enable(DesktopGeometry.Pointer())) return;
        var trial = new MovementTrialWindow(movement, cameraId, displayKey) { Owner = this };
        trial.ShowDialog();
        ActionMessage.Text = trial.ResultText;
        RefreshState();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam == (IntPtr)1)
        {
            movement.Pause();
            if (calibration != null) CancelCalibration(this, new RoutedEventArgs());
            RefreshState(); handled = true;
        }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
