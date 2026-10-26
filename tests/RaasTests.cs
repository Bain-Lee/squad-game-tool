using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SquadTools;

internal static class RaasTests
{
    private static int count;
    private static void Check(bool value,string label) { count++; if(!value) throw new Exception(label); }
    private static void Near(double a,double b,string label) { Check(!double.IsNaN(b) && Math.Abs(a-b)<1e-9,label+": "+a+" != "+b); }
    private static T Field<T>(object o,string name) { return (T)o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o); }
    private static void Set(object o,string name,object value) { o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(o,value); }
    private static object Call(object o,string name,params object[] args) { return o.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(o,args); }
    private static void Invalid(Action action,string label) { try { action(); } catch(FormatException) { count++; return; } throw new Exception("Expected invalid graph: "+label); }
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            var repo=new RaasRepository(args[0]);
            TestEveryLayer(repo,args[1],args[2]); TestGoose(repo); TestReference(repo,args[3]); TestInvalidGraphs(); TestWindow(repo,args[1],args[4]); RenderFlags(Path.Combine(Path.GetDirectoryName(args[4]),"faction-flags-preview.png"));
            Console.WriteLine("PASS: "+count+" RAAS assertions; "+repo.Maps.Count(m=>m.Layers.Count>0)+" maps, "+repo.Maps.Sum(m=>m.Layers.Count)+" layers, 234 upstream reference states, both directions, full chains, undo/reset, safety and WPF interaction. No network.");
            return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void TestEveryLayer(RaasRepository repo,string mapsDirectory,string auditFile)
    {
        var maps=MapCatalog.Load(mapsDirectory);
        Check(repo.Maps.Count==maps.Count,"all bundled maps have explicit RAAS coverage entries");
        var report=new List<string>{"# RAAS 图层覆盖与验证","","以下图层均由正式 C# 求解器验证：两个主基地方向都完成完整旗点链，并检查概率归一化、撤销与重置。","","| 地图 | 图层 | 路线数 | 旗位数 | 物理旗点数 |","| --- | --- | --- | --- | --- |"};
        foreach(var map in maps) {
            var info=repo.FindMap(map.Id); Check(info!=null,"catalog map missing: "+map.Id);
            bool expected=map.Id!="jensen"&&map.Id!="pacific"&&map.Id!="belaya";
            Check((info.Layers.Count>0)==expected,"unexpected RAAS availability: "+map.Id);
            if(info.Layers.Count==0) report.Add("| "+map.Name+" | "+info.Note+" | — | — | — |");
            foreach(var entry in info.Layers) {
                var layer=repo.Load(map.Id,entry.RawName); var session=new RaasSession(layer);
                Check(!string.IsNullOrEmpty(layer.Team1) && !string.IsNullOrEmpty(layer.Team2),"every RAAS layer includes validated offline default factions");
                Check(layer.SourceCommit==repo.SourceCommit,"data source revision consistency");
                foreach(var point in layer.Flags) Check(point.Position.X>=0 && point.Position.Y>=0 && point.Position.X<=map.Width && point.Position.Y<=map.Height,"flag outside calibrated map: "+entry.RawName+" / "+point.Name+" "+point.Position);
                foreach(var main in layer.Mains) {
                    session.ChooseMain(main); int guard=0;
                    while(!session.Complete && guard++<128) {
                        var candidates=layer.Flags.Where(p=>session.Result.Chance(p,session.NextStep)>0).ToList();
                        Near(1,candidates.Sum(p=>session.Result.Chance(p,session.NextStep)),entry.RawName+" next probabilities normalize");
                        Check(candidates.Count>0,entry.RawName+" has a next candidate");
                        int before=session.Result.Alive;
                        Check(session.Confirm(candidates[0]),"sequential candidate confirmation succeeds");
                        Check(session.Result.Alive>0 && session.Result.Alive<=before,"confirmation narrows routes");
                    }
                    Check(session.Complete && session.Confirmed.Count>0,"full flag chain completes: "+entry.RawName);
                    int confirmed=session.Confirmed.Count; session.Undo(); Check(session.Confirmed.Count==confirmed-1 && !session.Complete,"undo restores next candidates");
                    session.Reset(); Check(session.NextStep==1 && session.Result.Alive==session.Solver.Routes.Count,"reset restores full route set");
                }
                report.Add("| "+map.Name+" | "+entry.RawName+" | "+session.Solver.Routes.Count+" | "+session.Solver.StepCount+" | "+layer.Flags.Count+" |");
            }
        }
        Check(repo.Maps.Sum(m=>m.Layers.Count)>=39,"all audited vanilla RAAS variants are bundled");
        File.WriteAllLines(auditFile,report);
        Invalid(()=>repo.Load("goosebay","../../bad"),"unregistered layer path");
    }
    private static void TestGoose(RaasRepository repo)
    {
        var layer=repo.Load("goosebay","GooseBay_RAAS_v2"); var session=new RaasSession(layer);
        session.ChooseMain(layer.Mains.Single(m=>m.Id==session.Solver.End));
        var fob=layer.Flags.Single(p=>p.Name=="FOB Inkpen"); var churchill=layer.Flags.Single(p=>p.Name=="Churchill");
        Near(2.0/3,session.Result.Chance(fob,1),"GooseBay first flag reference");
        Check(session.Confirm(fob) && session.Result.Alive==2,"GooseBay first choice reduces three routes to two");
        Near(0.5,session.Result.Chance(churchill,2),"GooseBay Churchill second chance");
        Check(session.Solver.Solve(new[]{new RaasConstraint{Ids=fob.CandidateIds,Step=1},new RaasConstraint{Ids=churchill.CandidateIds,Step=1}},true).Alive==0,"two distinct confirmations cannot occupy the same step");
        Check(!session.Confirm(layer.Flags.Single(p=>p.Name=="CFB Goose Bay")),"incompatible next point is rejected");
        Check(session.Confirm(churchill)&&session.Result.Alive==1,"GooseBay second choice isolates one route");
        Near(0.5,session.Result.Chance(layer.Flags.Single(p=>p.Name=="Sheshatshiu East"),3),"GooseBay third eastern flag");
        session.Undo(); Near(0.5,session.Result.Chance(churchill,2),"GooseBay undo restores 50/50");
    }
    private static void TestReference(RaasRepository repo,string path)
    {
        var root=ServerData.Object(ServerData.Serializer().DeserializeObject(File.ReadAllText(path))); int cases=0;
        foreach(var raw in (object[])root["layers"]) {
            var entry=ServerData.Object(raw); var solver=new RaasSolver(repo.Load((string)entry["mapId"],(string)entry["rawName"]));
            Near(Convert.ToDouble(entry["routeCount"]),solver.Routes.Count,"route count equals upstream"); Near(Convert.ToDouble(entry["stepCount"]),solver.StepCount,"step count equals upstream");
            foreach(var item in (object[])entry["cases"]) {
                var sample=ServerData.Object(item); var constraints=((object[])sample["constraints"]).Select(ServerData.Object).Select(c=>new RaasConstraint{Ids=((object[])c["ids"]).Select(Convert.ToString).ToList(),Step=Convert.ToInt32(c["step"])}).ToList();
                var result=solver.Solve(constraints,(bool)sample["reverse"]); Near(Convert.ToDouble(sample["alive"]),result.Alive,"surviving routes equal upstream");
                var expected=ServerData.Object(sample["steps"]); Check(result.Steps.Count==expected.Count,"same depth set as upstream");
                foreach(var step in expected) {
                    int depth=int.Parse(step.Key,CultureInfo.InvariantCulture); var values=ServerData.Object(step.Value);
                    Check(result.Steps[depth].Count==values.Count,"same candidate IDs as upstream");
                    foreach(var value in values) Near(Convert.ToDouble(value.Value,CultureInfo.InvariantCulture),result.Steps[depth][value.Key],"same candidate probability as upstream");
                }
                cases++;
            }
        }
        Check(cases==234,"all captured reference states verified");
    }
    private static RaasLayer Graph(int stages)
    {
        var layer=new RaasLayer(); layer.Mains.Add(new RaasPoint{Id="start",IsMain=true}); layer.Mains.Add(new RaasPoint{Id="end",IsMain=true});
        var previous=new List<string>{"start"};
        for(int i=0;i<stages;i++) {
            var current=new List<string>{"a"+i,"b"+i};
            foreach(string id in current) layer.Clusters.Add(new RaasCluster{Id=id,Ids=new List<string>{"point-"+id}});
            foreach(string a in previous) foreach(string b in current) layer.Links.Add(new RaasLink{From=a,To=b}); previous=current;
        }
        foreach(string id in previous) layer.Links.Add(new RaasLink{From=id,To="end"}); return layer;
    }
    private static void TestInvalidGraphs()
    {
        Invalid(()=>new RaasSolver(Graph(12)),"route enumeration cap rejects incomplete probabilities");
        var cycle=Graph(2); cycle.Links.Add(new RaasLink{From="a1",To="a0"}); Invalid(()=>new RaasSolver(cycle),"cycle detection");
        var noEnd=Graph(1); noEnd.Links.RemoveAll(l=>l.To=="end"); Invalid(()=>new RaasSolver(noEnd),"missing unique end main");
    }
    private static void TestWindow(RaasRepository repo,string mapsDirectory,string imageFile)
    {
        var window=new MainWindow(true,mapsDirectory);
        try {
            window.Show(); var root=(FrameworkElement)window.Content;
            ((Button)root.FindName("RaasTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var view=Field<RaasView>(window,"raasView"); var map=Field<MapView>(window,"map");
            Check(view!=null && map.PredictionMode,"RAAS tab enters independent prediction mode");
            int audited=0;
            foreach(var info in repo.Maps) foreach(var entry in info.Layers) {
                var picker=Field<ComboBox>(view,"mapPicker"); picker.SelectedItem=picker.Items.Cast<RaasMapInfo>().Single(m=>m.Id==info.Id);
                var layers=Field<ComboBox>(view,"layerPicker"); layers.SelectedItem=layers.Items.Cast<RaasLayerInfo>().Single(l=>l.RawName==entry.RawName);
                Check(view.Session!=null&&view.Session.Layer.RawName==entry.RawName,"actual WPF loads layer: "+entry.RawName); audited++;
            }
            Check(audited==39,"every bundled RAAS layer loaded through actual selectors");
            var server=new ServerRecord{SessionId=new string('a',32),Name="RAAS 全地图离线验证",Map="GooseBay_RAAS_v2",TeamOne="BAF_LO_CombinedArms",TeamTwo="RGF_LO_CombinedArms",RetrievedAt=DateTime.UtcNow};
            Call(window,"SelectServerPage",true);
            var beforeFollow=view.Session;
            Call(window,"ChooseServerMap",server);
            Check(view.Session==beforeFollow && Field<bool>(window,"serverPageActive"),"enabling follow does not change predictor or leave browser");
            ((Button)root.FindName("RaasTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(view.Session.Layer.RawName==server.Map && map.PredictionMode,"manual RAAS navigation applies full followed variant");
            var markers=Field<IList<RaasMarker>>(map,"predictionMarkers");
            Check(markers.Single(m=>m.Point.IsMain && m.Point.Id.Contains("Team1")).FactionId=="BAF" && markers.Single(m=>m.Point.IsMain && m.Point.Id.Contains("Team2")).FactionId=="RGF","actual server factions are bound to the correct team main, independently of counting direction");
            var layer=view.Session.Layer; int mainIndex=layer.Mains.FindIndex(m=>m.Id==view.Session.Solver.End);
            window.UpdateLayout(); var mainScreen=(Point)Call(map,"Screen",layer.Mains[mainIndex].Position);
            Call(map,"HandlePointerDown",MouseButton.Left,mainScreen+new Vector(18,12),1);
            Check(view.Session.Main==layer.Mains[mainIndex],"clicking a rectangular national flag near its corner selects the correct main");
            Field<ComboBox>(view,"mainPicker").SelectedIndex=mainIndex+1;
            window.UpdateLayout();
            string weapon=Field<TextBox>(window,"weaponGrid").Text,target=Field<TextBox>(window,"targetGrid").Text;
            var fob=layer.Flags.Single(f=>f.Name=="FOB Inkpen"); var point=(Point)Call(map,"Screen",fob.Position);
            Call(map,"HandlePointerDown",MouseButton.Left,point,1);
            Check(view.Session.Confirmed.Count==1 && view.Session.Confirmed[0]==fob,"map click confirms actual next flag");
            Check(Field<TextBox>(window,"weaponGrid").Text==weapon && Field<TextBox>(window,"targetGrid").Text==target,"prediction click does not edit mortar or target");
            Call(window,"UpdateFollowedServer",server); Check(view.Session.Confirmed.Count==1,"same-layer server refresh preserves guessed flags");
            server.TeamOne="USA_LO_CombinedArms"; Call(window,"UpdateFollowedServer",server);
            Check(view.Session.Confirmed.Count==1 && Field<IList<RaasMarker>>(map,"predictionMarkers").Any(m=>m.FactionId=="USA"),"same-layer faction refresh updates national flag without losing chain");
            server.TeamOne=null; server.TeamTwo="SU_RGF"; Call(window,"UpdateFollowedServer",server);
            Check(Field<IList<RaasMarker>>(map,"predictionMarkers").Where(m=>m.Point.IsMain).All(m=>m.FactionId==""),"missing or modded current factions display unknown instead of default national flags");
            server.TeamOne="BAF_LO_CombinedArms"; server.TeamTwo="RGF_LO_CombinedArms";
            var originalImage=map.MapImage; server.Map="GooseBay_RAAS_v1"; Call(window,"UpdateFollowedServer",server);
            Check(view.Session.Layer.RawName==server.Map && view.Session.Confirmed.Count==0 && view.Session.Main==null,"same-map different-layer server rotation resets prediction state");
            Check(map.MapImage==originalImage,"layer rotation preserves calibrated base image");
            server.Map="GooseBay_AAS_v1"; Call(window,"UpdateFollowedServer",server);
            var manual=view.Session;
            Check(manual!=null && Field<TextBlock>(view,"message").Text.Contains("手动选择"),"non-RAAS server preserves usable manual prediction instead of disabling it");
            server.Map="UnknownMap_RAAS_v1"; Call(window,"UpdateFollowedServer",server);
            Check(view.Session==manual && Field<TextBlock>(view,"message").Text.Contains("手动选择"),"unsupported server layer preserves manual prediction with visible notice");
            server.Map="GooseBay_RAAS_v1"; Call(window,"UpdateFollowedServer",server);
            var layerPicker=Field<ComboBox>(view,"layerPicker"); layerPicker.SelectedItem=layerPicker.Items.Cast<RaasLayerInfo>().Single(l=>l.RawName=="GooseBay_RAAS_v2");
            Check(Field<string>(window,"followedServerId")==null,"manual variant selection stops server follow");
            view.ClickPoint(view.Session.Layer.Mains.Single(m=>m.Id==view.Session.Solver.End));
            view.ClickPoint(view.Session.Layer.Flags.Single(f=>f.Name=="FOB Inkpen"));
            view.ClickPoint(view.Session.Layer.Flags.Single(f=>f.Name=="Churchill"));
            Field<Button>(view,"undo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(view.Session.Confirmed.Count==1,"WPF undo restores previous choice");
            var state=view.Session;
            var nonRaas=new ServerRecord{SessionId=new string('b',32),Name="非 RAAS 地图跟随测试",Map="Narva_Invasion_v1",TeamOne="USA_LO_CombinedArms",TeamTwo="RGF_LO_CombinedArms",RetrievedAt=DateTime.UtcNow};
            Call(window,"SelectServerPage",true); Call(window,"ChooseServerMap",nonRaas);
            ((Button)root.FindName("RaasTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(view.Session==state && view.Session.Confirmed.Count==1 && Field<MapDefinition>(window,"selectedMap").Id=="goosebay","Invasion follow leaves manual RAAS map and confirmed chain intact");
            ((Button)root.FindName("MortarTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(!map.PredictionMode,"shooting mode restores ordinary map gestures");
            Check(Field<MapDefinition>(window,"selectedMap").Id=="narva" && view.Session==state,"shooting follows Invasion while retaining independent RAAS state");
            nonRaas.Map="AlBasrah_AAS_v1"; Call(window,"UpdateFollowedServer",nonRaas);
            Check(Field<MapDefinition>(window,"selectedMap").Id=="albasrah" && view.Session==state,"AAS rotation updates shooting without overwriting hidden predictor");
            ((Button)root.FindName("RaasTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(view.Session==state && view.Session.Confirmed.Count==1,"mode switching preserves prediction choices");
            Check(Field<MapDefinition>(window,"selectedMap").Id=="goosebay" && Field<string>(window,"followedServerId")==nonRaas.SessionId,"returning to manual RAAS restores its own map without stopping shooting follow");
            Field<ComboBox>(view,"layerPicker").SelectedItem=Field<ComboBox>(view,"layerPicker").Items.Cast<RaasLayerInfo>().Single(l=>l.RawName=="GooseBay_RAAS_v1");
            Check(Field<string>(window,"followedServerId")==nonRaas.SessionId && view.Session.Layer.RawName=="GooseBay_RAAS_v1","manual RAAS variant selection leaves non-RAAS shooting follow enabled");
            nonRaas.Map="GooseBay_RAAS_v2"; Call(window,"UpdateFollowedServer",nonRaas);
            Check(view.Session.Layer.RawName==nonRaas.Map && view.Session.Confirmed.Count==0,"RAAS becomes eligible for automatic sync after server mode changes");
            view.ClickPoint(view.Session.Layer.Mains.Single(m=>m.Id==view.Session.Solver.End)); view.ClickPoint(view.Session.Layer.Flags.Single(f=>f.Name=="FOB Inkpen"));
            Call(window,"SelectServerPage",true); server.Map="GooseBay_RAAS_v2"; Call(window,"ChooseServerMap",server);
            ((Button)root.FindName("RaasTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(view.Session.Confirmed.Count==0,"following a different server on the same layer never reuses another match's confirmations");
            view.ClickPoint(view.Session.Layer.Mains.Single(m=>m.Id==view.Session.Solver.End)); view.ClickPoint(view.Session.Layer.Flags.Single(f=>f.Name=="FOB Inkpen"));
            weapon=Field<TextBox>(window,"weaponGrid").Text;
            Point center=new Point(300,300);
            Call(map,"HandlePointerDown",MouseButton.Right,center,1); Call(map,"HandlePointerUp",MouseButton.Right,center);
            Call(map,"HandlePointerDown",MouseButton.Right,center,2); Call(map,"HandlePointerUp",MouseButton.Right,center);
            Check(Field<TextBox>(window,"weaponGrid").Text==weapon,"right double click in prediction mode cannot move the mortar");
            window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));
            var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(imageFile)) encoder.Save(file);
            Field<Button>(view,"reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(view.Session.Confirmed.Count==0,"WPF reset clears flags");
            var choices=Field<ListBox>(view,"candidates"); choices.SelectedIndex=0;
            choices.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(choices),Environment.TickCount,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent});
            Check(view.Session.Confirmed.Count==1,"Enter confirms the selected next candidate");
            var browser=Field<ServerBrowserView>(window,"serverBrowser"); var snapshot=new ServerSnapshot{RetrievedAt=DateTime.UtcNow}; snapshot.Servers.Add(server); browser.LoadSmokeSnapshot(snapshot);
            Call(window,"SelectServerPage",true); window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{})); window.UpdateLayout();
            var browserRoot=(FrameworkElement)browser.Child;
            var followStatus=(TextBlock)browserRoot.FindName("FollowStatus"); var table=(DataGrid)browserRoot.FindName("Servers");
            Check(followStatus.TranslatePoint(new Point(0,followStatus.ActualHeight),browserRoot).Y<=table.TranslatePoint(new Point(),browserRoot).Y,"persistent follow status fits above the server table without clipping");
            var serverBitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); serverBitmap.Render(window);
            var serverEncoder=new PngBitmapEncoder(); serverEncoder.Frames.Add(BitmapFrame.Create(serverBitmap)); using(var file=File.Create(Path.Combine(Path.GetDirectoryName(imageFile),"server-follow-preview.png"))) serverEncoder.Save(file);
            Field<Button>(browser,"follow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<string>(window,"followedServerId")==null && Field<bool>(window,"serverPageActive") && view.Session.Confirmed.Count==1,"browser stop-follow button disables follow without navigating or clearing manual chain");
        } finally { window.Close(); }
    }
    private static void RenderFlags(string output)
    {
        var ids=new[]{"USA","USMC","RGF","VDV","BAF","CAF","ADF","AFU","PLA","PLAAGF","PLANMC","TLF","GFI","CRF","IMF","INS","MEI","MEA","WPMC",""};
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen()) {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(12,20,28)),null,new Rect(0,0,840,380));
            for(int i=0;i<ids.Length;i++) {
                int x=20+i%5*166,y=18+i/5*93;
                var image=FactionCatalog.Image(ids[i]); Check(image.Width>0 && image.Height>0,"national flag or neutral faction badge can render without external files/codecs: "+ids[i]);
                dc.DrawImage(image,new Rect(x,y,90,60));
                dc.DrawText(new FormattedText((ids[i].Length==0?"未知":ids[i])+" "+FactionCatalog.Name(ids[i]),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),10,Brushes.White,1),new Point(x,y+64));
            }
        }
        var bitmap=new RenderTargetBitmap(840,390,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(output)) encoder.Save(file);
    }
}
