using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
namespace HandPilot;
public partial class MainWindow : Window
{
    private readonly GestureEngine gestures = new(new WindowsMouse());
    private bool enabled;
    private DateTime lastFrame = DateTime.MinValue;
    private readonly DispatcherTimer watchdog = new() { Interval=TimeSpan.FromMilliseconds(150) };
    private HwndSource? source;
    public MainWindow()
    {
        InitializeComponent();
        Loaded += Initialize;
        watchdog.Tick += (_,_) => { if (DateTime.UtcNow-lastFrame>TimeSpan.FromMilliseconds(500)) gestures.Reset(); };
        Closed += (_,_) => { watchdog.Stop(); gestures.Reset(); if(source!=null) { UnregisterHotKey(source.Handle,1); source.RemoveHook(Hook); } CameraView.Dispose(); };
    }
    private async void Initialize(object sender,RoutedEventArgs e)
    {
        try
        {
            source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(Hook);
            if(!RegisterHotKey(source.Handle,1,0x4000|0x0002|0x0001,0x20)) throw new InvalidOperationException("Pause shortcut is already in use. Close the conflicting app and reopen HandPilot.");
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HandPilot","WebView2"));
            await CameraView.EnsureCoreWebView2Async(env);
            var core=CameraView.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("handpilot.example",Path.Combine(AppContext.BaseDirectory,"Web"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_,args) => { if(args.Uri!="https://handpilot.example/index.html") args.Cancel=true; };
            core.NewWindowRequested += (_,args) => args.Handled=true;
            core.PermissionRequested += (_,args) => { if(args.PermissionKind!=CoreWebView2PermissionKind.Camera || !args.Uri.StartsWith("https://handpilot.example/",StringComparison.Ordinal)) args.State=CoreWebView2PermissionState.Deny; };
            core.WebMessageReceived += Receive;
            core.Navigate("https://handpilot.example/index.html");
            watchdog.Start();
        }
        catch(Exception ex) { Status.Text="Could not start: "+ex.Message; }
    }
    private void Receive(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        if(e.Source!="https://handpilot.example/index.html") return;
        try
        {
            using var doc=JsonDocument.Parse(e.WebMessageAsJson);
            var root=doc.RootElement;
            if(root.TryGetProperty("status",out var status)) { Status.Text=status.GetString(); return; }
            if(root.TryGetProperty("ready",out var ready)) { Pause(); lastFrame=DateTime.MinValue; ControlButton.IsEnabled=ready.GetBoolean(); if(!ready.GetBoolean()) return; Status.Text="Preview ready. Enable mouse control when comfortable."; return; }
            if(!root.TryGetProperty("points",out var points)) return;
            lastFrame=points.GetArrayLength()==21 ? DateTime.UtcNow : DateTime.MinValue;
            if(enabled) gestures.Process(points.EnumerateArray().Select(p=>new HandPoint(p.GetProperty("x").GetDouble(),p.GetProperty("y").GetDouble())).ToArray());
        }
        catch(Exception ex) { Pause(); Status.Text="Control paused: "+ex.Message; }
    }
    private void SmoothingChanged(object sender,RoutedEventArgs e) => gestures.SmoothingEnabled=((System.Windows.Controls.CheckBox)sender).IsChecked==true;
    private void SensitivityChanged(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        gestures.Sensitivity=e.NewValue;
        if(enabled) Pause();
    }
    private void ToggleControl(object sender,RoutedEventArgs e)
    {
        if(enabled) { Pause(); return; }
        if(DateTime.UtcNow-lastFrame>TimeSpan.FromMilliseconds(500)) { Status.Text="Show your hand to the camera before enabling control."; return; }
        gestures.Start(); enabled=true; ControlButton.Content="Pause mouse control"; Status.Text="Mouse control active — Ctrl + Alt + Space to pause.";
    }
    private void Pause() { enabled=false; gestures.Reset(); ControlButton.Content="Enable mouse control"; Status.Text="Mouse control paused."; }
    private IntPtr Hook(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)
    { if(msg==0x0312 && wParam==(IntPtr)1) { Pause(); handled=true; } return IntPtr.Zero; }
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd,int id);
}
