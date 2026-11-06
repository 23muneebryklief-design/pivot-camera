namespace HandPilot;

/// <summary>One comfortable open-hand hold learns a small, safe palm workspace automatically.</summary>
public sealed class ReachCalibration
{
    private const double RequiredHoldSeconds = 1;
    private const double MaximumSampleAgeSeconds = .15;
    private const double MaximumMissingSeconds = .35;
    private const double MaximumStableSpan = .04;
    private const double Padding = .025;
    private readonly List<Observation> observations = [];
    private double lastSampleTime = double.NegativeInfinity;
    private double lastValidTime = double.NegativeInfinity;
    private string hint = "Show your open hand where it feels comfortable.";

    private sealed record Observation(double Time, HandPoint? Palm, double Left = 0, double Top = 0, double Right = 0, double Bottom = 0);
    public bool IsComplete => Result.HasValue;
    public CameraBounds? Result { get; private set; }
    public double Progress { get; private set; }
    public string Prompt => IsComplete ? "Your comfortable reach is ready." : "Hold your open hand comfortably for a moment. Setup saves automatically.";

    public void Reset()
    {
        observations.Clear();
        Result = null;
        Progress = 0;
        lastSampleTime = lastValidTime = double.NegativeInfinity;
        hint = "Show your open hand where it feels comfortable.";
    }

    public void AddSample(HandPoint[] points, double sampleTimeSeconds)
    {
        if (IsComplete) return;
        if (!double.IsFinite(sampleTimeSeconds) || sampleTimeSeconds <= lastSampleTime)
        {
            ClearHold();
            hint = "Waiting for fresh hand tracking…";
            return;
        }
        if (sampleTimeSeconds - lastValidTime > MaximumMissingSeconds) ClearHold();
        lastSampleTime = sampleTimeSeconds;
        if (!TryObserve(points, sampleTimeSeconds, out var observation, out hint))
        {
            observations.Add(new(sampleTimeSeconds, null));
            Trim(sampleTimeSeconds);
            UpdateProgress();
            return;
        }
        lastValidTime = sampleTimeSeconds;
        observations.Add(observation!);
        Trim(sampleTimeSeconds);
        if (!Stable(ValidObservations()))
        {
            // A deliberate move starts a new comfortable hold. A few noisy detections
            // are ignored by the percentile span rather than erasing all progress.
            ClearHold();
            observations.Add(observation!);
            hint = "Relax your hand here for a moment. Small tremors are fine.";
        }
        UpdateProgress();
    }

    public bool TryCapture(double nowSeconds, out string message)
    {
        if (IsComplete) { message = "Setup is already complete."; return false; }
        if (!double.IsFinite(nowSeconds) || observations.Count == 0)
        {
            message = hint;
            return false;
        }
        var latest = observations[^1];
        double age = nowSeconds - latest.Time;
        if (age < 0 || age > MaximumSampleAgeSeconds)
        {
            if (nowSeconds - lastValidTime > MaximumMissingSeconds) ClearHold();
            message = "Show your open hand again. Setup will continue when tracking is fresh.";
            return false;
        }
        if (latest.Palm == null) { message = hint; return false; }
        var valid = ValidObservations();
        double activeSeconds = ActiveSeconds();
        if (valid.Length < 8 || activeSeconds < RequiredHoldSeconds - 1e-6 ||
            valid.Length < observations.Count * .7 || !Stable(valid))
        {
            message = "Relax your open hand here for a moment. Setup saves automatically.";
            return false;
        }

        var centre = new HandPoint(Median(valid.Select(v => v.Palm!.Value.X)), Median(valid.Select(v => v.Palm!.Value.Y)));
        if (Math.Abs(latest.Palm.Value.X - centre.X) > MaximumStableSpan ||
            Math.Abs(latest.Palm.Value.Y - centre.Y) > MaximumStableSpan)
        {
            message = "Relax your hand here for a moment. Setup will save when it settles.";
            return false;
        }
        // Extents are measured in the mirrored image, including the asymmetric thumb.
        // The entire observed hand can translate to each learned corner with padding.
        double safeLeft = valid.Max(v => v.Left) + Padding;
        double safeTop = valid.Max(v => v.Top) + Padding;
        double safeRight = 1 - valid.Max(v => v.Right) - Padding;
        double safeBottom = 1 - valid.Max(v => v.Bottom) - Padding;
        double halfWidth = Math.Min(.16, .85 * Math.Min(centre.X - safeLeft, safeRight - centre.X));
        double halfHeight = Math.Min(.14, .85 * Math.Min(centre.Y - safeTop, safeBottom - centre.Y));
        if (halfWidth < .06 || halfHeight < .06)
        {
            Progress = 0;
            message = safeRight - safeLeft < .16 || safeBottom - safeTop < .16
                ? "Move a little farther from the camera so your whole hand has room to move."
                : "Move your hand a little toward the centre of the preview, then relax it there.";
            return false;
        }
        var bounds = new CameraBounds(centre.X - halfWidth, centre.Y - halfHeight, centre.X + halfWidth, centre.Y + halfHeight);
        if (!bounds.IsValid) { message = "Move your open hand toward the centre of the preview."; return false; }
        Result = bounds;
        Progress = 1;
        message = "Reach setup complete. Your hand is ready to move the cursor.";
        return true;
    }

