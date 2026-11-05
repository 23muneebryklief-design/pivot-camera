using System.ComponentModel;
using System.Runtime.InteropServices;
namespace HandPilot;
public sealed class WindowsMouse : IMouseOutput
{
    public void Move(double x,double y) => Send(0x0001|0x8000|0x4000,(int)(x*65535),(int)(y*65535));
    public void Down() => Send(0x0002);
    public void Up() => Send(0x0004);
    public void DoubleClick()
    {
        INPUT[] clicks = [
            new() { mouse=new MOUSEINPUT { dwFlags=0x0002 } },
            new() { mouse=new MOUSEINPUT { dwFlags=0x0004 } },
            new() { mouse=new MOUSEINPUT { dwFlags=0x0002 } },
            new() { mouse=new MOUSEINPUT { dwFlags=0x0004 } }
        ];
        if(SendInput(4,clicks,Marshal.SizeOf<INPUT>())!=4)
        {
            int error=Marshal.GetLastWin32Error();
            Up();
            throw new Win32Exception(error,"Windows rejected double-click input.");
        }
    }
    public void Scroll(int delta) => Send(0x0800,data:unchecked((uint)delta));
    private static void Send(uint flags,int x=0,int y=0,uint data=0)
    {
        var input = new INPUT { type=0, mouse=new MOUSEINPUT { dx=x,dy=y,mouseData=data,dwFlags=flags } };
        if (SendInput(1,[input],Marshal.SizeOf<INPUT>())!=1) throw new Win32Exception(Marshal.GetLastWin32Error(),"Windows rejected mouse input.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public MOUSEINPUT mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx,dy; public uint mouseData,dwFlags,time; public UIntPtr extraInfo; }
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,INPUT[] inputs,int size);
}
