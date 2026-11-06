using HandPilot;

MovementChecks.Run();
RestingChecks.Run();

public static class MovementChecks
{
    private static int assertions;
    private static void Check(bool ok, string message)
    {
        assertions++;
        if (!ok) throw new Exception(message);
    }
    private static double Pixels(HandPoint a, HandPoint b) => Math.Sqrt(Math.Pow((a.X - b.X) * 1920, 2) + Math.Pow((a.Y - b.Y) * 1080, 2));
    public static HandPoint[] Hand(double mirroredX, double y, bool closed = false)
    {
        double x = 1 - mirroredX;
        var p = Enumerable.Repeat(new HandPoint(x, y), 21).ToArray();
        p[0] = new(x, y + .09);
        double[] xs = [-.06, -.02, .02, .06], ys = [-.01, -.03, -.025, -.025];
        for (int i = 0; i < 4; i++)
        {
            int tip = 8 + i * 4;
            p[tip - 3] = new(x + xs[i], y + ys[i]);
            p[tip - 2] = new(x + xs[i], y - .08);
            p[tip - 1] = new(x + xs[i], y + (closed ? .02 : -.13));
            p[tip] = new(x + xs[i], y + (closed ? .035 : -.18));
        }
        p[1] = new(x - .03, y + .04);
        p[2] = new(x - .07, y + .01);
        p[3] = new(x - .10, y - .02);
        p[4] = new(x - .12, y - .04);
        return p;
    }
    private static HandPoint[] AtTarget(CameraBounds bounds, HandPoint target, bool closed = false) =>
        Hand(bounds.Left + target.X * bounds.Width, bounds.Top + target.Y * bounds.Height, closed);

