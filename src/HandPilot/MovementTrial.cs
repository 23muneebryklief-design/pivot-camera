namespace HandPilot;

public enum TrialPhase { Corners, Steady, HideHand, Recover, Complete, Failed }
public sealed record TrialStep(string Name, double Seconds);

// Acceptance checks use the real Windows cursor, not just the requested model coordinates.
public sealed class MovementTrial(double width, double height)
{
    private static readonly HandPoint[] Corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
    private static readonly string[] CornerNames = ["top left", "top right", "bottom right", "bottom left"];
    private double phaseStarted = double.NaN, holdStarted = double.NaN;
    private double minX, maxX, minY, maxY;
    private HandPoint? lossAnchor;
    private int corner;
    public TrialPhase Phase { get; private set; } = TrialPhase.Corners;
    public List<TrialStep> Steps { get; } = [];
    public double StableSpanPixels { get; private set; }
    // A 33 ms UI sampling interval can miss the first recovery output. Certify
    // hold/reacquisition here; the transition itself still needs visual review.
    public bool RecoverySmoothnessVerified => false;
    public string? Failure { get; private set; }
    public bool Finished => Phase is TrialPhase.Complete or TrialPhase.Failed;
    public HandPoint? Target => Phase switch
    {
        TrialPhase.Corners => Corners[corner % 4],
        TrialPhase.Steady or TrialPhase.Recover => new(.5, .5),
        _ => null
    };
    public string Prompt => Phase switch
    {
        TrialPhase.Corners => $"Round {corner / 4 + 1} of 2 · Reach the {CornerNames[corner % 4]} corner and hold briefly",
        TrialPhase.Steady => "Move to the centre and hold your hand still for 2 seconds",
        TrialPhase.HideHand => "Lower your hand completely out of camera view",
        TrialPhase.Recover => "Bring your hand back, then return the cursor to the centre",
        TrialPhase.Complete => "Movement check passed",
        _ => "Movement check needs another attempt"
    };
    public string Detail => Phase switch
    {
        TrialPhase.Corners => "Keep your entire hand visible and the physical mouse still. Reach the corner without stretching to the camera edges.",
        TrialPhase.Steady => "The cursor must stay within a 12-pixel span for two seconds.",
        TrialPhase.HideHand => "The cursor should stay put while your hand is missing.",
        TrialPhase.Recover => "Control should resume smoothly after tracking finds your hand again.",
        TrialPhase.Complete => $"8 corner holds · stable pointing ({StableSpanPixels:0.0}px span) · cursor held during hand loss and control reacquired. Recovery smoothness still needs visual review.",
        _ => Failure ?? "Try calibrating your comfortable reach again."
    };

    public void Update(double nowSeconds, MovementSnapshot snapshot, HandPoint actualPointer)
    {
        if (Finished) return;
        if (!double.IsFinite(phaseStarted)) phaseStarted = nowSeconds;
        if (!snapshot.Enabled) { Fail("Mouse control was paused. Start the check again when ready."); return; }
        double timeout = Phase is TrialPhase.HideHand or TrialPhase.Recover ? 20 : 30;
        if (nowSeconds - phaseStarted > timeout)
        {
            Fail(Phase == TrialPhase.Steady ? "The cursor did not settle within a 12-pixel span. Check tracking quality and retry." :
                Phase == TrialPhase.Corners ? "The corner was not reached in time. Recalibrate with a smaller, comfortable reach." :
                "The hand-loss and recovery step did not complete in time.");
            return;
        }
        if (Phase == TrialPhase.HideHand)
        {
            // Delayed inference alone does not count as deliberate hand loss.
            if (snapshot.Condition != TrackingCondition.NoHand)
            {
                lossAnchor = null;
                Hold(false, nowSeconds, .5);
                return;
            }
            lossAnchor ??= actualPointer;
            if (snapshot.Pointer is not HandPoint expected ||
                PixelDistance(actualPointer, lossAnchor.Value) > 3 ||
                PixelDistance(expected, lossAnchor.Value) > 3)
            {
                Fail("The cursor moved while your hand was missing. Keep the physical mouse still and retry.");
                return;
            }
            if (Hold(true, nowSeconds, .5))
                Advance(TrialPhase.Recover, nowSeconds, "Hand removed");
            return;
        }
        // Count a target only when a fresh, fully visible hand drove the real
        // cursor there. Physical mouse movement alone cannot satisfy the check.
        bool tracking = snapshot.Condition == TrackingCondition.Tracking &&
            double.IsFinite(snapshot.ProcessingAgeMs) && snapshot.ProcessingAgeMs >= 0 &&
            snapshot.ProcessingAgeMs <= MovementSession.MaximumResultAgeMs &&
            snapshot.Sample?.Points is { Length: 21 } points &&
            points.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y) &&
                p.X >= 0 && p.X <= 1 && p.Y >= 0 && p.Y <= 1) &&
            snapshot.Pointer is HandPoint requested && PixelDistance(actualPointer, requested) <= 4;
        if (Phase == TrialPhase.Corners)
        {
            var target = Target!.Value;
            if (Hold(tracking && Math.Abs(actualPointer.X - target.X) * width <= 24 &&
                Math.Abs(actualPointer.Y - target.Y) * height <= 24, nowSeconds, .4))
            {
                Steps.Add(new($"Round {corner / 4 + 1}: {CornerNames[corner % 4]}", nowSeconds - phaseStarted));
                corner++;
                phaseStarted = nowSeconds;
                holdStarted = double.NaN;
                if (corner == 8) Phase = TrialPhase.Steady;
            }
            return;
        }
        bool nearCentre = tracking && Math.Abs(actualPointer.X - .5) * width <= 55 &&
            Math.Abs(actualPointer.Y - .5) * height <= 55;
        if (Phase == TrialPhase.Recover)
        {
            if (Hold(nearCentre, nowSeconds, .75)) Advance(TrialPhase.Complete, nowSeconds, "Hand recovered and centred");
            return;
        }
        if (!nearCentre) { holdStarted = double.NaN; return; }
        double x = actualPointer.X * width, y = actualPointer.Y * height;
        if (!double.IsFinite(holdStarted))
        {
            holdStarted = nowSeconds;
            minX = maxX = x;
            minY = maxY = y;
        }
        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        double span = Math.Max(maxX - minX, maxY - minY);
        if (span > 12) { holdStarted = double.NaN; return; }
        if (nowSeconds - holdStarted >= 2)
        {
            StableSpanPixels = span;
            Advance(TrialPhase.HideHand, nowSeconds, "Steady pointing");
        }
    }

    private bool Hold(bool valid, double now, double duration)
    {
        if (!valid) { holdStarted = double.NaN; return false; }
        if (!double.IsFinite(holdStarted)) holdStarted = now;
        return now - holdStarted >= duration;
    }
    private double PixelDistance(HandPoint a, HandPoint b) =>
        Math.Sqrt(Math.Pow((a.X - b.X) * width, 2) + Math.Pow((a.Y - b.Y) * height, 2));
    private void Advance(TrialPhase next, double now, string label)
    {
        Steps.Add(new(label, now - phaseStarted));
        Phase = next;
        phaseStarted = now;
        holdStarted = double.NaN;
    }
    private void Fail(string message) { Failure = message; Phase = TrialPhase.Failed; }
}
