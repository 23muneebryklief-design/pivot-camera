namespace HandPilot;

public enum PointerMode { Direct, Trackpad }
public enum HandGesture { None, Moving, Clutched, Pinching, Dragging }

/// <summary>
/// Adaptive low-pass filter: heavy smoothing when the signal moves slowly, little lag when it moves fast
/// (Casiez, Roussel and Vogel, "1€ Filter", CHI 2012).
/// </summary>
public sealed class OneEuroFilter(double minCutoff, double beta, double derivativeCutoff = 1)
{
    private double value, derivative, time;
    private bool primed;

    public void Reset() => primed = false;

    public double Filter(double sample, double seconds)
    {
        if (!primed)
        {
            value = sample; derivative = 0; time = seconds; primed = true;
            return value;
        }
        if (seconds <= time) return value;
        double dt = Math.Clamp(seconds - time, 1e-3, .15);
        time = seconds;
        double rawDerivative = (sample - value) / dt;
        derivative += Alpha(derivativeCutoff, dt) * (rawDerivative - derivative);
        value += Alpha(minCutoff + beta * Math.Abs(derivative), dt) * (sample - value);
        return value;
    }

    private static double Alpha(double cutoff, double dt) => 1 / (1 + 1 / (2 * Math.PI * cutoff) / dt);
}

/// <summary>
/// Camera hand control that behaves like a laptop trackpad: hand motion pushes the cursor from where it is,
/// with speed-dependent gain. A closed hand lifts the "finger" to reposition, and a thumb–index pinch clicks
/// or drags. Distances are measured in hand units (wrist to middle knuckle) so behaviour does not depend on
/// how far the hand is from the camera.
/// </summary>
public sealed class TrackpadController(IMouseOutput mouse, Func<HandPoint?>? readCursor = null)
{
    // Pinch ratio = thumb tip to index tip distance / hand unit. Hysteresis prevents chatter.
    public const double PinchClose = .25, PinchOpen = .40;
    // Hand travel while pinched before a press becomes a drag. Below this the cursor stays locked.
    public const double DragStart = .25;
    // A click lands where the cursor was this long before the pinch closed, undoing pinch-induced drift.
    public const double RollbackSeconds = .12;
    // After a pinch opens the cursor holds briefly, because releasing a pinch also nudges the hand.
    public const double ReleaseSettleSeconds = .15;
    private const double SpeedWindowSeconds = .10, MaximumGapSeconds = .15, HistorySeconds = .5;
    // Hand speed (hand units per second) → gain (reference pixels per hand unit at 1920 px wide).
    private const double StillSpeed = .08, PrecisionSpeed = .20, FastSpeed = 1.6;
    private const double PrecisionGain = 220, FastGain = 1500;

    private readonly OneEuroFilter filterX = new(1, 15), filterY = new(1, 15);
    private readonly List<(double Time, HandPoint Point)> trail = [];
    private readonly List<(double Time, HandPoint Pointer)> history = [];
    private HandPoint? reference, pinchOrigin, position;
    private double handScale, settleUntil;
    private double? lastTime;
    private bool buttonDown, pinched, dragging, armed, clutched;

    public double Sensitivity { get; set; } = 1;
    public double CameraAspect { get; set; } = 4.0 / 3;
    public HandPoint? Position => position;
    public HandGesture Gesture { get; private set; }

