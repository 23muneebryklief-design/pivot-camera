using HandPilot;

/// <summary>Behavioural regressions for resting-hand vibration and intentional fine travel.</summary>
public static class RestingChecks
{
    private static int assertions;
    private static readonly List<string> failures = [];
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) failures.Add(message);
    }

    private static double Distance(HandPoint a, HandPoint b, double width, double height) =>
        Math.Sqrt(Math.Pow((a.X - b.X) * width, 2) + Math.Pow((a.Y - b.Y) * height, 2));

    private static double Span(IEnumerable<HandPoint> positions, double width, double height)
    {
        var points = positions.ToArray();
        return Math.Max((points.Max(p => p.X) - points.Min(p => p.X)) * width,
            (points.Max(p => p.Y) - points.Min(p => p.Y)) * height);
    }

    private static HandPoint Noise(HandPoint centre, double seconds, double width, double height) => new(
        centre.X + (6 * Math.Sin(2 * Math.PI * 4.3 * seconds) + 2 * Math.Sin(2 * Math.PI * 7.1 * seconds)) / width,
        centre.Y + (4 * Math.Cos(2 * Math.PI * 5.7 * seconds) + 3 * Math.Sin(2 * Math.PI * 8.3 * seconds)) / height);

    private static double Warm(PointerMotion motion, HandPoint point, double fps, double width, double height, double time = 1)
    {
        for (int i = 0; i <= fps; i++) motion.Update(point, time + i / fps, width, height);
        return time + 1;
    }

    public static void Run()
    {
        double maximumRestSpan = 0, maximumFineStart = 0, maximumFineLag = 0;
        double noisyFineStart = 0, noisyFineLag = 0;
        foreach (double fps in new[] { 20.0, 30.0, 60.0 })
        foreach (double width in new[] { 1280.0, 1920.0, 3840.0 })
        {
            double height = width * 9 / 16;
            var motion = new PointerMotion();
            var centre = new HandPoint(.5, .5);
            double time = Warm(motion, centre, fps, width, height);
            var resting = new List<HandPoint>();
            for (int i = 1; i <= fps * 4; i++)
            {
                double elapsed = i / fps;
                var position = motion.Update(Noise(centre, elapsed, width, height), time + elapsed, width, height);
                if (elapsed >= .4) resting.Add(position);
            }
            double span = Span(resting, width, height);
            maximumRestSpan = Math.Max(maximumRestSpan, span);
            Check(span <= 2, $"Resting 5–10px tracker jitter must stay within 2px at {fps}FPS/{width}px; span={span:0.00}px");

            // A one- or two-frame involuntary twitch must not drag the resting anchor away.
            time += 4;
            var anchor = motion.Update(centre, time + 1 / fps, width, height);
            var pulses = new List<HandPoint> { anchor };
            for (int i = 2; i <= fps; i++)
            {
                double pulseX = i is 4 or 5 ? 12 : i == 12 ? -13 : 0;
                pulses.Add(motion.Update(new(centre.X + pulseX / width, centre.Y), time + i / fps, width, height));
            }
            double pulseSpan = Span(pulses, width, height);
            Check(pulseSpan <= 2, $"Brief sub-18px twitches must not release a resting cursor at {fps}FPS/{width}px; span={pulseSpan:0.00}px");

            // Parking must not delay a deliberate move across the screen.
            time += 1;
            var destination = new HandPoint(.8, .3);
            motion.Update(destination, time + 1 / fps, width, height);
            var arrived = motion.Update(destination, time + 2 / fps, width, height);
            Check(Distance(arrived, destination, width, height) < 2,
                $"Large movement must escape rest within two samples at {fps}FPS/{width}px");

            // Tremor immediately after travel should settle, rather than oscillate indefinitely.
            var afterTravel = new List<HandPoint>();
            for (int i = 3; i <= fps * 3; i++)
            {
                double elapsed = i / fps;
                var position = motion.Update(Noise(destination, elapsed, width, height), time + elapsed, width, height);
                if (elapsed >= .75) afterTravel.Add(position);
            }
            double settledSpan = Span(afterTravel, width, height);
            maximumRestSpan = Math.Max(maximumRestSpan, settledSpan);
            Check(settledSpan <= 2, $"After travel, resting tremor must settle within 2px at {fps}FPS/{width}px; span={settledSpan:0.00}px");
        }

        foreach (double fps in new[] { 20.0, 30.0, 60.0 })
        foreach (double speed in new[] { 5.0, 20.0 })
        {
            const double width = 1920, height = 1080;
            var motion = new PointerMotion();
            var start = new HandPoint(.4, .5);
            double time = Warm(motion, start, fps, width, height);
            double firstMovement = double.PositiveInfinity;
            HandPoint position = start, target = start;
            for (int i = 1; i <= fps * 2; i++)
            {
                double elapsed = i / fps;
                target = new(start.X + speed * elapsed / width, start.Y);
                position = motion.Update(target, time + elapsed, width, height);
                if (Distance(position, start, width, height) >= .25) firstMovement = Math.Min(firstMovement, elapsed);
            }
            double lag = Distance(position, target, width, height);
            maximumFineStart = Math.Max(maximumFineStart, firstMovement);
            maximumFineLag = Math.Max(maximumFineLag, lag);
            Check(firstMovement <= .35, $"Coherent {speed}px/s fine motion must begin within 350ms at {fps}FPS; first={firstMovement:0.000}s");
            Check(lag <= (speed == 5 ? 4 : 6), $"Fine {speed}px/s motion must not stay stuck behind a dead zone at {fps}FPS; lag={lag:0.00}px");
        }

        // Fine adjustments happen while the same tracker tremor continues. Clean ramps alone
        // cannot prove that parking releases when the observed path is still noisy.
        foreach (double fps in new[] { 20.0, 30.0, 60.0 })
        foreach (double speed in new[] { 5.0, 20.0 })
        {
            var motion = new PointerMotion();
            var origin = new HandPoint(.4, .5);
            double time = Warm(motion, origin, fps, 1920, 1080);
            double firstProgress = double.PositiveInfinity;
            HandPoint actual = origin, intended = origin;
            for (int i = 1; i <= fps * 3; i++)
            {
                double elapsed = i / fps;
                intended = new(origin.X + speed * elapsed / 1920, origin.Y);
                actual = motion.Update(Noise(intended, elapsed, 1920, 1080), time + elapsed, 1920, 1080);
                if ((actual.X - origin.X) * 1920 >= .5) firstProgress = Math.Min(firstProgress, elapsed);
            }
            double lag = Distance(actual, intended, 1920, 1080);
            noisyFineStart = Math.Max(noisyFineStart, firstProgress);
            noisyFineLag = Math.Max(noisyFineLag, lag);
            Check(firstProgress <= (speed == 5 ? 1 : .6),
                $"Fine {speed}px/s travel with ongoing tremor must release rest at {fps}FPS; first={firstProgress:0.000}s");
            Check(lag <= (speed == 5 ? 8 : 10),
                $"Fine {speed}px/s travel with ongoing tremor must follow the intended position at {fps}FPS; lag={lag:0.00}px");
            var stoppedHold = new List<HandPoint>();
            for (int i = 1; i <= fps * 3; i++)
            {
                double elapsed = i / fps;
                var restingPosition = motion.Update(Noise(intended, 3 + elapsed, 1920, 1080), time + 3 + elapsed, 1920, 1080);
                if (elapsed >= 1.25) stoppedHold.Add(restingPosition);
            }
            double stoppedSpan = Span(stoppedHold, 1920, 1080);
            maximumRestSpan = Math.Max(maximumRestSpan, stoppedSpan);
            Check(stoppedSpan <= 2,
                $"After noisy {speed}px/s travel stops, ongoing tremor must park again at {fps}FPS; span={stoppedSpan:0.00}px");
        }

        foreach (var corner in new[] { new HandPoint(0, 0), new(1, 0), new(0, 1), new(1, 1) })
        {
            var motion = new PointerMotion();
            var near = new HandPoint(corner.X == 0 ? 8.0 / 1920 : 1 - 8.0 / 1920,
                corner.Y == 0 ? 8.0 / 1080 : 1 - 8.0 / 1080);
            double time = Warm(motion, near, 30, 1920, 1080);
            HandPoint position = near;
            for (int i = 1; i <= 12; i++) position = motion.Update(corner, time + i / 30.0, 1920, 1080);
            Check(position == corner, "A parked cursor near an edge must still reach the exact corner");
            var cornerHold = new List<HandPoint>();
            for (int i = 13; i <= 90; i++)
            {
                double inward = Math.Abs(6 * Math.Sin(i * 2.3));
                var target = new HandPoint(corner.X == 0 ? inward / 1920 : 1 - inward / 1920,
                    corner.Y == 0 ? inward / 1080 : 1 - inward / 1080);
                cornerHold.Add(motion.Update(target, time + i / 30.0, 1920, 1080));
            }
            Check(Span(cornerHold, 1920, 1080) <= 2, "Resting edge jitter must not vibrate away from a reached corner");
        }

        var recovery = new PointerMotion();
        HandPoint oldCentre = new(.2, .3), newCentre = new(.8, .7);
        double clock = Warm(recovery, oldCentre, 30, 1920, 1080);
        recovery.Suspend();
        var first = recovery.Update(newCentre, clock + 1, 1920, 1080);
        Check(first == oldCentre, "Tracking recovery must preserve the old cursor on its first frame");
        HandPoint recovered = first;
        for (int i = 1; i <= 5; i++) recovered = recovery.Update(newCentre, clock + 1 + i / 30.0, 1920, 1080);
        Check(Distance(recovered, newCentre, 1920, 1080) < .01, "Recovery must clear the old rest anchor and reach its absolute target");
        var recoveredHold = new List<HandPoint>();
        for (int i = 6; i <= 90; i++)
        {
            var position = recovery.Update(Noise(newCentre, i / 30.0, 1920, 1080), clock + 1 + i / 30.0, 1920, 1080);
            if (i >= 24) recoveredHold.Add(position);
        }
        Check(Span(recoveredHold, 1920, 1080) <= 2, "Reacquired resting hand must settle at its new location");

        // A small camera-coordinate error becomes several screen pixels after quick setup.
        var narrow = new CameraBounds(.43, .43, .57, .59);
        var mouse = new CheckMouse();
        var engine = new GestureEngine(mouse) { Bounds = narrow };
        double x = (narrow.Left + narrow.Right) / 2, y = (narrow.Top + narrow.Bottom) / 2;
        engine.Start();
        for (int i = 0; i <= 30; i++) engine.Process(MovementChecks.Hand(x, y), 1 + i / 30.0);
        var amplified = new List<HandPoint>();
        for (int i = 1; i <= 120; i++)
        {
            double t = i / 30.0;
            engine.Process(MovementChecks.Hand(x + .0006 * Math.Sin(2 * Math.PI * 4.7 * t),
                y + .00075 * Math.Cos(2 * Math.PI * 6.1 * t)), 2 + t);
            if (i > 15) amplified.Add(mouse.Last);
        }
        Check(Span(amplified, 1920, 1080) <= 2, "Quick-setup gain must not amplify tiny camera tremor into cursor vibration");

        // Seeded enable blends while the camera is noisy, then must settle at the new location.
        var seeded = new PointerMotion();
        var desktopPointer = new HandPoint(.1, .2);
        var seedTarget = new HandPoint(.55, .6);
        seeded.Reset(desktopPointer);
        Check(seeded.Update(Noise(seedTarget, 0, 1920, 1080), 1, 1920, 1080) == desktopPointer,
            "Seeded enable must preserve the real cursor on its first noisy sample");
        var seededRest = new List<HandPoint>();
        HandPoint seededPosition = desktopPointer;
        for (int i = 1; i <= 120; i++)
        {
            double elapsed = i / 30.0;
            seededPosition = seeded.Update(Noise(seedTarget, elapsed, 1920, 1080), 1 + elapsed, 1920, 1080);
            if (elapsed >= .8) seededRest.Add(seededPosition);
        }
        Check(Span(seededRest, 1920, 1080) <= 2 && Distance(seededPosition, seedTarget, 1920, 1080) < 20,
            "Seeded recovery with continuing tremor must settle near the new hand rather than retain the desktop seed");

        foreach (double fps in new[] { 20.0, 30.0, 60.0 })
        {
            var diagonal = new PointerMotion();
            var origin = new HandPoint(.5, .5);
            double time = Warm(diagonal, origin, fps, 1920, 1080);
            HandPoint position = origin, target = origin;
            for (int i = 1; i <= fps * 1.5; i++)
            {
                double elapsed = i / fps;
                target = new(origin.X + 5 * elapsed / 1920, origin.Y + 5 * elapsed / 1080);
                position = diagonal.Update(target, time + elapsed, 1920, 1080);
            }
            Check(Distance(position, target, 1920, 1080) <= 4, $"Slow diagonal precision travel must remain responsive at {fps}FPS");
            var turnTarget = target;
            var turnOutput = position;
            double reverseStarted = double.PositiveInfinity;
            for (int i = 1; i <= fps * 1.5; i++)
            {
                double elapsed = i / fps;
                target = new(turnTarget.X - 5 * elapsed / 1920, turnTarget.Y - 5 * elapsed / 1080);
                position = diagonal.Update(target, time + 1.5 + elapsed, 1920, 1080);
                if ((turnOutput.X - position.X) * 1920 >= .25 && (turnOutput.Y - position.Y) * 1080 >= .25)
                    reverseStarted = Math.Min(reverseStarted, elapsed);
            }
            Check(reverseStarted <= .35 && Distance(position, target, 1920, 1080) <= 4,
                $"Reversing a slow diagonal must not leave the cursor parked at {fps}FPS; first={reverseStarted:0.000}s");
        }

        foreach (double stepPixels in new[] { 8.0, 12.0 })
        {
            var adjustment = new PointerMotion();
            var origin = new HandPoint(.5, .5);
            double time = Warm(adjustment, origin, 30, 1920, 1080);
            var target = new HandPoint(origin.X + stepPixels / 1920, origin.Y);
            double firstAdjustment = double.PositiveInfinity;
            HandPoint position = origin;
            for (int i = 1; i <= 18; i++)
            {
                position = adjustment.Update(target, time + i / 30.0, 1920, 1080);
                if (Distance(position, origin, 1920, 1080) >= .25) firstAdjustment = Math.Min(firstAdjustment, i / 30.0);
            }
            Check(firstAdjustment <= .35 && Distance(position, target, 1920, 1080) <= 1.5,
                $"A held {stepPixels}px adjustment must move rather than stay inside the rest zone; first={firstAdjustment:0.000}s");
        }

        var impulses = new PointerMotion();
        var impulseCentre = new HandPoint(.5, .5);
        double impulseClock = Warm(impulses, impulseCentre, 30, 1920, 1080);
        var impulseOutputs = new List<HandPoint>();
        for (int i = 1; i <= 90; i++)
        {
            var target = i switch
            {
                7 => new HandPoint(.5 - 12.0 / 1920, .5),
                24 or 25 => new HandPoint(.5 + 9.0 / 1920, .5 - 7.0 / 1080),
                63 => new HandPoint(.5, .5 + 12.0 / 1080),
                _ => impulseCentre
            };
            impulseOutputs.Add(impulses.Update(target, impulseClock + i / 30.0, 1920, 1080));
        }
        Check(Span(impulseOutputs, 1920, 1080) <= 2, "Isolated multi-axis twitches must not release rest merely because their pattern differs from sinusoidal noise");

        // Real camera delivery changes pace; intent must depend on elapsed time rather than frame count alone.
        var variable = new PointerMotion();
        var variableOrigin = new HandPoint(.4, .5);
        double variableTime = Warm(variable, variableOrigin, 30, 1920, 1080), elapsedVariable = 0;
        double[] intervals = [1.0 / 20, 1.0 / 60, 1.0 / 30, 1.0 / 24, 1.0 / 50];
        var variableRest = new List<HandPoint>();
        int frame = 0;
        while (elapsedVariable < 3)
        {
            elapsedVariable += intervals[frame++ % intervals.Length];
            var position = variable.Update(Noise(variableOrigin, elapsedVariable, 1920, 1080), variableTime + elapsedVariable, 1920, 1080);
            if (elapsedVariable > .5) variableRest.Add(position);
        }
        Check(Span(variableRest, 1920, 1080) <= 2, "Variable20–60FPS delivery must keep a resting pointer stable");
        variableTime += elapsedVariable;
        elapsedVariable = 0;
        double variableStart = double.PositiveInfinity;
        HandPoint variablePosition = variableOrigin, variableTarget = variableOrigin;
        while (elapsedVariable < 2)
        {
            elapsedVariable += intervals[frame++ % intervals.Length];
            variableTarget = new(variableOrigin.X + 20 * elapsedVariable / 1920, variableOrigin.Y);
            variablePosition = variable.Update(variableTarget, variableTime + elapsedVariable, 1920, 1080);
            if (Distance(variablePosition, variableOrigin, 1920, 1080) >= .25) variableStart = Math.Min(variableStart, elapsedVariable);
        }
        Check(variableStart <= .4 && Distance(variablePosition, variableTarget, 1920, 1080) <= 6,
            $"Variable-rate fine travel must release and follow intent; first={variableStart:0.000}s");

        if (failures.Count > 0) throw new Exception($"Resting stabilization failed {failures.Count}/{assertions} assertions:\n" + string.Join("\n", failures));
        Console.WriteLine($"Passed resting stabilization checks ({assertions} assertions): mapped tremor, brief twitches, post-travel parking, slow precision movement, corners, recovery, and desktop sizes.");
        Console.WriteLine($"Synthetic metrics: maximum settled span {maximumRestSpan:0.000}px; slow5/20px/s first movement <= {maximumFineStart:0.000}s; final lag <= {maximumFineLag:0.000}px.");
        Console.WriteLine($"Synthetic noisy-motion metrics: first forward movement <= {noisyFineStart:0.000}s; final intended-position error <= {noisyFineLag:0.000}px.");
    }
}
