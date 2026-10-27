using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SquadTools;

internal static class ReviewTests
{
    private static int assertions;
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object target,string name) { return (T)target.GetType().GetField(name,Private).GetValue(target); }
    private static object Call(object target,string name,params object[] args) { return target.GetType().GetMethod(name,Private).Invoke(target,args); }
    private static void Check(bool condition,string label) { assertions++; if(!condition) throw new Exception(label); }
    private static void Near(double expected,double actual,double tolerance,string label) { Check(!double.IsNaN(actual) && Math.Abs(expected-actual)<=tolerance,label+": "+expected+" != "+actual); }
    private static void Pump(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{})); window.UpdateLayout(); }
    private static T Child<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
            var current=VisualTreeHelper.GetChild(parent,i); if(current is T) return (T)current;
            var found=Child<T>(current); if(found!=null) return found;
        }
        return null;
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            TestPreviewScroll(args[0],args[1]); TestHeightDatum(args[0]);
            Console.WriteLine("PASS: "+assertions+" CR assertions: preview scroll/selection retention, signed SDK height datum, datum-invariant firing solutions and terrain profiles, explicit UI height reference. No network or user settings."); return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void TestPreviewScroll(string maps,string layers)
    {
        var repository=new RaasRepository(layers); Tuple<string,string,string,int> best=null;
        foreach(var map in repository.Maps) foreach(var info in map.Layers) {
            var layer=repository.Load(map.Id,info.RawName);
            foreach(var main in layer.Mains) {
                var session=new RaasSession(layer); session.ChooseMain(main);
                int count=layer.Flags.Count(f=>session.Result.Chance(f,session.NextStep)>0);
                if(best==null || count>best.Item4) best=Tuple.Create(map.Id,info.RawName,main.Id,count);
            }
        }
        Check(best.Item4>=8,"real layer provides enough initial candidates to exercise scrolling");
        var window=new MainWindow(true,maps);
        try {
            window.Width=1060; window.Height=740; window.Show(); var root=(FrameworkElement)window.Content;
            ((Button)root.FindName("RaasTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var view=Field<RaasView>(window,"raasView"); view.AutoComplete=false;
            var mapPicker=Field<ComboBox>(view,"mapPicker"); mapPicker.SelectedItem=mapPicker.Items.Cast<RaasMapInfo>().Single(m=>m.Id==best.Item1);
            var layerPicker=Field<ComboBox>(view,"layerPicker"); layerPicker.SelectedItem=layerPicker.Items.Cast<RaasLayerInfo>().Single(l=>l.RawName==best.Item2);
            view.ClickPoint(view.Session.Layer.Mains.Single(m=>m.Id==best.Item3));
            var list=Field<ListBox>(view,"candidates"); list.Height=90; list.SelectedIndex=-1; Pump(window);
            var scroll=Child<ScrollViewer>(list); Check(scroll!=null && scroll.ScrollableHeight>0,"candidate list really scrolls");
            scroll.ScrollToEnd(); Pump(window); double previous=scroll.VerticalOffset;
            Check(previous>0,"candidate list is positioned away from its beginning");
            var candidate=list.Items.Cast<RaasCandidate>().Last(); var result=view.Session.Result;
            view.PreviewPoint(candidate.Point); Pump(window); view.PreviewPoint(null); Pump(window);
            Near(previous,scroll.VerticalOffset,0.001,"leaving a hypothetical preview must retain the candidate list's scroll position");
            Check(view.Session.Result==result && view.Session.Confirmed.Count==0,"scroll preservation does not commit the preview");
            candidate=list.Items.Cast<RaasCandidate>().Last(); list.SelectedItem=candidate; scroll.ScrollToEnd(); Pump(window);
            var row=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(candidate); Check(row!=null && row.Focus(),"candidate row accepts keyboard focus");
            view.PreviewPoint(candidate.Point); Pump(window); view.PreviewPoint(null); Pump(window);
            Check(list.IsKeyboardFocusWithin,"leaving hover must preserve keyboard access to candidate confirmation");
            Check(list.SelectedItem is RaasCandidate && ((RaasCandidate)list.SelectedItem).Point==candidate.Point && Field<Button>(view,"confirm").IsEnabled,"leaving hover retains the selected physical candidate and confirmation action");
            Field<Button>(view,"confirm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(view.Session.Confirmed.Contains(candidate.Point),"real confirmation still updates the state after preview exit");
        } finally {window.Close();}
    }
    private static void TestHeightDatum(string mapsDirectory)
    {
        var maps=MapCatalog.Load(mapsDirectory); var mestia=maps.Single(m=>m.Id=="mestia");
        Near(-153.9385,mestia.HeightOffset,1e-9,"Mestia uses its SDK world-Z datum");
        var terrain=TerrainHeightMap.Load(mestia); var first=new Vec2(1000,1200); var last=new Vec2(1600,1400);
        double h1=terrain.Sample(first),h2=terrain.Sample(last); var weapon=WeaponCatalog.Find("tech-mortar"); var shell=weapon.Shells[0];
        var signed=Ballistics.Solve(first,last,h1,h2,weapon,shell);
        var translated=Ballistics.Solve(first,last,h1-mestia.HeightOffset,h2-mestia.HeightOffset,weapon,shell);
        Check(signed.HighUsable && translated.HighUsable,"height datum examples both have valid high arcs");
        Near(signed.HeightDifference,translated.HeightDifference,1e-10,"a common height datum cannot change ground-height difference");
        Near(signed.HighMil,translated.HighMil,1e-10,"a common height datum cannot change elevation");
        Near(signed.HighTimeSeconds,translated.HighTimeSeconds,1e-10,"a common height datum cannot change flight time");
        var profile=TrajectoryProfile.Build(first,last,h1,h2,weapon,shell,signed,terrain.Sample);
        var shifted=TrajectoryProfile.Build(first,last,h1-mestia.HeightOffset,h2-mestia.HeightOffset,weapon,shell,translated,p=>terrain.Sample(p)-mestia.HeightOffset);
        foreach(int i in new[]{0,profile.Samples.Count/2,profile.Samples.Count-1}) {
            Near(profile.Samples[i].High.Value-mestia.HeightOffset,shifted.Samples[i].High.Value,1e-8,"trajectory translates by the same datum");
            Near(profile.Samples[i].Ground.Value-mestia.HeightOffset,shifted.Samples[i].Ground.Value,1e-8,"terrain translates by the same datum");
        }
        var window=new MainWindow(true,mapsDirectory);
        try {
            var picker=Field<ComboBox>(window,"mapSelector"); picker.SelectedItem=picker.Items.Cast<object>().Single(item=>item.ToString()==mestia.DisplayName);
            Call(window,"PlacePoint",first,true); Call(window,"PlacePoint",last,false);
            Near(h1,double.Parse(Field<TextBox>(window,"weaponHeight").Text,CultureInfo.InvariantCulture),0.00501,"signed height is displayed without abs or zero clamping");
            Check(Field<TextBlock>(window,"heightHint").Text.Contains("SDK") && Field<TextBlock>(window,"terrainNote").Text.Contains("海拔"),"automatic height explanation names its SDK datum and distinguishes sea-level altitude");
            Check(Field<TextBox>(window,"weaponHeight").IsReadOnly,"datum explanation preserves automatic terrain sampling");
        } finally {window.Close();}
    }
}