    public static void Run()
    {
        var area = CameraBounds.Default;
        Check(area.IsValid, "Default workspace must be valid");
        foreach (var invalidArea in new[] { new CameraBounds(double.NaN, .2, .8, .8), new CameraBounds(.5, .3, .4, .7), new CameraBounds(-.1, .2, .8, .8), new CameraBounds(.4, .4, .45, .9) })
            Check(!invalidArea.IsValid, "Invalid camera bounds must be rejected");
        Check(new CameraBounds(.2, .25, .8, .85).Map(new(.8, .25)) == new HandPoint(0, 0), "Mirrored workspace maps raw right to screen left");
        Check(new CameraBounds(.2, .25, .8, .85).Map(new(.2, .85)) == new HandPoint(1, 1), "Workspace maps raw left to screen right");
        var mouse = new CheckMouse();
        var engine = new GestureEngine(mouse);
        engine.Start();
        Check(mouse.Moves == 0, "Enable must not move before a hand sample");
        double time = 1;
        engine.Process(AtTarget(area, new(.5, .5)), time);
        Check(Pixels(mouse.Last, new(.5, .5)) < 1e-8, "Open palm uses absolute mirrored position");
        foreach (var corner in new[] { new HandPoint(0, 0), new HandPoint(1, 0), new HandPoint(0, 1), new HandPoint(1, 1) })
        {
            var hand = AtTarget(area, corner);
            Check(hand.All(p => p.X >= 0 && p.X <= 1 && p.Y >= 0 && p.Y <= 1), "Corner fixture must keep whole hand visible");
            for (int i = 0; i < 6; i++) engine.Process(hand, time += 1.0 / 30);
            Check(mouse.Last == corner, "Each calibrated corner must reach the exact desktop corner");
        }
        // An asymmetric learned workspace still maps all corners, not merely the default rectangle.
        engine.Bounds = new(.33, .37, .67, .74);
        foreach (var corner in new[] { new HandPoint(0, 0), new HandPoint(1, 0), new HandPoint(0, 1), new HandPoint(1, 1) })
        {
            for (int i = 0; i < 6; i++) engine.Process(AtTarget(engine.Bounds, corner), time += 1.0 / 60);
            Check(mouse.Last == corner, "Learned reach must reach all four screen corners");
        }

        var steadyMouse = new CheckMouse();
        var steady = new GestureEngine(steadyMouse);
        steady.Start();
        steady.Process(AtTarget(area, new(.5, .5)), 1);
        double excursion = 0;
        for (int i = 1; i <= 180; i++)
        {
            var point = new HandPoint(.5 + Math.Sin(i * 2.41) * .8 / 1920, .5 + Math.Cos(i * 1.89) * .8 / 1080);
            steady.Process(AtTarget(area, point), 1 + i / 60.0);
            excursion = Math.Max(excursion, Pixels(steadyMouse.Last, new(.5, .5)));
        }
        Check(excursion < .01, "Subpixel tremor must not move a resting pointer");
        double largerExcursion = 0;
        for (int i = 1; i <= 180; i++)
        {
            var point = new HandPoint(.5 + Math.Sin(i * 2.41) * 2 / 1920, .5 + Math.Cos(i * 1.89) * 2 / 1080);
            steady.Process(AtTarget(area, point), 4 + i / 60.0);
            largerExcursion = Math.Max(largerExcursion, Pixels(steadyMouse.Last, new(.5, .5)));
        }
        Check(largerExcursion < 1.8, "Two-pixel camera tremor must be attenuated at rest");

        foreach (double fps in new[] { 30.0, 60.0 })
        {
            var output = new CheckMouse();
            var quick = new GestureEngine(output);
            quick.Start();
            quick.Process(AtTarget(area, new(.2, .4)), 1);
            quick.Process(AtTarget(area, new(.8, .6)), 1 + 1 / fps);
            quick.Process(AtTarget(area, new(.8, .6)), 1 + 2 / fps);
            Check(Pixels(output.Last, new(.8, .6)) < 2, "Deliberate travel must settle within two samples at 30/60 FPS");
        }
        HandPoint PathAt(double fps)
        {
            var output = new CheckMouse(); var session = new GestureEngine(output);
            session.Start();
            for (int i = 0; i <= fps; i++) session.Process(AtTarget(area, new(.2 + .6 * i / fps, .5)), 1 + i / fps);
            return output.Last;
        }
        Check(Pixels(PathAt(30), PathAt(60)) < 12, "Equivalent 30/60 FPS paths should finish within 12 pixels");

        var recoveryMouse = new CheckMouse(); var recovery = new GestureEngine(recoveryMouse);
        recovery.Start(); recovery.Process(AtTarget(area, new(.2, .3)), 1);
        var beforeLoss = recoveryMouse.Last;
        recovery.LoseTracking(); int lossMoves = recoveryMouse.Moves;
        Check(recovery.PointerPosition == beforeLoss && recoveryMouse.Moves == lossMoves, "Loss preserves position without emitting movement");
        recovery.Process(AtTarget(area, new(.8, .7)), 2);
        Check(recoveryMouse.Last == beforeLoss, "First reacquired sample must not teleport the cursor");
        for (int i = 1; i <= 5; i++) recovery.Process(AtTarget(area, new(.8, .7)), 2 + i / 30.0);
        Check(Pixels(recoveryMouse.Last, new(.8, .7)) < .01, "Recovery must finish at the absolute target within 150ms");
        var priorGap = recoveryMouse.Last;
        recovery.Process(AtTarget(area, new(.1, .2)), 2.5);
        Check(recoveryMouse.Last == priorGap, "An unannounced sample gap must start recovery rather than teleporting");
        recovery.Start(new(.1, .1));
        recovery.Process(AtTarget(area, new(.9, .9)), 3);
        Check(recoveryMouse.Last == new HandPoint(.1, .1), "Enable seeded with Windows position must start there");
        int validMoves = recoveryMouse.Moves;
        recovery.Process(AtTarget(area, new(.2, .2)), 3);
        recovery.Process(AtTarget(area, new(.3, .3)), 2.9);
        Check(recoveryMouse.Moves == validMoves, "Duplicate and out-of-order samples must not emit input");
        recovery.Process([], 3.1);
        var invalid = Hand(.5, .5); invalid[8] = new(double.NaN, .2); recovery.Process(invalid, 3.2);
        var outside = Hand(.5, .5); outside[4] = new(1.3, .2); recovery.Process(outside, 3.3);
        Check(recoveryMouse.Moves == validMoves, "Absent, nonfinite and wildly off-camera hands must not move");

        var poseMouse = new CheckMouse(); var poses = new GestureEngine(poseMouse);
        poses.Start(); poses.Process(AtTarget(area, new(.2, .5)), 1);
        for (int i = 1; i <= 5; i++) poses.Process(AtTarget(area, new(.8, .5), true), 1 + i / 30.0);
        Check(Pixels(poseMouse.Last, new(.8, .5)) < 2, "Folded fingers must not freeze movement-only control");
        Check(poseMouse.Downs == 0 && poseMouse.Ups == 0 && poseMouse.DoubleClicks == 0 && poseMouse.Wheel == 0,
            "Movement trial must never emit clicks, drags or scrolling");

        // Preserve input-release guarantees while the gesture path awaits the later clicking milestone.
        var clickMouse = new CheckMouse(); var clicks = new GestureEngine(clickMouse) { MovementOnly = false };
        clicks.Start(); clicks.Process(Hand(.5, .5), 1); clicks.Process(Hand(.5, .5, true), 1.03);
        Check(clickMouse.Downs == 1, "Legacy fist closes one mouse button");
        clicks.LoseTracking(); Check(clickMouse.Ups == 1, "Loss releases a held mouse button immediately");
        clicks.Process(Hand(.5, .5, true), 2); Check(clickMouse.Downs == 1, "Reacquiring a fist cannot press until an open hand arms input");
        clicks.Process(Hand(.5, .5), 2.03); clicks.Process(Hand(.5, .5, true), 2.06); clicks.Reset();
        Check(clickMouse.Ups == 2, "Pause releases a held mouse button");
        var doubleMouse = new CheckMouse(); var doubles = new GestureEngine(doubleMouse) { MovementOnly = false, DoubleClickEnabled = true };
        doubles.Start(); doubles.Process(Hand(.5, .5), 1); doubles.Process(Hand(.5, .5, true), 1.03); doubles.Process(Hand(.5, .5), 1.06);
        Check(doubleMouse.DoubleClicks == 1 && doubleMouse.Downs == 0, "Legacy double click fires once on release");
        doubles.Process(Hand(.5, .5, true), 1.09); doubles.LoseTracking(); doubles.Process(Hand(.5, .5), 2);
        Check(doubleMouse.DoubleClicks == 1, "Tracking loss cancels pending double click");
        doubles.Process(Hand(.5, .5, true), 2.03); doubles.Process(Hand(.6, .5, true), 2.06); doubles.Process(Hand(.6, .5), 2.09);
        Check(doubleMouse.Downs == 1 && doubleMouse.Ups == 1 && doubleMouse.DoubleClicks == 1, "Legacy deliberate drag releases without double-clicking");

        Console.WriteLine($"Passed movement checks ({assertions} assertions): four corners, calibrated reach, resting jitter, 30/60 FPS response, bounded recovery, malformed/old samples, movement-only poses, and input-release guarantees.");
    }
}

public sealed class CheckMouse : IMouseOutput
{
    public int Downs, Ups, Wheel, Moves, DoubleClicks;
    public HandPoint Last;
    public List<HandPoint> Positions { get; } = [];
    public void Move(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1) throw new Exception("Out-of-bounds output");
        Moves++; Last = new(x, y); Positions.Add(Last);
    }
    public void Down() => Downs++;
    public void Up() => Ups++;
    public void Scroll(int delta) => Wheel += delta;
    public void DoubleClick() => DoubleClicks++;
}
