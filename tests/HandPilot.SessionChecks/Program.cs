using HandPilot;
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
HandPoint[] Hand(double mirroredX = .5, double y = .52)
{
 // A compact, fully-visible hand whose wrist/knuckle average is the requested palm.
 var hand = Enumerable.Repeat(new HandPoint(1-mirroredX, y), 21).ToArray();
 hand[0] = new(1-mirroredX, y+.06);
 for(int i=0;i<4;i++)
 {
  int knuckle=5+i*4;
  hand[knuckle]=new(1-mirroredX+(i-1.5)*.02,y-.015);
  hand[knuckle+1]=new(hand[knuckle].X,y-.05);
  hand[knuckle+2]=new(hand[knuckle].X,y-.09);
  hand[knuckle+3]=new(hand[knuckle].X,y-.13);
 }
 return hand;
}
var mouse = new FakeMouse();
using var session = new MovementSession(mouse);
session.Configure(CameraBounds.Default, 1920, 1080);
session.StartStream("A");
long sequence = 0;
void Frame(double captured, double now, HandPoint[]? points = null)
{
 session.Submit(new("A", ++sequence, captured, points ?? Hand())); session.Tick(now);
}
Check(!session.Enable(new(.5,.5)), "No control before tracking");
Check(!session.Submit(new("old-camera", 1, 1000, Hand())), "Old camera results rejected");
Frame(1000, 1000); Check(session.Snapshot(1000).Condition == TrackingCondition.Acquiring, "First hand frame must acquire");
Frame(1050, 1050); Check(session.Snapshot(1050).Condition == TrackingCondition.Acquiring, "Two hand frames insufficient");
Frame(1100, 1100); Check(session.Snapshot(1100).Condition == TrackingCondition.Tracking, "Continuous100ms tracking arms session");
Check(session.Enable(new(.5,.5)), "Fresh tracked hand can enable");
Frame(1133, 1133); Check(mouse.Moves == 1, "One result causes one movement");
session.Tick(1140); Check(mouse.Moves == 1, "Timer must not repeat old model result");
Check(!session.Submit(new("A", sequence, 1133, Hand())), "Duplicate result rejected");
Check(!session.Submit(new("A", sequence+1, 1120, Hand())), "Regressing capture timestamp rejected");
Frame(1166, 1300); Check(mouse.Moves == 1 && session.Snapshot(1300).Condition == TrackingCondition.Delayed, "134ms old result cannot move cursor");
Frame(1310, 1310, Hand(.7,.32)); Frame(1360, 1360, Hand(.7,.32));
Check(mouse.Moves == 1, "Reappearance needs acquisition before output");
Frame(1410, 1410, Hand(.7,.32));
Check(mouse.Moves == 2 && Math.Abs(mouse.Last.X-.5) < 1e-9, "Recovery starts at last emitted cursor without teleport");
Frame(1460, 1460, Hand(.7,.32)); Frame(1510, 1510, Hand(.7,.32)); Frame(1540, 1540, Hand(.7,.32));
Check(mouse.Last == new HandPoint(1,0), "Recovered hand reaches exact absolute corner");
Frame(1570, 1570, []); Check(session.Snapshot(1570).Condition == TrackingCondition.NoHand, "Empty detection immediately holds cursor");
int moves = mouse.Moves; session.Tick(1600); Check(mouse.Moves == moves, "No output while hand missing");
Frame(1620,1620); Frame(1670,1670); Frame(1720,1720);
Check(session.Snapshot(1720).Condition == TrackingCondition.Tracking, "Hand is reacquired after loss");
session.Tick(1871); Check(session.Snapshot(1871).Condition == TrackingCondition.Delayed, "Stalled stream times out independently of UI");
Check(mouse.Moves > 0 && mouse.Actions == 0, "Movement milestone never emits clicks or scrolls");
session.Pause(); moves = mouse.Moves;
Frame(1900,1900);Frame(1950,1950);Frame(2000,2000); Check(mouse.Moves == moves && !session.Snapshot(2000).Enabled, "Explicit pause must not auto-resume");
session.StartStream("B");Check(!session.Submit(new("A",999,2050,Hand())), "Late frame after camera change rejected");
session.Submit(new("B",1,2200,Hand()));session.Tick(2100); Check(session.Snapshot(2100).Condition == TrackingCondition.Delayed, "Future clock-invalid sample rejected");

