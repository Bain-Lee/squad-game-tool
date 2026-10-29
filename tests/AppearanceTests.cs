using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SquadTools;

internal static class AppearanceTests
{
    private static int assertions;
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    [DllImport("user32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(IntPtr window,out uint colorKey,out byte alpha,out uint flags);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW",SetLastError=true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW",SetLastError=true)]
    private static extern int GetWindowLong32(IntPtr window,int index);
    private static void Check(bool value,string label) { assertions++; if(!value) throw new Exception(label); }
    private static void Near(double expected,double actual,double tolerance,string label) { Check(!double.IsNaN(actual) && Math.Abs(expected-actual)<=tolerance,label+": "+expected+" != "+actual); }
    private static T Field<T>(object target,string name) { return (T)target.GetType().GetField(name,Private).GetValue(target); }
    private static object Call(object target,string name,params object[] args) { return target.GetType().GetMethod(name,Private).Invoke(target,args); }
    private static T Named<T>(MainWindow window,string name) where T:class { return ((FrameworkElement)window.Content).FindName(name) as T; }
    private static void Pump(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{})); window.UpdateLayout(); }
    private static Color ColorOf(Brush brush) { Check(brush is SolidColorBrush,"surface uses a solid theme brush"); return ((SolidColorBrush)brush).Color; }
    private static double Brightness(Color color) { return (color.R*0.2126+color.G*0.7152+color.B*0.0722)/255; }
    private static void WarmSurface(Color color,string label)
    {
        Check(Brightness(color)>0.85 && color.R>=color.G && color.G>color.B && color.R-color.B<=22 && color.B>=225,label+": subtle warm off-white surface; actual="+color);
    }
    private static Color Sample(FrameworkElement element,int x,int y)
    {
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(element); var pixel=new byte[4]; bitmap.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);
        return Color.FromArgb(pixel[3],pixel[2],pixel[1],pixel[0]);
    }
    private static void NativeOpacity(Window window,double expected,string label)
    {
        var handle=new WindowInteropHelper(window).Handle;
        long style=IntPtr.Size==8?GetWindowLongPtr64(handle,-20).ToInt64():GetWindowLong32(handle,-20);
        if(expected==1 && (style&0x80000)==0) Check(true,label+": fully opaque HWND no longer needs WS_EX_LAYERED");
        else {
            Check((style&0x80000)!=0,label+": actual HWND has WS_EX_LAYERED");
            uint color,flags; byte alpha;
            bool queried=GetLayeredWindowAttributes(handle,out color,out alpha,out flags); int error=Marshal.GetLastWin32Error();
            Check(queried && (flags&2)!=0 && Math.Abs(alpha-expected*255)<=1,label+": native alpha matches selected opacity; alpha="+alpha+", flags="+flags+", error="+error);
        }
        Near(1,window.Opacity,0,label+": WPF content stays fully opaque so native alpha is not applied twice");
    }
    private static void Capture(Window window,string path)
    {
        Pump(window); var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(path)) encoder.Save(file);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            TestAppearance(args[0],args[1]); TestSettings(args[0],args[1]);
            Console.WriteLine("PASS: "+assertions+" appearance assertions: icon toolbar, live opacity preview, light/dark subviews, unchanged ballistic/RAAS state, signed height-difference input, legacy migration and bounded settings. Offline fixtures only."); return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void TestAppearance(string maps,string output)
    {
        var window=new MainWindow(true,maps);
        try {
            window.Show(); Pump(window);
            var opacity=Named<ToggleButton>(window,"OpacityToggle"); var pin=Named<CheckBox>(window,"Pin");
            var theme=Named<ToggleButton>(window,"ThemeToggle"); var help=Named<Button>(window,"Help");
            var slider=Named<Slider>(window,"OpacitySlider"); var popup=Named<Popup>(window,"OpacityPopup");
            var sun=Named<System.Windows.Shapes.Path>(window,"SunIcon"); var moon=Named<System.Windows.Shapes.Path>(window,"MoonIcon");
            Check(opacity!=null && pin!=null && theme!=null && help!=null && slider!=null && popup!=null,"all four appearance tools and opacity popover exist");
            Check(sun!=null && moon!=null && !sun.IsVisible && moon.IsVisible,"dark mode displays the current-theme moon icon");
            Check(Named<FrameworkElement>(window,"ModeBadge")==null && Named<FrameworkElement>(window,"PageDescription")==null && Named<FrameworkElement>(window,"HeightHint")==null,"requested captions and offline badge are removed");
            Control[] tools={opacity,pin,theme,help}; double previous=-1;
            foreach(var tool in tools) {
                double left=tool.TranslatePoint(new Point(),window).X;
                Check(left>previous,"header order is opacity, pin, theme, help"); previous=left;
                Check(!(tool is ContentControl) || !(((ContentControl)tool).Content is string),"header tools use icons rather than text");
                Check(tool.ToolTip!=null && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(tool)),"icon tools expose a tooltip and accessible name");
            }
            Check(!popup.IsOpen,"opacity controls start closed"); opacity.IsChecked=true; Pump(window);
            Check(popup.IsOpen,"opacity icon opens its slider below the toolbar");
            Near(30,slider.Minimum,0,"opacity can retain at least thirty percent visibility"); Near(100,slider.Maximum,0,"opacity can restore full visibility");
            slider.Value=65; Pump(window); Near(0.65,Field<double>(window,"windowOpacity"),0.00001,"dragging opacity updates the selected preference immediately");
            NativeOpacity(window,0.65,"sixty-five percent opacity preview");
            pin.IsChecked=true; Pump(window); NativeOpacity(window,0.65,"pinning preserves native opacity");
            var nativeHandle=new WindowInteropHelper(window).Handle;
            Check(((IntPtr.Size==8?GetWindowLongPtr64(nativeHandle,-20).ToInt64():GetWindowLong32(nativeHandle,-20))&8)!=0,"native opacity preserves the Windows topmost style");
            pin.IsChecked=false; Pump(window); NativeOpacity(window,0.65,"unpinning preserves native opacity");
            Check(((IntPtr.Size==8?GetWindowLongPtr64(nativeHandle,-20).ToInt64():GetWindowLong32(nativeHandle,-20))&8)==0,"unpinning removes only the Windows topmost state");
            double originalWidth=window.Width; window.Width+=40; Pump(window); NativeOpacity(window,0.65,"resizing preserves native opacity"); window.Width=originalWidth;
            window.WindowState=WindowState.Maximized; Pump(window); NativeOpacity(window,0.65,"maximizing preserves native opacity");
            window.WindowState=WindowState.Normal; Pump(window); NativeOpacity(window,0.65,"restoring preserves native opacity");
            Console.WriteLine("PASS: real HWND uses WS_EX_LAYERED and queryable native alpha near 166/255 at 65% opacity.");
            slider.Value=slider.Minimum; Pump(window); Near(0.3,Field<double>(window,"windowOpacity"),0.00001,"minimum opacity keeps the controls recoverable"); NativeOpacity(window,0.3,"thirty percent preview");
            slider.Value=100; Pump(window); Near(1,Field<double>(window,"windowOpacity"),0.00001,"full opacity restores the opaque window"); NativeOpacity(window,1,"return to fully opaque rendering");
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            window.RaiseEvent(escape); Pump(window);
            Check(escape.Handled && !popup.IsOpen && opacity.IsChecked!=true,"Escape from the main window dismisses the opacity slider and resets its toggle");
            opacity.IsChecked=true; Pump(window);
            var popupEscape=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(popup.Child),Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            popup.Child.RaiseEvent(popupEscape); Pump(window);
            Check(popupEscape.Handled && !popup.IsOpen,"Escape from inside the popup also dismisses it");
            opacity.IsChecked=true; Pump(window);
            Named<TextBlock>(window,"PageTitle").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent}); Pump(window);
            Check(!popup.IsOpen && opacity.IsChecked!=true,"a routed click on the page outside the popup dismisses it");
            pin.IsChecked=true; Check(window.Topmost,"pin icon enables Windows topmost"); pin.IsChecked=false; Check(!window.Topmost,"pin icon disables Windows topmost");

            var picker=Field<ComboBox>(window,"mapSelector");
            picker.SelectedItem=picker.Items.Cast<object>().Single(item=>item.ToString()==MapCatalog.Load(maps).Single(m=>m.Id=="mestia").DisplayName);
            var weaponPoint=new Vec2(1000.125,1200.375); var targetPoint=new Vec2(1600.625,1400.875);
            Call(window,"PlacePoint",weaponPoint,true); Call(window,"PlacePoint",targetPoint,false); Pump(window);
            var solution=Field<FiringSolution>(window,"solution"); var map=Field<MapView>(window,"map");
            Check(solution!=null && solution.HighUsable,"signed SDK terrain produces a usable shot fixture");
            Check(!Field<TextBox>(window,"weaponHeight").IsVisible && !Field<TextBox>(window,"targetHeight").IsVisible,"neither absolute SDK height is visible");
            Check(!Named<FrameworkElement>(window,"ManualHeightInputs").IsVisible,"automatic maps do not expose manual height input");
            double shown=double.Parse(Named<TextBlock>(window,"Difference").Text.Replace(" m",""),CultureInfo.InvariantCulture);
            Near(solution.HeightDifference,shown,0.051,"only the signed target-minus-weapon height difference is displayed");
            Check(!Named<TextBlock>(window,"Status").IsVisible,"normal calculation status no longer adds a small-text line");
            slider.Value=65; Call(window,"ShowTrajectory"); var detail=Field<TrajectoryWindow>(window,"trajectoryWindow"); Pump(detail);
            NativeOpacity(detail,0.65,"a newly-opened trajectory window inherits the chosen window opacity");
            var profile=detail.Chart.Profile;
            slider.Value=55; Pump(window); Pump(detail); NativeOpacity(detail,0.55,"opacity changes also preview in an already-open trajectory window"); slider.Value=100; Pump(detail); NativeOpacity(detail,1,"trajectory also returns to fully opaque rendering");
            Near(0,double.Parse(((string)Call(detail.Chart,"FormatHeight",(double?)profile.WeaponGround)).Replace(" m",""),CultureInfo.InvariantCulture),0,"trajectory height readouts use the weapon ground as zero");
            Near(profile.TargetGround-profile.WeaponGround,double.Parse(((string)Call(detail.Chart,"FormatHeight",(double?)profile.TargetGround)).Replace(" m",""),CultureInfo.InvariantCulture),0.051,"trajectory readouts show signed relative height rather than negative SDK elevation");
            opacity.IsChecked=true; Pump(window); Check(popup.IsOpen,"opacity popover can remain visible during a live theme change");
            var dark=ColorOf(window.Background); var darkCard=ColorOf(Named<Border>(window,"Sidebar").Background); var darkDetail=ColorOf(detail.Background);
            var darkMap=Sample(map,2,2);
            var popupBorder=popup.Child as Border; Color? darkPopup=popupBorder==null?(Color?)null:ColorOf(popupBorder.Background);
            Check(Brightness(dark)<0.25 && Brightness(darkCard)<0.25,"default theme uses dark surfaces");
            theme.IsChecked=true; Pump(window); Pump(detail);
            Check(sun.IsVisible && !moon.IsVisible,"light mode displays the current-theme sun icon");
            Check(Brightness(ColorOf(window.Background))>0.8 && Brightness(ColorOf(Named<Border>(window,"Sidebar").Background))>0.8,"theme icon updates the current window and card to light surfaces");
            WarmSurface(ColorOf(window.Background),"application background"); WarmSurface(ColorOf(Named<Border>(window,"Sidebar").Background),"card background");
            WarmSurface(Sample(map,2,2),"map frame background updates from an already-rendered dark viewport");
            WarmSurface(ColorOf(detail.Background),"trajectory window background");
            Check(ColorOf(detail.Background)!=darkDetail && Brightness(ColorOf(detail.Background))>0.8,"an already-open trajectory window changes theme live");
            if(darkPopup.HasValue) Check(ColorOf(popupBorder.Background)!=darkPopup.Value,"an open opacity popover changes theme live");
            Check(Field<FiringSolution>(window,"solution")==solution && detail.Chart.Profile==profile,"theme changes preserve the current solution and profile model");
            Near(weaponPoint.X,map.Weapon.X,0,"theme changes preserve exact weapon coordinates"); Near(targetPoint.Y,map.Target.Y,0,"theme changes preserve exact target coordinates");
            opacity.IsChecked=false; Pump(window); Check(!popup.IsOpen,"opacity icon closes the slider without changing its setting");
            foreach(var size in new[]{new Size(1060,740),new Size(1280,850)}) {
                window.Width=size.Width; window.Height=size.Height;
                foreach(bool light in new[]{false,true}) {
                    theme.IsChecked=light; Pump(window);
                    Check(sun.IsVisible==light && moon.IsVisible!=light,"theme icon describes the visible theme at each viewport size");
                    if(light) WarmSurface(Sample(map,2,2),"light map frame stays warm at each viewport size");
                    else Check(Sample(map,2,2)==darkMap,"returning to dark restores the map frame background");
                    foreach(var tool in tools) { var topLeft=tool.TranslatePoint(new Point(),window); Check(topLeft.X>=0 && topLeft.Y>=0 && topLeft.X+tool.ActualWidth<window.ActualWidth,"icon toolbar remains inside each supported viewport"); }
                    Capture(window,Path.Combine(output,"appearance-"+(light?"light":"dark")+"-"+(int)size.Width+".png"));
                }
            }
            Capture(detail,Path.Combine(output,"appearance-trajectory-light.png")); detail.Close();

            Named<Button>(window,"RaasTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
            var raas=Field<RaasView>(window,"raasView"); raas.AutoComplete=false;
            var raasMaps=Field<ComboBox>(raas,"mapPicker"); raasMaps.SelectedItem=raasMaps.Items.Cast<RaasMapInfo>().Single(m=>m.Id=="goosebay");
            var layers=Field<ComboBox>(raas,"layerPicker"); layers.SelectedItem=layers.Items.Cast<RaasLayerInfo>().Single(l=>l.RawName=="GooseBay_RAAS_v2");
            raas.ClickPoint(raas.Session.Layer.Mains.First()); Pump(window);
            var beforeResult=raas.Session.Result; int beforeCount=raas.Session.Confirmed.Count;
            var candidates=Field<ListBox>(raas,"candidates"); var lightCandidates=ColorOf(candidates.Background);
            theme.IsChecked=false; Pump(window);
            Check(ColorOf(candidates.Background)!=lightCandidates && Brightness(ColorOf(candidates.Background))<0.25,"RAAS candidate panel updates to dark without rebuilding the session");
            Check(raas.Session.Result==beforeResult && raas.Session.Confirmed.Count==beforeCount,"theme switching preserves the RAAS chain and solver result");
            theme.IsChecked=true; Pump(window); Capture(window,Path.Combine(output,"appearance-raas-light.png"));

            Named<Button>(window,"ServerTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var browser=Field<ServerBrowserView>(window,"serverBrowser"); var snapshot=new ServerSnapshot{RetrievedAt=DateTime.UtcNow};
            snapshot.Servers.Add(new ServerRecord{SessionId=new string('a',32),Name="主题验证 · 离线样本",Map="GooseBay_RAAS_v2",Reported=87,Registered=83,Queue=2,Capacity=100,RetrievedAt=DateTime.UtcNow});
            browser.LoadSmokeSnapshot(snapshot); Pump(window);
            var table=Field<DataGrid>(browser,"table"); var lightTable=ColorOf(table.Background); var selected=table.SelectedItem;
            theme.IsChecked=false; Pump(window);
            Check(ColorOf(table.Background)!=lightTable && Brightness(ColorOf(table.Background))<0.25,"a populated server table changes theme live");
            Check(table.SelectedItem==selected && !browser.HasRequestedNetwork,"theme changes preserve server selection and do not query EOS");
            theme.IsChecked=true; Pump(window); Capture(window,Path.Combine(output,"appearance-server-light.png"));
            theme.IsChecked=false; Named<Button>(window,"MortarTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            picker.SelectedItem=picker.Items[picker.Items.Count-1]; Pump(window);
            var manual=Named<TextBox>(window,"ManualDifference");
            Check(manual.IsVisible,"maps without terrain expose one signed height-difference input");
            foreach(double delta in new[]{40.5,-25.75,0}) { manual.Text=delta.ToString(CultureInfo.InvariantCulture); Near(delta,Field<FiringSolution>(window,"solution").HeightDifference,1e-10,"manual signed height difference drives the shot"); }
            manual.Text="-1"; manual.CaretIndex=manual.Text.Length; manual.AppendText(".");
            Check(manual.Text=="-1.","live recalculation preserves an in-progress decimal instead of eating the decimal separator");
            manual.CaretIndex=manual.Text.Length; manual.AppendText("5"); Near(-1.5,Field<FiringSolution>(window,"solution").HeightDifference,1e-10,"typing a signed fractional height produces the intended value");
            manual.Text="NaN"; Pump(window);
            Check(Field<FiringSolution>(window,"solution")==null && Named<TextBlock>(window,"Difference").Text=="—" && Named<TextBlock>(window,"Status").IsVisible,"non-finite manual height clears stale shot values and shows an error");
            manual.Text="-12.5"; Check(Field<FiringSolution>(window,"solution")!=null,"a valid manual height recovers the calculation");
            picker.SelectedIndex=0; Pump(window);
            Check(!manual.IsVisible && Field<TextBox>(window,"weaponHeight").IsReadOnly,"switching back to terrain restores automatic sampling");
        } finally { window.Close(); UiTheme.Apply(false); }
    }
    private static void TestSettings(string maps,string output)
    {
        string folder=Path.Combine(output,"appearance-settings-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try {
            string settings=Path.Combine(folder,"settings.xml");
            File.WriteAllText(settings,"<settings><mapId>custom</mapId><weapon>D05-5-5</weapon><target>F06-5-5</target><weaponHeight>-78.88</weaponHeight><targetHeight>-58.14</targetHeight></settings>");
            var old=new MainWindow(false,maps,folder);
            try {
                Near(20.74,Field<FiringSolution>(old,"solution").HeightDifference,0.00001,"legacy SDK heights migrate to the same signed manual difference");
                Near(20.74,double.Parse(Named<TextBox>(old,"ManualDifference").Text,CultureInfo.InvariantCulture),0.00001,"legacy manual settings populate the single visible difference field");
                Near(1,Field<double>(old,"windowOpacity"),0,"legacy settings default to full visibility"); Check(Named<ToggleButton>(old,"ThemeToggle").IsChecked!=true,"legacy settings default to dark theme");
                Named<ToggleButton>(old,"ThemeToggle").IsChecked=true; Named<Slider>(old,"OpacitySlider").Value=57; Named<TextBox>(old,"ManualDifference").Text="-35.25"; Call(old,"SaveSettings");
                string saved=File.ReadAllText(settings); Check(saved.Contains("windowOpacity") && saved.Contains("lightTheme") && saved.Contains("heightDifference"),"appearance and manual difference preferences are saved");
            } finally {old.Close();}
            var restored=new MainWindow(false,maps,folder);
            try {
                Near(0.57,Field<double>(restored,"windowOpacity"),0.00001,"window opacity survives the settings round-trip");
                Check(Named<ToggleButton>(restored,"ThemeToggle").IsChecked==true && Brightness(ColorOf(restored.Background))>0.8,"light theme restores before first display");
                restored.Show(); Pump(restored); NativeOpacity(restored,0.57,"restored opacity applies to the real HWND on its first display");
                Near(-35.25,Field<FiringSolution>(restored,"solution").HeightDifference,0.00001,"manual height difference survives settings round-trip");
                Check(Field<ServerBrowserView>(restored,"serverBrowser")==null,"restoring appearance preferences does not create the server browser");
            } finally {restored.Close();}
            foreach(var example in new[]{Tuple.Create("-10",0.3),Tuple.Create("2",1.0),Tuple.Create("NaN",1.0),Tuple.Create("Infinity",1.0),Tuple.Create("broken",1.0)}) {
                File.WriteAllText(settings,"<settings><windowOpacity>"+example.Item1+"</windowOpacity><lightTheme>broken</lightTheme></settings>");
                var invalid=new MainWindow(false,maps,folder);
                try { Near(example.Item2,Field<double>(invalid,"windowOpacity"),0.00001,"out-of-range or corrupt saved opacity remains recoverable: "+example.Item1); Check(Named<ToggleButton>(invalid,"ThemeToggle").IsChecked!=true,"invalid theme preference falls back to dark"); }
                finally {invalid.Close();}
            }
        } finally { foreach(var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); UiTheme.Apply(false); }
    }
}
