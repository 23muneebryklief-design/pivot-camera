using HandPilot;
var mouse=new FakeMouse();var engine=new GestureEngine(mouse);
HandPoint[] Hand(bool closed=false){var p=Enumerable.Repeat(new HandPoint(.5,.5),21).ToArray();p[0]=new(.5,.9);foreach(int tip in new[]{8,12,16,20}){p[tip-3]=new(.5,.6);p[tip-2]=new(.5,.4);p[tip]=new(.5,closed?.65:.1);}return p;}
void Check(bool ok,string message){if(!ok)throw new Exception(message);}
engine.Start();Check(mouse.Last==new HandPoint(.5,.5),"Enable must centre");
engine.Process(Hand(true));Check(mouse.Downs==0,"Starting with fist must not click");
engine.Process(Hand());Check(mouse.Last==new HandPoint(.5,.5),"Opening fingers must not move palm pointer");
engine.Process(Hand(true));Check(mouse.Downs==1,"Fist must press once");Check(mouse.Last==new HandPoint(.5,.5),"Closing fingers must not move palm pointer");
engine.Process(Hand(true));Check(mouse.Downs==1,"Held fist must not repeat");
engine.Process(Hand());Check(mouse.Ups==1,"Open hand must release click");
var partial=Hand();partial[8]=new(.5,.65);engine.Process(partial);Check(mouse.Downs==1,"One folded finger must not click");
var moved=Hand();for(int i=0;i<21;i++)moved[i]=new(moved[i].X-.1,moved[i].Y+.1);engine.Process(moved);Check(mouse.Last.X>.5 && mouse.Last.Y>.5,"Pointer must follow palm movement");
engine.Process(Hand(true));engine.Process([]);Check(mouse.Ups==2,"Lost hand must release fist drag");
engine.Process(Hand(true));Check(mouse.Downs==2,"Reacquiring fist must not click");
engine.Process(Hand());engine.Process(Hand(true));engine.Reset();Check(mouse.Ups==3,"Pause must release drag");
var invalid=Hand();invalid[8]=new(double.NaN,0);engine.Process(invalid);Check(mouse.Downs==3,"Invalid frame must not click");
engine.Start();Check(mouse.Last==new HandPoint(.5,.5),"Re-enable must centre");
var scroll=Hand();scroll[16]=new(.5,.65);scroll[20]=new(.5,.65);engine.Process(scroll);scroll[8]=new(.5,0);scroll[12]=new(.5,0);engine.Process(scroll);Check(mouse.Wheel==120,"Two-finger scroll must work");
foreach(var offset in new[]{new HandPoint(-.17,0),new HandPoint(.17,0),new HandPoint(0,-.17),new HandPoint(0,.17)})
{
 var output=new FakeMouse();var range=new GestureEngine(output);range.Start();range.Process(Hand());
 var edge=Hand();for(int i=0;i<21;i++)edge[i]=new(edge[i].X+offset.X,edge[i].Y+offset.Y);
 for(int i=0;i<50;i++)range.Process(edge);
 Check(offset.X<0 ? output.Last.X==1 : offset.X>0 ? output.Last.X==0 : offset.Y<0 ? output.Last.Y==0 : output.Last.Y==1,"All four edges must be reachable with small palm movements");
}
var lowMouse=new FakeMouse();var low=new GestureEngine(lowMouse){Sensitivity=1};low.Start();low.Process(Hand());
var shifted=Hand();for(int i=0;i<21;i++)shifted[i]=new(shifted[i].X-.17,shifted[i].Y);
for(int i=0;i<50;i++)low.Process(shifted);Check(lowMouse.Last.X<.8,"Lower sensitivity must allow finer movement");
var clickMouse=new FakeMouse();var clickEngine=new GestureEngine(clickMouse);clickEngine.Start();clickEngine.Process(Hand());
HandPoint[] Offset(bool closed,double delta){var h=Hand(closed);for(int i=0;i<21;i++)h[i]=new(h[i].X+delta,h[i].Y);return h;}
var folding=Offset(false,.005);folding[8]=new(folding[8].X,.65);folding[12]=new(folding[12].X,.65);
clickEngine.Process(folding);Check(clickMouse.Last==new HandPoint(.5,.5),"Folding fingers must freeze pointer");
clickEngine.Process(Offset(true,.01));Check(clickMouse.Downs==1 && clickMouse.Last==new HandPoint(.5,.5),"Fist must press at frozen target");
clickEngine.Process(Offset(true,.02));Check(clickMouse.Last==new HandPoint(.5,.5),"Small fist tremor must not drag");
clickEngine.Process(Offset(false,.02));Check(clickMouse.Ups==1 && clickMouse.Last==new HandPoint(.5,.5),"Release must keep clicked target");
clickEngine.Process(Offset(false,.02));Check(clickMouse.Last==new HandPoint(.5,.5),"Open palm must resume without jump");
clickEngine.Process(Offset(false,-.08));Check(clickMouse.Last.X>.82,"Direct pointer must follow in one frame without smoothing delay");
clickEngine.Process(Offset(true,-.08));clickEngine.Process(Offset(true,-.13));Check(clickMouse.Last.X>.98,"Deliberate fist movement must drag");
Console.WriteLine("Passed: centred startup, palm movement, fist click and drag, open release, partial hand, hand loss, pause, scroll, sensitivity, screen edges, click stability, drag threshold, direct response.");
sealed class FakeMouse:IMouseOutput{public int Downs,Ups,Wheel;public HandPoint Last;public void Move(double x,double y){Last=new(x,y);if(x<0||x>1||y<0||y>1)throw new Exception("Out of bounds");}public void Down()=>Downs++;public void Up()=>Ups++;public void Scroll(int delta)=>Wheel+=delta;}
