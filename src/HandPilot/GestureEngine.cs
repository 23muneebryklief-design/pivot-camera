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
    public bool SmoothingEnabled { get; set; }
    private HandPoint? pressPalm;
    private bool dragging;
    private bool held;
    private bool armed;
    private HandPoint? smooth;
    private long lastUpdate;
    public void Start() => Reset();
    private double? scrollY;
    public void Reset()
    {
        if (held) mouse.Up();
        held = false; armed = false; smooth = null; scrollY = null; lastUpdate = 0; pressPalm = null; dragging = false;
    }
    private static double SnapEdge(double value,double target) => (target == 0 || target == 1) && Math.Abs(value-target)<.001 ? target : value;
    public void Process(HandPoint[] p)
    {
        if (p.Length != 21 || p.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y))) { Reset(); return; }
        var palm = new HandPoint((p[0].X+p[5].X+p[9].X+p[13].X+p[17].X)/5, (p[0].Y+p[5].Y+p[9].Y+p[13].Y+p[17].Y)/5);
        double Distance(int a, int b) => Math.Sqrt(Math.Pow(p[a].X-p[b].X,2)+Math.Pow(p[a].Y-p[b].Y,2));
        // Compare fingertips to their middle joints relative to the wrist.
        // A wider release threshold avoids repeated clicks near the fist boundary.
        bool Closed(int tip) => Distance(0,tip) < Math.Max(Distance(0,tip-2),.025)*(held ? 1.05 : .9);
        int folded = new[]{8,12,16,20}.Count(Closed);
        bool fist = folded == 4;
        if (!fist) armed = true;
        bool nextHeld = armed && fist;
        bool scrolling = !nextHeld && p[8].Y < p[6].Y && p[12].Y < p[10].Y && p[16].Y > p[14].Y;
        if (scrolling)
        {
            if (held) { mouse.Up(); held = false; dragging=false; pressPalm=null; }
            double y = (p[8].Y+p[12].Y)/2;
            if (scrollY is double previous && Math.Abs(y-previous) > .025)
            { mouse.Scroll(y < previous ? 120 : -120); scrollY = y; }
            scrollY ??= y; return;
        }
        scrollY = null;
        // Keep the pointer on its target while fingers fold; require deliberate palm travel to drag.
        if (nextHeld && !held)
        {
            mouse.Down(); held=true; dragging=false; pressPalm=palm;
           
        }
        else if (!nextHeld && held)
        {
            mouse.Up(); held=false; dragging=false; pressPalm=null;
            return;
        }
        if (held && !dragging && pressPalm is HandPoint pressed)
        {
            if (Math.Sqrt(Math.Pow(palm.X-pressed.X,2)+Math.Pow(palm.Y-pressed.Y,2)) < .03) return;
            dragging=true;
        }
        if (!held && folded>=2)
        {
            return;
        }
        // Match the mirrored preview: left/right and top/bottom map directly to the desktop.
        var target = new HandPoint(Math.Clamp(1-palm.X,0,1), Math.Clamp(palm.Y,0,1));
        // Time-based smoothing keeps response similar at different camera frame rates.
        // Follow deliberate motion quickly while filtering small tracking jitter.
        var now = Stopwatch.GetTimestamp();
        double dt = lastUpdate == 0 ? 1.0/30 : Math.Clamp(Stopwatch.GetElapsedTime(lastUpdate,now).TotalSeconds,1.0/120,.1);
        lastUpdate = now;
        if (SmoothingEnabled && smooth is HandPoint old)
        {
            double distance = Math.Sqrt(Math.Pow(target.X-old.X,2)+Math.Pow(target.Y-old.Y,2));
            double tau = .03 - .024*Math.Clamp(distance/.06,0,1);
            double alpha = 1-Math.Exp(-dt/tau);
            smooth = new(old.X+(target.X-old.X)*alpha,old.Y+(target.Y-old.Y)*alpha);
        }
        else smooth = target;
        // Settle exactly at screen edges rather than approaching them indefinitely.
        smooth = new(SnapEdge(smooth.Value.X,target.X), SnapEdge(smooth.Value.Y,target.Y));
        mouse.Move(smooth.Value.X,smooth.Value.Y);

    }
}