    public void Start(HandPoint? pointer)
    {
        Reset();
        position = pointer is HandPoint p && double.IsFinite(p.X) && double.IsFinite(p.Y)
            ? new(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1)) : null;
    }

    public void Reset()
    {
        Suspend();
        position = null;
    }

    /// <summary>Release any button and forget hand history; the cursor stays where it is.</summary>
    public void Suspend()
    {
        if (buttonDown) mouse.Up();
        buttonDown = pinched = dragging = armed = clutched = false;
        filterX.Reset(); filterY.Reset();
        trail.Clear(); history.Clear();
        reference = pinchOrigin = null;
        handScale = 0; settleUntil = 0;
        lastTime = null;
        Gesture = HandGesture.None;
    }

    public void Process(HandPoint[] points, double seconds, double screenWidth, double screenHeight)
    {
        if (!double.IsFinite(seconds) || !TryMeasure(points, out var raw, out double scale, out double pinch, out int folded))
        {
            Suspend();
            return;
        }
        if (lastTime is double last)
        {
            if (seconds <= last) return;
            if (seconds - last > MaximumGapSeconds) Suspend();
        }
        lastTime = seconds;
        double width = double.IsFinite(screenWidth) ? Math.Max(1, screenWidth) : 1920;
        double height = double.IsFinite(screenHeight) ? Math.Max(1, screenHeight) : 1080;
        handScale = handScale <= 0 ? scale : handScale + .15 * (scale - handScale);
        var filtered = new HandPoint(filterX.Filter(raw.X, seconds), filterY.Filter(raw.Y, seconds));
        trail.Add((seconds, filtered));
        trail.RemoveAll(s => s.Time < seconds - SpeedWindowSeconds - 1e-6);
        try { Step(filtered, pinch, folded, seconds, width, height); }
        finally
        {
            if (position is HandPoint current) history.Add((seconds, current));
            history.RemoveAll(s => s.Time < seconds - HistorySeconds);
        }
    }

    private void Step(HandPoint filtered, double pinch, int folded, double seconds, double width, double height)
    {
        // A closed hand lifts the virtual finger off the pad: move freely without moving the cursor.
        if (clutched ? folded >= 2 : folded == 3)
        {
            if (buttonDown) { mouse.Up(); buttonDown = false; }
            clutched = true;
            pinched = dragging = armed = false;
            pinchOrigin = reference = null;
            // Re-prime on reopening so filter lag from the repositioning travel cannot drift the cursor.
            filterX.Reset(); filterY.Reset();
            Gesture = HandGesture.Clutched;
            return;
        }
        clutched = false;

        if (reference is null)
        {
            // New contact (start, reacquired hand or opened clutch). Follow the real cursor in case the
            // physical mouse moved, and never move on the first sample.
            if (readCursor?.Invoke() is HandPoint actual) position = actual;
            position ??= new(.5, .5);
            reference = filtered;
            trail.Clear();
            trail.Add((seconds, filtered));
            Gesture = HandGesture.Moving;
            if (pinch > PinchOpen) armed = true;
            return;
        }

        if (!pinched && pinch > PinchOpen) armed = true;
        if (!pinched && armed && pinch < PinchClose)
        {
            // Return to where the user was aiming before the pinch started disturbing the hand.
            var aim = PointerAt(seconds - RollbackSeconds) ?? position;
            if (aim is HandPoint target && target != position)
            {
                position = target;
                mouse.Move(target.X, target.Y);
            }
            mouse.Down();
            buttonDown = pinched = true;
            dragging = armed = false;
            pinchOrigin = reference = filtered;
            Gesture = HandGesture.Pinching;
            return;
        }
        if (pinched && pinch > PinchOpen)
        {
            if (buttonDown) mouse.Up();
            buttonDown = pinched = dragging = false;
            pinchOrigin = null;
            armed = true;
            reference = filtered;
            settleUntil = seconds + ReleaseSettleSeconds;
            Gesture = HandGesture.Moving;
            return;
        }
        if (pinched && !dragging)
        {
            reference = filtered;
            // Locked until the hand clearly travels, so a click never drifts into a drag.
            if (pinchOrigin is HandPoint origin && Distance(filtered, origin) / handScale < DragStart) return;
            dragging = true;
            Gesture = HandGesture.Dragging;
            return;
        }
        if (seconds < settleUntil)
        {
            reference = filtered;
            return;
        }

        var from = reference.Value;
        reference = filtered;
        double gain = Gain(Speed()) * Math.Clamp(Sensitivity, .2, 4) * width / 1920;
        Gesture = dragging ? HandGesture.Dragging : HandGesture.Moving;
        if (gain <= 0 || position is not HandPoint previous) return;
        double dxPixels = (filtered.X - from.X) / handScale * gain;
        double dyPixels = (filtered.Y - from.Y) / handScale * gain;
        var next = new HandPoint(Math.Clamp(previous.X + dxPixels / width, 0, 1), Math.Clamp(previous.Y + dyPixels / height, 0, 1));
        if (next == previous) return;
        position = next;
        mouse.Move(next.X, next.Y);
    }

    /// <summary>Net filtered hand speed over a short window. Zero-mean jitter cancels; real travel does not.</summary>
    private double Speed()
    {
        if (trail.Count < 2 || handScale <= 0) return 0;
        double dt = trail[^1].Time - trail[0].Time;
        return dt <= 1e-6 ? 0 : Distance(trail[^1].Point, trail[0].Point) / handScale / dt;
    }

    public static double Gain(double speed)
    {
        if (!double.IsFinite(speed) || speed <= StillSpeed) return 0;
        if (speed < PrecisionSpeed) return PrecisionGain * (speed - StillSpeed) / (PrecisionSpeed - StillSpeed);
        double s = Math.Clamp((speed - PrecisionSpeed) / (FastSpeed - PrecisionSpeed), 0, 1);
        return PrecisionGain + (FastGain - PrecisionGain) * s * s * (3 - 2 * s);
    }

    private HandPoint? PointerAt(double time)
    {
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Time <= time) return history[i].Pointer;
        return history.Count > 0 ? history[0].Pointer : null;
    }

    private static double Distance(HandPoint a, HandPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private bool TryMeasure(HandPoint[]? points, out HandPoint anchor, out double scale, out double pinch, out int folded)
    {
        anchor = default; scale = pinch = 0; folded = 0;
        if (points is not { Length: 21 } || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
            p.X < -.05 || p.X > 1.05 || p.Y < -.05 || p.Y > 1.05)) return false;
        double aspect = double.IsFinite(CameraAspect) && CameraAspect > .2 && CameraAspect < 5 ? CameraAspect : 4.0 / 3;
        // Mirrored, with x in image-height units so distances are the same in both directions.
        HandPoint At(int i) => new((1 - points[i].X) * aspect, points[i].Y);
        double D(int a, int b) => Distance(At(a), At(b));
        // Wrist and the middle/ring/pinky knuckles barely move when the thumb and index pinch.
        anchor = new((At(0).X + At(9).X + At(13).X + At(17).X) / 4, (At(0).Y + At(9).Y + At(13).Y + At(17).Y) / 4);
        scale = D(0, 9);
        if (scale < .02) return false;
        pinch = D(4, 8) / scale;
        double tolerance = clutched ? 1.0 : .9;
        folded = new[] { 12, 16, 20 }.Count(tip => D(0, tip) < D(0, tip - 2) * tolerance);
        return true;
    }
}
