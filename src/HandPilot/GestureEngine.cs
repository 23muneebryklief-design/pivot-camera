using System.Diagnostics;
namespace HandPilot;
public readonly record struct HandPoint(double X, double Y);
public interface IMouseOutput
{
    void Move(double x, double y);
    void Down();
    void Up();
    void Scroll(int delta);
}
public sealed class GestureEngine(IMouseOutput mouse)
{
    private bool held;
    private bool armed;
    private HandPoint? smooth;
    private HandPoint? origin;
    private long lastUpdate;
    public void Start()
    {
        Reset();
        smooth = new(.5, .5);
        mouse.Move(.5, .5);
    }
    private double? scrollY;
    public void Reset()
    {
        if (held) mouse.Up();
        held = false; armed = false; smooth = null; origin = null; scrollY = null; lastUpdate = 0;
    }
    public void Process(HandPoint[] p)
    {
        if (p.Length != 21 || p.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y))) { Reset(); return; }
        var palm = new HandPoint((p[0].X+p[5].X+p[9].X+p[13].X+p[17].X)/5, (p[0].Y+p[5].Y+p[9].Y+p[13].Y+p[17].Y)/5);
        origin ??= palm;
        double Distance(int a, int b) => Math.Sqrt(Math.Pow(p[a].X-p[b].X,2)+Math.Pow(p[a].Y-p[b].Y,2));
        // Compare fingertips to their middle joints relative to the wrist.
        // A wider release threshold avoids repeated clicks near the fist boundary.
        bool Closed(int tip) => Distance(0,tip) < Math.Max(Distance(0,tip-2),.025)*(held ? 1.05 : .9);
        bool fist = new[]{8,12,16,20}.All(Closed);
        if (!fist) armed = true;
        bool nextHeld = armed && fist;
        bool scrolling = !nextHeld && p[8].Y < p[6].Y && p[12].Y < p[10].Y && p[16].Y > p[14].Y;
        if (scrolling)
        {
            if (held) { mouse.Up(); held = false; }
            double y = (p[8].Y+p[12].Y)/2;
            if (scrollY is double previous && Math.Abs(y-previous) > .025)
            { mouse.Scroll(y < previous ? 120 : -120); scrollY = y; }
            scrollY ??= y; return;
        }
        scrollY = null;
        // Treat the first palm position as screen centre; mirror horizontal motion.
        var target = new HandPoint(Math.Clamp(.5+(origin.Value.X-palm.X)/.76,0,1), Math.Clamp(.5+(palm.Y-origin.Value.Y)/.76,0,1));
        // Time-based smoothing keeps response similar at different camera frame rates.
        // Follow deliberate motion quickly while filtering small tracking jitter.
        var now = Stopwatch.GetTimestamp();
        double dt = lastUpdate == 0 ? 1.0/30 : Math.Clamp(Stopwatch.GetElapsedTime(lastUpdate,now).TotalSeconds,1.0/120,.1);
        lastUpdate = now;
        if (smooth is HandPoint old)
        {
            double distance = Math.Sqrt(Math.Pow(target.X-old.X,2)+Math.Pow(target.Y-old.Y,2));
            double tau = .065 - .05*Math.Clamp(distance/.06,0,1);
            double alpha = 1-Math.Exp(-dt/tau);
            smooth = new(old.X+(target.X-old.X)*alpha,old.Y+(target.Y-old.Y)*alpha);
        }
        else smooth = target;
        mouse.Move(smooth.Value.X,smooth.Value.Y);
        if (nextHeld && !held) mouse.Down();
        if (!nextHeld && held) mouse.Up();
        held = nextHeld;
    }
}
