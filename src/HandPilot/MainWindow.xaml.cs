using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace HandPilot;

public partial class MainWindow : Window
{
    private readonly MovementSession movement = new(new WindowsMouse());
    private readonly CalibrationProfileStore profiles = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandPilot", "calibration.json"));
    private readonly DispatcherTimer uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private System.Threading.Timer? controlTimer;
    private HwndSource? source;
    private ReachCalibration? calibration;
    private string sessionId = "", cameraId = "", displayKey = "";
    private CameraBounds bounds = CameraBounds.Default;
    private bool cameraReady, calibrated, closing;
    private int displayCheckTicks;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += Initialize;
        PreviewKeyDown += (_, e) =>
        {
            if (calibration != null && e.Key == Key.Enter) { CaptureCalibration(this, new RoutedEventArgs()); e.Handled = true; }
        };
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
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(Hook);
            if (!RegisterHotKey(source.Handle, 1, 0x4000 | 0x0002 | 0x0001, 0x20))
                throw new InvalidOperationException("Pause shortcut is already in use. Close the conflicting app and reopen HandPilot.");
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
                root.TryGetProperty("backend", out var backend) ? backend.GetString() ?? "" : "");
            if (movement.Submit(frame) && calibration != null)
            {
                double age = NowMs - frame.CapturedAtUnixMs;
                calibration.AddSample(age >= 0 && age <= MovementSession.MaximumResultAgeMs ? frame.Points : [], frame.CapturedAtUnixMs / 1000);
            }
        }
        catch (Exception ex)
        {
            movement.Pause();
            ActionMessage.Text = "Movement paused: " + ex.Message;
        }
    }

    private void LoadReach()
    {
        calibrated = profiles.TryLoad(cameraId, displayKey, out var saved);
        bounds = calibrated ? saved : CameraBounds.Default;
        movement.Configure(bounds, DesktopGeometry.Width, DesktopGeometry.Height);
        ReachStatus.Text = calibrated ? "Your saved comfortable reach is ready for this camera and display arrangement." : "Calibrate your comfortable reach before starting movement.";
        ActionMessage.Text = "Movement only · clicking and scrolling are paused during this milestone.";
    }

    private void RefreshState()
    {
        if (closing) return;
        if (++displayCheckTicks >= 10)
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
        var state = movement.Snapshot(NowMs);
        TrackingStatus.Text = !cameraReady ? "Camera setup" : state.Condition switch
        {
            TrackingCondition.Tracking => state.Enabled ? "Movement active" : "Hand tracked · ready",
            TrackingCondition.Acquiring => "Finding a steady hand…",
            TrackingCondition.Delayed => "Tracking delayed · cursor held",
            TrackingCondition.NoHand => "Hand out of view · cursor held",
            TrackingCondition.Error => "Movement paused · input error",
            _ => "Show your hand to the camera"
        };
        if (state.Error != null) ActionMessage.Text = state.Error;
        ControlButton.Content = state.Enabled ? "Pause movement" : "Start movement";
        ControlButton.IsEnabled = state.Enabled || cameraReady && calibrated && calibration == null && state.Condition == TrackingCondition.Tracking;
        CalibrateButton.IsEnabled = cameraReady && calibration == null;
        TrialButton.IsEnabled = cameraReady && calibrated && calibration == null && state.Condition == TrackingCondition.Tracking;
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
        CalibrationPrompt.Text = "Position 1 of 5 · " + calibration.Prompt;
        ActionMessage.Text = "Movement is paused. Hold one hand in view and use your other hand to capture each position.";
        UpdatePreview(); RefreshState();
    }

    private void CaptureCalibration(object sender, RoutedEventArgs e)
    {
        if (calibration == null) return;
        if (!calibration.TryCapture(NowMs / 1000, out var message)) { ActionMessage.Text = message; return; }
        if (calibration.Result is CameraBounds result)
        {
            bounds = result;
            movement.Configure(bounds, DesktopGeometry.Width, DesktopGeometry.Height);
            calibrated = true;
            calibration = null;
            CalibrationPanel.Visibility = Visibility.Collapsed;
            try
            {
                profiles.Save(cameraId, displayKey, bounds);
                ReachStatus.Text = "Comfortable reach saved. All four calibrated poses map to the screen corners.";
                ActionMessage.Text = "Start movement, then use Check movement to validate your webcam.";
            }
            catch (Exception ex) { ReachStatus.Text = "Reach calibrated for this session."; ActionMessage.Text = "Could not save calibration: " + ex.Message; }
        }
        else
        {
            CalibrationPrompt.Text = $"Position {calibration.StepIndex + 1} of 5 · " + calibration.Prompt;
            ActionMessage.Text = "Position captured. Move comfortably to the next position, keeping all fingers visible.";
        }
        UpdatePreview(); RefreshState();
    }

    private void CancelCalibration(object sender, RoutedEventArgs e)
    {
        calibration = null;
        CalibrationPanel.Visibility = Visibility.Collapsed;
        ActionMessage.Text = calibrated ? "Calibration cancelled. Your previous reach is still available." : "Calibration cancelled. Calibrate reach before starting movement.";
        UpdatePreview(); RefreshState();
    }

    private void UpdatePreview()
    {
        CameraView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            bounds = new { left = bounds.Left, top = bounds.Top, right = bounds.Right, bottom = bounds.Bottom },
            calibration = new { active = calibration != null, prompt = calibration?.Prompt ?? "" }
        }));
    }

    private void RunMovementTrial(object sender, RoutedEventArgs e)
    {
        if (!calibrated || calibration != null) return;
        if (!movement.Snapshot(NowMs).Enabled && !movement.Enable(DesktopGeometry.Pointer())) return;
        var trial = new MovementTrialWindow(movement, cameraId, displayKey) { Owner = this };
        trial.ShowDialog();
        ActionMessage.Text = trial.ResultText;
        RefreshState();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam == (IntPtr)1) { movement.Pause(); RefreshState(); handled = true; }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
