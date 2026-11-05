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
    private HandPoint? smooth;
    private double? scrollY;
    public void Reset()
    {
        if (held) mouse.Up();
        held = false; smooth = null; scrollY = null;
    }
    public void Process(HandPoint[] p)
    {
        if (p.Length != 21 || p.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y))) { Reset(); return; }
        double Distance(int a, int b) => Math.Sqrt(Math.Pow(p[a].X-p[b].X,2)+Math.Pow(p[a].Y-p[b].Y,2));
        var pinch = Distance(4,8) / Math.Max(Distance(0,9), .025);
        bool nextHeld = pinch < (held ? .38 : .25);
        bool scrolling = !nextHeld && p[8].Y < p[6].Y && p[12].Y < p[10].Y && p[16].Y > p[14].Y;
        if (scrolling)
        {
            if (held) { mouse.Up(); held = false; }
            double y = (p[8].Y+p[12].Y)/2;
            if (scrollY is double previous && Math.Abs(y-previous) > .025)
            { mouse.Scroll(y < previous ? 120 : -120); scrollY = y; }
            scrollY ??= y; smooth = null; return;
        }
        scrollY = null;
        // Mirror camera coordinates and reserve margins so the whole screen is reachable.
        var target = new HandPoint(Math.Clamp((1-p[8].X-.12)/.76,0,1), Math.Clamp((p[8].Y-.12)/.76,0,1));
        smooth = smooth is HandPoint old ? new(old.X+(target.X-old.X)*.3,old.Y+(target.Y-old.Y)*.3) : target;
        mouse.Move(smooth.Value.X,smooth.Value.Y);
        if (nextHeld && !held) mouse.Down();
        if (!nextHeld && held) mouse.Up();
        held = nextHeld;
    }
}
