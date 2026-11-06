namespace HandPilot;

/// <summary>
/// Holds a resting cursor against tracker tremor, with separate intent detection
/// for slow adjustments. Deliberate travel stays responsive; recovery never predicts a lost hand.
/// </summary>
public sealed class PointerMotion
{
    private const double RecoverySeconds = .12;
    private const double IntentWindowSeconds = .22;
    private const double MinimumIntentSeconds = .18;
    // Reference-desktop pixels keep the same camera-space tolerance on larger displays.
    private const double RestRadius = 20;
    private readonly List<(double Time, HandPoint Target)> history = [];
    private readonly List<(double Time, HandPoint Target)> trendHistory = [];
    private HandPoint? output, recoveryFrom, restAnchor;
    private HandPoint? trendAnchor;
    private double? lastTime, recoveryStarted;
    private double? driftSince;
    private double parkedAt;
    private bool trendAnchorSettled;
    private double fastUntil, movingUntil;
    private bool recoveryPending;

    public void Reset(HandPoint? pointer = null)
    {
        output = pointer is HandPoint p && double.IsFinite(p.X) && double.IsFinite(p.Y)
            ? new(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1)) : null;
        lastTime = recoveryStarted = null;
        recoveryFrom = null;
        recoveryPending = output.HasValue;
        fastUntil = 0;
        ClearRest();
    }

    public void Suspend()
    {
        lastTime = recoveryStarted = null;
        recoveryFrom = null;
        recoveryPending = output.HasValue;
        fastUntil = 0;
        ClearRest();
    }

    private void ClearRest()
    {
        restAnchor = null;
        trendAnchor = null;
        driftSince = null;
        trendAnchorSettled = false;
        history.Clear();
        trendHistory.Clear();
        movingUntil = 0;
    }

    public HandPoint Update(HandPoint target, double seconds, double screenWidth, double screenHeight)
    {
        double width = double.IsFinite(screenWidth) ? Math.Max(1, screenWidth) : 1920;
        double height = double.IsFinite(screenHeight) ? Math.Max(1, screenHeight) : 1080;
        if (output is not HandPoint previous)
        {
            output = target;
            lastTime = seconds;
            restAnchor = target;
            trendAnchor = target;
            parkedAt = seconds;
            Remember(target, seconds);
            return target;
        }
        if (recoveryPending)
        {
            recoveryPending = false;
            recoveryFrom = previous;
            recoveryStarted = seconds;
        }
        if (recoveryStarted is double began && recoveryFrom is HandPoint origin)
        {
            double progress = Math.Clamp((seconds - began) / RecoverySeconds, 0, 1);
            double blend = progress * progress * (3 - 2 * progress);
            output = new(origin.X + (target.X - origin.X) * blend, origin.Y + (target.Y - origin.Y) * blend);
            lastTime = seconds;
            if (progress >= 1)
            {
                recoveryStarted = null; recoveryFrom = null;
                ClearRest();
                movingUntil = seconds + .12;
                Remember(target, seconds);
            }
            return output.Value;
        }

        double dt = lastTime is double last ? Math.Clamp(seconds - last, 1.0 / 240, .15) : 1.0 / 30;
        lastTime = seconds;
        Remember(target, seconds);
        var stats = Statistics();
        if (restAnchor is HandPoint anchor)
        {
            var mean = TrendMean();
            // Settle the raw reference once, never continuously chase a parked hand.
            if (!trendAnchorSettled && seconds - parkedAt >= .6)
            {
                trendAnchor = mean;
                trendAnchorSettled = true;
            }
            bool trendReady = trendAnchorSettled && trendHistory.Count >= 8 && trendHistory[^1].Time - trendHistory[0].Time >= .5;
            var recentCentre = MedianTarget();
            bool drifting = trendReady && trendAnchor is HandPoint trendOrigin &&
                ReferenceDistance(mean, trendOrigin) >= 2.25 &&
                ReferenceDistance(recentCentre, trendOrigin) >= ReferenceDistance(mean, trendOrigin) * .5 &&
                (recentCentre.X - trendOrigin.X) * (mean.X - trendOrigin.X) * 1920 * 1920 +
                (recentCentre.Y - trendOrigin.Y) * (mean.Y - trendOrigin.Y) * 1080 * 1080 > 0;
            if (!drifting) driftSince = null;
            else driftSince ??= seconds;
            bool noisyFineIntent = driftSince is double since && seconds - since >= .12;
            bool largeMove = ReferenceDistance(target, anchor) > RestRadius;
            bool enoughHistory = history.Count >= 4 && stats.Duration >= MinimumIntentSeconds - 1e-6;
            // Alternating/noisy travel has little net direction. Coherent slow travel
            // accumulates across time, so fine adjustments can leave rest without a big jump.
            bool fineIntent = enoughHistory && stats.Net >= .45 && stats.Coherence >= .92 && stats.MaximumStep <= 3;
            // A small new position held consistently also counts as intent, unlike
            // a one- or two-frame twitch. Never recenter the resting anchor on noise.
            bool settledOffset = enoughHistory && stats.SpanX <= 1.25 && stats.SpanY <= 1.25 &&
                ReferenceDistance(MedianTarget(), anchor) >= 2;
            if (!largeMove && !fineIntent && !settledOffset && !noisyFineIntent)
            {
                output = new(SnapEdge(previous.X, target.X, width), SnapEdge(previous.Y, target.Y, height));
                return output.Value;
            }
            restAnchor = null;
            trendAnchor = null;
            driftSince = null;
            movingUntil = seconds + (noisyFineIntent ? .8 : .12);
            history.Clear();
            trendHistory.Clear();
            Remember(target, seconds);
        }
        else if (seconds >= movingUntil && history.Count >= 4 && stats.Duration >= MinimumIntentSeconds - 1e-6 &&
            stats.SpanX <= RestRadius * 2 && stats.SpanY <= RestRadius * 2 &&
            (stats.Travel <= .15 || stats.Coherence < .35 || stats.SpanX <= 1.25 && stats.SpanY <= 1.25 && stats.Net <= .15))
        {
            // Keep the frozen output separate from the measured raw centre; the
            // previous smoothing step can leave the cursor slightly off that centre.
            restAnchor = MedianTarget();
            trendAnchor = TrendMean();
            trendAnchorSettled = false;
            parkedAt = seconds;
            driftSince = null;
            // The slow-intent baseline must contain only the new resting period,
            // never the tail of the travel that just ended.
            trendHistory.Clear();
            trendHistory.Add((seconds, target));
            output = new(SnapEdge(previous.X, target.X, width), SnapEdge(previous.Y, target.Y, height));
            return output.Value;
        }

        double dx = (target.X - previous.X) * width, dy = (target.Y - previous.Y) * height;
        double distancePixels = Math.Sqrt(dx * dx + dy * dy);
        if (distancePixels <= .15)
        {
            output = new(SnapEdge(previous.X, target.X, width), SnapEdge(previous.Y, target.Y, height));
            return output.Value;
        }
        double travel = Math.Clamp((distancePixels - 2) / 70, 0, 1);
        if (distancePixels >= 70) fastUntil = seconds + .06;
        double tau = seconds < fastUntil ? .004 : .045 - .041 * travel;
        double alpha = 1 - Math.Exp(-dt / tau);
        output = new(SnapEdge(previous.X + (target.X - previous.X) * alpha, target.X, width),
            SnapEdge(previous.Y + (target.Y - previous.Y) * alpha, target.Y, height));
        return output.Value;
    }

    private void Remember(HandPoint target, double seconds)
    {
        history.Add((seconds, target));
        history.RemoveAll(s => s.Time < seconds - IntentWindowSeconds - 1e-6);
        trendHistory.Add((seconds, target));
        trendHistory.RemoveAll(s => s.Time < seconds - .6 - 1e-6);
    }

    private HandPoint TrendMean() => new(trendHistory.Average(s => s.Target.X), trendHistory.Average(s => s.Target.Y));

    private readonly record struct MotionStatistics(double Duration, double SpanX, double SpanY, double Net, double Travel, double MaximumStep)
    {
        public double Coherence => Travel <= 1e-9 ? 0 : Net / Travel;
    }

    private MotionStatistics Statistics()
    {
        double minX = history[0].Target.X, maxX = minX, minY = history[0].Target.Y, maxY = minY;
        double travel = 0, maximumStep = 0;
        for (int i = 0; i < history.Count; i++)
        {
            var p = history[i].Target;
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            if (i == 0) continue;
            double step = ReferenceDistance(p, history[i - 1].Target);
            travel += step; maximumStep = Math.Max(maximumStep, step);
        }
        return new(history[^1].Time - history[0].Time, (maxX - minX) * 1920, (maxY - minY) * 1080,
            ReferenceDistance(history[^1].Target, history[0].Target), travel, maximumStep);
    }

    private HandPoint MedianTarget()
    {
        var xs = history.Select(s => s.Target.X).Order().ToArray();
        var ys = history.Select(s => s.Target.Y).Order().ToArray();
        return new((xs[(xs.Length - 1) / 2] + xs[xs.Length / 2]) / 2,
            (ys[(ys.Length - 1) / 2] + ys[ys.Length / 2]) / 2);
    }

    private static double ReferenceDistance(HandPoint a, HandPoint b) =>
        Math.Sqrt(Math.Pow((a.X - b.X) * 1920, 2) + Math.Pow((a.Y - b.Y) * 1080, 2));

    private static double SnapEdge(double value, double target, double screenSize) =>
        (target == 0 || target == 1) && Math.Abs(value - target) * screenSize <= 2 ? target : value;
}
