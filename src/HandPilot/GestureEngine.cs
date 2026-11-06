using System.Diagnostics;

namespace HandPilot;

public readonly record struct HandPoint(double X, double Y);

/// <summary>A comfortable palm workspace in the mirrored camera preview.</summary>
public readonly record struct CameraBounds(double Left, double Top, double Right, double Bottom)
{
    public static CameraBounds Default => new(.30, .32, .70, .72);
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Right) && double.IsFinite(Bottom) &&
        Left >= 0 && Top >= 0 && Right <= 1 && Bottom <= 1 &&
        Width >= .10 && Height >= .10;

    public HandPoint Map(HandPoint rawPalm)
    {
        var area = IsValid ? this : Default;
        return new(Axis((1 - rawPalm.X - area.Left) / area.Width),
            Axis((rawPalm.Y - area.Top) / area.Height));
    }

    private static double Axis(double value) => value <= 1e-9 ? 0 : value >= 1 - 1e-9 ? 1 : value;
}

public interface IMouseOutput
{
    void Move(double x, double y);
    void Down();
    void Up();
    void Scroll(int delta);
    void DoubleClick();
}

public sealed class GestureEngine(IMouseOutput mouse)
{
    private readonly PointerMotion motion = new();
    private bool buttonPressed, doubleClickForPress, dragging, held, armed;
    private HandPoint? pressPalm;
    private double? scrollY, lastSampleTime;

    public CameraBounds Bounds { get; set; } = CameraBounds.Default;
    public bool MovementOnly { get; set; } = true;
    public bool DoubleClickEnabled { get; set; }
    public double ScreenWidth { get; set; } = 1920;
    public double ScreenHeight { get; set; } = 1080;
    public HandPoint? PointerPosition { get; private set; }

    public void Start(HandPoint? currentPointer = null)
    {
        Reset();
        motion.Reset(currentPointer);
        PointerPosition = currentPointer;
    }

    public void Reset()
    {
        CancelGesture();
        lastSampleTime = null;
        motion.Reset();
        PointerPosition = null;
    }

    /// <summary>Release input immediately, keeping the last position for a bounded recovery.</summary>
    public void LoseTracking()
    {
        CancelGesture();
        lastSampleTime = null;
        motion.Suspend();
    }

    private void CancelGesture()
    {
        if (buttonPressed) mouse.Up();
        buttonPressed = false;
        held = armed = dragging = doubleClickForPress = false;
        pressPalm = null;
        scrollY = null;
    }

    public static bool TryGetPalm(HandPoint[]? points, out HandPoint palm)
    {
        palm = default;
        if (points is not { Length: 21 } || points.Any(p => !double.IsFinite(p.X) ||
            !double.IsFinite(p.Y) || p.X < -.05 || p.X > 1.05 || p.Y < -.05 || p.Y > 1.05))
            return false;
        palm = new((points[0].X + points[5].X + points[9].X + points[13].X + points[17].X) / 5,
            (points[0].Y + points[5].Y + points[9].Y + points[13].Y + points[17].Y) / 5);
        return true;
    }

    public void Process(HandPoint[] points) => Process(points, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    /// <param name="sampleTimeSeconds">Monotonic time of the captured sample, in seconds.</param>
    public void Process(HandPoint[] points, double sampleTimeSeconds)
    {
        if (!TryGetPalm(points, out var palm) || !double.IsFinite(sampleTimeSeconds))
        {
            LoseTracking();
            return;
        }
        // Duplicate/out-of-order samples must never move or trigger an action.
        if (lastSampleTime is double previous && sampleTimeSeconds <= previous) return;
        if (lastSampleTime is double last && sampleTimeSeconds - last > .15) LoseTracking();
        lastSampleTime = sampleTimeSeconds;

        if (MovementOnly)
        {
            // Finger pose has no say in pointer movement during this milestone.
            // Cancel any previous action if the session switched from gesture mode.
            CancelGesture();
            Move(palm, sampleTimeSeconds);
            return;
        }

        double Distance(int a, int b) => Math.Sqrt(Math.Pow(points[a].X - points[b].X, 2) + Math.Pow(points[a].Y - points[b].Y, 2));
        bool Closed(int tip) => Distance(0, tip) < Math.Max(Distance(0, tip - 2), .025) * (held ? 1.05 : .9);
        int folded = new[] { 8, 12, 16, 20 }.Count(Closed);
        bool fist = folded == 4;
        if (!fist) armed = true;
        bool nextHeld = armed && fist;
        bool scrolling = !nextHeld && points[8].Y < points[6].Y && points[12].Y < points[10].Y && points[16].Y > points[14].Y;
        if (scrolling)
        {
            if (held)
            {
                if (buttonPressed) mouse.Up();
                buttonPressed = held = dragging = false;
                pressPalm = null;
            }
            double y = (points[8].Y + points[12].Y) / 2;
            if (scrollY is double previousY && Math.Abs(y - previousY) > .025)
            {
                mouse.Scroll(y < previousY ? 120 : -120);
                scrollY = y;
            }
            scrollY ??= y;
            return;
        }
        scrollY = null;
        if (nextHeld && !held)
        {
            doubleClickForPress = DoubleClickEnabled;
            if (!doubleClickForPress) { mouse.Down(); buttonPressed = true; }
            held = true;
            dragging = false;
            pressPalm = palm;
        }
        else if (!nextHeld && held)
        {
            if (buttonPressed) mouse.Up();
            else if (doubleClickForPress && !dragging) mouse.DoubleClick();
            buttonPressed = held = dragging = false;
            pressPalm = null;
            return;
        }
        if (held && !dragging && pressPalm is HandPoint pressed)
        {
            if (Math.Sqrt(Math.Pow(palm.X - pressed.X, 2) + Math.Pow(palm.Y - pressed.Y, 2)) < .03) return;
            if (!buttonPressed) { mouse.Down(); buttonPressed = true; }
            dragging = true;
        }
        if (!held && folded >= 2) return;
        Move(palm, sampleTimeSeconds);
    }

    private void Move(HandPoint palm, double sampleTimeSeconds)
    {
        var point = motion.Update(Bounds.Map(palm), sampleTimeSeconds, ScreenWidth, ScreenHeight);
        mouse.Move(point.X, point.Y);
        PointerPosition = point;
    }
}
