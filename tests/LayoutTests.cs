using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SquadTools;

internal static class LayoutTests
{
    private static int assertions;
    private static void Check(bool condition,string label) { assertions++; if(!condition) throw new Exception(label); }
    private static T Field<T>(object target,string name) { return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target); }
    private static object Call(object target,string name,params object[] args) { return target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,args); }
    private static void Pump(MainWindow window) { window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{})); window.UpdateLayout(); }
    private static Rect Bounds(FrameworkElement element,Visual parent) { return new Rect(element.TranslatePoint(new Point(),(UIElement)parent),new Size(element.ActualWidth,element.ActualHeight)); }
    private static void Inside(FrameworkElement child,FrameworkElement parent,string label)
    {
        var rect=Bounds(child,parent);
        Check(rect.Left>=-1 && rect.Top>=-1 && rect.Right<=parent.ActualWidth+1 && rect.Bottom<=parent.ActualHeight+1 && rect.Width>0 && rect.Height>0,label+": "+rect+" / "+parent.ActualWidth+"x"+parent.ActualHeight);
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
            var child=VisualTreeHelper.GetChild(parent,i); var typed=child as T; if(typed!=null) yield return typed;
            foreach(var nested in Children<T>(child)) yield return nested;
        }
    }
    private static void Capture(MainWindow window,string path)
    {
        Pump(window); var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var stream=File.Create(path)) encoder.Save(stream);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            var window=new MainWindow(true,args[0]);
            try { window.Show(); TestShooting(window,args[1]); TestServers(window,args[1]); TestRaas(window,args[1]); }
            finally { window.Close(); }
            Console.WriteLine("PASS: "+assertions+" layout assertions: three viewport sizes, all weapons, manual heights, slim scrollbar paging, no scrolling detail card, long/missing server fields, advanced filters and RAAS controls. No network or user settings."); return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void TestShooting(MainWindow window,string output)
    {
        var root=(FrameworkElement)window.Content; var sidebar=(Border)root.FindName("Sidebar"); var results=(StackPanel)root.FindName("ResultPanel");
        var scroll=(ScrollViewer)root.FindName("InputScroll"); var picker=Field<ComboBox>(window,"mapSelector");
        var automaticMap=picker.SelectedItem; var manualMap=picker.Items[picker.Items.Count-1];
        Check(root.FindName("AutomaticHeight")==null,"automatic-height switch has been removed");
        foreach(var size in new[]{new Size(1060,740),new Size(1280,850),new Size(1440,900)}) {
            window.Width=size.Width; window.Height=size.Height; Pump(window);
            foreach(var weapon in WeaponCatalog.All) foreach(bool manual in new[]{false,true}) {
                picker.SelectedItem=manual?manualMap:automaticMap; Call(window,"SelectWeapon",weapon.Id,weapon.Shells[0].Id); Pump(window);
                var mapCard=(Border)root.FindName("MapCard");
                Check(Math.Abs(sidebar.ActualHeight-mapCard.ActualHeight)<1 && Math.Abs(Bounds(sidebar,root).Top-Bounds(mapCard,root).Top)<1,"shooting sidebar and entire map card have matching height and top edge");
                Check(((Grid)root.FindName("HeightInputs")).IsVisible,"elevation fields remain visible for every weapon and map type");
                Check(Field<TextBox>(window,"weaponHeight").IsReadOnly!=manual && Field<TextBox>(window,"targetHeight").IsReadOnly!=manual,"available terrain is always automatic; missing terrain permits manual entry");
                if(!manual) {
                    var terrain=Field<TerrainHeightMap>(window,"terrain"); var map=Field<MapView>(window,"map");
                    double shown=double.Parse(Field<TextBox>(window,"weaponHeight").Text,System.Globalization.CultureInfo.InvariantCulture);
                    Check(Math.Abs(shown-terrain.Sample(map.Weapon))<0.005001,"visible elevation samples the actual weapon position");
                }
                Inside(results,sidebar,"results always fit sidebar: "+weapon.Id+" / "+size+" / "+manual);
                foreach(string id in new[]{"Bearing","Elevation","Status","Distance","Difference","Flight"}) Inside((FrameworkElement)root.FindName(id),results,"readout remains visible: "+id);
                Check(Bounds((FrameworkElement)root.FindName("Bearing"),results).Right<=Bounds((FrameworkElement)root.FindName("Elevation"),results).Left+1,"bearing and elevation do not overlap");
                Check(scroll.ComputedHorizontalScrollBarVisibility==Visibility.Collapsed,"shooting inputs do not require horizontal scrolling");
                if(size.Height>=850) Check(scroll.ScrollableHeight<1,"normal-size input panel fits without scrolling: "+weapon.Id+" / "+manual+" / "+scroll.ScrollableHeight);
                if(scroll.ScrollableHeight<1) {
                    double gap=Bounds(results,sidebar).Top-Bounds(scroll,sidebar).Top-scroll.ExtentHeight;
                    Check(gap>=-1 && gap<=24,"results follow inputs without a stretched blank band: "+weapon.Id+" / "+size+" / "+manual+" / "+gap);
                }
                if(manual) {
                    scroll.ScrollToEnd(); Pump(window); Inside(Field<TextBox>(window,"weaponHeight"),scroll,"manual weapon height is reachable"); Inside(Field<TextBox>(window,"targetHeight"),scroll,"manual target height is reachable");
                }
            }
            picker.SelectedItem=automaticMap; Field<TextBox>(window,"weaponGrid").Text="invalid"; Pump(window); Inside((FrameworkElement)root.FindName("Status"),results,"invalid-input message fits fixed results");
            Check(string.IsNullOrEmpty(Field<TextBox>(window,"weaponHeight").Text) && string.IsNullOrEmpty(Field<TextBox>(window,"targetHeight").Text),"invalid coordinates clear stale automatic elevations");
            Field<TextBox>(window,"weaponGrid").Text="D05-5-5"; Check(!string.IsNullOrEmpty(Field<TextBox>(window,"weaponHeight").Text),"valid coordinates recover automatic elevation display");
        }
        window.Width=1060; window.Height=740; picker.SelectedItem=manualMap; Call(window,"SelectWeapon","m1064","impact"); Pump(window);
        Check(scroll.ScrollableHeight>1,"minimum-size multi-ammunition/manual configuration exercises scrolling");
        var bar=(ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar",scroll);
        Check(bar.ActualWidth<=8 && bar.ActualWidth>0,"vertical scrollbar uses at most eight pixels; actual="+bar.ActualWidth);
        scroll.ScrollToTop(); Pump(window); ScrollBar.PageDownCommand.Execute(null,bar); Pump(window);
        Check(scroll.VerticalOffset>0,"custom scrollbar still handles page-down commands"); scroll.ScrollToEnd(); Pump(window);
        Inside(Field<TextBox>(window,"targetHeight"),scroll,"end of manual inputs is reachable after paging");
        var horizontal=new ScrollViewer{Width=220,Height=80,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,Content=new Border{Width=1000,Height=30}};
        var rootGrid=(Grid)root; Grid.SetRow(horizontal,2); rootGrid.Children.Add(horizontal);
        try {
            Pump(window); var horizontalBar=(ScrollBar)horizontal.Template.FindName("PART_HorizontalScrollBar",horizontal);
            Check(horizontalBar.ActualHeight>0 && horizontalBar.ActualHeight<=8,"horizontal scrollbar does not inherit a tall vertical thumb");
            ScrollBar.PageRightCommand.Execute(null,horizontalBar); Pump(window); Check(horizontal.HorizontalOffset>0,"horizontal page-right commands still work");
        } finally { rootGrid.Children.Remove(horizontal); }
        Capture(window,Path.Combine(output,"layout-shooting-min.png"));
        window.Width=1280; window.Height=850; picker.SelectedItem=automaticMap; Call(window,"SelectWeapon","mortar",null); scroll.ScrollToTop(); Capture(window,Path.Combine(output,"layout-shooting.png"));
    }
    private static void TestServers(MainWindow window,string output)
    {
        Call(window,"SelectServerPage",true); var browser=Field<ServerBrowserView>(window,"serverBrowser"); var view=(FrameworkElement)browser.Child;
        var longName="布局验证 · 模拟数据 · "+new string('长',300);
        var server=new ServerRecord {SessionId=new string('a',32),Name=longName,Map="GooseBay_RAAS_v2",Address="2001:db8:1234:5678:9abc:def0:1234:5678",Port=7787,TeamOne="USA_LO_CombinedArms",TeamTwo="RGF_LO_CombinedArms",Reported=88,Registered=76,Observed=63,ObservedAt=DateTime.UtcNow,RetrievedAt=DateTime.UtcNow,Queue=5,ReservedQueue=2,Capacity=100,CheckState="待复核",DuplicateCount=2,Version="v10.6"};
        var snapshot=new ServerSnapshot{RetrievedAt=DateTime.UtcNow}; snapshot.Servers.Add(server); browser.LoadSmokeSnapshot(snapshot);
        foreach(var size in new[]{new Size(1060,740),new Size(1280,850),new Size(1440,900)}) {
            window.Width=size.Width; window.Height=size.Height;
            foreach(bool expanded in new[]{false,true}) {
                ((Expander)view.FindName("AdvancedFilters")).IsExpanded=expanded; Pump(window);
                var card=(Border)view.FindName("DetailCard"); Inside(card,view,"detail card fits viewport");
                Check(!Children<ScrollViewer>(card).Any(),"server detail card contains no scroll viewer");
                foreach(string id in new[]{"DetailTitle","DetailMeta","DetailReported","DetailRegistered","DetailObserved","DetailQueue","Detail","DetailInfo","Follow","Check","Favorite"}) Inside((FrameworkElement)view.FindName(id),card,"server detail item stays within card: "+id);
                Check(((DataGrid)view.FindName("Servers")).ActualHeight>=100,"server list remains usable with expanded filters");
            }
        }
        Check(((TextBlock)view.FindName("DetailReported")).Text=="88" && ((TextBlock)view.FindName("DetailRegistered")).Text=="76" && ((TextBlock)view.FindName("DetailObserved")).Text=="63","compact card preserves separate population sources");
        var full=((TextBlock)((TextBlock)view.FindName("DetailInfo")).ToolTip).Text;
        Check(full.Contains(longName) && full.Contains(server.Endpoint) && full.Contains("100") && full.Contains("v10.6"),"full name, IPv6 endpoint, capacity and version remain accessible");
        var tooltipText=new string('长',200); var tooltip=new ToolTip{Content=tooltipText,Style=(Style)((FrameworkElement)window.Content).FindResource(typeof(ToolTip)),PlacementTarget=view,IsOpen=true};
        try {
            Pump(window); var wrapped=Children<TextBlock>(tooltip).Single(t=>t.Text==tooltipText);
            Check(wrapped.ActualWidth<=491 && wrapped.ActualHeight>30,"long string tooltips wrap instead of clipping retained information");
        } finally { tooltip.IsOpen=false; }
        Call(window,"ChooseServerMap",server); Pump(window);
        Inside((TextBlock)view.FindName("FollowStatus"),view,"long followed server name fits summary row");
        server.Reported=null; server.Registered=null; server.Observed=null; server.Queue=null; server.ReservedQueue=null; server.ObservedAt=DateTime.MinValue; browser.LoadSmokeSnapshot(snapshot); Pump(window);
        Check(((TextBlock)view.FindName("DetailReported")).Text=="—" && ((TextBlock)view.FindName("DetailObserved")).Text=="—","unavailable counts remain unknown rather than zero");
        server.Name="布局验证 · 数据模拟"; server.Reported=88; server.Registered=76; server.Queue=5; server.ReservedQueue=2; browser.LoadSmokeSnapshot(snapshot); browser.FollowServer(server.SessionId,server.Name);
        ((Expander)view.FindName("AdvancedFilters")).IsExpanded=false; window.Width=1060; window.Height=740; Capture(window,Path.Combine(output,"layout-server-min.png"));
        window.Width=1280; window.Height=850; Capture(window,Path.Combine(output,"layout-server.png"));
        Call(window,"StopServerFollow");
    }
    private static void TestRaas(MainWindow window,string output)
    {
        var root=(FrameworkElement)window.Content; ((Button)root.FindName("RaasTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var view=Field<RaasView>(window,"raasView"); var maps=Field<ComboBox>(view,"mapPicker"); maps.SelectedItem=maps.Items.Cast<RaasMapInfo>().Single(m=>m.Id=="goosebay");
        var layers=Field<ComboBox>(view,"layerPicker"); layers.SelectedItem=layers.Items.Cast<RaasLayerInfo>().Single(l=>l.RawName=="GooseBay_RAAS_v2");
        view.ClickPoint(view.Session.Layer.Mains.Single(m=>m.Id==view.Session.Solver.End)); view.ClickPoint(view.Session.Layer.Flags.Single(f=>f.Name=="FOB Inkpen"));
        foreach(var size in new[]{new Size(1060,740),new Size(1280,850),new Size(1440,900)}) {
            window.Width=size.Width; window.Height=size.Height; Pump(window);
            Check(Field<ListBox>(view,"candidates").ActualHeight>=80,"RAAS candidate list retains useful height: "+size+" / "+Field<ListBox>(view,"candidates").ActualHeight);
            foreach(string id in new[]{"confirm","undo","reset"}) Inside(Field<Button>(view,id),(Border)root.FindName("Sidebar"),"RAAS action stays visible: "+id);
        }
        window.Width=1060; window.Height=740; view.ShowNotice(new string('测',200)); Pump(window); Check(Field<TextBlock>(view,"message").ActualHeight<=61,"long RAAS notice stays bounded"); Check(Convert.ToString(Field<TextBlock>(view,"message").ToolTip).Contains(new string('测',200)),"full RAAS notice remains accessible");
        Check(Field<ListBox>(view,"candidates").ActualHeight>=80,"long notice at minimum size leaves candidate list usable");
        view.ShowNotice(null); window.Width=1280; window.Height=850; Capture(window,Path.Combine(output,"layout-raas.png"));
    }
}
