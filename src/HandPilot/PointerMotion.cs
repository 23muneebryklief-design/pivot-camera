namespace HandPilot;

/// <summary>
/// Time-based, pixel-aware filtering. Small tremors stay still; deliberate travel
/// follows quickly. Recovery blends for 120 ms and never extrapolates a lost hand.
/// </summary>
public sealed class PointerMotion
{
    private const double RecoverySeconds = .12;
    private HandPoint? output, recoveryFrom;
    private double? lastTime, recoveryStarted;
    private double fastUntil;
    private bool recoveryPending;

    public void Reset(HandPoint? pointer = null)
    {
        output = pointer is HandPoint p && double.IsFinite(p.X) && double.IsFinite(p.Y)
            ? new(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1)) : null;
        lastTime = recoveryStarted = null;
        recoveryFrom = null;
        recoveryPending = output.HasValue;
        fastUntil = 0;
    }

    public void Suspend()
    {
        lastTime = recoveryStarted = null;
        recoveryFrom = null;
        recoveryPending = output.HasValue;
        fastUntil = 0;
    }

    public HandPoint Update(HandPoint target, double seconds, double screenWidth, double screenHeight)
    {
        double width = double.IsFinite(screenWidth) ? Math.Max(1, screenWidth) : 1920;
        double height = double.IsFinite(screenHeight) ? Math.Max(1, screenHeight) : 1080;
        if (output is not HandPoint previous)
        {
            output = target;
            lastTime = seconds;
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
            if (progress >= 1) { recoveryStarted = null; recoveryFrom = null; }
            return output.Value;
        }

        double dt = lastTime is double last ? Math.Clamp(seconds - last, 1.0 / 240, .15) : 1.0 / 30;
        lastTime = seconds;
        double dx = (target.X - previous.X) * width, dy = (target.Y - previous.Y) * height;
        double distancePixels = Math.Sqrt(dx * dx + dy * dy);
        if (distancePixels <= 1.5)
        {
            output = new(SnapEdge(previous.X, target.X, width), SnapEdge(previous.Y, target.Y, height));
            return output.Value;
        }
        // This distance-based time constant suppresses tiny local motion without
        // adding the same delay to a large intentional move.
        double travel = Math.Clamp((distancePixels - 2) / 70, 0, 1);
        if (distancePixels >= 70) fastUntil = seconds + .06;
        // Keep the short response through the end of a deliberate move, avoiding
        // a slow trailing tail as the remaining distance becomes small.
        double tau = seconds < fastUntil ? .004 : .045 - .041 * travel;
        double alpha = 1 - Math.Exp(-dt / tau);
        output = new(SnapEdge(previous.X + (target.X - previous.X) * alpha, target.X, width),
            SnapEdge(previous.Y + (target.Y - previous.Y) * alpha, target.Y, height));
        return output.Value;
    }

    private static double SnapEdge(double value, double target, double screenSize) =>
        (target == 0 || target == 1) && Math.Abs(value - target) * screenSize <= 2 ? target : value;
}
