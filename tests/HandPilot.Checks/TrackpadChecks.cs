using HandPilot;

/// <summary>Trackpad mode: steady at rest, precise when slow, fast when flicked, clean pinch clicks.</summary>
public static class TrackpadChecks
{
    private static int assertions;
    private const double Fps = 30;
    private static void Check(bool ok, string message)
    {
        assertions++;
        if (!ok) throw new Exception(message);
    }

    private static double Pixels(HandPoint a, HandPoint b) => Math.Sqrt(Math.Pow((a.X - b.X) * 1920, 2) + Math.Pow((a.Y - b.Y) * 1080, 2));

    private static HandPoint[] Hand(double mirroredX, double y, bool closed = false, bool pinch = false, double noise = 0, int frame = 0)
    {
        var hand = MovementChecks.Hand(mirroredX, y, closed);
        if (pinch) hand[4] = new(hand[8].X + .006, hand[8].Y + .006);
        if (noise == 0) return hand;
        // Deterministic per-landmark tracker jitter.
        return hand.Select((p, i) => new HandPoint(
            p.X + noise * Math.Sin(frame * 2.399 + i * 1.7) * Math.Cos(frame * .77 + i),
            p.Y + noise * Math.Cos(frame * 1.913 + i * 2.3) * Math.Sin(frame * 1.31 + i))).ToArray();
    }

    private static (GestureEngine Engine, CheckMouse Mouse) Create()
    {
        var mouse = new CheckMouse();
        var engine = new GestureEngine(mouse) { Mode = PointerMode.Trackpad };
        engine.Start(new(.5, .5));
        return (engine, mouse);
    }

    /// <summary>Moves the hand linearly between two mirrored camera positions and returns the time after the last frame.</summary>
    private static double Glide(GestureEngine engine, double time, double fromX, double toX, double seconds, double y = .5, bool pinch = false, bool closed = false)
    {
        int frames = Math.Max(1, (int)Math.Round(seconds * Fps));
        for (int i = 1; i <= frames; i++)
            engine.Process(Hand(fromX + (toX - fromX) * i / frames, y, closed, pinch), time += 1 / Fps);
        return time;
    }

    private static double Hold(GestureEngine engine, double time, double x, double seconds, double y = .5, bool pinch = false, bool closed = false)
    {
        for (int i = 0; i < seconds * Fps; i++) engine.Process(Hand(x, y, closed, pinch), time += 1 / Fps);
        return time;
    }

