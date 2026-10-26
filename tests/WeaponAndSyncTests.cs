using System;
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

internal static class WeaponAndSyncTests
{
    private static int assertions;
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual, double tolerance, string message) { Check(!double.IsNaN(actual) && !double.IsInfinity(actual) && Math.Abs(expected - actual) <= tolerance, message + ": " + expected + " != " + actual); }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static object Call(object target, string method, params object[] args) { return target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); }

    [STAThread]
    public static int Main(string[] args)
    {
        try {
            TestWeaponMath(); TestMapNames(args[0]); TestWindow(args[0], args[1]); TestSettings(args[0]);
            Console.WriteLine("PASS: " + assertions + " weapon/map-sync assertions: nine weapon profiles, ammunition, independent trajectories, offsets, lifespan, units, safe layer matching, actual server choice, terrain changes and range redraw. No network or Steam.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void TestWeaponMath()
    {
        Check(WeaponCatalog.All.Count == 9 && WeaponCatalog.All.Select(w => w.Id).Distinct().Count() == 9, "nine distinct vanilla weapons");
        Check(WeaponCatalog.All.Count(w => w.Category == "工事") == 3 && WeaponCatalog.All.Count(w => w.Category == "载具") == 6, "deployable/vehicle categories");
        Check(WeaponCatalog.Find("missing") == WeaponCatalog.Default, "unknown saved weapon falls back to mortar");
        var truck = WeaponCatalog.Find("tech-mortar");
        double range = 110 * 110 * Math.Sqrt(3) / (2 * 9.78);
        var known = Ballistics.Solve(new Vec2(0, 0), new Vec2(range, 0), 0, 2.5, truck, truck.Shells[0]);
        Near(55, known.HighMil * 360 / 6400, 1e-9, "technical mortar has five-degree sight correction");
        Near(110 * Math.Sqrt(3) / 9.78, known.HighTimeSeconds, 1e-9, "flight time uses launch angle rather than corrected sight angle");
        var bm21 = WeaponCatalog.Find("bm21");
        var rocket = Ballistics.Solve(new Vec2(0, 0), new Vec2(200 * 200 * Math.Sqrt(3) / (2 * 19.56), 0), 0, 3, bm21, bm21.Shells[0]);
        Near(30, rocket.LowMil * 360 / 6400, 1e-9, "BM21 doubles gravity and selects low arc");
        Check(rocket.HighUsable && rocket.LowUsable && !rocket.SelectedHigh, "low-preferred weapon does not silently use high arc");
        foreach (var profile in WeaponCatalog.All) foreach (var shell in profile.Shells) {
            foreach (double distance in new[] { 100.0, 300.0, 700.0, 1100.0 }) foreach (double height in new[] { -70.0, 0.0, 50.0 }) {
                var solution = Ballistics.Solve(new Vec2(0, 0), new Vec2(distance, 0), 12, 12 + height, profile, shell);
                if (double.IsNaN(solution.HighMil)) continue;
                double velocity = profile.EffectiveVelocity(shell, distance), gravity = 9.78 * profile.GravityScale;
                foreach (bool high in new[] { true, false }) {
                    double mil = high ? solution.HighMil : solution.LowMil, time = high ? solution.HighTimeSeconds : solution.LowTimeSeconds;
                    double launch = (mil * 360 / 6400 + profile.AngleOffset) * Math.PI / 180;
                    Near(distance, velocity * Math.Cos(launch) * time, 1e-6, profile.Id + " horizontal landing");
                    Near(height - profile.MuzzleHeight, velocity * Math.Sin(launch) * time - gravity * time * time / 2, 1e-6, profile.Id + " muzzle-adjusted vertical landing");
                    bool usable = high ? solution.HighUsable : solution.LowUsable;
                    if (usable) Check(time <= profile.Lifespan && mil * 360 / 6400 >= profile.MinDegrees - 1e-7 && mil * 360 / 6400 <= profile.MaxDegrees + 1e-7, "usable arc respects sight and lifespan");
                }
            }
        }
        var mk19 = WeaponCatalog.Find("mk19"); var life = Ballistics.Solve(new Vec2(0, 0), new Vec2(700, 0), 0, 0, mk19, mk19.Shells[0]);
        Check(life.HighTimeSeconds > 20 && !life.HighUsable && life.LowUsable, "Mk19 high arc expires before impact");
        var ub32 = WeaponCatalog.Find("ub32"); var ubShell = ub32.Shells[0];
        Near(600 - Math.Sqrt(100000), ub32.EffectiveVelocity(ubShell, 100), 1e-10, "UB32 deceleration phase matches source approximation");
        Near(1000.0 / 4.5, ub32.EffectiveVelocity(ubShell, 1000), 1e-10, "UB32 cruise phase matches source approximation");
        Check(ub32.EffectiveVelocity(ubShell, 100) != ub32.EffectiveVelocity(ubShell, 1000), "rocket velocity depends on distance");
        var m1064 = WeaponCatalog.Find("m1064");
        Check(m1064.Shells.Count == 2 && m1064.Shells[0].Id == "impact" && m1064.Shells[1].Id == "nearSurface", "M1064 supports both source ammunition choices");
        var impact = Ballistics.Solve(new Vec2(0, 0), new Vec2(900, 0), 0, 0, m1064, m1064.Shells[0]);
        var air = Ballistics.Solve(new Vec2(0, 0), new Vec2(900, 0), 0, 0, m1064, m1064.Shells[1]);
        Near(impact.HighMil, air.HighMil, 0, "same-speed M1064 shells share elevation, not invented ballistics");
        try { Ballistics.Solve(new Vec2(0, 0), new Vec2(900, 0), 0, 0, m1064, ubShell); throw new Exception("Foreign ammunition accepted"); } catch (ArgumentException) { assertions++; }
        Check(WeaponCatalog.Default.FormatElevation(1234.4) == "1234", "mortar displays whole NATO mils");
        Check(mk19.FormatElevation(10.25 * 6400 / 360) == "10.3°", "degree display keeps one decimal");
        var ags = WeaponCatalog.Find("btr4-ags");
        Check(ags.FormatElevation((12 + 59.6 / 60) * 6400 / 360) == "13°00′", "degree-minute rounding carries correctly");
        Check(ags.FormatElevation(-0.5 * 6400 / 360) == "−0°30′", "negative sub-degree sight preserves sign");
    }

    private static void TestMapNames(string directory)
    {
        var maps = MapCatalog.Load(directory);
        foreach (var map in maps) Check(MapCatalog.FromServerLayer(maps, map.Id + "_RAAS_v1") == map, "catalog map id layer resolves: " + map.Id);
        string[] layers = { "AlBasrah_Invasion_v1", "JensensRange_Training", "KamdeshHighlands_RAAS_v2", "KohatToi_AAS_v1", "LashkarValley_RAAS_v1", "LogarValley_Seed_v1", "PacificProvingGrounds_Training", "BelayaPass_AAS_v1", "Fool's Road RAAS v1", "SanxianIslands_Invasion_v1", "TallilOutskirts_AAS_v1" };
        string[] ids = { "albasrah", "jensen", "kamdesh", "kohat", "lashkar", "logar", "pacific", "belaya", "foolsroad", "sanxian", "tallil" };
        for (int i = 0; i < layers.Length; i++) Check(MapCatalog.FromServerLayer(maps, layers[i]).Id == ids[i], "known EOS alias resolves: " + layers[i]);
        foreach (string unknown in new[] { null, "", "", "UnknownMap_RAAS_v1", "BlackCoastExtra_RAAS_v1", "SU_Gorodok_RAAS_v1", "../Gorodok", "Gorodok2_RAAS_v1" }) Check(MapCatalog.FromServerLayer(maps, unknown) == null, "unknown map never picks an approximate match");
    }

    private static void TestWindow(string directory, string screenshot)
    {
        var window = new MainWindow(true, directory);
        try {
            var category = Field<ComboBox>(window,"weaponCategory"); category.SelectedItem = "载具";
            Check(Field<WeaponDefinition>(window,"selectedWeapon").Id == "tech-mortar", "category changes active weapon");
            Call(window,"SelectWeapon","m1064","nearSurface");
            Check(Field<ShellDefinition>(window,"selectedShell").Id == "nearSurface" && Field<ComboBox>(window,"shellSelector").IsEnabled, "ammunition selection restores by id");
            Check(Field<TextBlock>(window,"heightHint").Text.Contains("3m"), "height hint reflects weapon muzzle offset");
            Call(window,"SelectWeapon","missing","missing");
            Check(Field<WeaponDefinition>(window,"selectedWeapon").Id == "mortar" && !Field<ComboBox>(window,"shellSelector").IsEnabled, "old or invalid settings fall back safely");
            Call(window,"SelectServerPage",true);
            var browser = Field<ServerBrowserView>(window,"serverBrowser"); int choices = 0; browser.ServerChosen += s => choices++;
            var sample = new ServerSnapshot { RetrievedAt=DateTime.UtcNow };
            sample.Servers.Add(new ServerRecord { SessionId=new string('a',32), Name="地图同步离线测试服务器", Map="BlackCoast_RAAS_v1", Reported=80, RetrievedAt=DateTime.UtcNow });
            sample.Servers.Add(new ServerRecord { SessionId=new string('b',32), Name="仅查看的其他服务器", Map="Narva_AAS_v1", Reported=20, RetrievedAt=DateTime.UtcNow });
            browser.LoadSmokeSnapshot(sample);
            Check(choices == 0 && Field<MapDefinition>(window,"selectedMap").Id == "yehorivka", "automatic table selection never silently changes map");
            window.Show(); window.UpdateLayout();
            var table = Field<DataGrid>(browser,"table");
            var row = (DataGridRow)table.ItemContainerGenerator.ContainerFromItem(table.SelectedItem);
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left) { RoutedEvent=Mouse.MouseUpEvent });
            Check(choices == 0 && Field<MapDefinition>(window,"selectedMap").Id == "yehorivka" && Field<bool>(browser,"active"), "row click only selects and keeps the server page and map");
            table.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(table),Environment.TickCount,Key.Enter) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
            Check(choices==0 && Field<string>(browser,"followedSession")==null,"Enter on a row does not enable follow or navigate");
            Field<Button>(browser,"follow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(choices==1 && Field<bool>(browser,"active") && Field<MapDefinition>(window,"selectedMap").Id=="yehorivka","explicit follow button enables without navigating or changing map");
            Check(Field<string>(browser,"followedSession") == sample.Servers[0].SessionId, "follow target is independent of table selection");
            table.SelectedItem=sample.Servers[1];
            Check(Field<string>(browser,"followedSession")==sample.Servers[0].SessionId && choices==1,"browsing another row does not change the enabled follow target");
            table.SelectedItem=sample.Servers[0];
            Call(window,"SelectServerPage",false);
            Check(Field<TerrainHeightMap>(window,"terrain")!=null && Field<MapDefinition>(window,"selectedMap").Id=="blackcoast","manual page switch applies followed map and terrain");
            Call(window,"PlacePoint",new Vec2(1000.125,1000.375),true);
            var exact = Field<Vec2?>(window,"preciseWeapon"); var image = Field<MapView>(window,"map").MapImage;
            Call(window,"SelectServerPage",true); window.UpdateLayout();
            table.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(table),Environment.TickCount,Key.Enter) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
            Check(choices == 1 && Field<bool>(browser,"active"), "Enter while following still keeps the server page");
            Call(window,"UpdateFollowedServer",sample.Servers[0]);
            Check(Field<Vec2?>(window,"preciseWeapon").Value.X == exact.Value.X && Field<MapView>(window,"map").MapImage == image, "same map refresh preserves exact placement and image");
            sample.Servers[0].Map="Narva_Invasion_v1"; Call(window,"UpdateFollowedServer",sample.Servers[0]);
            Check(Field<MapDefinition>(window,"selectedMap").Id=="blackcoast" && Field<bool>(browser,"active"),"server rotation while browsing is deferred without navigation");
            Call(window,"SelectServerPage",false);
            Check(Field<MapDefinition>(window,"selectedMap").Id == "narva" && !Field<Vec2?>(window,"preciseWeapon").HasValue, "server map rotation changes terrain and resets old positions");
            sample.Servers[0].Map="Unknown_RAAS_v1"; Call(window,"UpdateFollowedServer",sample.Servers[0]);
            Check(Field<MapDefinition>(window,"selectedMap").Id == "narva" && Field<TextBlock>(window,"serverMapNote").Text.Contains("暂不支持"), "unsupported map leaves current map with explicit notice");
            Call(window,"UpdateFollowedServer",new object[]{null});
            Check(Field<MapDefinition>(window,"selectedMap").Id == "narva" && Field<TextBlock>(window,"serverMapNote").Text.Contains("未取得"), "missing followed server does not select another server's map");
            var selector = Field<ComboBox>(window,"mapSelector"); selector.SelectedIndex=0;
            Check(Field<string>(window,"followedServerId") == null && Field<string>(browser,"followedSession") == null, "manual map change stops background follow");
            Call(window,"SelectWeapon","bm21",null);
            Near(200 * 200 / 19.56, Field<MapView>(window,"map").RangeMeters, 1e-8, "map range follows selected weapon");
            Check(Field<TextBlock>(window,"elevationLabel").Text.Contains("DEG"), "degree-based weapon uses degree label");
            Call(window,"SelectWeapon","btr4-ags",null);
            Check(Field<TextBlock>(window,"elevationLabel").Text.Contains("度分"), "BTR4 uses degree-minute label");
            Call(window,"SelectWeapon","mortar",null);
            Check(Field<TextBlock>(window,"elevationLabel").Text.Contains("MIL"), "switching back restores mil label");
            sample.Servers[0].Map="Gorodok_RAAS_v1"; Call(window,"ChooseServerMap",sample.Servers[0]); Call(window,"SelectServerPage",false);
            Call(window,"SelectWeapon","m1064","impact");
            window.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate {}));
            var elevation = Field<TextBlock>(window,"elevation"); var point = elevation.TranslatePoint(new Point(), window);
            Check(point.Y >= 0 && point.Y + elevation.ActualHeight < window.ActualHeight - 30, "firing elevation stays visible without scrolling weapon controls");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var stream=File.Create(screenshot)) encoder.Save(stream);
        } finally { window.Close(); }
    }

    private static void TestSettings(string maps)
    {
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"weapon-settings-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            File.WriteAllText(Path.Combine(folder,"settings.xml"),"<settings><mapId>gorodok</mapId><weapon>D05-5-5</weapon><target>F06-5-5</target></settings>");
            var original = new MainWindow(false,maps,folder);
            try {
                Check(Field<WeaponDefinition>(original,"selectedWeapon").Id == "mortar", "legacy settings without weapon id keep standard mortar");
                Call(original,"SelectWeapon","m1064","nearSurface"); Call(original,"SaveSettings");
            } finally { original.Close(); }
            var restored = new MainWindow(false,maps,folder);
            try {
                Check(Field<WeaponDefinition>(restored,"selectedWeapon").Id == "m1064" && Field<ShellDefinition>(restored,"selectedShell").Id == "nearSurface", "weapon and ammunition survive actual settings save/load");
                Check(Field<MapDefinition>(restored,"selectedMap").Id == "gorodok", "weapon preference preserves saved map");
                Check(Field<ServerBrowserView>(restored,"serverBrowser") == null, "restoring weapon settings does not create a network browser");
            } finally { restored.Close(); }
        } finally { foreach (string file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }
}