    private void ClearHold() { observations.Clear(); Progress = 0; }
    private void Trim(double time) => observations.RemoveAll(v => v.Time < time - 1.6);
    private Observation[] ValidObservations() => observations.Where(v => v.Palm.HasValue).ToArray();
    private void UpdateProgress()
    {
        var valid = ValidObservations();
        double coverage = observations.Count == 0 ? 0 : valid.Length / (double)observations.Count;
        Progress = Stable(valid) && coverage >= .7 ? Math.Clamp(ActiveSeconds() / RequiredHoldSeconds, 0, .99) : 0;
    }
    private double ActiveSeconds()
    {
        double seconds = 0;
        for (int i = 1; i < observations.Count; i++)
        {
            double gap = observations[i].Time - observations[i - 1].Time;
            if (observations[i].Palm.HasValue && observations[i - 1].Palm.HasValue && gap <= .15)
                seconds += gap;
        }
        return seconds;
    }
    private static bool Stable(Observation[] valid) => valid.Length < 3 ||
        Span(valid.Select(v => v.Palm!.Value.X)) <= MaximumStableSpan &&
        Span(valid.Select(v => v.Palm!.Value.Y)) <= MaximumStableSpan;
    private static double Span(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling((sorted.Length - 1) * .9)] - sorted[(int)Math.Floor((sorted.Length - 1) * .1)];
    }
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }
    private static bool TryObserve(HandPoint[] points, double time, out Observation? observation, out string message)
    {
        observation = null;
        message = "Keep your wrist and fingers in view. Move back a little if your hand is clipped.";
        if (points.Length != 21 || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1)) return false;
        if (points.Max(p => p.X) - points.Min(p => p.X) < .015 || points.Max(p => p.Y) - points.Min(p => p.Y) < .015) return false;
        double Distance(int a, int b) => Math.Sqrt(Math.Pow(points[a].X - points[b].X, 2) + Math.Pow(points[a].Y - points[b].Y, 2));
        if (new[] { 8, 12, 16, 20 }.Count(tip => Distance(0, tip) > Math.Max(.015, Distance(0, tip - 2)) * 1.1) < 2)
        {
            message = "Open your hand naturally with your palm facing the camera.";
            return false;
        }
        var palm = new HandPoint(1 - (points[0].X + points[5].X + points[9].X + points[13].X + points[17].X) / 5,
            (points[0].Y + points[5].Y + points[9].Y + points[13].Y + points[17].Y) / 5);
        double minX = points.Min(p => 1 - p.X), maxX = points.Max(p => 1 - p.X);
        observation = new(time, palm, palm.X - minX, palm.Y - points.Min(p => p.Y), maxX - palm.X, points.Max(p => p.Y) - palm.Y);
        message = "Relax your open hand here for a moment. Setup saves automatically.";
        return true;
    }
}
