using HandPilot;
var mouse=new FakeMouse();var engine=new GestureEngine(mouse);
HandPoint[] Hand(double pinch){var p=Enumerable.Repeat(new HandPoint(.5,.5),21).ToArray();p[0]=new(.5,.9);p[9]=new(.5,.5);p[8]=new(.5,.3);p[4]=new(.5+pinch,.3);return p;}
void Check(bool ok,string message){if(!ok)throw new Exception(message);}
engine.Process(Hand(.04));Check(mouse.Downs==1,"Pinch must press once");
engine.Process(Hand(.04));Check(mouse.Downs==1,"Held pinch must not repeat");
engine.Process(Hand(.12));Check(mouse.Ups==0,"Hysteresis must retain drag");
engine.Process(Hand(.2));Check(mouse.Ups==1,"Release must lift button");
engine.Process(Hand(.04));engine.Process([]);Check(mouse.Ups==2,"Lost hand must release drag");
engine.Process(Hand(.04));engine.Reset();Check(mouse.Ups==3,"Pause must release drag");
var invalid=Hand(.04);invalid[8]=new(double.NaN,0);engine.Process(invalid);Check(mouse.Downs==3,"Invalid frame must not click");
var scroll=Hand(.2);scroll[6]=new(.5,.6);scroll[12]=new(.6,.3);scroll[10]=new(.6,.6);scroll[16]=new(.7,.8);scroll[14]=new(.7,.6);
engine.Process(scroll);scroll[8]=new(.5,.2);scroll[12]=new(.6,.2);engine.Process(scroll);Check(mouse.Wheel==120,"Two fingers moving up must scroll up");
Console.WriteLine("Passed: pinch, drag, hysteresis, hand loss, pause, invalid frames, scroll.");
sealed class FakeMouse:IMouseOutput{public int Downs,Ups,Wheel;public void Move(double x,double y){if(x<0||x>1||y<0||y>1)throw new Exception("Out of bounds");}public void Down()=>Downs++;public void Up()=>Ups++;public void Scroll(int delta)=>Wheel+=delta;}
