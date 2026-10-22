using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        private TextBox weaponGrid, targetGrid, weaponHeight, targetHeight, mapSize;
        private TextBlock bearing, elevation, elevationLabel, distance, difference, flight, status, mapName;
        private TextBlock mapInfo, terrainNote, heightHint;
        private ComboBox mapSelector;
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
        private FiringSolution solution;
        private Vec2? preciseWeapon, preciseTarget;
        private readonly string dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private string ImagePath { get { return Path.Combine(dataDirectory, "custom-map.png"); } }

        private sealed class MapChoice
        {
            public MapDefinition Definition;
            public string Id { get { return Definition == null ? "custom" : Definition.Id; } }
            public override string ToString() { return Definition == null ? "自定义图片 / 空白网格" : Definition.DisplayName; }
        }

        public MainWindow(bool testMode)
        {
            Title = "Squad Tools · 迫击炮计算器";
            Width = 1280; Height = 850; MinWidth = 1060; MinHeight = 740;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = (Brush)new BrushConverter().ConvertFromString("#0C141C");
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Content = XamlReader.Parse(Layout);
            var root = (FrameworkElement)Content;
            weaponGrid = (TextBox)root.FindName("WeaponGrid"); targetGrid = (TextBox)root.FindName("TargetGrid");
            weaponHeight = (TextBox)root.FindName("WeaponHeight"); targetHeight = (TextBox)root.FindName("TargetHeight");
            mapSize = (TextBox)root.FindName("MapSize");
            bearing = (TextBlock)root.FindName("Bearing"); elevation = (TextBlock)root.FindName("Elevation");
            elevationLabel = (TextBlock)root.FindName("ElevationLabel");
            distance = (TextBlock)root.FindName("Distance"); difference = (TextBlock)root.FindName("Difference"); flight = (TextBlock)root.FindName("Flight");
            status = (TextBlock)root.FindName("Status"); mapName = (TextBlock)root.FindName("MapName");
            mapInfo = (TextBlock)root.FindName("MapInfo"); terrainNote = (TextBlock)root.FindName("TerrainNote"); heightHint = (TextBlock)root.FindName("HeightHint");
            mapSelector = (ComboBox)root.FindName("MapSelector"); automaticHeight = (CheckBox)root.FindName("AutomaticHeight");
            map = new MapView(); ((Border)root.FindName("MapHost")).Child = map;
            map.PointPlaced += PlacePoint;
            ((Button)root.FindName("ImportMap")).Click += delegate { ImportMap(); };
            ((Button)root.FindName("ClearMap")).Click += delegate { ClearMap(); };
            ((Button)root.FindName("ZoomIn")).Click += delegate { map.ZoomBy(1.3); };
            ((Button)root.FindName("ZoomOut")).Click += delegate { map.ZoomBy(1 / 1.3); };
            ((Button)root.FindName("ResetView")).Click += delegate { map.ResetView(); };
            ((Button)root.FindName("Help")).Click += delegate { ShowHelp(); };
            ((CheckBox)root.FindName("Pin")).Checked += delegate { Topmost = true; };
            ((CheckBox)root.FindName("Pin")).Unchecked += delegate { Topmost = false; };
            ((RadioButton)root.FindName("PlaceMortar")).Checked += delegate { map.PlaceWeapon = true; };
            ((RadioButton)root.FindName("PlaceTarget")).Checked += delegate { map.PlaceWeapon = false; };
            mapSelector.SelectionChanged += delegate { if (!loading) ApplySelectedMap(true); };
            automaticHeight.Checked += delegate { if (!loading) { preferAutomaticHeight = true; UpdateHeightMode(); Recalculate(); } };
            automaticHeight.Unchecked += delegate { if (!loading) { preferAutomaticHeight = false; UpdateHeightMode(); Recalculate(); } };
            weaponGrid.TextChanged += delegate { if (!loading) { preciseWeapon = null; Recalculate(); } };
            targetGrid.TextChanged += delegate { if (!loading) { preciseTarget = null; Recalculate(); } };
            foreach (var box in new[] { weaponHeight, targetHeight, mapSize }) box.TextChanged += delegate { if (!loading) Recalculate(); };
            foreach (MapDefinition definition in MapCatalog.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "maps"))) mapChoices.Add(new MapChoice { Definition = definition });
            mapChoices.Add(new MapChoice());
            mapSelector.ItemsSource = mapChoices;
            if (!testMode) LoadSettings();
            MapChoice initial = mapChoices.Find(delegate(MapChoice choice) { return choice.Id == restoredMapId; });
            if (initial == null) { preciseWeapon = null; preciseTarget = null; }
            mapSelector.SelectedItem = initial ?? mapChoices[0];
            ApplySelectedMap(false);
            loading = false; Recalculate();
            if (!testMode) Closing += delegate { SaveSettings(); };
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
                if (selectedMap == null)
                {
                    double previous;
                    if (double.TryParse(mapSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out previous) && previous >= 300 && previous <= 7800) customMapSize = previous;
                }
                var choice = (MapChoice)mapSelector.SelectedItem;
                selectedMap = choice.Definition; terrain = null; mapError = null;
                map.MapImage = null;
                if (selectedMap == null)
                {
                    mapSize.Text = customMapSize.ToString("0.##", CultureInfo.InvariantCulture);
                    mapSize.IsReadOnly = false;
                    map.WorldSize = customMapSize; map.WorldHeight = customMapSize;
                    if (File.Exists(ImagePath)) map.MapImage = ReadImage(ImagePath);
                    mapName.Text = map.MapImage == null ? "离线坐标网格" : "自定义离线地图";
                    mapInfo.Text = "自定义地图需自行填写真实边长";
                }
                else
                {
                    mapSize.Text = selectedMap.Width.ToString("0.##", CultureInfo.InvariantCulture); mapSize.IsReadOnly = true;
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
            heightHint.Text = automatic ? "自动读取地形高度，另计炮口高 1m。" : "高差需手动填写，另计炮口高 1m。";
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
            elevationLabel.Text = "高抛密位 / MIL";
            try
            {
                if (mapError != null) throw new FormatException(mapError);
                double width = selectedMap == null ? Number(mapSize.Text, "地图边长：") : selectedMap.Width;
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
                solution = Ballistics.Solve(mortar, target, h1, h2);
                bearing.Text = solution.BearingDegrees.ToString("F1", CultureInfo.InvariantCulture) + "°";
                distance.Text = solution.Distance.ToString("F1") + " m";
                difference.Text = solution.HeightDifference.ToString("+0.0;-0.0;0.0") + " m";
                if (solution.HighUsable || solution.LowUsable)
                {
                    elevation.Text = SelectedMil.ToString("F0", CultureInfo.InvariantCulture);
                    flight.Text = SelectedTime.ToString("F1") + " s";
                    elevationLabel.Text = solution.HighUsable ? "高抛密位 / MIL" : "低抛密位 / MIL";
                    status.Text = (solution.HighUsable ? "高抛解可用 · " : "仅低抛解可用 · ") + (automatic ? "自动地形高差" : "手动地面高差");
                    status.Foreground = new SolidColorBrush(Color.FromRgb(184, 235, 126));
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

        private double SelectedMil { get { return solution.HighUsable ? solution.HighMil : solution.LowMil; } }
        private double SelectedTime { get { return solution.HighUsable ? solution.HighTimeSeconds : solution.LowTimeSeconds; } }

        private void ImportMap()
        {
            var dialog = new OpenFileDialog { Filter = "地图图片（PNG / JPEG / BMP）|*.png;*.jpg;*.jpeg;*.bmp", Title = "选择裁齐地图边缘的正方形图片" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                BitmapSource image = ReadImage(dialog.FileName);
                if (image.PixelWidth > 12000 || image.PixelHeight > 12000) throw new InvalidDataException("图片过大，请缩小到 12000 像素以内。");
                if (Math.Abs((double)image.PixelWidth / image.PixelHeight - 1) > 0.01) throw new InvalidDataException("请使用正方形地图，并裁去界面边框、图例与空白。否则坐标无法准确对齐。");
                Directory.CreateDirectory(dataDirectory);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(ImagePath)) encoder.Save(stream);
                mapSelector.SelectedItem = mapChoices[mapChoices.Count - 1]; ApplySelectedMap(false);
                MessageBox.Show(this, "图片已保存到软件的 data 文件夹，可离线使用。\n\n请设置地图真实边长（米）。图像左上角必须对应 A01 左上角。\n图片没有高程信息，两处地面高度仍需手动填写。", "地图已导入");
            }
            catch (Exception ex) { MessageBox.Show(this, "导入失败：" + ex.Message + "\n建议使用 PNG 或 JPEG 图片。", "地图导入"); }
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
                    var boxes = new[] { weaponGrid, targetGrid, weaponHeight, targetHeight, mapSize };
                    var names = new[] { "weapon", "target", "weaponHeight", "targetHeight", "mapSize" };
                    for (int i = 0; i < names.Length; i++) { XmlNode node = document.SelectSingleNode("/settings/" + names[i]); if (node != null) boxes[i].Text = node.InnerText; }
                    XmlNode mapNode = document.SelectSingleNode("/settings/mapId");
                    if (mapNode != null) restoredMapId = mapNode.InnerText;
                    else if (File.Exists(ImagePath)) restoredMapId = "custom";
                    XmlNode autoNode = document.SelectSingleNode("/settings/automaticHeight");
                    preferAutomaticHeight = autoNode == null || autoNode.InnerText != "false";
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
                    double size;
                    if (selectedMap == null && double.TryParse(mapSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out size) && size >= 300 && size <= 7800) customMapSize = size;
                    string[] names = { "weapon", "target", "weaponHeight", "targetHeight", "mapSize", "mapId", "automaticHeight" };
                    string[] values = { weaponGrid.Text, targetGrid.Text, weaponHeight.Text, targetHeight.Text, customMapSize.ToString("0.##", CultureInfo.InvariantCulture), ((MapChoice)mapSelector.SelectedItem).Id, preferAutomaticHeight ? "true" : "false" };
                    for (int i = 0; i < names.Length; i++) writer.WriteElementString(names[i], values[i]);
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
                "地图左键放目标，右键放炮位；滚轮缩放，中键拖动。地图点击保留实际位置，不吸附到网格中心；框内网格仅作参考，悬停可查看实际米制坐标。\n\n" +
                "3. 默认自动读取本地地形高程，可关闭后手动输入高度。地形数据不含桥梁、房顶等附加高度；目标更高时高差为正。软件另计炮口高 1 米。\n\n" +
                "4. 方位 0° 为北、90° 为东。密位采用 6400/圈；标准迫击炮可用角 800–1580 mil。飞行时间为理论估算。\n\n" +
                "5. 导入地图须是裁齐地图范围的正方形图片，并正确设置地图边长。范围圈仅代表平地理论最大射程。\n\n" +
                "贝拉亚为暂退役的旧版地图，仅提供底图和手动高差。本版不判断地形遮挡，不含 OCR 或游戏读取，命中精度尚未经游戏实测。\n\n" +
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
            mapSize.Text = "900";
            if (solution != null || map.HasMarkers) throw new InvalidOperationException("Precise points were not checked against custom map bounds.");
            mapSize.Text = "3000";
            weaponGrid.Text = "D05-5-5";
            if (preciseWeapon.HasValue || !preciseTarget.HasValue || map.Weapon.X != 1050 || map.Target.X != target.X) throw new InvalidOperationException("Manual edit did not clear only the edited point.");
            targetGrid.Text = "invalid";
            if (preciseTarget.HasValue || solution != null || map.HasMarkers) throw new InvalidOperationException("Invalid edit reused a previous precise point.");
            preciseWeapon = null; preciseTarget = null;
            weaponGrid.Text = "D05-5-5"; targetGrid.Text = "F06-5-5"; weaponHeight.Text = "0"; targetHeight.Text = "0";
            Recalculate();
        }

        private const string Layout = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Margin='28,22,28,20'>
  <Grid.Resources>
    <Style TargetType='TextBlock'><Setter Property='Foreground' Value='#DCE5E8'/></Style>
    <Style TargetType='TextBox'>
      <Setter Property='Background' Value='#0F1B25'/><Setter Property='Foreground' Value='#EDF4F3'/><Setter Property='BorderBrush' Value='#344755'/><Setter Property='CaretBrush' Value='#B8EB7E'/><Setter Property='SelectionBrush' Value='#496E55'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Padding' Value='11,9'/><Setter Property='FontSize' Value='15'/><Setter Property='VerticalContentAlignment' Value='Center'/>
    </Style>
    <Style TargetType='Button'>
      <Setter Property='Background' Value='#233543'/><Setter Property='Foreground' Value='#E4ECEF'/><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='13,9'/><Setter Property='Cursor' Value='Hand'/><Setter Property='FontSize' Value='12'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='Surface' Background='{TemplateBinding Background}' CornerRadius='5' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='0.8'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='0.6'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType='RadioButton'><Setter Property='Foreground' Value='#CEDBDD'/><Setter Property='Margin' Value='0,0,16,0'/><Setter Property='VerticalAlignment' Value='Center'/></Style>
    <Style TargetType='ComboBoxItem'>
      <Setter Property='Foreground' Value='#E4ECEF'/><Setter Property='Padding' Value='10,8'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'><Border x:Name='Item' Background='Transparent' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Item' Property='Background' Value='#304756'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='Item' Property='Background' Value='#37583F'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType='ComboBox'>
      <Setter Property='Foreground' Value='#EDF4F3'/><Setter Property='FontSize' Value='11'/><Setter Property='IsTextSearchEnabled' Value='True'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'><Grid>
        <ToggleButton Focusable='False' IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
          <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border Background='#203340' BorderBrush='#405565' BorderThickness='1' CornerRadius='5' Padding='10,10'><Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='18'/></Grid.ColumnDefinitions><ContentPresenter Content='{Binding SelectionBoxItem, RelativeSource={RelativeSource AncestorType=ComboBox}}'/><TextBlock Grid.Column='1' Text='▾' HorizontalAlignment='Right' Foreground='#B8EB7E'/></Grid></Border></ControlTemplate></ToggleButton.Template>
        </ToggleButton>
        <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'><Border Background='#1B2B37' BorderBrush='#405565' BorderThickness='1' MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='430'><ScrollViewer VerticalScrollBarVisibility='Auto'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer></Border></Popup>
      </Grid></ControlTemplate></Setter.Value></Setter>
    </Style>
  </Grid.Resources>
  <Grid.RowDefinitions><RowDefinition Height='50'/><RowDefinition Height='68'/><RowDefinition Height='*'/><RowDefinition Height='30'/></Grid.RowDefinitions>
  <DockPanel Grid.Row='0' LastChildFill='False'>
    <StackPanel DockPanel.Dock='Left' Orientation='Horizontal'><Border Background='#B8EB7E' CornerRadius='5' Width='30' Height='30'><TextBlock Text='S' Foreground='#15251B' FontSize='20' FontWeight='Bold' HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><TextBlock Text='SQUAD TOOLS' FontWeight='SemiBold' FontSize='16' Margin='12,4,0,0'/><TextBlock Text=' /  本地工具箱' Foreground='#758D9F' FontSize='12' Margin='10,8,0,0'/></StackPanel>
    <StackPanel DockPanel.Dock='Right' Orientation='Horizontal'><Border Background='#172D24' CornerRadius='12' Padding='12,5' VerticalAlignment='Top' Margin='0,0,18,0'><TextBlock Text='●  离线可用' Foreground='#B8EB7E' FontSize='11'/></Border><CheckBox x:Name='Pin' Content='窗口置顶' Foreground='#B5C5CC' VerticalAlignment='Top' Margin='0,6,18,0'/><Button x:Name='Help' Content='使用说明' Padding='11,6' VerticalAlignment='Top'/></StackPanel>
  </DockPanel>
  <StackPanel Grid.Row='1'><TextBlock Text='迫击炮计算器' FontSize='27' FontWeight='SemiBold'/><TextBlock Text='标记炮位与目标，计算方位、密位与飞行时间。' Foreground='#8298A8' Margin='0,7,0,0' FontSize='12'/></StackPanel>
  <Grid Grid.Row='2'>
    <Grid.ColumnDefinitions><ColumnDefinition Width='300'/><ColumnDefinition Width='18'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
    <Border Background='#14212C' BorderBrush='#283B49' BorderThickness='1' CornerRadius='8' Padding='20,17'>
      <ScrollViewer VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'>
      <StackPanel>
        <TextBlock Text='游戏地图 · 标准迫击炮' FontSize='12' Foreground='#A0B2BE' Margin='0,0,0,7'/><ComboBox x:Name='MapSelector' Margin='0,0,0,13'/>
        <TextBlock Text='M   炮位坐标' Foreground='#B8EB7E' FontSize='12' Margin='0,0,0,7'/><TextBox x:Name='WeaponGrid' Text='D05-5-5' ToolTip='例如 D05-5-5，字母为列、数字为行'/>
        <TextBlock Text='T    目标坐标' Foreground='#FFA88C' FontSize='12' Margin='0,10,0,7'/><TextBox x:Name='TargetGrid' Text='F06-5-5'/>
        <TextBlock Text='点击精确定位 · 手输按小格中心计算' Foreground='#728A9B' FontSize='10' Margin='0,7,0,10'/>
        <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='12'/><ColumnDefinition/></Grid.ColumnDefinitions>
          <StackPanel><TextBlock Text='炮位地面高 / m' FontSize='11' Foreground='#A0B2BE' Margin='0,0,0,7'/><TextBox x:Name='WeaponHeight' Text='0'/></StackPanel>
          <StackPanel Grid.Column='2'><TextBlock Text='目标地面高 / m' FontSize='11' Foreground='#A0B2BE' Margin='0,0,0,7'/><TextBox x:Name='TargetHeight' Text='0'/></StackPanel>
        </Grid>
        <CheckBox x:Name='AutomaticHeight' Content='自动读取地形高程' Foreground='#B8EB7E' FontSize='11' Margin='0,11,0,0'/>
        <TextBlock x:Name='HeightHint' Text='高差需手动填写，另计炮口高 1m。' Foreground='#8196A5' FontSize='10' Margin='0,5,0,10'/>
        <Border Height='1' Background='#2B3C49'/>
        <TextBlock Text='射击诸元' FontSize='15' FontWeight='SemiBold' Margin='0,12,0,8'/>
        <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
          <StackPanel><TextBlock Text='方位角 / AZIMUTH' FontSize='10' Foreground='#879EAE'/><TextBlock x:Name='Bearing' Text='—' FontSize='29' FontWeight='SemiBold' Margin='0,7,0,0'/></StackPanel>
          <StackPanel Grid.Column='1'><TextBlock x:Name='ElevationLabel' Text='高抛密位 / MIL' FontSize='10' Foreground='#879EAE'/><TextBlock x:Name='Elevation' Text='—' Foreground='#B8EB7E' FontSize='33' FontWeight='SemiBold' Margin='0,3,0,0'/></StackPanel>
        </Grid>
        <TextBlock x:Name='Status' TextWrapping='Wrap' FontSize='11' Margin='0,7,0,10' Foreground='#B8EB7E'/>
        <DockPanel Margin='0,0,0,8'><TextBlock Text='水平距离' Foreground='#91A5B4' FontSize='12'/><TextBlock x:Name='Distance' Text='—' HorizontalAlignment='Right'/></DockPanel>
        <DockPanel Margin='0,0,0,8'><TextBlock Text='地面高差' Foreground='#91A5B4' FontSize='12'/><TextBlock x:Name='Difference' Text='—' HorizontalAlignment='Right'/></DockPanel>
        <DockPanel Margin='0,0,0,14'><TextBlock Text='理论飞行时间' Foreground='#91A5B4' FontSize='12'/><TextBlock x:Name='Flight' Text='—' HorizontalAlignment='Right'/></DockPanel>
        <TextBlock x:Name='TerrainNote' Text='本地地图与地形高程' Foreground='#91A5B4' TextWrapping='Wrap' FontSize='10'/>
      </StackPanel>
      </ScrollViewer>
    </Border>
    <Border Grid.Column='2' Background='#101B24' BorderBrush='#283B49' BorderThickness='1' CornerRadius='8'>
      <Grid><Grid.RowDefinitions><RowDefinition Height='56'/><RowDefinition Height='*'/><RowDefinition Height='79'/></Grid.RowDefinitions>
        <DockPanel Margin='16,0' LastChildFill='False'><StackPanel DockPanel.Dock='Left' Orientation='Horizontal' VerticalAlignment='Center'><StackPanel Width='230' Margin='0,0,16,0'><TextBlock x:Name='MapName' Text='离线坐标网格' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/><TextBlock x:Name='MapInfo' Foreground='#8196A5' FontSize='10' Margin='0,4,0,0'/></StackPanel><RadioButton x:Name='PlaceTarget' Content='放目标' IsChecked='True' GroupName='Mode'/><RadioButton x:Name='PlaceMortar' Content='放炮位' GroupName='Mode'/></StackPanel><StackPanel DockPanel.Dock='Right' Orientation='Horizontal' VerticalAlignment='Center'><Button x:Name='ZoomOut' Content='−' Padding='11,5' Margin='0,0,5,0'/><Button x:Name='ZoomIn' Content='+' Padding='11,5' Margin='0,0,5,0'/><Button x:Name='ResetView' Content='全图' Padding='10,5'/></StackPanel></DockPanel>
        <Border Grid.Row='1' x:Name='MapHost' BorderBrush='#283B49' BorderThickness='0,1'/>
        <Grid Grid.Row='2' Margin='16,10'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
          <StackPanel><StackPanel Orientation='Horizontal'><TextBlock Text='地图边长 / m' FontSize='11' Foreground='#91A5B4' VerticalAlignment='Center' Margin='0,0,10,0'/><TextBox x:Name='MapSize' Text='3000' Width='77' Padding='8,4' FontSize='12'/></StackPanel><TextBlock Text='左键目标 · 右键炮位 · 滚轮缩放 · 中键拖动' Foreground='#728A9B' FontSize='10' Margin='0,7,0,0'/></StackPanel>
          <StackPanel Grid.Column='1' Orientation='Horizontal' VerticalAlignment='Top'><Button x:Name='ClearMap' Content='空白网格' Margin='0,0,8,0'/><Button x:Name='ImportMap' Content='导入自定义图片'/></StackPanel>
        </Grid>
      </Grid>
    </Border>
  </Grid>
  <DockPanel Grid.Row='3' VerticalAlignment='Bottom'><TextBlock Text='SQUAD 游戏辅助工具  /  本地版 0.2.1' Foreground='#607B8E' FontSize='10'/><TextBlock Text='全官方地图 · 本地地形高程 · 无网络请求' Foreground='#607B8E' FontSize='10' HorizontalAlignment='Right'/></DockPanel>
</Grid>";
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                var application = new Application(); var window = new MainWindow(args.Length == 2 && args[0] == "--smoke-test");
                if (args.Length == 2 && args[0] == "--smoke-test")
                {
                    string path = Path.GetFullPath(args[1]);
                    window.Loaded += delegate { window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { try { window.SmokeTest(path); } catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(path, ".txt"), ex.ToString()); Environment.ExitCode = 1; } finally { window.Close(); } })); };
                }
                application.Run(window); return Environment.ExitCode;
            }
            catch (Exception ex) { MessageBox.Show("软件启动失败：" + ex.Message, "Squad Tools"); return 1; }
        }
    }
}