// Real cursor position must agree with the engine, and the whole hand must be visible.
MovementSnapshot TrackedAt(HandPoint pointer)
{
 var area=CameraBounds.Default;
 var sample=new TrackingSample("trial",1,1000,Hand(area.Left+pointer.X*area.Width,area.Top+pointer.Y*area.Height));
 return new(TrackingCondition.Tracking,true,pointer,sample,10,null);
}
MovementSnapshot Clipped(MovementSnapshot state)
{
 var points=state.Sample!.Points.ToArray();points[8]=new(points[8].X,-.01);
 return state with {Sample=state.Sample with {Points=points}};
}
double ReachAllCorners(MovementTrial check)
{
 double now=0;
 for(int i=0;i<8;i++)
 {
  var target=check.Target!.Value;var driven=TrackedAt(target);
  check.Update(now,driven,target);check.Update(now+.41,driven,target);now+=.5;
 }
 return now;
}
var trial = new MovementTrial(1920,1080); double time = ReachAllCorners(trial);
var tracked = TrackedAt(new(.5,.5));
Check(trial.Phase == TrialPhase.Steady && trial.Steps.Count == 8, "Eight hand-driven corner holds required across two rounds");
trial.Update(time,tracked,new(.5,.5));trial.Update(time+2.1,TrackedAt(new(.502,.502)),new(.502,.502));time+=2.2;
Check(trial.Phase == TrialPhase.HideHand && trial.StableSpanPixels < 12, "Two-second pointing check measures actual pixel span");
var delayed = tracked with {Condition=TrackingCondition.Delayed};trial.Update(time,delayed,new(.5,.5));trial.Update(time+1,delayed,new(.5,.5));
Check(trial.Phase == TrialPhase.HideHand, "Slow inference cannot count as deliberate hand removal");
var lost = tracked with {Condition=TrackingCondition.NoHand,Sample=tracked.Sample! with {Points=[]}};
trial.Update(time+1.1,lost,new(.5,.5));trial.Update(time+1.7,lost,new(.5,.5));time+=1.8;
Check(trial.Phase == TrialPhase.Recover, "Half-second observed hand loss and held cursor required");
trial.Update(time,tracked,new(.5,.5));trial.Update(time+.8,tracked,new(.5,.5));Check(trial.Phase == TrialPhase.Complete, "Fresh centred recovery completes check");
Check(!trial.RecoverySmoothnessVerified, "Automatic hold and reacquisition checks must not certify unmeasured transition smoothness");

