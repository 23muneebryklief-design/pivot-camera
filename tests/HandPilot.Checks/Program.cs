using HandPilot;
var mouse=new FakeMouse();var engine=new GestureEngine(mouse){CameraMargin=0};
HandPoint[] HandAt(double x,double y,bool closed=false)
{
 var p=Enumerable.Repeat(new HandPoint(.5,.5),21).ToArray();p[0]=new(.5,.9);
 foreach(int tip in new[]{8,12,16,20}){p[tip-3]=new(.5,.6);p[tip-2]=new(.5,.4);p[tip]=new(.5,closed?.65:.1);}
 for(int i=0;i<21;i++)p[i]=new(p[i].X+x-.5,p[i].Y+y-.66);
 return p;
}
void Check(bool ok,string message){if(!ok)throw new Exception(message);}
void Position(double x,double y,string message)=>Check(Math.Abs(mouse.Last.X-x)<1e-9 && Math.Abs(mouse.Last.Y-y)<1e-9,message);
engine.Start();Check(mouse.Moves==0,"Enable must wait for hand instead of recentering");
foreach(var point in new[]{new HandPoint(.5,.5),new HandPoint(.75,.25),new HandPoint(.25,.75),new HandPoint(0,0),new HandPoint(1,1),new HandPoint(1,0),new HandPoint(0,1)})
{
 engine.Process(HandAt(point.X,point.Y));Position(1-point.X,point.Y,"Palm must match mirrored preview coordinates");
}
engine.Reset();engine.Process(HandAt(.8,.2));Position(.2,.2,"Reacquired hand must use absolute position");
engine.Process(HandAt(.81,.21,true));Check(mouse.Downs==1,"Fist must press once");Position(.2,.2,"Fist closure must keep click target");
engine.Process(HandAt(.82,.22,true));Check(mouse.Downs==1,"Held fist must not repeat");Position(.2,.2,"Fist tremor must not move click target");
engine.Process(HandAt(.7,.3,true));Position(.3,.3,"Deliberate dragging must use absolute position");
engine.Process(HandAt(.7,.3));Check(mouse.Ups==1,"Opening hand must release");
engine.Process(HandAt(.6,.4));Position(.4,.4,"Movement after click must not retain an offset");
engine.Process(HandAt(.6,.4,true));engine.Process([]);Check(mouse.Ups==2,"Hand loss must release drag");
engine.Process(HandAt(.5,.5,true));Check(mouse.Downs==2,"Reacquiring closed fist must not click");
engine.Process(HandAt(.5,.5));engine.Process(HandAt(.5,.5,true));engine.Reset();Check(mouse.Ups==3,"Pause must release drag");
var invalid=HandAt(.5,.5);invalid[8]=new(double.NaN,0);engine.Process(invalid);Check(mouse.Downs==3,"Invalid data must not click");
var scroll=HandAt(.5,.5);scroll[16]=new(.5,.39);scroll[20]=new(.5,.39);engine.Process(scroll);scroll[8]=new(.5,-.2);scroll[12]=new(.5,-.2);engine.Process(scroll);Check(mouse.Wheel==120,"Two-finger scrolling must work");
var doubleMouse=new FakeMouse();var doubleEngine=new GestureEngine(doubleMouse){DoubleClickEnabled=true};
doubleEngine.Start();doubleEngine.Process(HandAt(.5,.5));doubleEngine.Process(HandAt(.5,.5,true));
Check(doubleMouse.Downs==0 && doubleMouse.DoubleClicks==0,"Double click must wait for release");
doubleEngine.Process(HandAt(.5,.5,true));doubleEngine.Process(HandAt(.5,.5));
Check(doubleMouse.DoubleClicks==1 && doubleMouse.Downs==0,"One fist cycle must produce one double-click");
doubleEngine.Process(HandAt(.5,.5));Check(doubleMouse.DoubleClicks==1,"Open frames must not repeat double-click");
doubleEngine.Process(HandAt(.5,.5,true));doubleEngine.Process(HandAt(.6,.5,true));
Check(doubleMouse.Downs==1,"Double-click mode must still start deliberate drag");
doubleEngine.Process(HandAt(.6,.5));Check(doubleMouse.Ups==1 && doubleMouse.DoubleClicks==1,"Drag release must not double-click");
doubleEngine.Process(HandAt(.6,.5,true));doubleEngine.Process([]);Check(doubleMouse.DoubleClicks==1,"Tracking loss must cancel pending double-click");
doubleEngine.Process(HandAt(.6,.5));doubleEngine.Process(HandAt(.6,.5,true));doubleEngine.Reset();Check(doubleMouse.DoubleClicks==1,"Pause must cancel pending double-click");
// Verify reachable corners with the entire hand still inside the camera image.
foreach(var corner in new[]{new HandPoint(.25,.25),new HandPoint(.75,.25),new HandPoint(.25,.75),new HandPoint(.75,.75)})
{
 var cornerMouse=new FakeMouse();var cornerEngine=new GestureEngine(cornerMouse){DoubleClickEnabled=true};
 HandPoint[] Compact(bool closed){var h=HandAt(.5,.5,closed);return h.Select(p=>new HandPoint(corner.X+(p.X-.5)*.35,corner.Y+(p.Y-.5)*.35)).ToArray();}
 var open=Compact(false);Check(open.All(p=>p.X>=0 && p.X<=1 && p.Y>=0 && p.Y<=1),"Corner fixture must keep the whole hand in frame");
 cornerEngine.Start();cornerEngine.Process(open);
 Check(Math.Abs(cornerMouse.Last.X-(corner.X<.5?1:0))<1e-9 && Math.Abs(cornerMouse.Last.Y-(corner.Y<.5?0:1))<1e-9,"Inner-box corners must reach exact screen corners");
 cornerEngine.Process(Compact(true));cornerEngine.Process(open);Check(cornerMouse.DoubleClicks==1,"Corner double-click must remain possible");
}
Console.WriteLine("Passed: absolute mapping, mirrored coordinates, corners, reacquisition, fist click stability, absolute drag, release, hand loss, pause scrolling, double-click, drag in double-click mode cancellation, in-frame corner reach and corner double-clicks.");
sealed class FakeMouse:IMouseOutput
{
 public int Downs,Ups,Wheel,Moves,DoubleClicks;public HandPoint Last;
 public void Move(double x,double y){Moves++;Last=new(x,y);if(x<0||x>1||y<0||y>1)throw new Exception("Out of bounds");}
 public void DoubleClick()=>DoubleClicks++;
 public void Down()=>Downs++;public void Up()=>Ups++;public void Scroll(int delta)=>Wheel+=delta;
}
