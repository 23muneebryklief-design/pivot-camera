namespace HandPilot;

public enum TrackingCondition { Waiting, NoHand, Acquiring, Tracking, Delayed, Error }

public sealed record TrackingSample(string SessionId, long Sequence, double CapturedAtUnixMs,
    HandPoint[] Points, double InferenceMs = 0, string Backend = "");

public sealed record MovementSnapshot(TrackingCondition Condition, bool Enabled, HandPoint? Pointer,
    TrackingSample? Sample, double ProcessingAgeMs, string? Error,
    double LastOutputAgeMs = 0, long OutputCount = 0, long StaleResults = 0);

// The host ticks this on a background timer. The UI only submits the newest result and reads snapshots.
public sealed class MovementSession(IMouseOutput output) : IDisposable
{
    public const double MaximumResultAgeMs = 120;
    public const double TrackingTimeoutMs = 150;
    private readonly object sync = new();
    private readonly GestureEngine engine = new(output);
    private string streamId = "";
    private TrackingSample? latest;
    private long receivedSequence = -1, processedSequence = -1;
    private double receivedCapture = double.NegativeInfinity;
    private double acquireSince = double.NaN, previousCapture = double.NaN;
    private int acquireCount;
    private bool enabled, disposed;
    private TrackingCondition condition = TrackingCondition.Waiting;
    private string? error;
    private double lastOutputAgeMs;
    private long outputCount, staleResults;

    public void StartStream(string id)
    {
        lock (sync)
        {
            if (disposed) return;
            enabled = false;
            engine.Reset();
            streamId = id;
            latest = null;
            receivedSequence = processedSequence = -1;
            receivedCapture = double.NegativeInfinity;
            acquireSince = previousCapture = double.NaN;
            acquireCount = 0;
            error = null;
            lastOutputAgeMs = 0;
            outputCount = staleResults = 0;
            condition = TrackingCondition.Waiting;
        }
    }

    public bool Submit(TrackingSample sample)
    {
        lock (sync)
        {
            if (disposed || streamId.Length == 0 || sample.SessionId != streamId ||
                sample.Sequence <= receivedSequence || !double.IsFinite(sample.CapturedAtUnixMs) ||
                sample.CapturedAtUnixMs <= receivedCapture) return false;
            receivedSequence = sample.Sequence;
            receivedCapture = sample.CapturedAtUnixMs;
            latest = sample;
            return true;
        }
    }

    public void Configure(CameraBounds bounds, double screenWidth, double screenHeight)
    {
        if (!bounds.IsValid) throw new ArgumentException("Invalid camera reach area.", nameof(bounds));
        lock (sync)
        {
            if (disposed) return;
            enabled = false;
            engine.Reset();
            engine.Bounds = bounds;
            engine.ScreenWidth = screenWidth;
            engine.ScreenHeight = screenHeight;
        }
    }

    public bool Enable(HandPoint currentPointer)
    {
        lock (sync)
        {
            if (disposed || condition != TrackingCondition.Tracking || error != null) return false;
            engine.Start(currentPointer);
            enabled = true;
            return true;
        }
    }

    public void Pause()
    {
        lock (sync)
        {
            enabled = false;
            engine.Reset();
        }
    }

    public void Tick(double nowUnixMs)
    {
        lock (sync)
        {
            if (disposed || condition == TrackingCondition.Error) return;
            try
            {
                if (latest == null) return;
                double age = nowUnixMs - latest.CapturedAtUnixMs;
                if (latest.Sequence == processedSequence)
                {
                    if (age > TrackingTimeoutMs || age < -25) Lose(TrackingCondition.Delayed);
                    return;
                }
                processedSequence = latest.Sequence;
                if (age > MaximumResultAgeMs || age < -25)
                {
                    staleResults++;
                    Lose(TrackingCondition.Delayed);
                    return;
                }
                if (!GestureEngine.TryGetPalm(latest.Points, out _))
                {
                    Lose(TrackingCondition.NoHand);
                    return;
                }
                if (condition != TrackingCondition.Tracking)
                {
                    if (!double.IsFinite(acquireSince) || latest.CapturedAtUnixMs - previousCapture > TrackingTimeoutMs ||
                        latest.CapturedAtUnixMs <= previousCapture)
                    {
                        acquireSince = latest.CapturedAtUnixMs;
                        acquireCount = 0;
                    }
                    previousCapture = latest.CapturedAtUnixMs;
                    acquireCount++;
                    condition = TrackingCondition.Acquiring;
                    if (acquireCount < 3 || latest.CapturedAtUnixMs - acquireSince < 100) return;
                    condition = TrackingCondition.Tracking;
                }
                previousCapture = latest.CapturedAtUnixMs;
                if (enabled)
                {
                    engine.Process(latest.Points, latest.CapturedAtUnixMs / 1000);
                    lastOutputAgeMs = Math.Max(0, age);
                    outputCount++;
                }
            }
            catch (Exception ex)
            {
                enabled = false;
                error = ex.Message;
                condition = TrackingCondition.Error;
                try { engine.Reset(); } catch { /* Preserve the original input error for the UI. */ }
            }
        }
    }

    private void Lose(TrackingCondition next)
    {
        if (condition is TrackingCondition.Tracking or TrackingCondition.Acquiring) engine.LoseTracking();
        condition = next;
        acquireSince = previousCapture = double.NaN;
        acquireCount = 0;
    }

    public MovementSnapshot Snapshot(double nowUnixMs)
    {
        lock (sync)
            return new(condition, enabled, engine.PointerPosition, latest,
                latest == null ? 0 : Math.Max(0, nowUnixMs - latest.CapturedAtUnixMs), error,
                lastOutputAgeMs, outputCount, staleResults);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            enabled = false;
            engine.Reset();
        }
    }
}
