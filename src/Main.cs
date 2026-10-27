using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using Microsoft.Win32;

namespace SquadTools
{
    public sealed class MainWindow : Window
    {
        private TextBox weaponGrid, targetGrid, weaponHeight, targetHeight;
        private TextBlock bearing, elevation, elevationLabel, distance, difference, flight, status, mapName;
        private TextBlock mapInfo, terrainNote, heightHint;
        private ComboBox mapSelector, weaponCategory, weaponSelector, shellSelector;
        private TextBlock weaponInfo, serverMapNote;
        private Button stopServerFollow;
        private WeaponDefinition selectedWeapon = WeaponCatalog.Default;
        private ShellDefinition selectedShell = WeaponCatalog.Default.Shells[0];
        private string restoredWeaponId = "mortar", restoredShellId = "default", followedServerId;
        private CheckBox automaticHeight;
        private MapDefinition selectedMap;
        private TerrainHeightMap terrain;
        private readonly List<MapChoice> mapChoices = new List<MapChoice>();
        private bool preferAutomaticHeight = true;
        private double customMapSize = 3000;
        private string restoredMapId = "yehorivka";
        private string mapError;
        private MapView map;
        private bool loading = true;
        private readonly bool testMode;
        private ServerBrowserView serverBrowser;
        private FiringSolution solution;
        private Vec2? preciseWeapon, preciseTarget;
        private readonly string dataDirectory;
        private readonly string layersDirectory;
        private RaasView raasView;
        private bool raasMode;
        private bool serverPageActive;
        private ServerRecord followedServer;
        private string currentLayerRawName;
        private string ImagePath { get { return Path.Combine(dataDirectory, "custom-map.png"); } }

        private sealed class MapChoice
        {
            public MapDefinition Definition;
            public string Id { get { return Definition == null ? "custom" : Definition.Id; } }
            public override string ToString() { return Definition == null ? "自定义图片 / 空白网格" : Definition.DisplayName; }
        }

        public MainWindow(bool testMode) : this(testMode, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "maps")) { }

