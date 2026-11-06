using HandPilot;
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

// Full landmark fixture in mirrored camera coordinates, with an asymmetric thumb.
HandPoint[] HandAt(double mirroredX, double y, double scale = 1, int extended = 4)
{
    var offsets = new HandPoint[] {
        new(0,.06), new(-.025,.035), new(-.06,.02), new(-.09,0), new(-.12,-.015),
        new(-.05,-.015), new(-.055,-.08), new(-.055,-.13), new(-.055,-.17),
        new(-.017,-.015), new(-.017,-.10), new(-.017,-.16), new(-.017,-.20),
        new(.017,-.015), new(.017,-.09), new(.017,-.14), new(.017,-.18),
        new(.05,-.015), new(.05,-.06), new(.05,-.095), new(.05,-.125)
    };
    for(int finger=extended;finger<4;finger++)
    {
        int tip=8+finger*4;
        offsets[tip-2]=new(offsets[tip-3].X,-.04);
        offsets[tip-1]=new(offsets[tip-3].X,0);
        offsets[tip]=new(offsets[tip-3].X,.02);
    }
    return offsets.Select(p => new HandPoint(1-(mirroredX+p.X*scale), y+p.Y*scale)).ToArray();
}
void Feed(ReachCalibration calibration, double x, double y, ref double now, double seconds=1.2, int fps=30,
    double jitter=0, double scale=1, int extended=4, bool poll=false)
{
    for(int i=0;i<(int)Math.Ceiling(seconds*fps);i++)
    {
        now+=1.0/fps;
        calibration.AddSample(HandAt(x+jitter*Math.Sin(i*1.7),y+jitter*Math.Cos(i*1.1),scale,extended),now);
        if(poll) calibration.TryCapture(now,out _);
    }
}

var reach = new ReachCalibration();double now=10;
reach.AddSample(HandAt(.5,.55),now);
Check(!reach.TryCapture(now,out _),"One detection must not complete setup.");
Feed(reach,.5,.55,ref now,.5,jitter:.014);
Check(reach.Progress > .3 && reach.Progress < 1,"A natural hold must show progress before completion.");
Check(!reach.TryCapture(now,out _),"Half a second must not complete a one-second hold.");
Feed(reach,.5,.55,ref now,.7,jitter:.014);
Check(!reach.TryCapture(now+.2,out _),"Frozen stale samples must not complete setup when polled.");
Check(reach.TryCapture(now,out _),"A fresh natural hand hold must complete without corner captures.");
Check(reach.IsComplete && reach.Result.HasValue && reach.Progress==1,"Completion must expose one result and full progress.");
Check(!reach.TryCapture(now,out _),"Completion must not repeat.");
var bounds=reach.Result!.Value;
Check(bounds.IsValid,"Estimated workspace must be valid.");
Check(bounds.Width<=.320001 && bounds.Height<=.280001,"Workspace must cap comfortable hand travel.");
Check(Math.Abs((bounds.Left+bounds.Right)/2-.5)<.01 && Math.Abs((bounds.Top+bounds.Bottom)/2-.55)<.01,
    "Workspace centre must follow the user's neutral hand position.");
