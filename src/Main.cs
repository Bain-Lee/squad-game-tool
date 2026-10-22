using System;
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
        private MapView map;
        private bool loading = true;
        private FiringSolution solution;
        private readonly string dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private string ImagePath { get { return Path.Combine(dataDirectory, "custom-map.png"); } }

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
            map = new MapView(); ((Border)root.FindName("MapHost")).Child = map;
            map.PointPlaced += delegate(Vec2 p, bool mortar) { string grid = GridReference.Format(p, 3); if (mortar) weaponGrid.Text = grid; else targetGrid.Text = grid; };
            ((Button)root.FindName("ImportMap")).Click += delegate { ImportMap(); };
            ((Button)root.FindName("ClearMap")).Click += delegate { ClearMap(); };
            ((Button)root.FindName("ZoomIn")).Click += delegate { map.ZoomBy(1.3); };
            ((Button)root.FindName("ZoomOut")).Click += delegate { map.ZoomBy(1 / 1.3); };
            ((Button)root.FindName("ResetView")).Click += delegate { map.ResetView(); };
            ((Button)root.FindName("Copy")).Click += delegate { CopySolution(); };
            ((Button)root.FindName("Help")).Click += delegate { ShowHelp(); };
            ((CheckBox)root.FindName("Pin")).Checked += delegate { Topmost = true; };
            ((CheckBox)root.FindName("Pin")).Unchecked += delegate { Topmost = false; };
            ((RadioButton)root.FindName("PlaceMortar")).Checked += delegate { map.PlaceWeapon = true; };
            ((RadioButton)root.FindName("PlaceTarget")).Checked += delegate { map.PlaceWeapon = false; };
            foreach (var box in new[] { weaponGrid, targetGrid, weaponHeight, targetHeight, mapSize }) box.TextChanged += delegate { if (!loading) Recalculate(); };
            if (!testMode) LoadSettings();
            loading = false; Recalculate();
            if (!testMode) Closing += delegate { SaveSettings(); };
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
                double width = Number(mapSize.Text, "地图边长：");
                if (width < 300 || width > 7800) throw new FormatException("地图边长应为 300–7800 米。");
                map.WorldSize = width;
                Vec2 mortar = GridReference.Parse(weaponGrid.Text), target = GridReference.Parse(targetGrid.Text);
                if (mortar.X >= width || mortar.Y >= width || target.X >= width || target.Y >= width)
                    throw new FormatException("坐标超出当前地图，请检查坐标或地图边长。");
                double h1 = Number(weaponHeight.Text, "炮位地面高度："), h2 = Number(targetHeight.Text, "目标地面高度：");
                if (Math.Abs(h1) > 10000 || Math.Abs(h2) > 10000) throw new FormatException("高度绝对值应小于等于 10000 米。");
                map.Weapon = mortar; map.Target = target; map.HasMarkers = true;
                solution = Ballistics.Solve(mortar, target, h1, h2);
                bearing.Text = solution.BearingDegrees.ToString("F1", CultureInfo.InvariantCulture) + "°";
                distance.Text = solution.Distance.ToString("F1") + " m";
                difference.Text = solution.HeightDifference.ToString("+0.0;-0.0;0.0") + " m";
                if (solution.HighUsable || solution.LowUsable)
                {
                    elevation.Text = SelectedMil.ToString("F0", CultureInfo.InvariantCulture);
                    flight.Text = SelectedTime.ToString("F1") + " s";
                    elevationLabel.Text = solution.HighUsable ? "高抛密位 / MIL" : "低抛密位 / MIL";
                    status.Text = solution.HighUsable ? "高抛解可用 · 手动地面高差" : "仅低抛解可用 · 手动地面高差";
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
            }
            map.InvalidateVisual();
        }

        private void CopySolution()
        {
            if (solution == null || (!solution.HighUsable && !solution.LowUsable)) { MessageBox.Show(this, "请先输入可用的炮位、目标和高度。", "暂无可复制结果"); return; }
            try
            {
                Clipboard.SetText(string.Format(CultureInfo.InvariantCulture, "目标 {0} | 方位 {1:F1}° | 密位 {2:F0} | 距离 {3:F0}m | 高差 {4:+0.0;-0.0;0.0}m | 飞行 {5:F1}s | {6}", targetGrid.Text.Trim().ToUpperInvariant(), solution.BearingDegrees, SelectedMil, solution.Distance, solution.HeightDifference, SelectedTime, solution.HighUsable ? "高抛" : "低抛"));
            }
            catch (System.Runtime.InteropServices.COMException) { MessageBox.Show(this, "剪贴板正被其他程序占用，请稍后重试。", "复制失败"); }
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
                map.MapImage = image; mapName.Text = "自定义离线地图"; map.ResetView();
                MessageBox.Show(this, "图片已保存到软件的 data 文件夹，可离线使用。\n\n请设置地图真实边长（米）。图像左上角必须对应 A01 左上角。\n图片没有高程信息，两处地面高度仍需手动填写。", "地图已导入");
            }
            catch (Exception ex) { MessageBox.Show(this, "导入失败：" + ex.Message + "\n建议使用 PNG 或 JPEG 图片。", "地图导入"); }
        }

        private static BitmapSource ReadImage(string path)
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze(); return image;
        }

        private void ClearMap()
        {
            try
            {
                if (File.Exists(ImagePath)) File.Delete(ImagePath);
                map.MapImage = null; mapName.Text = "离线坐标网格"; map.InvalidateVisual();
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
                }
                if (File.Exists(ImagePath)) { map.MapImage = ReadImage(ImagePath); mapName.Text = "自定义离线地图"; }
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
                    string[] names = { "weapon", "target", "weaponHeight", "targetHeight", "mapSize" };
                    string[] values = { weaponGrid.Text, targetGrid.Text, weaponHeight.Text, targetHeight.Text, mapSize.Text };
                    for (int i = 0; i < names.Length; i++) writer.WriteElementString(names[i], values[i]);
                    writer.WriteEndElement();
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "无法保存本地设置：" + ex.Message + "\n请将软件放到可写文件夹。", "保存设置"); }
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "1. 填写游戏网格坐标，如 D05-5-5。每个大格 300 米，九宫格顺序与数字小键盘一致。按最细小格中心计算。\n\n" +
                "2. 地图左键放目标，右键放炮位；滚轮缩放，中键拖动。标点会吸附到三级九宫格中心，分辨率约 11.1 米。\n\n" +
                "3. 分别填写炮位和目标的地面高度。目标更高时高差为正。软件另计炮口高 1 米。\n\n" +
                "4. 方位 0° 为北、90° 为东。密位采用 6400/圈；标准迫击炮可用角 800–1580 mil。飞行时间为理论估算。\n\n" +
                "5. 导入地图须是裁齐地图范围的正方形图片，并正确设置地图边长。范围圈仅代表平地理论最大射程。\n\n" +
                "此首版不含自动高程、地形遮挡判断、OCR 或游戏读取。双高度为 0 时按同地面高度计算，并非地形验证。\n\n" +
                "算法及游戏参数参考 sh4rkman/SquadCalc（非商业许可）。地图素材归各自权利人。详见随附说明与 third_party。",
                "使用说明 · 游戏 Squad 专用");
        }

        public void SmokeTest(string outputPath)
        {
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
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(outputPath)) encoder.Save(file);
            File.WriteAllText(Path.ChangeExtension(outputPath, ".txt"), "PASS: WPF startup, default firing solution, invalid grid/height clearing and recovery, coincident points, map bounds, low-only fallback, local PNG pipeline, topmost, map zoom/reset, PNG render.\r\n", Encoding.UTF8);
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
        <DockPanel Margin='0,0,0,15'><TextBlock Text='射击设置' FontSize='15' FontWeight='SemiBold'/><TextBlock Text='标准迫击炮' Foreground='#8AA08F' FontSize='11' HorizontalAlignment='Right' Margin='0,4,0,0'/></DockPanel>
        <TextBlock Text='M   炮位坐标' Foreground='#B8EB7E' FontSize='12' Margin='0,0,0,7'/><TextBox x:Name='WeaponGrid' Text='D05-5-5' ToolTip='例如 D05-5-5，字母为列、数字为行'/>
        <TextBlock Text='T    目标坐标' Foreground='#FFA88C' FontSize='12' Margin='0,15,0,7'/><TextBox x:Name='TargetGrid' Text='F06-5-5'/>
        <TextBlock Text='按最末级小格中心计算 · 300m 大格' Foreground='#728A9B' FontSize='10' Margin='0,7,0,14'/>
        <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='12'/><ColumnDefinition/></Grid.ColumnDefinitions>
          <StackPanel><TextBlock Text='炮位地面高 / m' FontSize='11' Foreground='#A0B2BE' Margin='0,0,0,7'/><TextBox x:Name='WeaponHeight' Text='0'/></StackPanel>
          <StackPanel Grid.Column='2'><TextBlock Text='目标地面高 / m' FontSize='11' Foreground='#A0B2BE' Margin='0,0,0,7'/><TextBox x:Name='TargetHeight' Text='0'/></StackPanel>
        </Grid>
        <TextBlock Text='高差需手动填写，另计炮口高 1m。' Foreground='#8196A5' FontSize='10' Margin='0,7,0,18'/>
        <Border Height='1' Background='#2B3C49'/>
        <TextBlock Text='射击诸元' FontSize='15' FontWeight='SemiBold' Margin='0,16,0,12'/>
        <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
          <StackPanel><TextBlock Text='方位角 / AZIMUTH' FontSize='10' Foreground='#879EAE'/><TextBlock x:Name='Bearing' Text='—' FontSize='29' FontWeight='SemiBold' Margin='0,7,0,0'/></StackPanel>
          <StackPanel Grid.Column='1'><TextBlock x:Name='ElevationLabel' Text='高抛密位 / MIL' FontSize='10' Foreground='#879EAE'/><TextBlock x:Name='Elevation' Text='—' Foreground='#B8EB7E' FontSize='33' FontWeight='SemiBold' Margin='0,3,0,0'/></StackPanel>
        </Grid>
        <TextBlock x:Name='Status' TextWrapping='Wrap' FontSize='11' Margin='0,11,0,14' Foreground='#B8EB7E'/>
        <DockPanel Margin='0,0,0,8'><TextBlock Text='水平距离' Foreground='#91A5B4' FontSize='12'/><TextBlock x:Name='Distance' Text='—' HorizontalAlignment='Right'/></DockPanel>
        <DockPanel Margin='0,0,0,8'><TextBlock Text='地面高差' Foreground='#91A5B4' FontSize='12'/><TextBlock x:Name='Difference' Text='—' HorizontalAlignment='Right'/></DockPanel>
        <DockPanel Margin='0,0,0,14'><TextBlock Text='理论飞行时间' Foreground='#91A5B4' FontSize='12'/><TextBlock x:Name='Flight' Text='—' HorizontalAlignment='Right'/></DockPanel>
        <Button x:Name='Copy' Content='复制射击诸元' Background='#B8EB7E' Foreground='#183020' FontWeight='SemiBold'/>
        <TextBlock Text='采用 SquadCalc 游戏参数。未判断地形遮挡。' Foreground='#6F8798' TextWrapping='Wrap' FontSize='10' Margin='0,12,0,0'/>
      </StackPanel>
      </ScrollViewer>
    </Border>
    <Border Grid.Column='2' Background='#101B24' BorderBrush='#283B49' BorderThickness='1' CornerRadius='8'>
      <Grid><Grid.RowDefinitions><RowDefinition Height='56'/><RowDefinition Height='*'/><RowDefinition Height='79'/></Grid.RowDefinitions>
        <DockPanel Margin='16,0' LastChildFill='False'><StackPanel DockPanel.Dock='Left' Orientation='Horizontal' VerticalAlignment='Center'><TextBlock x:Name='MapName' Text='离线坐标网格' FontWeight='SemiBold' Margin='0,0,18,0'/><RadioButton x:Name='PlaceTarget' Content='放目标' IsChecked='True' GroupName='Mode'/><RadioButton x:Name='PlaceMortar' Content='放炮位' GroupName='Mode'/></StackPanel><StackPanel DockPanel.Dock='Right' Orientation='Horizontal' VerticalAlignment='Center'><Button x:Name='ZoomOut' Content='−' Padding='11,5' Margin='0,0,5,0'/><Button x:Name='ZoomIn' Content='+' Padding='11,5' Margin='0,0,5,0'/><Button x:Name='ResetView' Content='全图' Padding='10,5'/></StackPanel></DockPanel>
        <Border Grid.Row='1' x:Name='MapHost' BorderBrush='#283B49' BorderThickness='0,1'/>
        <Grid Grid.Row='2' Margin='16,10'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
          <StackPanel><StackPanel Orientation='Horizontal'><TextBlock Text='地图边长 / m' FontSize='11' Foreground='#91A5B4' VerticalAlignment='Center' Margin='0,0,10,0'/><TextBox x:Name='MapSize' Text='3000' Width='77' Padding='8,4' FontSize='12'/></StackPanel><TextBlock Text='左键目标 · 右键炮位 · 滚轮缩放 · 中键拖动' Foreground='#728A9B' FontSize='10' Margin='0,7,0,0'/></StackPanel>
          <StackPanel Grid.Column='1' Orientation='Horizontal' VerticalAlignment='Top'><Button x:Name='ClearMap' Content='清除图片' Margin='0,0,8,0'/><Button x:Name='ImportMap' Content='导入地图图片'/></StackPanel>
        </Grid>
      </Grid>
    </Border>
  </Grid>
  <DockPanel Grid.Row='3' VerticalAlignment='Bottom'><TextBlock Text='SQUAD 游戏辅助工具  /  本地首版 0.1' Foreground='#607B8E' FontSize='10'/><TextBlock Text='手动高差 · 自定义地图 · 无网络请求' Foreground='#607B8E' FontSize='10' HorizontalAlignment='Right'/></DockPanel>
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
