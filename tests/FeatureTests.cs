using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SquadTools;

internal static class FeatureTests
{
    private static int assertions;
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static void Check(bool condition,string label) { assertions++; if(!condition) throw new Exception(label); }
    private static void Near(double expected,double actual,double tolerance,string label)
    { Check(!double.IsNaN(actual) && !double.IsInfinity(actual) && Math.Abs(expected-actual)<=tolerance,label+": "+expected+" != "+actual); }
    private static T Field<T>(object target,string name) { return (T)target.GetType().GetField(name,Private).GetValue(target); }
    private static object Call(object target,string name,params object[] args) { return target.GetType().GetMethod(name,Private).Invoke(target,args); }
    private static void Pump(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{})); window.UpdateLayout(); }
    private static void Capture(Window window,string path)
    {
        Pump(window); var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var stream=File.Create(path)) encoder.Save(stream);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            TestMath(); TestFootprintRendering(); var repository=new RaasRepository(args[1]); TestAllPredictions(repository); TestRaasTransactions(repository);
            TestWindow(args[0],args[2]); TestSettings(args[0],args[2]);
            Console.WriteLine("PASS: "+assertions+" feature assertions: RAAS non-mutating preview and reversible automatic chains, all 39 layers/both sides, arc selection, upstream damage references, scatter orientation, trajectory endpoints, missing terrain, live WPF updates and isolated settings. No network.");
            return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static Rect FindFootprint(Drawing drawing,Matrix transform)
    {
        var group=drawing as DrawingGroup;
        if(group!=null) {
            var next=group.Transform==null?Matrix.Identity:group.Transform.Value; next.Append(transform);
            foreach(var child in group.Children) { var found=FindFootprint(child,next); if(!found.IsEmpty) return found; }
        }
        var geometry=drawing as GeometryDrawing;
        if(geometry!=null && geometry.Brush is SolidColorBrush && ((SolidColorBrush)geometry.Brush).Color==(Color)ColorConverter.ConvertFromString("#2894C9FF")) {
            var bounds=geometry.Geometry.Bounds; bounds.Transform(transform); return bounds;
        }
        return Rect.Empty;
    }
    private static void TestFootprintRendering()
    {
        var weapon=WeaponCatalog.Find("tech-mortar"); var start=new Vec2(1000,1000); var end=new Vec2(1900,1000);
        var shot=Ballistics.Solve(start,end,0,0,weapon,weapon.Shells[0]); var area=ShotFootprint.Calculate(shot,weapon,weapon.Shells[0],true);
        var view=new MapView { WorldSize=3000,WorldHeight=3000,Weapon=start,Target=end,HasMarkers=true };
        view.Measure(new Size(900,650)); view.Arrange(new Rect(0,0,900,650));
        double scale=(double)typeof(MapView).GetProperty("Scale",Private).GetValue(view,null);
        view.SetShotOverlay(area,true,false,90); Call(view,"UpdateViewport");
        var east=FindFootprint(Field<DrawingVisual>(view,"overlay").Drawing,Matrix.Identity);
        Near(2*area.AlongRadius*scale,east.Width,1e-8,"east-facing ellipse along axis is horizontal");
        Near(2*area.AcrossRadius*scale,east.Height,1e-8,"east-facing ellipse cross axis is vertical");
        view.SetShotOverlay(area,true,true,0); Call(view,"UpdateViewport");
        var north=FindFootprint(Field<DrawingVisual>(view,"overlay").Drawing,Matrix.Identity);
        Near(east.Height,north.Width,1e-8,"north-facing spread rotates with bearing"); Near(east.Width,north.Height,1e-8,"north-facing spread exchanges screen axes");
        int builds=view.SceneBuildCount; typeof(MapView).GetField("pan",Private).SetValue(view,new Vector(70,60)); Call(view,"UpdateViewport");
        Check(view.SceneBuildCount==builds,"panning still retains scene with spread and damage overlays");
        view.SetShotOverlay(area,false,true,0); Call(view,"UpdateViewport"); Check(FindFootprint(Field<DrawingVisual>(view,"overlay").Drawing,Matrix.Identity).IsEmpty,"hiding spread removes its drawing while retaining damage");
        view.SetShotOverlay(null,true,true,0); Call(view,"UpdateViewport"); Check(view.Footprint==null,"invalidating selected arc clears cached map analysis");
    }
    private static void TestMath()
    {
        // Golden damage radii from SquadCalc 24828d6d weapons.js + calculateDistanceForDamage.
        double[,] references={
            {50,6.505627907419443,12.538085433373755},{300,6.5016734794994475,15.089321954648582},
            {100,10.798147989354469,40.19950248448357},{50,6.424421722598438,12.496142858281475},
            {300,6.5016734794994475,15.089321954648582},{200,10.712418884983304,28.92788006228523},
            {40,7.1419195515942,13.057512136672724},{50,5.723635208501674,41.99428587843335},
            {50,2.2424021686179114,11.83216007744565},{15,2.7104921113743004,11.92972808149032}};
        int index=0;
        foreach(var weapon in WeaponCatalog.All) foreach(var shell in weapon.Shells) {
            Near(references[index,0],shell.Moa,0,weapon.Id+" source MOA");
            Near(references[index,1],ShotFootprint.DamageRadius(shell,100),1e-10,weapon.Id+" 100 damage reference");
            Near(references[index,2],ShotFootprint.DamageRadius(shell,25),1e-10,weapon.Id+" 25 damage reference");
            Near(0,ShotFootprint.DamageRadius(shell,shell.ExplosionDamage+1),0,"damage above maximum has no radius"); index++;
            foreach(double distance in new[]{100.0,500.0,900.0}) foreach(double delta in new[]{-100.0,0.0,100.0}) {
                var start=new Vec2(123.456,234.567); var end=new Vec2(start.X+distance,start.Y+50);
                var shot=Ballistics.Solve(start,end,25,25+delta,weapon,shell);
                foreach(ArcPreference mode in Enum.GetValues(typeof(ArcPreference))) {
                    bool high; bool available=shot.TrySelectArc(mode,out high);
                    Check(available==(high?shot.HighUsable:shot.LowUsable),"arc availability follows requested arc");
                    if(mode==ArcPreference.High) Check(high,"explicit high cannot silently become low");
                    if(mode==ArcPreference.Low) Check(!high,"explicit low cannot silently become high");
                    if(mode==ArcPreference.Automatic) Check(high==shot.SelectedHigh,"automatic preserves default/fallback behavior");
                    var footprint=ShotFootprint.Calculate(shot,weapon,shell,high);
                    Check((footprint!=null)==available,"invalid arcs cannot leave a footprint");
                    if(footprint!=null) Check(footprint.AlongRadius>=0 && footprint.AcrossRadius>=0 && footprint.Damage25Radius>=footprint.Damage100Radius,"finite ordered footprint");
                }
                if(!shot.HighUsable && !shot.LowUsable) continue;
                int samples=0; Vec2 first=new Vec2(),last=new Vec2();
                var profile=TrajectoryProfile.Build(start,end,25,25+delta,weapon,shell,shot,p=>{if(samples++==0) first=p;last=p;return 7+p.X*0.02;});
                Check(profile.HasTerrain && samples==profile.Samples.Count && samples>=129 && samples<=2049,"bounded terrain sampling");
                Near(start.X,first.X,1e-12,"precise start sampling"); Near(end.X,last.X,1e-9,"precise end sampling"); Near(end.Y,last.Y,1e-9,"precise end Y sampling");
                foreach(bool high in new[]{true,false}) {
                    bool usable=high?shot.HighUsable:shot.LowUsable;
                    var heights=profile.Samples.Select(s=>high?s.High:s.Low).ToList();
                    Check(heights.All(h=>h.HasValue)==usable,"only usable arcs have curves");
                    if(usable) {
                        Near(25+weapon.MuzzleHeight,heights.First().Value,1e-7,"curve begins at actual muzzle height");
                        Near(25+delta,heights.Last().Value,1e-6,"curve lands at target surface after sight correction");
                        Check(heights.All(h=>!double.IsNaN(h.Value) && !double.IsInfinity(h.Value)),"curve contains finite values");
                    }
                }
                var unknown=TrajectoryProfile.Build(start,end,25,25+delta,weapon,shell,shot,null);
                Check(!unknown.HasTerrain && unknown.Samples.All(s=>!s.Ground.HasValue),"missing terrain is unknown, never an invented flat surface");
            }
        }
        var truck=WeaponCatalog.Find("tech-mortar"); double range=110*110*Math.Sqrt(3)/(2*9.78);
        var known=Ballistics.Solve(new Vec2(0,0),new Vec2(range,0),0,2.5,truck,truck.Shells[0]);
        var area=ShotFootprint.Calculate(known,truck,truck.Shells[0],true);
        Near(31.16649417573991,area.AcrossRadius,1e-9,"known 60-degree launch uses consistent flight time for cross spread");
        Near(17.993983802060125,area.AlongRadius,1e-9,"known spread uses launch angle, not corrected 55-degree sight");
        var curve=TrajectoryProfile.Build(new Vec2(0,0),new Vec2(range,0),0,2.5,truck,truck.Shells[0],known,null);
        var middle=curve.Samples[curve.Samples.Count/2];
        double fraction=middle.Distance/range;
        Near(2.5+110*110*3/(2*9.78)*fraction*(1-fraction),middle.High.Value,1e-7,"known analytic trajectory interior");
    }
    private static void TestAllPredictions(RaasRepository repository)
    {
        foreach(var map in repository.Maps) foreach(var info in map.Layers) {
            var layer=repository.Load(map.Id,info.RawName);
            foreach(var main in layer.Mains) {
                var session=new RaasSession(layer); session.ChooseMain(main); int guard=0;
                while(!session.Complete && guard++<128) {
                    var point=layer.Flags.First(f=>session.Result.Chance(f,session.NextStep)>0);
                    var before=session.Result; int depth=session.Confirmed.Count;
                    var preview=session.Preview(point);
                    Check(preview!=null && preview.Alive>0 && preview.Alive<=before.Alive,"preview narrows valid routes: "+info.RawName);
                    Check(session.Result==before && session.Confirmed.Count==depth && session.Automatic.Count<=depth,"preview cannot mutate actual state");
                    session.AdvanceUnique();
                    Check(session.Automatic.All(session.Confirmed.Contains),"automatic labels only refer to retained points");
                    if(!session.Complete) Check(session.Confirm(layer.Flags.First(f=>session.Result.Chance(f,session.NextStep)>0)),"manual choice can continue after auto-fill");
                }
                Check(session.Complete,"auto-fill and manual branch choices complete from either side: "+info.RawName);
                session.Reset(); Check(session.Confirmed.Count==0 && session.Automatic.Count==0,"reset clears inferred labels");
            }
        }
    }
    private static void TestRaasTransactions(RaasRepository repository)
    {
        var layer=new RaasLayer { RawName="Synthetic",Team1="USA",Team2="RGF" };
        var main=new RaasPoint { Id="Team1",Name="Main",IsMain=true,Position=new Vec2(100,100) };
        layer.Mains.Add(main); layer.Mains.Add(new RaasPoint {Id="Team2",Name="Main",IsMain=true,Position=new Vec2(1000,1000)});
        foreach(string id in new[]{"a","b","c","d"}) layer.Flags.Add(new RaasPoint {Id=id,Name=id,CandidateIds=new List<string>{id},Position=new Vec2(300+layer.Flags.Count*100,400)});
        layer.Clusters.Add(new RaasCluster {Id="A",Ids=new List<string>{"a"}}); layer.Clusters.Add(new RaasCluster {Id="B",Ids=new List<string>{"b","c"}}); layer.Clusters.Add(new RaasCluster {Id="C",Ids=new List<string>{"d"}});
        foreach(var edge in new[]{new[]{"Team1","A"},new[]{"A","B"},new[]{"B","C"},new[]{"C","Team2"}}) layer.Links.Add(new RaasLink {From=edge[0],To=edge[1]});
        var view=new RaasView(repository); typeof(RaasView).GetProperty("Session").GetSetMethod(true).Invoke(view,new object[]{new RaasSession(layer)});
        Check(view.AutoComplete,"auto-fill is enabled by default"); view.ClickPoint(main);
        Check(view.Session.Confirmed.Count==1 && view.Session.Automatic.Contains(layer.Flags[0]),"unique first point is inferred");
        view.ClickPoint(layer.Flags[1]);
        Check(view.Session.Confirmed.Count==3 && view.Session.Automatic.Count==2 && view.Session.Complete,"manual branch cascades through unique next point");
        Field<Button>(view,"undo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(view.Session.Confirmed.Count==1 && view.Session.Automatic.Count==1 && !view.Session.Complete,"one undo reverses manual action and dependent auto-fill");
        Field<Button>(view,"undo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(view.Session.Confirmed.Count==0,"auto-fill does not immediately undo the user's undo");
        view.AutoComplete=false; view.ClickPoint(main); Check(view.Session.Confirmed.Count==0,"auto-fill can be disabled");
        view.AutoComplete=true; Check(view.Session.Confirmed.Count==1,"enabling auto-fill advances a unique candidate");
        IList<RaasMarker> previewMarkers=null; view.MarkersChanged+=markers=>previewMarkers=markers;
        view.PreviewPoint(layer.Flags[1]); Check(view.Session.Confirmed.Count==1,"hover stays transient with auto-fill enabled");
        Check(previewMarkers.Any(m=>m.Point==layer.Flags[3] && !m.Clickable),"hypothetical next flag cannot become an actual click target before its prerequisite");
        view.ClickPoint(layer.Flags[3]); Check(view.Session.Confirmed.Count==1 && previewMarkers.All(m=>!m.IsPreview),"invalid forecast confirmation clears preview without changing the real chain");
        view.PreviewPoint(null); Field<Button>(view,"reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(view.Session.Confirmed.Count==0,"reset intentionally clears the whole chain without refilling");
        var variable=new RaasLayer(); variable.Mains.AddRange(layer.Mains); variable.Flags.AddRange(layer.Flags.Take(2)); variable.Clusters.AddRange(layer.Clusters.Take(1));
        variable.Clusters.Add(new RaasCluster {Id="B",Ids=new List<string>{"b"}});
        foreach(var edge in new[]{new[]{"Team1","A"},new[]{"A","Team2"},new[]{"A","B"},new[]{"B","Team2"}}) variable.Links.Add(new RaasLink {From=edge[0],To=edge[1]});
        var shortRoutes=new RaasSession(variable); shortRoutes.ChooseMain(variable.Mains[0]);
        Check(shortRoutes.AdvanceUnique()==1 && shortRoutes.Confirmed.Count==1,"a sole point on only some remaining routes must not be inferred as certain");
    }
    private static void TestWindow(string maps,string output)
    {
        var window=new MainWindow(true,maps);
        try {
            window.Show(); var root=(FrameworkElement)window.Content; var picker=Field<ComboBox>(window,"mapSelector"); var original=picker.SelectedItem;
            picker.SelectedItem=picker.Items[picker.Items.Count-1]; Call(window,"SelectWeapon","tech-mortar",null);
            Field<TextBox>(window,"weaponGrid").Text="A01-5"; Field<TextBox>(window,"targetGrid").Text="D01-5";
            var arc=(ComboBox)root.FindName("ArcSelector"); var map=Field<MapView>(window,"map");
            Check(arc.SelectedIndex==0 && ((ComboBoxItem)arc.Items[1]).IsEnabled && ((ComboBoxItem)arc.Items[2]).IsEnabled,"UI exposes both valid arcs");
            double highTime=double.Parse(Field<TextBlock>(window,"flight").Text.Split(' ')[0],CultureInfo.InvariantCulture); var highArea=map.Footprint;
            arc.SelectedIndex=2;
            Check(Field<TextBlock>(window,"elevationLabel").Text.StartsWith("低抛") && double.Parse(Field<TextBlock>(window,"flight").Text.Split(' ')[0],CultureInfo.InvariantCulture)<highTime,"manual low arc updates result and flight time");
            Check(map.Footprint.AcrossRadius<highArea.AcrossRadius,"switching arc updates spread to the selected trajectory");
            ((CheckBox)root.FindName("SpreadDisplay")).IsChecked=false; Check(!Field<bool>(map,"showSpread"),"spread checkbox reaches map renderer");
            ((CheckBox)root.FindName("DamageDisplay")).IsChecked=false; Check(!Field<bool>(map,"showDamage"),"damage checkbox reaches map renderer independently");
            ((Button)root.FindName("TrajectoryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var detail=Field<TrajectoryWindow>(window,"trajectoryWindow"); Pump(detail);
            Check(detail.Chart.Profile!=null && !detail.Chart.Profile.HasTerrain && detail.Chart.Profile.Samples.All(s=>s.High.HasValue && s.Low.HasValue),"profile opens with both curves and explicitly unknown custom-map terrain");
            Capture(detail,Path.Combine(output,"trajectory-manual-preview.png"));
            Call(window,"SelectWeapon","mk19",null); arc.SelectedIndex=1;
            Check(!((ComboBoxItem)arc.Items[1]).IsEnabled && Field<TextBlock>(window,"elevation").Text=="—" && map.Footprint==null,"manual unavailable arc is disabled and cannot display another arc's reading or footprint");
            arc.SelectedIndex=0; Check(map.Footprint!=null && Field<TextBlock>(window,"elevationLabel").Text.StartsWith("低抛"),"automatic recovers the valid low arc");
            Field<TextBox>(window,"weaponGrid").Text="invalid";
            Check(map.Footprint==null && detail.Chart.Profile==null && !((Button)root.FindName("TrajectoryButton")).IsEnabled,"invalid input clears footprint and an already-open profile");
            Field<TextBox>(window,"weaponGrid").Text="A01-5"; picker.SelectedItem=original; Call(window,"SelectWeapon","bm21",null);
            ((CheckBox)root.FindName("SpreadDisplay")).IsChecked=true; ((CheckBox)root.FindName("DamageDisplay")).IsChecked=true;
            Call(window,"PlacePoint",new Vec2(1800.125,1900.375),true); Call(window,"PlacePoint",new Vec2(2500.625,2200.875),false); Pump(window); Pump(detail);
            Check(detail.Chart.Profile!=null && detail.Chart.Profile.HasTerrain,"changing back to a built-in map refreshes the open terrain profile");
            Near(Field<TerrainHeightMap>(window,"terrain").Sample(map.Weapon),detail.Chart.Profile.Samples[0].Ground.Value,1e-10,"profile samples exact clicked weapon coordinate");
            Capture(window,Path.Combine(output,"features-shooting-preview.png")); Capture(detail,Path.Combine(output,"trajectory-terrain-preview.png"));
            double currentMaximum=Field<double>(detail.Chart,"maximum"); Field<CheckBox>(detail,"compare").IsChecked=true; Pump(detail);
            Check(detail.Chart.CompareArcs && Field<double>(detail.Chart,"maximum")>currentMaximum,"comparison toggle reveals both arcs while default scaling retains low-arc terrain detail");
            Capture(detail,Path.Combine(output,"trajectory-comparison-preview.png"));
            Call(map,"ZoomAt",Call(map,"Screen",map.Target),4.0); Pump(window); Capture(window,Path.Combine(output,"features-spread-detail.png")); map.ResetView();
            foreach(var size in new[]{new Size(1060,740),new Size(1280,850),new Size(1440,900)}) {
                window.Width=size.Width; window.Height=size.Height; Pump(window);
                var display=(FrameworkElement)root.FindName("ShootingDisplay");
                var location=display.TranslatePoint(new Point(),(UIElement)root.FindName("MapCard"));
                Check(display.ActualWidth>0 && location.X>=0 && location.X+display.ActualWidth<=((FrameworkElement)root.FindName("MapCard")).ActualWidth,"new map controls fit at each supported viewport");
            }
            detail.Close(); window.Width=1280; window.Height=850;
            ((Button)root.FindName("RaasTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var raas=Field<RaasView>(window,"raasView"); var mapsPicker=Field<ComboBox>(raas,"mapPicker"); mapsPicker.SelectedItem=mapsPicker.Items.Cast<RaasMapInfo>().Single(m=>m.Id=="goosebay");
            var layers=Field<ComboBox>(raas,"layerPicker"); layers.SelectedItem=layers.Items.Cast<RaasLayerInfo>().Single(l=>l.RawName=="GooseBay_RAAS_v2");
            raas.ClickPoint(raas.Session.Layer.Mains.Single(m=>m.Id==raas.Session.Solver.End));
            var fob=raas.Session.Layer.Flags.Single(p=>p.Name=="FOB Inkpen"); var before=raas.Session.Result;
            var list=Field<ListBox>(raas,"candidates"); list.SelectedItem=list.Items.Cast<RaasCandidate>().Single(c=>c.Point==fob);
            Pump(window); Call(map,"SetHoveredPrediction",fob);
            Check(raas.Session.Result==before && raas.Session.Confirmed.Count==0 && Field<IList<RaasMarker>>(map,"predictionMarkers").Any(m=>m.IsPreview),"real map hover event previews without confirming: same result="+(raas.Session.Result==before)+", count="+raas.Session.Confirmed.Count+", preview="+Field<IList<RaasMarker>>(map,"predictionMarkers").Count(m=>m.IsPreview));
            Check(Field<TextBlock>(raas,"progress").Text.Contains("2 条路线"),"preview shows remaining route count");
            Field<CheckBox>(raas,"allPoints").IsChecked=true; Call(map,"SetHoveredPrediction",fob);
            Check(Field<IList<RaasMarker>>(map,"predictionMarkers").Any(m=>m.IsPreview),"same-point hover can restart after an ordinary board refresh");
            Capture(window,Path.Combine(output,"raas-hover-preview.png"));
            Call(map,"SetHoveredPrediction",(object)null); Check(list.SelectedItem!=null && Field<Button>(raas,"confirm").IsEnabled,"leaving preview retains selected candidate and confirm action");
            Field<Button>(raas,"confirm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(raas.Session.Confirmed.Count>=1,"confirmation still works after hover exit");
            int guard=0;
            while(!raas.Session.Complete && raas.Session.Automatic.Count==0 && guard++<128) raas.ClickPoint(raas.Session.Layer.Flags.First(p=>raas.Session.Result.Chance(p,raas.Session.NextStep)>0));
            Capture(window,Path.Combine(output,"raas-auto-preview.png"));
            Check(((FrameworkElement)root.FindName("ShootingDisplay")).Visibility==Visibility.Collapsed,"shooting overlays/tools do not enter RAAS mode");
        } finally { window.Close(); }
    }
    private static void TestSettings(string maps,string output)
    {
        string folder=Path.Combine(output,"feature-settings-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try {
            var window=new MainWindow(true,maps,folder);
            try {
                ((ComboBox)((FrameworkElement)window.Content).FindName("ArcSelector")).SelectedIndex=2;
                ((CheckBox)((FrameworkElement)window.Content).FindName("SpreadDisplay")).IsChecked=false;
                ((CheckBox)((FrameworkElement)window.Content).FindName("DamageDisplay")).IsChecked=false;
                Call(window,"EnsureRaasView"); Field<RaasView>(window,"raasView").AutoComplete=false; Call(window,"SaveSettings");
            } finally { window.Close(); }
            var restored=new MainWindow(false,maps,folder);
            try {
                var root=(FrameworkElement)restored.Content;
                Check(((ComboBox)root.FindName("ArcSelector")).SelectedIndex==2,"manual arc choice persists even when temporarily unavailable");
                Check(((CheckBox)root.FindName("SpreadDisplay")).IsChecked==false && ((CheckBox)root.FindName("DamageDisplay")).IsChecked==false,"overlay preferences survive restart");
                Call(restored,"EnsureRaasView"); Check(!Field<RaasView>(restored,"raasView").AutoComplete,"RAAS automatic preference survives lazy view creation");
                Check(Field<ServerBrowserView>(restored,"serverBrowser")==null,"settings reload does not initiate network services");
            } finally { restored.Close(); }
        } finally { foreach(string file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }
}
