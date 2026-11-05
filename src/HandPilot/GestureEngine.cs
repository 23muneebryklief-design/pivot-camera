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
    private HandPoint? origin;
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
        held = false; smooth = null; origin = null; scrollY = null;
    }
    public void Process(HandPoint[] p)
    {
        if (p.Length != 21 || p.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y))) { Reset(); return; }
        origin ??= p[8];
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
            scrollY ??= y; return;
        }
        scrollY = null;
        // Treat the first fingertip position as screen centre; mirror horizontal motion.
        var target = new HandPoint(Math.Clamp(.5+(origin.Value.X-p[8].X)/.76,0,1), Math.Clamp(.5+(p[8].Y-origin.Value.Y)/.76,0,1));
        smooth = smooth is HandPoint old ? new(old.X+(target.X-old.X)*.3,old.Y+(target.Y-old.Y)*.3) : target;
        mouse.Move(smooth.Value.X,smooth.Value.Y);
        if (nextHeld && !held) mouse.Down();
        if (!nextHeld && held) mouse.Up();
        held = nextHeld;
    }
}