    public static void Run()
    {
        // 1. Resting tracker jitter must not wander the cursor.
        {
            var (engine, mouse) = Create();
            double time = 1;
            var positions = new List<HandPoint>();
            for (int frame = 0; frame < 3 * Fps; frame++)
            {
                engine.Process(Hand(.5, .5, noise: .003, frame: frame), time += 1 / Fps);
                if (frame > 9 && engine.PointerPosition is HandPoint p) positions.Add(p);
            }
            double span = Math.Max((positions.Max(p => p.X) - positions.Min(p => p.X)) * 1920, (positions.Max(p => p.Y) - positions.Min(p => p.Y)) * 1080);
            Check(span <= 3, $"Resting jitter of ±0.3% of the image must keep the cursor within 3px; span={span:0.00}px");
            Check(mouse.Downs == 0, "Resting jitter must never click");
        }

        // 2. Slow travel is precise; the same hand distance done fast travels much farther.
        double slow, fast;
        {
            var (engine, _) = Create();
            double time = Hold(engine, 1, .5, .3);
            var before = engine.PointerPosition!.Value;
            time = Glide(engine, time, .5, .53, 1);
            Hold(engine, time, .53, .3);
            slow = Pixels(engine.PointerPosition!.Value, before);
            Check(engine.PointerPosition!.Value.X > before.X, "Moving the hand right must move the cursor right");
            Check(slow >= 15 && slow <= 250, $"A slow 3% hand glide should move a modest, precise distance; moved {slow:0}px");
        }
        {
            var (engine, _) = Create();
            double time = Hold(engine, 1, .5, .3);
            var before = engine.PointerPosition!.Value;
            time = Glide(engine, time, .5, .53, .1);
            Hold(engine, time, .53, .3);
            fast = Pixels(engine.PointerPosition!.Value, before);
        }
        Check(fast > slow * 2, $"Pointer acceleration: a fast glide ({fast:0}px) must travel well beyond the slow one ({slow:0}px)");
        {
            var (engine, mouse) = Create();
            double time = Hold(engine, 1, .5, .2);
            time = Glide(engine, time, .5, .8, .25);
            Check(mouse.Positions.Count > 0 && mouse.Last.X > .7, $"A quick sweep should cross most of the screen; reached x={mouse.Last.X:0.00}");
            for (int i = 0; i < 6; i++) { time = Glide(engine, time, .8, .5, .25, closed: true); time = Glide(engine, time, .5, .8, .25); }
            Check(mouse.Last.X == 1, "Repeated sweeps clamp at the screen edge without leaving the desktop");
        }

        // 3. A closed hand repositions without moving the cursor, and reopening does not jump.
        {
            var (engine, mouse) = Create();
            double time = Hold(engine, 1, .5, .3);
            int moves = mouse.Moves;
            var before = engine.PointerPosition!.Value;
            time = Hold(engine, time, .5, .1, closed: true);
            time = Glide(engine, time, .5, .3, .4, closed: true);
            Check(mouse.Moves == moves && engine.Gesture == HandGesture.Clutched, "Clutched hand travel must not move the cursor");
            Hold(engine, time, .3, .4);
            Check(Pixels(engine.PointerPosition!.Value, before) < 2, "Opening the hand again must not jump the cursor");
            Check(mouse.Downs == 0 && mouse.Ups == 0, "Clutching must never click");
        }

        // 4. A pinch clicks once in place; releasing does not nudge the cursor.
        {
            var (engine, mouse) = Create();
            double time = Hold(engine, 1, .5, .3);
            var before = engine.PointerPosition!.Value;
            time = Hold(engine, time, .5, .3, pinch: true);
            Check(mouse.Downs == 1 && mouse.Ups == 0, "Pinch presses the button once");
            Check(engine.Gesture == HandGesture.Pinching, "Short pinch reports a click");
            Check(Pixels(engine.PointerPosition!.Value, before) < 1, "Cursor must stay locked while pinched");
            time = Hold(engine, time, .5, .3);
            Check(mouse.Ups == 1 && mouse.Downs == 1, "Opening the pinch releases once");
            Check(Pixels(engine.PointerPosition!.Value, before) < 1, "Releasing the pinch must not nudge the cursor");
            // Small hand drift during a pinch stays a click, not a drag.
            time = Hold(engine, time, .5, .1, pinch: true);
            time = Glide(engine, time, .5, .51, .2, pinch: true);
            Hold(engine, time, .51, .2);
            Check(mouse.Downs == 2 && mouse.Ups == 2 && Pixels(engine.PointerPosition!.Value, before) < 1, "Slight drift while pinched must not drag");
        }

        // 5. Pinching while moving clicks where the cursor was aimed just before the pinch.
        {
            var (engine, mouse) = Create();
            double time = Hold(engine, 1, .5, .3);
            var trail = new List<HandPoint>();
            for (int i = 1; i <= 15; i++)
            {
                engine.Process(Hand(.5 + .002 * i, .5), time += 1 / Fps);
                trail.Add(engine.PointerPosition!.Value);
            }
            engine.Process(Hand(.5 + .002 * 16, .5, pinch: true), time += 1 / Fps);
            Check(mouse.Downs == 1, "Pinch while moving still clicks");
            var expected = trail[^4]; // 4 frames = 133 ms ≥ 120 ms rollback
            Check(Pixels(mouse.Last, expected) < 1e-6, "Click must land at the aimed position from just before the pinch");
            Check(mouse.Last.X <= trail[^1].X, "Rollback never overshoots past the current position");
        }

        // 6. Pinch and travel drags with the button held; release lets go.
        {
            var (engine, mouse) = Create();
            double time = Hold(engine, 1, .5, .3);
            time = Hold(engine, time, .5, .1, pinch: true);
            var pressedAt = engine.PointerPosition!.Value;
            time = Glide(engine, time, .5, .62, .5, pinch: true);
            Check(engine.Gesture == HandGesture.Dragging && mouse.Downs == 1 && mouse.Ups == 0, "Pinch and move must drag with the button held");
            Check(Pixels(engine.PointerPosition!.Value, pressedAt) > 30, "Dragging moves the cursor");
            Hold(engine, time, .62, .2);
            Check(mouse.Ups == 1, "Opening the pinch ends the drag");
        }

        // 7. Safety: loss releases the button; a returning pinched hand cannot click until it opens.
        {
            var (engine, mouse) = Create();
            double time = Hold(engine, 1, .5, .3);
            time = Hold(engine, time, .5, .1, pinch: true);
            engine.LoseTracking();
            Check(mouse.Ups == 1, "Tracking loss releases a held pinch immediately");
            time = Hold(engine, time + 1, .5, .3, pinch: true);
            Check(mouse.Downs == 1, "A hand that returns already pinched must not click");
            time = Hold(engine, time, .5, .1);
            Hold(engine, time, .5, .1, pinch: true);
            Check(mouse.Downs == 2, "Open then pinch clicks normally after reacquiring");
            engine.Reset();
            Check(mouse.Ups == 2, "Pause releases a held pinch");
            engine.Start(new(.5, .5));
            Hold(engine, 10, .5, .3, closed: true, pinch: true);
            Check(mouse.Downs == 2, "A closed fist (thumb tucked near the index) is a clutch, never a click");
        }

        // 8. The pointer is sized by hand units, so moving closer to the camera does not change speed much.
        {
            double Travel(double handScale)
            {
                var (engine, _) = Create();
                HandPoint[] Scaled(double x) => MovementChecks.Hand(.5, .5).Select(p => new HandPoint(.5 + (p.X - .5) * handScale + (x - .5), .5 + (p.Y - .5) * handScale)).ToArray();
                double t = 1;
                for (int i = 0; i < 9; i++) engine.Process(Scaled(.5), t += 1 / Fps);
                var start = engine.PointerPosition!.Value;
                for (int i = 1; i <= 15; i++) engine.Process(Scaled(.5 - .06 * handScale * i / 15), t += 1 / Fps);
                for (int i = 0; i < 9; i++) engine.Process(Scaled(.5 - .06 * handScale), t += 1 / Fps);
                return Pixels(engine.PointerPosition!.Value, start);
            }
            double near = Travel(1.3), far = Travel(.8);
            Check(Math.Abs(near - far) / Math.Max(near, far) < .2, $"Equal hand-sized motions should move similar distances near ({near:0}px) and far ({far:0}px)");
            Console.WriteLine($"Trackpad metrics: 3% glide slow {slow:0}px vs fast {fast:0}px; same hand-relative motion near {near:0}px vs far {far:0}px.");
        }

        Console.WriteLine($"Passed trackpad checks ({assertions} assertions): resting jitter, precision vs acceleration, edge clamp, clutch, pinch click/rollback/drag, and input-release safety.");
    }
}
