using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HandPilot;

public sealed class MovementTrialWindow : Window
{
    private readonly MovementSession session;
    private readonly MovementTrial trial = new(DesktopGeometry.Width, DesktopGeometry.Height);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly TextBlock heading = new() { FontSize = 28, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock detail = new() { FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock tracking = new() { FontSize = 14, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
    private readonly Canvas targets = new() { IsHitTestVisible = false };
    private readonly Ellipse ring = new() { Width = 48, Height = 48, Stroke = new SolidColorBrush(Color.FromRgb(112, 224, 196)), StrokeThickness = 4 };
    private bool saved, reportSaved;
    private long lastObservedOutput;
    private readonly List<double> outputAges = [];
    public bool Passed => trial.Phase == TrialPhase.Complete;
    public string? ReportPath { get; private set; }
    public string ResultText => Passed ? "Movement check passed. Recovery smoothness still needs visual review. " +
        (reportSaved ? "Your result was saved." : "The result could not be saved.") : trial.Failure ?? "Movement check cancelled.";

    public MovementTrialWindow(MovementSession session, string cameraId, string displayKey)
    {
        this.session = session;
        lastObservedOutput = session.Snapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).OutputCount;
        Title = "HandPilot · movement check";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(16, 21, 34));
        Foreground = Brushes.White;
        Topmost = true;
        var grid = new Grid();
        var panel = new StackPanel { Width = 720, MaxWidth = 900, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "HANDPILOT / LIVE MOVEMENT CHECK", Foreground = new SolidColorBrush(Color.FromRgb(112, 224, 196)), FontSize = 13, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 20) });
        panel.Children.Add(heading); panel.Children.Add(detail); panel.Children.Add(tracking);
        panel.Children.Add(new TextBlock { Text = "Movement only · Esc closes this check · Ctrl + Alt + Space pauses control", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 32, 0, 0), Foreground = Brushes.LightSlateGray });
        grid.Children.Add(panel); grid.Children.Add(targets); targets.Children.Add(ring);
        Content = grid;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        SizeChanged += (_, _) => PlaceTarget();
        timer.Tick += (_, _) =>
        {
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var snapshot = session.Snapshot(now);
            if (snapshot.OutputCount != lastObservedOutput)
            {
                lastObservedOutput = snapshot.OutputCount;
                outputAges.Add(snapshot.LastOutputAgeMs);
            }
            try { trial.Update(now / 1000, snapshot, DesktopGeometry.Pointer()); }
            catch (Exception ex) { session.Pause(); detail.Text = ex.Message; timer.Stop(); return; }
            heading.Text = trial.Prompt; detail.Text = trial.Detail;
            tracking.Text = $"Tracking: {snapshot.Condition} · last cursor update age {snapshot.LastOutputAgeMs:0} ms";
            PlaceTarget();
            if (trial.Finished && !saved)
            {
                saved = true;
                try
                {
                    string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandPilot", "MovementChecks");
                    Directory.CreateDirectory(directory);
                    ReportPath = System.IO.Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    File.WriteAllText(ReportPath, JsonSerializer.Serialize(new { recordedAt = DateTimeOffset.Now, cameraId, displayKey,
                        passed = Passed, trial.Failure, trial.StableSpanPixels, trial.RecoverySmoothnessVerified, trial.Steps,
                        timing = new { observedCursorUpdates = outputAges.Count,
                            averageProcessingToCursorMs = outputAges.Count == 0 ? 0 : outputAges.Average(),
                            p95ProcessingToCursorMs = outputAges.Count == 0 ? 0 : outputAges.Order().ElementAt((int)Math.Floor((outputAges.Count - 1) * .95)),
                            sessionStaleResults = snapshot.StaleResults,
                            scope = "Sampled snapshot-to-cursor age; excludes camera sensor/driver buffering and display latency. Recovery smoothness also needs visual confirmation." },
                        criteria = new { cornerTolerancePixels = 24, cornerHoldSeconds = .4, steadySpanPixels = 12, steadySeconds = 2, rounds = 2,
                            fullHandVisible = true, engineCursorAgreementPixels = 4, lostHandDriftPixels = 3 } }, new JsonSerializerOptions { WriteIndented = true }));
                    reportSaved = true;
                    detail.Text += "\nResult saved. Press Esc to return.";
                }
                catch (Exception ex) { detail.Text += "\nCould not save the result: " + ex.Message; }
                session.Pause();
                timer.Stop();
            }
        };
        Loaded += (_, _) => { DesktopGeometry.CoverDesktop(new WindowInteropHelper(this).Handle); heading.Text = trial.Prompt; detail.Text = trial.Detail; PlaceTarget(); timer.Start(); };
        Closed += (_, _) => { timer.Stop(); session.Pause(); };
    }

    private void PlaceTarget()
    {
        ring.Visibility = trial.Target.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (trial.Target is not HandPoint point) return;
        Canvas.SetLeft(ring, Math.Clamp(point.X * targets.ActualWidth - 24, 0, Math.Max(0, targets.ActualWidth - 48)));
        Canvas.SetTop(ring, Math.Clamp(point.Y * targets.ActualHeight - 24, 0, Math.Max(0, targets.ActualHeight - 48)));
    }
}