        internal MainWindow(bool testMode, string mapsDirectory) : this(testMode, mapsDirectory, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data")) { }

        internal MainWindow(bool testMode, string mapsDirectory, string settingsDirectory)
        {
            this.testMode = testMode; dataDirectory = settingsDirectory;
            layersDirectory=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mapsDirectory)),"layers");
            Title = "Squad Tools · 射击计算与服务器";
            Width = 1280; Height = 850; MinWidth = 1060; MinHeight = 740;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = (Brush)new BrushConverter().ConvertFromString("#101720");
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Content = XamlReader.Parse(Layout);
            var root = (FrameworkElement)Content;
            weaponGrid = (TextBox)root.FindName("WeaponGrid"); targetGrid = (TextBox)root.FindName("TargetGrid");
            weaponHeight = (TextBox)root.FindName("WeaponHeight"); targetHeight = (TextBox)root.FindName("TargetHeight");
            bearing = (TextBlock)root.FindName("Bearing"); elevation = (TextBlock)root.FindName("Elevation");
            elevationLabel = (TextBlock)root.FindName("ElevationLabel");
            distance = (TextBlock)root.FindName("Distance"); difference = (TextBlock)root.FindName("Difference"); flight = (TextBlock)root.FindName("Flight");
            status = (TextBlock)root.FindName("Status"); mapName = (TextBlock)root.FindName("MapName");
            mapInfo = (TextBlock)root.FindName("MapInfo"); terrainNote = (TextBlock)root.FindName("TerrainNote"); heightHint = (TextBlock)root.FindName("HeightHint");
            mapSelector = (ComboBox)root.FindName("MapSelector"); automaticHeight = (CheckBox)root.FindName("AutomaticHeight");
            weaponCategory = (ComboBox)root.FindName("WeaponCategory"); weaponSelector = (ComboBox)root.FindName("WeaponSelector"); shellSelector = (ComboBox)root.FindName("ShellSelector");
            weaponInfo = (TextBlock)root.FindName("WeaponInfo"); serverMapNote = (TextBlock)root.FindName("ServerMapNote"); stopServerFollow = (Button)root.FindName("StopServerFollow");
            weaponCategory.ItemsSource = new[] { "工事", "载具" };
            weaponCategory.SelectionChanged += delegate { if (!loading) SelectWeapon(WeaponCatalog.All.First(w => w.Category == (string)weaponCategory.SelectedItem).Id, null); };
            weaponSelector.SelectionChanged += delegate { if (!loading && weaponSelector.SelectedItem != null) SelectWeapon(((WeaponDefinition)weaponSelector.SelectedItem).Id, null); };
            shellSelector.SelectionChanged += delegate { if (!loading && shellSelector.SelectedItem != null) { selectedShell = (ShellDefinition)shellSelector.SelectedItem; Recalculate(); } };
            stopServerFollow.Click += delegate { StopServerFollow(); };
            map = new MapView(); ((Border)root.FindName("MapHost")).Child = map;
            map.PointPlaced += PlacePoint;
            ((Button)root.FindName("ImportMap")).Click += delegate { ImportMap(); };
            ((Button)root.FindName("ClearMap")).Click += delegate { ClearMap(); };
            ((Button)root.FindName("ZoomIn")).Click += delegate { map.ZoomBy(1.3); };
            ((Button)root.FindName("ZoomOut")).Click += delegate { map.ZoomBy(1 / 1.3); };
            ((Button)root.FindName("ResetView")).Click += delegate { map.ResetView(); };
            ((Button)root.FindName("Help")).Click += delegate { ShowHelp(); };
            ((Button)root.FindName("MortarTab")).Click += delegate { raasMode=false; SelectServerPage(false); };
            ((Button)root.FindName("RaasTab")).Click += delegate { raasMode=true; SelectServerPage(false); };
            ((Button)root.FindName("ServerTab")).Click += delegate { SelectServerPage(true); };
            Closed += delegate { if (serverBrowser != null) serverBrowser.Dispose(); };
            ((CheckBox)root.FindName("Pin")).Checked += delegate { Topmost = true; };
            ((CheckBox)root.FindName("Pin")).Unchecked += delegate { Topmost = false; };
            ((RadioButton)root.FindName("PlaceMortar")).Checked += delegate { map.PlaceWeapon = true; };
            ((RadioButton)root.FindName("PlaceTarget")).Checked += delegate { map.PlaceWeapon = false; };
            mapSelector.SelectionChanged += delegate { if (!loading) { if(!raasMode || CanFollowRaas()) StopServerFollow(); currentLayerRawName=null; ApplySelectedMap(true); if(raasMode) UpdateRaasMap(); } };
            automaticHeight.Checked += delegate { if (!loading) { preferAutomaticHeight = true; UpdateHeightMode(); Recalculate(); } };
            automaticHeight.Unchecked += delegate { if (!loading) { preferAutomaticHeight = false; UpdateHeightMode(); Recalculate(); } };
            weaponGrid.TextChanged += delegate { if (!loading) { preciseWeapon = null; Recalculate(); } };
            targetGrid.TextChanged += delegate { if (!loading) { preciseTarget = null; Recalculate(); } };
            foreach (var box in new[] { weaponHeight, targetHeight }) box.TextChanged += delegate { if (!loading) Recalculate(); };
            foreach (MapDefinition definition in MapCatalog.Load(mapsDirectory)) mapChoices.Add(new MapChoice { Definition = definition });
            mapChoices.Add(new MapChoice());
            mapSelector.ItemsSource = mapChoices;
            if (!testMode) LoadSettings();
            SelectWeapon(restoredWeaponId, restoredShellId);
            MapChoice initial = mapChoices.Find(delegate(MapChoice choice) { return choice.Id == restoredMapId; });
            if (initial == null) { preciseWeapon = null; preciseTarget = null; }
            mapSelector.SelectedItem = initial ?? mapChoices[0];
            ApplySelectedMap(false);
            loading = false; Recalculate();
            if (!testMode) Closing += delegate { SaveSettings(); };
        }

        private void SelectServerPage(bool selected)
        {
            var root = (FrameworkElement)Content;
            var host = (Border)root.FindName("ServerHost");
            if (selected && serverBrowser == null) {
                serverBrowser = new ServerBrowserView(testMode); host.Child = serverBrowser;
                serverBrowser.ServerChosen += ChooseServerMap;
                serverBrowser.FollowStopped += StopServerFollow;
                serverBrowser.FollowedServerUpdated += UpdateFollowedServer;
            }
            serverPageActive=selected;
            host.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            ((UIElement)root.FindName("MortarHeading")).Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
            ((UIElement)root.FindName("MortarBody")).Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)root.FindName("ModeBadge")).Text = selected ? "●  在线列表" : followedServerId == null ? "●  离线可用" : "●  地图跟随";
            ((Button)root.FindName("ServerTab")).Background = new SolidColorBrush(selected ? Color.FromRgb(48,74,100) : Colors.Transparent);
            ((Button)root.FindName("MortarTab")).Background = new SolidColorBrush(!selected && !raasMode ? Color.FromRgb(48,74,100) : Colors.Transparent);
            ((Button)root.FindName("RaasTab")).Background = new SolidColorBrush(!selected && raasMode ? Color.FromRgb(48,74,100) : Colors.Transparent);
            ((UIElement)root.FindName("FireControls")).Visibility=raasMode?Visibility.Collapsed:Visibility.Visible;
            ((UIElement)root.FindName("RaasHost")).Visibility=raasMode?Visibility.Visible:Visibility.Collapsed;
            ((Border)root.FindName("Sidebar")).VerticalAlignment=raasMode?VerticalAlignment.Stretch:VerticalAlignment.Top;
            ((TextBlock)root.FindName("PageTitle")).Text=raasMode?"RAAS 猜点":"射击计算";
            ((TextBlock)root.FindName("PageDescription")).Text=raasMode?"确认已知旗点，查看下一点。":"点选地图，或输入坐标。";
            ((RadioButton)root.FindName("PlaceTarget")).Visibility=((RadioButton)root.FindName("PlaceMortar")).Visibility=raasMode?Visibility.Collapsed:Visibility.Visible;
            map.PredictionMode=raasMode;
            ((TextBlock)root.FindName("MapGestureHint")).Text=raasMode?"点选旗点确认":"左键目标 · 右键双击炮位";
            if(raasMode) EnsureRaasView();
            if(!selected) {
                ApplyFollowedMap();
                if(raasMode) RestoreRaasDisplay();
            }
            if (serverBrowser != null) serverBrowser.SetActive(selected);
        }

        private void EnsureRaasView()
        {
            if(raasView==null) {
                try {
                    var repository=new RaasRepository(layersDirectory);
                    if(mapChoices.Any(c=>c.Definition!=null && c.Definition.SourceCommit!=repository.SourceCommit)) throw new FormatException("RAAS 数据与底图标定版本不一致，请更新完整数据包。");
                    raasView=new RaasView(repository);
                    ((Border)((FrameworkElement)Content).FindName("RaasHost")).Child=raasView;
                    raasView.MarkersChanged+=map.SetPredictionMarkers;
                    map.PredictionPointClicked+=raasView.ClickPoint;
                    raasView.MapRequested+=delegate(string id) {
                        var choice=mapChoices.Find(c=>c.Id==id); if(choice==null) return;
                        if(mapSelector.SelectedItem==choice) { if(CanFollowRaas()) StopServerFollow(); currentLayerRawName=null; UpdateRaasMap(); }
                        else mapSelector.SelectedItem=choice;
                    };
                    raasView.LayerRequested+=delegate(string raw) { if(CanFollowRaas()) StopServerFollow(); currentLayerRawName=raw; };
                    raasView.SetMap(selectedMap==null?"custom":selectedMap.Id,null);
                } catch(Exception ex) { if(!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is FormatException) && !(ex is ArgumentException)) throw; ((Border)((FrameworkElement)Content).FindName("RaasHost")).Child=new TextBlock { Text="RAAS 数据无法读取："+ex.Message,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Salmon }; return; }
            }
        }
        private void UpdateRaasMap() { if(raasView!=null) raasView.SetMap(selectedMap==null?"custom":selectedMap.Id,currentLayerRawName); }
        private void RestoreRaasDisplay()
        {
            if(raasView==null) return;
            var choice=mapChoices.Find(c=>c.Id==raasView.MapId);
            if(choice!=null) SetDisplayedMap(choice);
        }
        private void SetDisplayedMap(MapChoice choice)
        {
            if(mapSelector.SelectedItem==choice) return;
            bool wasLoading=loading; loading=true;
            try { mapSelector.SelectedItem=choice; } finally { loading=wasLoading; }
            ApplySelectedMap(true);
        }
        private bool CanFollowRaas()
        {
            if(followedServer==null || raasView==null) return false;
            var choice=ServerMapChoice(followedServer);
            return choice!=null && raasView.HasLayer(choice.Id,followedServer.Map);
        }

        private void SelectWeapon(string id, string shellId)
        {
            bool wasLoading = loading; loading = true;
            try {
                selectedWeapon = WeaponCatalog.Find(id);
                selectedShell = selectedWeapon.Shells.FirstOrDefault(s => s.Id == shellId) ?? selectedWeapon.Shells[0];
                weaponCategory.SelectedItem = selectedWeapon.Category;
                weaponSelector.ItemsSource = WeaponCatalog.All.Where(w => w.Category == selectedWeapon.Category).ToList();
                weaponSelector.SelectedItem = selectedWeapon;
                weaponSelector.ToolTip = selectedWeapon.Name;
                shellSelector.ItemsSource = selectedWeapon.Shells; shellSelector.SelectedItem = selectedShell;
                shellSelector.IsEnabled = selectedWeapon.Shells.Count > 1;
                weaponInfo.Text = string.Format(CultureInfo.InvariantCulture, "初速 {0:0.##} m/s · 仰角 {1:0.###}–{2:0.###}°{3}", selectedShell.Velocity, selectedWeapon.MinDegrees, selectedWeapon.MaxDegrees, selectedWeapon.Deceleration > 0 ? " · 距离相关速度近似" : "");
                weaponSelector.ToolTip=selectedWeapon.Name+"\n"+weaponInfo.Text;
                UpdateHeightMode();
            } finally { loading = wasLoading; }
            if (!loading) Recalculate();
        }

        private MapChoice ServerMapChoice(ServerRecord server)
        {
            var definition = MapCatalog.FromServerLayer(mapChoices.Where(c => c.Definition != null).Select(c => c.Definition), server.Map);
            return definition == null ? null : mapChoices.Find(c => c.Definition == definition);
        }

        private void ChooseServerMap(ServerRecord server)
        {
            if (!testMode && DateTime.UtcNow - server.RetrievedAt > TimeSpan.FromMinutes(2)) { serverBrowser.ShowMapSyncMessage("服务器数据较旧，请刷新后再同步地图。"); return; }
            if(!ServerData.ValidSession(server.SessionId)) { serverBrowser.ShowMapSyncMessage("服务器会话无效，请刷新列表后重试。"); return; }
            followedServerId = server.SessionId; followedServer=server;
            serverBrowser.FollowServer(followedServerId,server.Name);
            serverBrowser.ShowMapSyncMessage("已启用地图跟随。请手动切换至射击计算或 RAAS 猜点；当前页面和地图保持不变。");
            serverMapNote.Text="已启用跟随："+server.Name+"\n手动进入地图页面后同步。";
            stopServerFollow.Visibility=Visibility.Visible;
        }

        private void UpdateFollowedServer(ServerRecord server)
        {
            if (followedServerId == null) return;
            if(server!=null && server.SessionId!=followedServerId) return;
            followedServer=server;
            if(!serverPageActive) ApplyFollowedMap();
        }
        private void ApplyFollowedMap()
        {
            if(followedServerId==null) return;
            if(followedServer==null) {
                serverMapNote.Text="未取得所跟随服务器的数据，保留当前地图。";
                if(raasMode && raasView!=null) { raasView.UseManualMode(); raasView.ShowNotice("暂未取得服务器最新状态，猜点保持手动选择。"); ((TextBlock)((FrameworkElement)Content).FindName("ModeBadge")).Text="●  RAAS 手动"; }
                return;
            }
            var choice=ServerMapChoice(followedServer);
            if(raasMode && !CanFollowRaas()) {
                string notice="服务器当前图层 "+followedServer.Map+" 不是可跟随的 RAAS 图层，猜点保持手动选择。";
                if(raasView!=null) { raasView.UseManualMode(); raasView.ShowNotice(notice); }
                serverMapNote.Text=notice;
                ((TextBlock)((FrameworkElement)Content).FindName("ModeBadge")).Text="●  RAAS 手动";
                return;
            }
            if(choice==null) { serverMapNote.Text="服务器地图暂不支持："+followedServer.Map+"，保留当前地图。"; return; }
            ApplyServerMap(followedServer,choice);
        }
        private void ApplyServerMap(ServerRecord server, MapChoice choice)
        {
            currentLayerRawName=server.Map;
            SetDisplayedMap(choice);
            if(raasMode && raasView!=null) {
                raasView.SetMap(choice.Id,server.Map,server.SessionId);
                raasView.SetFactions(server.Team1Faction,server.Team2Faction,"服务器当前阵营");
            }
            serverMapNote.Text = "跟随 · " + server.Name;
            serverMapNote.ToolTip = server.Name + " · 每 60 秒同步地图";
            stopServerFollow.Visibility = Visibility.Visible;
            ((TextBlock)((FrameworkElement)Content).FindName("ModeBadge")).Text="●  地图跟随";
            if(raasView!=null) raasView.ShowNotice(null);
        }

        private void StopServerFollow()
        {
            if (followedServerId == null) return;
            followedServerId = null; if (serverBrowser != null) serverBrowser.FollowServer(null);
            followedServer=null; if(raasView!=null) raasView.UseManualMode();
            serverMapNote.Text = "已停止跟随服务器，可手动选择地图。"; stopServerFollow.Visibility = Visibility.Collapsed;
            ((TextBlock)((FrameworkElement)Content).FindName("ModeBadge")).Text = "●  离线可用";
        }

        public void ServerSmokeTest(string outputPath, ServerSnapshot sample)
        {
            string oldWeapon = weaponGrid.Text, oldTarget = targetGrid.Text; var oldSolution = solution;
            SelectServerPage(true); serverBrowser.LoadSmokeSnapshot(sample);
            SelectServerPage(false);
            if (weaponGrid.Text != oldWeapon || targetGrid.Text != oldTarget || solution != oldSolution) throw new InvalidOperationException("Page switching changed mortar data.");
            SelectServerPage(true); UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(outputPath)) encoder.Save(file);
            File.WriteAllText(Path.ChangeExtension(outputPath, ".txt"), "PASS: server page, search/empty state, favorites, endpoint folding, mortar state preserved across page switches; no network, Steam launch or user settings in smoke mode.\r\n", Encoding.UTF8);
        }

        private void PlacePoint(Vec2 position, bool mortar)
        {
            bool wasLoading = loading; loading = true;
            try
            {
                string grid = GridReference.Format(position, 3);
                if (mortar) { preciseWeapon = position; weaponGrid.Text = grid; }
                else { preciseTarget = position; targetGrid.Text = grid; }
            }
            finally { loading = wasLoading; }
            if (!loading) Recalculate();
        }

        private void ApplySelectedMap(bool resetPositions)
        {
            bool wasLoading = loading; loading = true;
            try
            {
                var choice = (MapChoice)mapSelector.SelectedItem;
                selectedMap = choice.Definition; terrain = null; mapError = null;
                map.MapImage = null;
                if (selectedMap == null)
                {
                    map.WorldSize = customMapSize; map.WorldHeight = customMapSize;
                    if (File.Exists(ImagePath)) map.MapImage = ReadImage(ImagePath);
                    mapName.Text = map.MapImage == null ? "离线坐标网格" : "自定义离线地图";
                    mapInfo.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.##} × {0:0.##} m · 自定义标定", customMapSize);
                }
                else
                {
                    map.WorldSize = selectedMap.Width; map.WorldHeight = selectedMap.Height;
                    mapName.Text = selectedMap.Name;
                    mapInfo.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.##} × {1:0.##} m · 已标定", selectedMap.Width, selectedMap.Height);
                    try { map.MapImage = ReadImage(selectedMap.ImagePath); }
                    catch (Exception ex) { mapError = "地图图片无法读取：" + ex.Message; }
                    if (File.Exists(selectedMap.HeightmapPath))
                    {
                        try { terrain = TerrainHeightMap.Load(selectedMap); }
                        catch (Exception) { terrain = null; }
                    }
                }
                automaticHeight.IsEnabled = terrain != null;
                automaticHeight.IsChecked = terrain != null && preferAutomaticHeight;
                double mapWidth = map.WorldSize, mapHeight = map.WorldHeight;
                if (preciseWeapon.HasValue && !InsideMap(preciseWeapon.Value, mapWidth, mapHeight)) preciseWeapon = null;
                if (preciseTarget.HasValue && !InsideMap(preciseTarget.Value, mapWidth, mapHeight)) preciseTarget = null;
                if (resetPositions)
                {
                    preciseWeapon = null; preciseTarget = null;
                    weaponHeight.Text = "0"; targetHeight.Text = "0";
                    double x = map.WorldSize * 0.42, y = map.WorldHeight * 0.48;
                    weaponGrid.Text = GridReference.Format(new Vec2(x, y), 3);
                    targetGrid.Text = GridReference.Format(new Vec2(Math.Min(map.WorldSize * 0.9, x + 600), Math.Min(map.WorldHeight * 0.9, y + 300)), 3);
                }
                UpdateHeightMode(); map.ResetView();
            }
            catch (Exception ex) { mapError = "地图加载失败：" + ex.Message; terrain = null; automaticHeight.IsEnabled = false; automaticHeight.IsChecked = false; UpdateHeightMode(); }
            finally { loading = wasLoading; }
            if (!loading) Recalculate();
        }

        private void UpdateHeightMode()
        {
            bool automatic = terrain != null && automaticHeight.IsChecked == true;
            weaponHeight.IsReadOnly = automatic; targetHeight.IsReadOnly = automatic;
            weaponHeight.Opacity = automatic ? 0.75 : 1; targetHeight.Opacity = automatic ? 0.75 : 1;
            heightHint.Text = (automatic ? "自动读取地形高度" : "高差需手动填写") + "，另计炮口高 " + selectedWeapon.MuzzleHeight.ToString("0.###", CultureInfo.InvariantCulture) + "m。";
            terrainNote.Text = automatic ? "自动地形高程 · 不含建筑附加高度" : (terrain == null ? "本图无高程数据 · 手动输入高度" : "手动地面高差 · 可开启自动高程");
        }

        private static double Number(string text, string label)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value))
                throw new FormatException(label + "请输入有效数字（小数使用 .）。");
            return value;
        }

        private void Recalculate()
        {
            solution = null;
            string unitLabel = selectedWeapon.Unit == "mil" ? "密位 / MIL" : selectedWeapon.Unit == "degMin" ? "仰角 / 度分" : "仰角 / DEG";
            elevationLabel.Text = (selectedWeapon.PreferHigh ? "高抛" : "低抛") + unitLabel;
            elevation.FontSize = selectedWeapon.Unit == "degMin" ? 26 : 33;
            map.RangeMeters = selectedWeapon.NominalRange(selectedShell);
            try
            {
                if (mapError != null) throw new FormatException(mapError);
                double width = selectedMap == null ? customMapSize : selectedMap.Width;
                if (width < 300 || width > 7800) throw new FormatException("地图边长应为 300–7800 米。");
                map.WorldSize = width; map.WorldHeight = selectedMap == null ? width : selectedMap.Height;
                Vec2 mortar = GridReference.Parse(weaponGrid.Text), target = GridReference.Parse(targetGrid.Text);
                if (preciseWeapon.HasValue) mortar = preciseWeapon.Value;
                if (preciseTarget.HasValue) target = preciseTarget.Value;
                if (mortar.X >= width || mortar.Y >= map.WorldHeight || target.X >= width || target.Y >= map.WorldHeight)
                    throw new FormatException("坐标超出当前地图，请检查坐标或地图边长。");
                bool automatic = terrain != null && automaticHeight.IsChecked == true;
                if (automatic)
                {
                    bool wasLoading = loading; loading = true;
                    try { weaponHeight.Text = terrain.Sample(mortar).ToString("F2", CultureInfo.InvariantCulture); targetHeight.Text = terrain.Sample(target).ToString("F2", CultureInfo.InvariantCulture); }
                    finally { loading = wasLoading; }
                }
                double h1 = Number(weaponHeight.Text, "炮位地面高度："), h2 = Number(targetHeight.Text, "目标地面高度：");
                if (Math.Abs(h1) > 10000 || Math.Abs(h2) > 10000) throw new FormatException("高度绝对值应小于等于 10000 米。");
                map.Weapon = mortar; map.Target = target; map.HasMarkers = true;
                weaponGrid.ToolTip = PositionHint(mortar, preciseWeapon.HasValue);
                targetGrid.ToolTip = PositionHint(target, preciseTarget.HasValue);
                solution = Ballistics.Solve(mortar, target, h1, h2, selectedWeapon, selectedShell);
                bearing.Text = solution.BearingDegrees.ToString("F1", CultureInfo.InvariantCulture) + "°";
                distance.Text = solution.Distance.ToString("F1") + " m";
                difference.Text = solution.HeightDifference.ToString("+0.0;-0.0;0.0") + " m";
                if (solution.HighUsable || solution.LowUsable)
                {
                    elevation.Text = selectedWeapon.FormatElevation(SelectedMil);
                    flight.Text = SelectedTime.ToString("F1") + " s";
                    elevationLabel.Text = (solution.SelectedHigh ? "高抛" : "低抛") + unitLabel;
                    status.Text = (solution.SelectedHigh ? "高抛解可用 · " : solution.HighUsable ? "低抛解可用 · " : "仅低抛解可用 · ") + (automatic ? "自动地形高差" : "手动地面高差");
                    status.Foreground = new SolidColorBrush(Color.FromRgb(169,189,207));
                }
                else
                {
                    elevation.Text = "—"; flight.Text = "—";
                    status.Text = solution.Message;
                    status.Foreground = new SolidColorBrush(Color.FromRgb(255, 168, 140));
                }
            }
            catch (Exception ex)
            {
                if (!(ex is FormatException) && !(ex is ArgumentException)) throw;
                bearing.Text = "—"; elevation.Text = "—"; distance.Text = "—"; difference.Text = "—"; flight.Text = "—";
                status.Text = ex.Message; status.Foreground = new SolidColorBrush(Color.FromRgb(255, 168, 140));
                map.HasMarkers = false;
                weaponGrid.ToolTip = "请输入有效网格坐标；在地图点击可精确定位。";
                targetGrid.ToolTip = "请输入有效网格坐标；在地图点击可精确定位。";
            }
            map.InvalidateVisual();
        }

        private static bool InsideMap(Vec2 position, double width, double height)
        {
            return position.X >= 0 && position.X < width && position.Y >= 0 && position.Y < height;
        }

        private static string PositionHint(Vec2 position, bool precise)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}：东 {1:F2}m，南 {2:F2}m。{3}", precise ? "实际点击位置" : "网格中心", position.X, position.Y, precise ? "框内网格为参考，计算保留实际点位。" : "按最末级小格中心计算。");
        }

        private double SelectedMil { get { return solution.SelectedHigh ? solution.HighMil : solution.LowMil; } }
        private double SelectedTime { get { return solution.SelectedHigh ? solution.HighTimeSeconds : solution.LowTimeSeconds; } }

        private void ImportMap()
        {
            var dialog = new OpenFileDialog { Filter = "地图图片（PNG / JPEG / BMP）|*.png;*.jpg;*.jpeg;*.bmp", Title = "选择裁齐地图边缘的正方形图片" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                BitmapSource image = ReadImage(dialog.FileName);
                if (image.PixelWidth > 12000 || image.PixelHeight > 12000) throw new InvalidDataException("图片过大，请缩小到 12000 像素以内。");
                if (Math.Abs((double)image.PixelWidth / image.PixelHeight - 1) > 0.01) throw new InvalidDataException("请使用正方形地图，并裁去界面边框、图例与空白。否则坐标无法准确对齐。");
                double? size = AskCustomMapSize();
                if (!size.HasValue) return;
                Directory.CreateDirectory(dataDirectory);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(ImagePath)) encoder.Save(stream);
                bool wasLoading = loading; loading = true;
                try { customMapSize = size.Value; mapSelector.SelectedItem = mapChoices[mapChoices.Count - 1]; }
                finally { loading = wasLoading; }
                ApplySelectedMap(true);
                MessageBox.Show(this, "图片与尺寸标定已保存，可离线使用。\n\n图像左上角必须对应 A01 左上角。图片没有高程信息，两处地面高度仍需手动填写。", "地图已导入");
            }
            catch (Exception ex) { MessageBox.Show(this, "导入失败：" + ex.Message + "\n建议使用 PNG 或 JPEG 图片。", "地图导入"); }
        }

        private double? AskCustomMapSize()
        {
            var dialog = new Window { Title = "标定自定义地图", Owner = this, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Brushes.White, FontFamily = FontFamily };
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = "地图实际边长（米）", FontSize = 16, Margin = new Thickness(0, 0, 0, 12) });
            var input = new TextBox { Text = customMapSize.ToString("0.##", CultureInfo.InvariantCulture), FontSize = 16, Padding = new Thickness(8), Background = new SolidColorBrush(Color.FromRgb(25, 42, 54)), Foreground = Brushes.White, CaretBrush = Brushes.White };
            panel.Children.Add(input);
            panel.Children.Add(new TextBlock { Text = "用于将图片上的位置换算为游戏距离。请填写整张正方形地图的真实边长；内置地图无需设置。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 10, 0, 12) });
            var error = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 10, 0) };
            var accept = new Button { Content = "导入", IsDefault = true, Padding = new Thickness(16, 7, 16, 7) };
            double result = customMapSize;
            accept.Click += delegate
            {
                try { result = Number(input.Text, "地图边长："); if (result < 300 || result > 7800) throw new FormatException("请输入 300–7800 米之间的地图边长。"); dialog.DialogResult = true; }
                catch (FormatException ex) { error.Text = ex.Message; input.Focus(); }
            };
            buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons); dialog.Content = panel;
            dialog.Loaded += delegate { input.Focus(); input.SelectAll(); };
            return dialog.ShowDialog() == true ? (double?)result : null;
        }

        private static BitmapSource ReadImage(string path)
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.CreateOptions = BitmapCreateOptions.IgnoreImageCache; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze(); return image;
        }

        private void ClearMap()
        {
            try
            {
                if (File.Exists(ImagePath)) File.Delete(ImagePath);
                mapSelector.SelectedItem = mapChoices[mapChoices.Count - 1]; ApplySelectedMap(false);
            }
            catch (IOException ex) { MessageBox.Show(this, "无法清除图片：" + ex.Message, "地图图片"); }
            catch (UnauthorizedAccessException ex) { MessageBox.Show(this, "无法清除图片：" + ex.Message, "地图图片"); }
        }

        private void LoadSettings()
        {
            try
            {
                string path = Path.Combine(dataDirectory, "settings.xml");
                if (File.Exists(path))
                {
                    var document = new XmlDocument { XmlResolver = null }; document.Load(path);
                    var boxes = new[] { weaponGrid, targetGrid, weaponHeight, targetHeight };
                    var names = new[] { "weapon", "target", "weaponHeight", "targetHeight" };
                    for (int i = 0; i < names.Length; i++) { XmlNode node = document.SelectSingleNode("/settings/" + names[i]); if (node != null) boxes[i].Text = node.InnerText; }
                    XmlNode sizeNode = document.SelectSingleNode("/settings/mapSize");
                    double savedSize;
                    if (sizeNode != null && double.TryParse(sizeNode.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out savedSize) && savedSize >= 300 && savedSize <= 7800) customMapSize = savedSize;
                    XmlNode mapNode = document.SelectSingleNode("/settings/mapId");
                    if (mapNode != null) restoredMapId = mapNode.InnerText;
                    else if (File.Exists(ImagePath)) restoredMapId = "custom";
                    XmlNode autoNode = document.SelectSingleNode("/settings/automaticHeight");
                    preferAutomaticHeight = autoNode == null || autoNode.InnerText != "false";
                    XmlNode weaponNode = document.SelectSingleNode("/settings/weaponId"), shellNode = document.SelectSingleNode("/settings/shellId");
                    if (weaponNode != null) restoredWeaponId = weaponNode.InnerText;
                    if (shellNode != null) restoredShellId = shellNode.InnerText;
                    preciseWeapon = ReadPrecisePoint(document, "weapon", weaponGrid.Text);
                    preciseTarget = ReadPrecisePoint(document, "target", targetGrid.Text);
                }
            }
            catch (Exception) { status.Text = "本地设置未能读取，使用默认值。"; }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
                using (var writer = XmlWriter.Create(Path.Combine(dataDirectory, "settings.xml"), settings))
                {
                    writer.WriteStartElement("settings");
                    string[] names = { "weapon", "target", "weaponHeight", "targetHeight", "mapSize", "mapId", "automaticHeight" };
                    string[] values = { weaponGrid.Text, targetGrid.Text, weaponHeight.Text, targetHeight.Text, customMapSize.ToString("0.##", CultureInfo.InvariantCulture), ((MapChoice)mapSelector.SelectedItem).Id, preferAutomaticHeight ? "true" : "false" };
                    for (int i = 0; i < names.Length; i++) writer.WriteElementString(names[i], values[i]);
                    writer.WriteElementString("weaponId", selectedWeapon.Id); writer.WriteElementString("shellId", selectedShell.Id);
                    WritePrecisePoint(writer, "weapon", preciseWeapon);
                    WritePrecisePoint(writer, "target", preciseTarget);
                    writer.WriteEndElement();
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "无法保存本地设置：" + ex.Message + "\n请将软件放到可写文件夹。", "保存设置"); }
        }

        private static void WritePrecisePoint(XmlWriter writer, string prefix, Vec2? point)
        {
            if (!point.HasValue) return;
            writer.WriteElementString(prefix + "X", point.Value.X.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteElementString(prefix + "Y", point.Value.Y.ToString("R", CultureInfo.InvariantCulture));
        }

        private static Vec2? ReadPrecisePoint(XmlDocument document, string prefix, string grid)
        {
            XmlNode xNode = document.SelectSingleNode("/settings/" + prefix + "X"), yNode = document.SelectSingleNode("/settings/" + prefix + "Y");
            double x, y;
            if (xNode == null || yNode == null || !double.TryParse(xNode.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out x) || !double.TryParse(yNode.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return null;
            var point = new Vec2(x, y);
            try
            {
                if (GridReference.Format(point, 3) != GridReference.Format(GridReference.Parse(grid), 3)) return null;
                return point;
            }
            catch (ArgumentException) { return null; }
            catch (FormatException) { return null; }
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "1. 在左侧选择游戏地图。地图图片和尺寸已本地保存；切换后使用该地图的实际宽高。也可选择自定义图片 / 空白网格。\n\n" +
                "2. 填写游戏网格坐标，如 D05-5-5。每个大格 300 米，九宫格顺序与数字小键盘一致。按最细小格中心计算。\n\n" +
                "地图左键放目标，双击右键放炮位；按住右键移动鼠标可拖动地图，滚轮缩放，也支持中键拖动。右键单击不放置标记，双击的第二次松开时设置炮位；拖动不会设置炮位。地图点击保留实际位置，不吸附到网格中心；框内网格仅作参考，悬停可查看实际米制坐标。\n\n" +
                "3. 默认自动读取本地地形高程，可关闭后手动输入高度。地形数据不含桥梁、房顶等附加高度；目标更高时高差为正。炮口高度按所选武器计算。\n\n" +
                "4. 左上角选择工事或载具、具体武器及弹药。程序使用各自初速、重力和仰角限制，按武器显示密位、度数或度分。方位 0° 为北、90° 为东；NATO 密位采用 6400/圈。飞行时间为理论估算。\n\n" +
                "5. 自定义图片须裁齐地图边缘，导入时在弹窗中填写真实边长；内置地图无需设置。范围圈仅代表平地理论最大射程。\n\n" +
                "贝拉亚为暂退役的旧版地图，仅提供底图和手动高差。本版不判断地形遮挡，不含 OCR 或游戏读取，命中精度尚未经游戏实测。\n\n" +
                "服务器列表：点选仅查看详情。点击“启用地图跟随”后仍停留在列表，手动进入射击计算或 RAAS 猜点才同步，并每 60 秒跟随换图。非 RAAS 或未收录图层时，猜点保留手动地图和确认链。可在列表或射击页停止跟随。上报人数、EOS 登记与可见成员口径不同；持续差异提示不等于已证实阴兵。\n\n" +
                "RAAS 猜点：顶部选择该页，指定地图、具体 RAAS 图层和己方基地，依次确认旗点。基地显示对应国旗或阵营代号；跟随时用服务器当前阵营，手动模式用图层默认阵营，缺失则显示未知。支持上一步与重置，数据内置且可离线计算。概率为路线模型估计，游戏更新后可能需要更新数据。\n\n" +
                "算法及游戏参数参考 sh4rkman/SquadCalc（非商业许可）。地图素材归各自权利人。详见随附说明与 third_party。",
                "使用说明 · 游戏 Squad 专用");
        }

        public void SmokeTest(string outputPath)
        {
            int bundledMaps = 0;
            foreach (MapChoice choice in mapChoices)
            {
                if (choice.Definition == null) continue;
                mapSelector.SelectedItem = choice;
                if (mapError != null || map.MapImage == null || map.WorldSize != choice.Definition.Width || map.WorldHeight != choice.Definition.Height) throw new InvalidOperationException("Map selection failed: " + choice.Id);
                if (choice.Id != "belaya" && (terrain == null || automaticHeight.IsChecked != true)) throw new InvalidOperationException("Terrain missing: " + choice.Id);
                if (choice.Id == "belaya" && (terrain != null || automaticHeight.IsEnabled)) throw new InvalidOperationException("Legacy map must use manual heights.");
                bundledMaps++;
            }
            if (bundledMaps < 25) throw new InvalidOperationException("Incomplete map catalog.");
            if (((FrameworkElement)Content).FindName("Copy") != null) throw new InvalidOperationException("Copy button was not removed.");
            if (((FrameworkElement)Content).FindName("MapSize") != null) throw new InvalidOperationException("Redundant map size field was not removed.");
            mapSelector.SelectedItem = mapChoices[mapChoices.Count - 1];
            weaponGrid.Text = "D05-5-5"; targetGrid.Text = "F06-5-5";
            TestPrecisePoints();
            string originalWeapon = weaponGrid.Text;
            string originalTarget = targetGrid.Text;
            targetGrid.Text = "!invalid";
            if (solution != null || elevation.Text != "—" || map.HasMarkers) throw new InvalidOperationException("Invalid input did not clear result.");
            targetGrid.Text = originalTarget;
            if (solution == null || !solution.HighUsable) throw new InvalidOperationException("Default solution is unavailable.");
            targetHeight.Text = "NaN";
            if (solution != null || elevation.Text != "—") throw new InvalidOperationException("Invalid height did not clear result.");
            targetHeight.Text = "0";
            targetGrid.Text = weaponGrid.Text;
            if (solution == null || solution.HighUsable || elevation.Text != "—") throw new InvalidOperationException("Coincident points were not rejected.");
            targetGrid.Text = "Z99";
            if (solution != null || map.HasMarkers) throw new InvalidOperationException("Map boundary was not enforced.");
            weaponGrid.Text = "A01-5-4"; targetGrid.Text = "A01-5-5"; targetHeight.Text = "100";
            if (solution == null || solution.HighUsable || !solution.LowUsable || elevation.Text != "1284" || elevationLabel.Text != "低抛密位 / MIL") throw new InvalidOperationException("Low-only solution was not shown.");
            weaponGrid.Text = originalWeapon; targetGrid.Text = originalTarget; targetHeight.Text = "0";

            // Synthetic image fixture verifies the local PNG pipeline without game assets.
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen()) { context.DrawRectangle(Brushes.DarkSlateGray, null, new Rect(0, 0, 300, 300)); }
            var fixture = new RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32); fixture.Render(drawing);
            var imageEncoder = new PngBitmapEncoder(); imageEncoder.Frames.Add(BitmapFrame.Create(fixture));
            string fixturePath = Path.ChangeExtension(outputPath, ".import-test.png");
            using (var file = File.Create(fixturePath)) imageEncoder.Save(file);
            map.MapImage = ReadImage(fixturePath); map.InvalidateVisual(); UpdateLayout();
            if (map.MapImage.PixelWidth != 300 || map.MapImage.PixelHeight != 300) throw new InvalidOperationException("Local map PNG load failed.");
            map.MapImage = null;
            Topmost = true; Topmost = false;
            map.ZoomBy(1.3); map.ResetView();
            PlacePoint(new Vec2(1012.34, 1321.09), true); PlacePoint(new Vec2(1601.72, 1522.65), false);
            mapSelector.SelectedItem = mapChoices.Find(delegate(MapChoice choice) { return choice.Id == "yehorivka"; });
            if (preciseWeapon.HasValue || preciseTarget.HasValue) throw new InvalidOperationException("Map switch retained precise points from another map.");
            var exactWeapon = new Vec2(2667.12, 3048.34);
            var exactTarget = new Vec2(3267.89, 3348.56);
            PlacePoint(exactWeapon, true); PlacePoint(exactTarget, false);
            automaticHeight.IsChecked = false; automaticHeight.IsChecked = true;
            if (map.Weapon.X != exactWeapon.X || map.Target.Y != exactTarget.Y || Math.Abs(Number(weaponHeight.Text, "height") - terrain.Sample(exactWeapon)) > 0.005001) throw new InvalidOperationException("Height mode lost precise positions or sampled the grid centre.");
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(outputPath)) encoder.Save(file);
            File.WriteAllText(Path.ChangeExtension(outputPath, ".txt"), "PASS: WPF startup, all " + bundledMaps + " map selections and dimensions, terrain activation, legacy manual fallback, copy button removed, exact clicks, repeat clicks within one grid, independent manual edit, precise XML round trip, stale/nonfinite point rejection, precise map bounds, height-mode preservation and sampling, invalid grid/height clearing and recovery, coincident points, low-only fallback, local PNG pipeline, topmost, map zoom/reset, PNG render.\r\n", Encoding.UTF8);
        }

        private void TestPrecisePoints()
        {
            var mortar = new Vec2(1012.34, 1321.09);
            var target = new Vec2(1600.72, 1522.65);
            PlacePoint(mortar, true); PlacePoint(target, false);
            if (map.Weapon.X != mortar.X || map.Weapon.Y != mortar.Y || map.Target.X != target.X || map.Target.Y != target.Y) throw new InvalidOperationException("Map click snapped to a grid centre.");
            string sameGrid = targetGrid.Text;
            target = new Vec2(1601.72, 1522.65); PlacePoint(target, false);
            double expectedDistance = Math.Sqrt(Math.Pow(target.X - mortar.X, 2) + Math.Pow(target.Y - mortar.Y, 2));
            if (targetGrid.Text != sameGrid || solution == null || Math.Abs(solution.Distance - expectedDistance) > 1e-8) throw new InvalidOperationException("Repeated clicks in the same grid did not update the solution.");

            var xml = new StringBuilder();
            using (var writer = XmlWriter.Create(xml, new XmlWriterSettings { OmitXmlDeclaration = true }))
            {
                writer.WriteStartElement("settings"); WritePrecisePoint(writer, "weapon", preciseWeapon); WritePrecisePoint(writer, "target", preciseTarget); writer.WriteEndElement();
            }
            var document = new XmlDocument { XmlResolver = null }; document.LoadXml(xml.ToString());
            Vec2? restoredWeapon = ReadPrecisePoint(document, "weapon", weaponGrid.Text), restoredTarget = ReadPrecisePoint(document, "target", targetGrid.Text);
            if (!restoredWeapon.HasValue || !restoredTarget.HasValue || restoredWeapon.Value.X != mortar.X || restoredWeapon.Value.Y != mortar.Y || restoredTarget.Value.X != target.X || restoredTarget.Value.Y != target.Y) throw new InvalidOperationException("Precise settings did not round trip.");
            if (ReadPrecisePoint(document, "weapon", "A01").HasValue) throw new InvalidOperationException("Stale precise point was not rejected.");
            document.SelectSingleNode("/settings/weaponX").InnerText = "NaN";
            if (ReadPrecisePoint(document, "weapon", weaponGrid.Text).HasValue) throw new InvalidOperationException("Nonfinite precise point was not rejected.");
            document.LoadXml("<settings />");
            if (ReadPrecisePoint(document, "weapon", weaponGrid.Text).HasValue) throw new InvalidOperationException("Old settings must use grid centres.");

            weaponHeight.Text = "40";
            if (!preciseWeapon.HasValue || !preciseTarget.HasValue || map.Weapon.X != mortar.X) throw new InvalidOperationException("Height edit moved a point.");
            customMapSize = 900; Recalculate();
            if (solution != null || map.HasMarkers) throw new InvalidOperationException("Precise points were not checked against custom map bounds.");
            customMapSize = 3000; Recalculate();
            weaponGrid.Text = "D05-5-5";
            if (preciseWeapon.HasValue || !preciseTarget.HasValue || map.Weapon.X != 1050 || map.Target.X != target.X) throw new InvalidOperationException("Manual edit did not clear only the edited point.");
            targetGrid.Text = "invalid";
            if (preciseTarget.HasValue || solution != null || map.HasMarkers) throw new InvalidOperationException("Invalid edit reused a previous precise point.");
            preciseWeapon = null; preciseTarget = null;
            weaponGrid.Text = "D05-5-5"; targetGrid.Text = "F06-5-5"; weaponHeight.Text = "0"; targetHeight.Text = "0";
            Recalculate();
        }

        private const string Layout = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:sys='clr-namespace:System;assembly=mscorlib' Margin='24,20,24,16'>
  <Grid.Resources>
    <sys:Double x:Key='{x:Static SystemParameters.VerticalScrollBarWidthKey}'>6</sys:Double>
    <sys:Double x:Key='{x:Static SystemParameters.HorizontalScrollBarHeightKey}'>6</sys:Double>
    <Style TargetType='TextBlock'><Setter Property='Foreground' Value='#E9EDF2'/></Style>
    <Style TargetType='ToolTip'><Style.Resources><DataTemplate DataType='{x:Type sys:String}'><TextBlock Text='{Binding}' TextWrapping='Wrap' MaxWidth='490'/></DataTemplate></Style.Resources><Setter Property='Background' Value='#242D39'/><Setter Property='Foreground' Value='#E9EDF2'/><Setter Property='BorderBrush' Value='#394454'/><Setter Property='Padding' Value='12,9'/><Setter Property='MaxWidth' Value='520'/></Style>
    <Style TargetType='TextBox'>
      <Setter Property='Background' Value='#111820'/><Setter Property='Foreground' Value='#EDF1F7'/><Setter Property='BorderBrush' Value='#35404E'/><Setter Property='CaretBrush' Value='#ADCDF9'/><Setter Property='SelectionBrush' Value='#365679'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Padding' Value='10,8'/><Setter Property='FontSize' Value='14'/><Setter Property='VerticalContentAlignment' Value='Center'/>
      <Style.Triggers><Trigger Property='IsKeyboardFocused' Value='True'><Setter Property='BorderBrush' Value='#8BB8F4'/></Trigger></Style.Triggers>
    </Style>
    <Style TargetType='Button'>
      <Setter Property='Background' Value='#26313F'/><Setter Property='Foreground' Value='#E9EDF2'/><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='13,8'/><Setter Property='Cursor' Value='Hand'/><Setter Property='FontSize' Value='12'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='Surface' Background='{TemplateBinding Background}' BorderBrush='Transparent' BorderThickness='1' CornerRadius='8' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='0.82'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='0.65'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#ADCDF9'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType='CheckBox'><Setter Property='Foreground' Value='#A6B2C1'/><Setter Property='FontSize' Value='12'/><Setter Property='VerticalAlignment' Value='Center'/></Style>
    <Style TargetType='RadioButton'><Setter Property='Foreground' Value='#BAC4D0'/><Setter Property='Margin' Value='0,0,12,0'/><Setter Property='FontSize' Value='11'/><Setter Property='VerticalAlignment' Value='Center'/></Style>
    <Style TargetType='Expander'><Setter Property='Foreground' Value='#A6B2C1'/><Setter Property='FontSize' Value='11'/></Style>
    <Style TargetType='ListBoxItem'><Setter Property='Foreground' Value='#E9EDF2'/><Setter Property='Padding' Value='9,7'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'><Border x:Name='Item' Background='Transparent' CornerRadius='6' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Item' Property='Background' Value='#253548'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='Item' Property='Background' Value='#304A64'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Item' Property='Background' Value='#304A64'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
    <Style TargetType='ComboBoxItem'>
      <Setter Property='Foreground' Value='#E9EDF2'/><Setter Property='Padding' Value='10,8'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'><Border x:Name='Item' Background='Transparent' CornerRadius='4' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Item' Property='Background' Value='#2D3A4A'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='Item' Property='Background' Value='#304A64'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType='ComboBox'>
      <Setter Property='Foreground' Value='#EDF1F7'/><Setter Property='FontSize' Value='12'/><Setter Property='IsTextSearchEnabled' Value='True'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'><Grid>
        <ToggleButton Focusable='False' IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
          <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border Background='#202B38' BorderBrush='#35404E' BorderThickness='1' CornerRadius='7' Padding='10,8'><Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='16'/></Grid.ColumnDefinitions><ContentPresenter Content='{Binding SelectionBoxItem, RelativeSource={RelativeSource AncestorType=ComboBox}}' ContentTemplate='{Binding SelectionBoxItemTemplate, RelativeSource={RelativeSource AncestorType=ComboBox}}' TextElement.Foreground='#EDF1F7'/><TextBlock Grid.Column='1' Text='⌄' HorizontalAlignment='Right' Foreground='#A4B4C8'/></Grid></Border></ControlTemplate></ToggleButton.Template>
        </ToggleButton>
        <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'><Border Background='#1D2733' BorderBrush='#35404E' BorderThickness='1' CornerRadius='7' Padding='3' MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='430'><ScrollViewer VerticalScrollBarVisibility='Auto'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer></Border></Popup>
      </Grid></ControlTemplate></Setter.Value></Setter>
      <Style.Triggers><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger></Style.Triggers>
    </Style>
    <Style TargetType='ScrollBar'>
      <Setter Property='Width' Value='6'/><Setter Property='Background' Value='Transparent'/><Setter Property='Focusable' Value='False'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
        <Grid Background='Transparent'><Track x:Name='PART_Track' Orientation='{TemplateBinding Orientation}' IsDirectionReversed='True' Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>
          <Track.DecreaseRepeatButton><RepeatButton x:Name='Decrease' Focusable='False' Command='{x:Static ScrollBar.PageUpCommand}'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
          <Track.Thumb><Thumb x:Name='ScrollThumb' MinHeight='24' MinWidth='4' Background='#596A80'><Thumb.Template><ControlTemplate TargetType='Thumb'><Border x:Name='Grip' Background='{TemplateBinding Background}' CornerRadius='3' Opacity='0.6'/><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Grip' Property='Opacity' Value='1'/></Trigger><Trigger Property='IsDragging' Value='True'><Setter TargetName='Grip' Property='Opacity' Value='1'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
          <Track.IncreaseRepeatButton><RepeatButton x:Name='Increase' Focusable='False' Command='{x:Static ScrollBar.PageDownCommand}'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
        </Track></Grid>
        <ControlTemplate.Triggers><Trigger Property='Orientation' Value='Horizontal'><Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/><Setter TargetName='ScrollThumb' Property='MinHeight' Value='4'/><Setter TargetName='ScrollThumb' Property='MinWidth' Value='24'/><Setter TargetName='Decrease' Property='Command' Value='{x:Static ScrollBar.PageLeftCommand}'/><Setter TargetName='Increase' Property='Command' Value='{x:Static ScrollBar.PageRightCommand}'/></Trigger></ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
      <Style.Triggers><Trigger Property='Orientation' Value='Horizontal'><Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='6'/></Trigger></Style.Triggers>
    </Style>
  </Grid.Resources>
  <Grid.RowDefinitions><RowDefinition Height='50'/><RowDefinition Height='68'/><RowDefinition Height='*'/><RowDefinition Height='26'/></Grid.RowDefinitions>
  <DockPanel LastChildFill='False'>
    <StackPanel DockPanel.Dock='Left' Orientation='Horizontal'><TextBlock Text='SQUAD TOOLS' FontWeight='SemiBold' FontSize='15' Margin='0,9,24,0'/><Border Background='#1B2531' CornerRadius='11' Padding='3' VerticalAlignment='Top'><StackPanel Orientation='Horizontal'><Button x:Name='MortarTab' Content='射击计算' Background='#304A64' Padding='16,7'/><Button x:Name='RaasTab' Content='RAAS 猜点' Background='Transparent' Padding='16,7'/><Button x:Name='ServerTab' Content='服务器' Background='Transparent' Padding='16,7'/></StackPanel></Border></StackPanel>
    <StackPanel DockPanel.Dock='Right' Orientation='Horizontal'><TextBlock x:Name='ModeBadge' Text='离线可用' Foreground='#9FAEC0' FontSize='11' Margin='0,11,18,0'/><CheckBox x:Name='Pin' Content='置顶' Foreground='#A6B2C1' VerticalAlignment='Top' Margin='0,9,16,0'/><Button x:Name='Help' Content='帮助' Background='Transparent' Padding='10,8' VerticalAlignment='Top'/></StackPanel>
  </DockPanel>
  <StackPanel x:Name='MortarHeading' Grid.Row='1'><TextBlock x:Name='PageTitle' Text='射击计算' FontSize='25' FontWeight='SemiBold'/><TextBlock x:Name='PageDescription' Text='点选地图，或输入坐标。' Foreground='#8F9EAF' Margin='0,6,0,0' FontSize='12'/></StackPanel>
  <Grid x:Name='MortarBody' Grid.Row='2'>
    <Grid.ColumnDefinitions><ColumnDefinition Width='318'/><ColumnDefinition Width='18'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
    <Border x:Name='Sidebar' Background='#171F29' BorderBrush='#2A3441' BorderThickness='1' CornerRadius='13' Padding='18' VerticalAlignment='Top'>
      <Grid><Border x:Name='RaasHost' Visibility='Collapsed'/>
      <Grid x:Name='FireControls'><Grid.RowDefinitions><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
      <ScrollViewer x:Name='InputScroll' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled' Padding='0,0,8,0'>
        <StackPanel>
          <TextBlock Text='武器' FontSize='11' Foreground='#96A5B6' Margin='0,0,0,8'/>
          <Grid Margin='0,0,0,8'><Grid.ColumnDefinitions><ColumnDefinition Width='66'/><ColumnDefinition Width='8'/><ColumnDefinition/></Grid.ColumnDefinitions><ComboBox x:Name='WeaponCategory'/><ComboBox x:Name='WeaponSelector' Grid.Column='2' ToolTipService.ShowDuration='20000'/></Grid>
          <ComboBox x:Name='ShellSelector' Margin='0,0,0,8'><ComboBox.Style><Style TargetType='ComboBox' BasedOn='{StaticResource {x:Type ComboBox}}'><Style.Triggers><Trigger Property='IsEnabled' Value='False'><Setter Property='Visibility' Value='Collapsed'/></Trigger></Style.Triggers></Style></ComboBox.Style></ComboBox>
          <TextBlock x:Name='WeaponInfo' Visibility='Collapsed'/>
          <TextBlock Text='地图' FontSize='11' Foreground='#96A5B6' Margin='0,10,0,8'/><ComboBox x:Name='MapSelector'/>
          <Grid Margin='0,7,0,0'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><TextBlock x:Name='ServerMapNote' Foreground='#91A6BE' FontSize='10' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' ToolTip='{Binding Text, RelativeSource={RelativeSource Self}}'/><Button x:Name='StopServerFollow' Grid.Column='1' Content='停止跟随' Background='Transparent' Visibility='Collapsed' Padding='7,3'/></Grid>
          <Grid Margin='0,18,0,0'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='12'/><ColumnDefinition/></Grid.ColumnDefinitions>
            <StackPanel><TextBlock Text='炮位' Foreground='#ADCDF9' FontSize='12' Margin='0,0,0,8'/><TextBox x:Name='WeaponGrid' Text='D05-5-5' ToolTip='在地图双击右键，或输入网格坐标。'/></StackPanel>
            <StackPanel Grid.Column='2'><TextBlock Text='目标' Foreground='#E4B6A4' FontSize='12' Margin='0,0,0,8'/><TextBox x:Name='TargetGrid' Text='F06-5-5' ToolTip='在地图左键点选，或输入网格坐标。'/></StackPanel>
          </Grid>
          <CheckBox x:Name='AutomaticHeight' Content='自动高程' Margin='0,18,0,12' ToolTip='{Binding Text, ElementName=HeightHint}'/>
          <Grid x:Name='HeightInputs' Margin='0,0,0,14'><Grid.Style><Style TargetType='Grid'><Setter Property='Visibility' Value='Collapsed'/><Style.Triggers><DataTrigger Binding='{Binding IsChecked, ElementName=AutomaticHeight}' Value='False'><Setter Property='Visibility' Value='Visible'/></DataTrigger></Style.Triggers></Style></Grid.Style><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='12'/><ColumnDefinition/></Grid.ColumnDefinitions>
            <StackPanel><TextBlock Text='炮位高程 · m' FontSize='11' Foreground='#96A5B6' Margin='0,0,0,8'/><TextBox x:Name='WeaponHeight' Text='0'/></StackPanel>
            <StackPanel Grid.Column='2'><TextBlock Text='目标高程 · m' FontSize='11' Foreground='#96A5B6' Margin='0,0,0,8'/><TextBox x:Name='TargetHeight' Text='0'/></StackPanel>
          </Grid>
          <TextBlock x:Name='HeightHint' Visibility='Collapsed'/>
        </StackPanel>
      </ScrollViewer>
      <StackPanel x:Name='ResultPanel' Grid.Row='1' Margin='0,10,0,0'>
        <Border Height='1' Background='#2C3745' Margin='0,0,0,16'/>
        <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
          <StackPanel><TextBlock Text='方位角' FontSize='11' Foreground='#96A5B6'/><TextBlock x:Name='Bearing' Text='—' FontSize='30' FontWeight='Medium' Margin='0,7,0,0'/></StackPanel>
          <StackPanel Grid.Column='1'><TextBlock x:Name='ElevationLabel' Text='高抛密位 / MIL' FontSize='11' Foreground='#96A5B6'/><TextBlock x:Name='Elevation' Text='—' Foreground='#ADCDF9' FontSize='33' FontWeight='Medium' Margin='0,4,0,0'/></StackPanel>
        </Grid>
        <TextBlock x:Name='Status' TextWrapping='Wrap' FontSize='11' Margin='0,10,0,18' Foreground='#A9BDCF'/>
        <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
          <StackPanel><TextBlock Text='距离' FontSize='10' Foreground='#96A5B6'/><TextBlock x:Name='Distance' Text='—' FontSize='13' Margin='0,7,0,0'/></StackPanel>
          <StackPanel Grid.Column='1' ToolTip='{Binding Text, ElementName=TerrainNote}'><TextBlock Text='高差' FontSize='10' Foreground='#96A5B6'/><TextBlock x:Name='Difference' Text='—' FontSize='13' Margin='0,7,0,0'/></StackPanel>
          <StackPanel Grid.Column='2'><TextBlock Text='飞行时间' FontSize='10' Foreground='#96A5B6'/><TextBlock x:Name='Flight' Text='—' FontSize='13' Margin='0,7,0,0'/></StackPanel>
        </Grid>
        <TextBlock x:Name='TerrainNote' Visibility='Collapsed'/>
      </StackPanel>
      </Grid></Grid>
    </Border>
    <Border Grid.Column='2' Background='#101B24' BorderBrush='#2A3441' BorderThickness='1' CornerRadius='13'>
      <Grid><Grid.RowDefinitions><RowDefinition Height='52'/><RowDefinition Height='*'/><RowDefinition Height='48'/></Grid.RowDefinitions>
        <DockPanel Margin='16,0' LastChildFill='False'><StackPanel DockPanel.Dock='Left' Orientation='Horizontal' VerticalAlignment='Center'><StackPanel Width='166' Margin='0,0,12,0'><TextBlock x:Name='MapName' Text='离线坐标网格' FontSize='13' FontWeight='Medium' TextTrimming='CharacterEllipsis' ToolTip='{Binding Text, ElementName=MapInfo}'/><TextBlock x:Name='MapInfo' Visibility='Collapsed'/></StackPanel><RadioButton x:Name='PlaceTarget' Content='目标' IsChecked='True' GroupName='Mode'/><RadioButton x:Name='PlaceMortar' Content='炮位' GroupName='Mode'/></StackPanel><StackPanel DockPanel.Dock='Right' Orientation='Horizontal' VerticalAlignment='Center'><Button x:Name='ZoomOut' Content='−' Padding='10,4' Margin='0,0,4,0'/><Button x:Name='ZoomIn' Content='+' Padding='10,4' Margin='0,0,4,0'/><Button x:Name='ResetView' Content='全图' Padding='10,4'/></StackPanel></DockPanel>
        <Border Grid.Row='1' x:Name='MapHost' BorderBrush='#2A3441' BorderThickness='0,1'/>
        <Grid Grid.Row='2' Margin='16,7'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
          <TextBlock x:Name='MapGestureHint' Text='左键目标 · 右键双击炮位' TextTrimming='CharacterEllipsis' ToolTip='按住右键或中键拖动；滚轮缩放。' Margin='0,0,10,0' Foreground='#8192A5' FontSize='10' VerticalAlignment='Center'/>
          <StackPanel Grid.Column='1' Orientation='Horizontal' VerticalAlignment='Center'><Button x:Name='ClearMap' Content='空白地图' Background='Transparent' Padding='9,5' Margin='0,0,4,0'/><Button x:Name='ImportMap' Content='导入地图' Padding='10,5'/></StackPanel>
        </Grid>
      </Grid>
    </Border>
  </Grid>
  <Border x:Name='ServerHost' Grid.Row='1' Grid.RowSpan='2' Visibility='Collapsed'/>
  <TextBlock Grid.Row='3' Text='Squad Tools · 0.6.1 预览版' Foreground='#65758A' FontSize='10' VerticalAlignment='Bottom'/>
</Grid>";
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--probe-servers") return ServerDiagnostics.Probe(args[1]);
                bool serverSmoke = args.Length >= 2 && args[0] == "--server-smoke-test";
                var application = new Application(); var window = new MainWindow(serverSmoke || (args.Length == 2 && args[0] == "--smoke-test"));
                if (serverSmoke) {
                    string path = Path.GetFullPath(args[1]);
                    window.Loaded += delegate { window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { try { window.ServerSmokeTest(path, ServerDiagnostics.SmokeSample(args.Length == 3 ? args[2] : null)); } catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(path, ".txt"), ex.ToString()); Environment.ExitCode = 1; } finally { window.Close(); } })); };
                }
                if (args.Length == 2 && args[0] == "--smoke-test")
                {
                    string path = Path.GetFullPath(args[1]);
                    window.Loaded += delegate { window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { try { window.SmokeTest(path); } catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(path, ".txt"), ex.ToString()); Environment.ExitCode = 1; } finally { window.Close(); } })); };
                }
                application.Run(window); return Environment.ExitCode;
            }
            catch (Exception ex) {
                MessageBox.Show("软件启动失败：" + ex.Message, "Squad Tools"); return 1;
            }
        }
    }
}