var physicalMouseTrial=new MovementTrial(1920,1080);var firstCorner=physicalMouseTrial.Target!.Value;
physicalMouseTrial.Update(0,tracked,firstCorner);physicalMouseTrial.Update(1,tracked,firstCorner);
Check(physicalMouseTrial.Steps.Count==0,"Physical mouse movement without matching engine output cannot pass a corner");
var clippedTrial=new MovementTrial(1920,1080);var clippedCorner=clippedTrial.Target!.Value;
clippedTrial.Update(0,Clipped(TrackedAt(clippedCorner)),clippedCorner);clippedTrial.Update(1,Clipped(TrackedAt(clippedCorner)),clippedCorner);
Check(clippedTrial.Steps.Count==0,"A corner with fingertips out of frame cannot pass");
var oldTrial=new MovementTrial(1920,1080);var oldCorner=oldTrial.Target!.Value;var oldState=TrackedAt(oldCorner) with {ProcessingAgeMs=121};
oldTrial.Update(0,oldState,oldCorner);oldTrial.Update(1,oldState,oldCorner);
Check(oldTrial.Steps.Count==0,"Stale tracking cannot count toward a corner hold");
var absentTrial=new MovementTrial(1920,1080);var absentCorner=absentTrial.Target!.Value;var absentState=TrackedAt(absentCorner) with {Sample=null};
absentTrial.Update(0,absentState,absentCorner);absentTrial.Update(1,absentState,absentCorner);
Check(absentTrial.Steps.Count==0,"A tracking label without current landmarks cannot pass");
var clippedSteady=new MovementTrial(1920,1080);var steadyTime=ReachAllCorners(clippedSteady);
clippedSteady.Update(steadyTime,Clipped(tracked),new(.5,.5));clippedSteady.Update(steadyTime+2.1,Clipped(tracked),new(.5,.5));
Check(clippedSteady.Phase==TrialPhase.Steady,"Steady pointing requires the entire hand visible");
clippedSteady.Update(steadyTime+2.2,tracked,new(.5,.5));clippedSteady.Update(steadyTime+4.3,tracked,new(.5,.5));
Check(clippedSteady.Phase==TrialPhase.HideHand,"Valid steady pointing can follow an invalid attempt");
clippedSteady.Update(steadyTime+4.4,lost,new(.5,.5));clippedSteady.Update(steadyTime+4.6,lost,new(.5+4.0/1920,.5));
Check(clippedSteady.Phase==TrialPhase.Failed,"More than3px cursor drift while hand is missing must fail the check");
var recovering=new MovementTrial(1920,1080);var recoverTime=ReachAllCorners(recovering);
recovering.Update(recoverTime,tracked,new(.5,.5));recovering.Update(recoverTime+2.1,tracked,new(.5,.5));
recovering.Update(recoverTime+2.2,lost,new(.5,.5));recovering.Update(recoverTime+2.8,lost,new(.5,.5));
recovering.Update(recoverTime+2.9,Clipped(tracked),new(.5,.5));recovering.Update(recoverTime+3.8,Clipped(tracked),new(.5,.5));
Check(recovering.Phase==TrialPhase.Recover,"A partial hand cannot complete recovery acceptance");
recovering.Update(recoverTime+3.9,tracked,new(.5+10.0/1920,.5));recovering.Update(recoverTime+4.8,tracked,new(.5+10.0/1920,.5));
Check(recovering.Phase==TrialPhase.Recover,"Physical cursor mismatch cannot complete recovery acceptance");
var pausedTrial = new MovementTrial(1920,1080);pausedTrial.Update(0,tracked with{Enabled=false},new(.5,.5));Check(pausedTrial.Phase == TrialPhase.Failed,"Pausing aborts acceptance check");
var timeout = new MovementTrial(1920,1080);timeout.Update(0,tracked,new(.5,.5));timeout.Update(31,tracked,new(.5,.5));Check(timeout.Phase == TrialPhase.Failed,"Unreachable corner cannot pass");
var bad = new FakeMouse { FailMove = true }; using var failingSession = new MovementSession(bad);failingSession.StartStream("F");
for(int i=0;i<3;i++){failingSession.Submit(new("F",i+1,1000+i*50,Hand()));failingSession.Tick(1000+i*50);}
failingSession.Enable(new(.5,.5));failingSession.Submit(new("F",4,1150,Hand()));failingSession.Tick(1150);
Check(failingSession.Snapshot(1150).Condition == TrackingCondition.Error && !failingSession.Snapshot(1150).Enabled,"Input rejection pauses with visible error");
Console.WriteLine($"Passed {checks} session and live-check assertions.");
sealed class FakeMouse:IMouseOutput
{
 public int Moves,Actions; public HandPoint Last; public bool FailMove;
 public void Move(double x,double y){if(FailMove)throw new Exception("Input rejected");Moves++;Last=new(x,y);}
 public void Down()=>Actions++;public void Up()=>Actions++;public void DoubleClick()=>Actions++;public void Scroll(int delta)=>Actions++;
}
