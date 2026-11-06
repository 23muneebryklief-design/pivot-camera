using HandPilot;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

HandPoint[] HandAt(double mirroredX, double y)
{
    double x = 1 - mirroredX;
    var hand = Enumerable.Repeat(new HandPoint(x, y), 21).ToArray();
    hand[0] = new(x, y + .04);
    foreach (int index in new[] { 5, 9, 13, 17 }) hand[index] = new(x, y - .01);
    foreach (int index in new[] { 8, 12, 16, 20 }) hand[index] = new(x + (index - 14) * .006, y - .12);
    hand[4] = new(x - .07, y - .025);
    return hand;
}

void Hold(ReachCalibration calibration, double x, double y, ref double now, double jitter = 0)
{
    for (int i = 0; i <= 21; i++)
    {
        now += .03;
        calibration.AddSample(HandAt(x + (i % 2 == 0 ? jitter : -jitter), y), now);
    }
}

var reach = new ReachCalibration();
double now = 10;
reach.AddSample(HandAt(.5, .5), now);
Check(!reach.TryCapture(now, out _), "One detection must not count as a stable hold.");
Hold(reach, .5, .5, ref now, .003);
Check(!reach.TryCapture(now + .2, out _), "An old stable hold must not be accepted after tracking stops.");
Check(reach.TryCapture(now, out _), "A fresh, stable centre hold must be captured.");
Check(reach.StepIndex == 1, "Centre capture must advance once.");
Check(!reach.TryCapture(now, out _), "One hold must not automatically fill another step.");
Hold(reach, .7, .3, ref now);
Check(!reach.TryCapture(now, out _), "Upper-left capture must reject a hand in the upper-right.");
Hold(reach, .29, .31, ref now);
Check(reach.TryCapture(now, out _), "Upper-left pose must be accepted.");
Hold(reach, .71, .29, ref now);
Check(reach.TryCapture(now, out _), "Upper-right pose must be accepted.");
Hold(reach, .69, .71, ref now);
Check(reach.TryCapture(now, out _), "Lower-right pose must be accepted.");
Hold(reach, .31, .69, ref now);
Check(reach.TryCapture(now, out _), "Lower-left pose must complete calibration.");
Check(reach.IsComplete && reach.StepIndex == 5 && reach.Result.HasValue, "Calibration must expose its completed result.");
var bounds = reach.Result!.Value;
Check(bounds.IsValid, "Completed calibration must have valid bounds.");
Check(Math.Abs(bounds.Left - .31) < .001 && Math.Abs(bounds.Right - .69) < .001 &&
      Math.Abs(bounds.Top - .31) < .001 && Math.Abs(bounds.Bottom - .69) < .001,
    "The inner intersection must make uneven captured poses reach exact corners.");
foreach (var point in new[] { new HandPoint(.29, .31), new(.71, .29), new(.69, .71), new(.31, .69) })
{
    double x = Math.Clamp((point.X - bounds.Left) / (bounds.Right - bounds.Left), 0, 1);
    double y = Math.Clamp((point.Y - bounds.Top) / (bounds.Bottom - bounds.Top), 0, 1);
    Check(Math.Abs(x - (point.X < .5 ? 0 : 1)) < 1e-12 && Math.Abs(y - (point.Y < .5 ? 0 : 1)) < 1e-12,
        "Each captured corner must reach the corresponding screen corner.");
}

reach.Reset();
Check(!reach.IsComplete && reach.StepIndex == 0 && reach.Result is null, "Restart must clear all captured reach.");
Hold(reach, .5, .5, ref now);
var clipped = HandAt(.5, .5); clipped[8] = new(-.01, .1);
now += .03; reach.AddSample(clipped, now);
Check(!reach.TryCapture(now, out _), "Clipped fingers must invalidate the hold.");
Hold(reach, .5, .5, ref now);
var invalid = HandAt(.5, .5); invalid[8] = new(double.NaN, .1);
now += .03; reach.AddSample(invalid, now);
Check(!reach.TryCapture(now, out _), "Invalid landmarks must invalidate the hold.");
Hold(reach, .5, .5, ref now);
reach.AddSample(HandAt(.5, .5), now - .1);
Check(!reach.TryCapture(now, out _), "A regressing frame timestamp must invalidate the hold.");
reach.Reset();
for (int i = 0; i < 22; i++)
{
    now += .03;
    reach.AddSample(HandAt(i % 2 == 0 ? .47 : .53, .5), now);
}
Check(!reach.TryCapture(now, out _), "A moving hand must not be captured as a stable pose.");

var temporaryDirectory = Path.Combine(Path.GetTempPath(), "HandPilot-calibration-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
try
{
    var path = Path.Combine(temporaryDirectory, "calibration.json");
    var store = new CalibrationProfileStore(path);
    Check(!store.TryLoad("camera-a", "display-a", out _), "A missing profile must be uncalibrated.");
    store.Save("camera-a", "display-a", bounds);
    Check(store.TryLoad("camera-a", "display-a", out var loaded) && loaded == bounds, "A saved profile must round-trip.");
    Check(!store.TryLoad("camera-b", "display-a", out _) && !store.TryLoad("camera-a", "display-b", out _), "Camera and desktop arrangements must be matched independently.");
    var alternate = new CameraBounds(.25, .25, .75, .75);
    store.Save("camera-b", "display-a", alternate);
    Check(store.TryLoad("camera-a", "display-a", out loaded) && loaded == bounds, "Saving another camera must preserve existing profiles.");
    store.Save("camera-a", "display-a", alternate);
    Check(store.TryLoad("camera-a", "display-a", out loaded) && loaded == alternate, "Saving the same setup must replace its previous calibration.");
    File.WriteAllText(path, "not valid json");
    Check(!store.TryLoad("camera-a", "display-a", out _), "Corrupt profiles must not fail startup.");
    File.WriteAllText(path, "{\"Version\":999,\"Profiles\":[]}");
    Check(!store.TryLoad("camera-a", "display-a", out _), "Unsupported profile versions must be ignored.");
    File.WriteAllText(path, "{\"Profiles\":[]}");
    Check(!store.TryLoad("camera-a", "display-a", out _), "A file without a format version must be ignored.");
    File.WriteAllText(path, "{\"Version\":1,\"Profiles\":[null,{\"CameraId\":\"camera-a\",\"DisplayKey\":\"display-a\",\"Bounds\":{\"Left\":0.8,\"Top\":0.2,\"Right\":0.2,\"Bottom\":0.8}}]}");
    Check(!store.TryLoad("camera-a", "display-a", out _), "Invalid or null profile entries must not be used.");
    Check(!Directory.EnumerateFiles(temporaryDirectory, "*.tmp-*").Any(), "Atomic saves must not leave temporary files behind.");
}
finally
{
    File.Delete(Path.Combine(temporaryDirectory, "calibration.json"));
    Directory.Delete(temporaryDirectory);
}

Console.WriteLine("Passed: stable calibration, fresh frames, full-hand visibility, corner directions, all four exact corners, restart, camera/display profile isolation, atomic persistence, and corrupt-profile recovery.");
