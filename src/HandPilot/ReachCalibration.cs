namespace HandPilot;

/// <summary>Collects a comfortable screen reach without requiring input control to be enabled.</summary>
public sealed class ReachCalibration
{
    private const double RequiredHoldSeconds = .5;
    private const double MaximumSampleAgeSeconds = .15;
    private const double MaximumSampleGapSeconds = .2;
    private const double MaximumPalmSpan = .02;
    private readonly List<(double Time, HandPoint Palm)> samples = [];
    private readonly List<HandPoint> captures = [];
    private double lastSampleTime = double.NegativeInfinity;
    private bool wholeHandVisible;

    public int StepIndex => captures.Count;
    public bool IsComplete => Result.HasValue;
    public CameraBounds? Result { get; private set; }
    public string Prompt => StepIndex switch
    {
        0 => "Hold your open hand comfortably in the centre. Keep your wrist and every finger visible, then capture.",
        1 => "Move to your comfortable upper-left reach in the mirrored preview. Hold still, then capture.",
        2 => "Move to your comfortable upper-right reach. Hold still, then capture.",
        3 => "Move to your comfortable lower-right reach. Hold still, then capture.",
        4 => "Move to your comfortable lower-left reach. Hold still, then capture.",
        _ => "Your comfortable reach is saved. You can now test all four screen corners."
    };

    public void Reset()
    {
        samples.Clear();
        captures.Clear();
        Result = null;
        lastSampleTime = double.NegativeInfinity;
        wholeHandVisible = false;
    }

    public void AddSample(HandPoint[] points, double sampleTimeSeconds)
    {
        if (IsComplete) return;
        if (!double.IsFinite(sampleTimeSeconds) || sampleTimeSeconds <= lastSampleTime)
        {
            // Never combine samples from different clocks or a repeated/older frame.
            samples.Clear();
            wholeHandVisible = false;
            return;
        }

        if (sampleTimeSeconds - lastSampleTime > MaximumSampleGapSeconds) samples.Clear();
        lastSampleTime = sampleTimeSeconds;
        wholeHandVisible = points.Length == 21 && points.All(point =>
            double.IsFinite(point.X) && double.IsFinite(point.Y) &&
            point.X >= 0 && point.X <= 1 && point.Y >= 0 && point.Y <= 1);
        if (!wholeHandVisible)
        {
            samples.Clear();
            return;
        }

        // A collapsed set of detections is not a usable hand for calibration.
        if (points.Max(p => p.X) - points.Min(p => p.X) < .015 ||
            points.Max(p => p.Y) - points.Min(p => p.Y) < .015)
        {
            samples.Clear();
            wholeHandVisible = false;
            return;
        }

        var palm = new HandPoint(
            1 - (points[0].X + points[5].X + points[9].X + points[13].X + points[17].X) / 5,
            (points[0].Y + points[5].Y + points[9].Y + points[13].Y + points[17].Y) / 5);
        samples.Add((sampleTimeSeconds, palm));
        samples.RemoveAll(sample => sample.Time < sampleTimeSeconds - .8);
    }

    public bool TryCapture(double nowSeconds, out string message)
    {
        if (IsComplete)
        {
            message = "Calibration is already complete.";
            return false;
        }
        if (!double.IsFinite(nowSeconds) || !wholeHandVisible || samples.Count == 0)
        {
            message = "Keep your wrist and every finger inside the camera view.";
            return false;
        }
        var latest = samples[^1];
        if (nowSeconds < latest.Time || nowSeconds - latest.Time > MaximumSampleAgeSeconds)
        {
            message = "Tracking is delayed or lost. Wait for your hand to be detected again.";
            return false;
        }

        // Work backwards from the newest sample so a delayed capture cannot accept an old hold.
        var hold = samples.Where(sample => sample.Time >= latest.Time - RequiredHoldSeconds - .04).ToArray();
        if (hold.Length < 4 || latest.Time - hold[0].Time < RequiredHoldSeconds - .001)
        {
            message = "Hold your hand still for half a second, then capture again.";
            return false;
        }
        if (hold.Max(sample => sample.Palm.X) - hold.Min(sample => sample.Palm.X) > MaximumPalmSpan ||
            hold.Max(sample => sample.Palm.Y) - hold.Min(sample => sample.Palm.Y) > MaximumPalmSpan)
        {
            message = "Your hand is still moving. Settle in a comfortable position, then capture again.";
            return false;
        }

        var point = new HandPoint(hold.Average(sample => sample.Palm.X), hold.Average(sample => sample.Palm.Y));
        if (StepIndex > 0 && !IsInExpectedCorner(point, captures[0], StepIndex))
        {
            message = "Move farther toward the requested corner while keeping your whole hand visible, then try again.";
            return false;
        }

        if (StepIndex == 4)
        {
            var upperLeft = captures[1];
            var upperRight = captures[2];
            var lowerRight = captures[3];
            var lowerLeft = point;
            // Use the intersection, not the outer extremes: every captured pose must reach its corner.
            var bounds = new CameraBounds(
                Math.Max(upperLeft.X, lowerLeft.X),
                Math.Max(upperLeft.Y, upperRight.Y),
                Math.Min(upperRight.X, lowerRight.X),
                Math.Min(lowerRight.Y, lowerLeft.Y));
            var centre = captures[0];
            if (!bounds.IsValid || bounds.Right - bounds.Left < .12 || bounds.Bottom - bounds.Top < .12 ||
                centre.X <= bounds.Left || centre.X >= bounds.Right ||
                centre.Y <= bounds.Top || centre.Y >= bounds.Bottom)
            {
                message = "The corner positions overlap. Restart calibration and use a slightly wider comfortable reach.";
                return false;
            }
            Result = bounds;
        }

        captures.Add(point);
        samples.Clear();
        wholeHandVisible = false;
        message = IsComplete ? "Calibration complete. Your reach is ready to save." : Prompt;
        return true;
    }

    private static bool IsInExpectedCorner(HandPoint point, HandPoint centre, int step)
    {
        bool left = step is 1 or 4;
        bool top = step is 1 or 2;
        return (left ? centre.X - point.X >= .06 : point.X - centre.X >= .06) &&
               (top ? centre.Y - point.Y >= .06 : point.Y - centre.Y >= .06);
    }
}