var neutral=HandAt(.5,.55);
foreach(var target in new[]{new HandPoint(bounds.Left,bounds.Top),new(bounds.Right,bounds.Top),new(bounds.Right,bounds.Bottom),new(bounds.Left,bounds.Bottom)})
{
    var translated=neutral.Select(p=>new HandPoint(p.X-(target.X-.5),p.Y+(target.Y-.55))).ToArray();
    Check(translated.All(p=>p.X>=0 && p.X<=1 && p.Y>=0 && p.Y<=1),"Asymmetric whole hand must remain visible at every learned corner.");
    Check(GestureEngine.TryGetPalm(translated,out var palm),"Translated corner must remain a valid hand.");
    var mapped=bounds.Map(palm);
    Check(mapped==new HandPoint(target.X==bounds.Left?0:1,target.Y==bounds.Top?0:1),"Each estimated reach corner must map exactly to its screen corner.");
}
foreach(int fps in new[]{15,30,60})
{
    var rate=new ReachCalibration();double clock=100;
    Feed(rate,.57,.59,ref clock,1.25,fps,.017,poll:true);
    Check(rate.IsComplete,$"Natural tremor must complete automatically at {fps}FPS.");
}
var relaxed=new ReachCalibration();double relaxedClock=50;
Feed(relaxed,.5,.55,ref relaxedClock,extended:2,poll:true);
Check(relaxed.IsComplete,"A relaxed hand with only two confidently extended fingers must be accepted.");
foreach(int extended in new[]{0,1})
{
    var fist=new ReachCalibration();double clock=100;
    Feed(fist,.5,.55,ref clock,extended:extended,poll:true);
    Check(!fist.IsComplete && fist.Progress==0,"A fist or mostly folded hand must not establish reach.");
    Check(!fist.TryCapture(clock,out var hint) && hint.Contains("Open your hand"),"Closed hand must get an actionable relaxed-open-hand hint.");
}
reach.Reset();Check(!reach.IsComplete && reach.Result==null && reach.Progress==0,"Reset must clear the single hold and result.");
Feed(reach,.5,.55,ref now,.55);
double beforeGap=reach.Progress;
for(int i=0;i<4;i++){now+=.04;reach.AddSample([],now);}
Check(reach.Progress>=beforeGap-.001,"A short missing-hand gap should pause progress, not erase the hold.");
Check(!reach.TryCapture(now,out _),"A missing latest hand must never auto-save an older hold.");
Feed(reach,.5,.55,ref now,.7,poll:true);Check(reach.IsComplete,"Setup should continue after a short tracking gap.");
reach.Reset();Feed(reach,.5,.55,ref now,.6);now+=.5;
Check(!reach.TryCapture(now,out _) && reach.Progress==0,"A long frozen gap must clear old progress.");
reach.AddSample(HandAt(.5,.55),now+.01);now+=.01;
Check(!reach.TryCapture(now,out _),"First hand after sustained loss must not finish the previous hold.");
Feed(reach,.5,.55,ref now,.5);
reach.AddSample(HandAt(.5,.55),now-.1);
Check(!reach.TryCapture(now,out _) && reach.Progress==0,"Regressing timestamp must invalidate old progress.");
Feed(reach,.5,.55,ref now,.5);reach.AddSample(HandAt(.5,.55),double.NaN);
Check(!reach.TryCapture(now,out _) && reach.Progress==0,"Nonfinite timestamp must invalidate old progress.");
reach.Reset();Feed(reach,.5,.55,ref now,.6);
var clipped=HandAt(.5,.55);clipped[8]=new(-.01,.2);now+=.03;reach.AddSample(clipped,now);
Check(!reach.TryCapture(now,out _) && reach.Progress>.4,"One clipped frame must pause setup without discarding a natural hold.");
Feed(reach,.5,.55,ref now,.6,poll:true);Check(reach.IsComplete,"A brief clipped detection may recover into a valid hold.");
reach.Reset();Feed(reach,.5,.55,ref now,.6);
var invalid=HandAt(.5,.55);invalid[8]=new(double.NaN,.2);now+=.03;reach.AddSample(invalid,now);
Check(!reach.TryCapture(now,out _),"Invalid latest landmarks must never complete setup.");
reach.Reset();
for(int i=0;i<90;i++)
{
    now+=1.0/30;
    reach.AddSample(HandAt(.25+(i%60)*.008,.55),now);reach.TryCapture(now,out _);
}
Check(!reach.IsComplete,"Sustained deliberate motion must not be mistaken for a comfortable hold.");
reach.Reset();
for(int i=0;i<90;i++)
{
    now+=1.0/30;reach.AddSample(i%4==0?HandAt(.5,.55):[],now);reach.TryCapture(now,out _);
}
Check(!reach.IsComplete && reach.Progress==0,"Mostly invalid tracking cannot accumulate setup duration.");
reach.Reset();Feed(reach,.5,.55,ref now,.7);
now+=.03;reach.AddSample(HandAt(.65,.55),now);
Feed(reach,.5,.55,ref now,.5);
Check(reach.TryCapture(now,out _),"An isolated positional outlier should not erase a robust natural hold.");
reach.Reset();Feed(reach,.5,.55,ref now,1.2);
now+=.03;reach.AddSample(HandAt(.65,.55),now);
Check(!reach.TryCapture(now,out _),"A latest palm far from the stable hold must not finish setup from older positions.");
now+=.03;reach.AddSample(HandAt(.5,.55),now);
Check(reach.TryCapture(now,out _),"One isolated outlier should recover when the latest palm returns to the comfortable hold.");
reach.Reset();Feed(reach,.14,.55,ref now);
Check(!reach.TryCapture(now,out var edgeHint) && edgeHint.Contains("toward the centre"),"Near-edge setup must give a useful centre hint instead of unsafe bounds.");
reach.Reset();Feed(reach,.5,.66,ref now,scale:3.1);
Check(!reach.TryCapture(now,out var sizeHint) && sizeHint.Contains("farther from"),"An oversized hand must get a move-back hint.");
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

Console.WriteLine($"Passed {checks} quick-setup assertions: one automatic comfortable hold, natural tremor, frame rates, short gaps, stale/malformed/moving hand rejection, safe mirrored corners, relaxed open hand, hints, and compatible profile persistence.");
