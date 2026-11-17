using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;

namespace SquadTools
{
    internal sealed class CrosshairFeature : IDisposable
    {
        private const int HotkeyId = 0x5351;
        private readonly MainWindow owner;
        private readonly bool testMode;
        private CrosshairOverlay overlay;
        private Window settingsWindow;
        private HwndSource hotkeySource;
        private CheckBox enabled;
        private ComboBox shape, color, monitor;
        private Slider size, thickness, opacity;
        private TextBlock sizeValue, thicknessValue, opacityValue, hotkeyStatus;
        private CrosshairVisual preview;
        private bool updating, disposed;
        private string lastError;

        public CrosshairSettings Settings { get; private set; }
        internal bool HotkeyRegistered { get; private set; }

        public CrosshairFeature(MainWindow owner, bool testMode)
        {
            this.owner = owner;
            this.testMode = testMode;
            Settings = new CrosshairSettings();
            owner.SourceInitialized += InitializeHotkey;
            owner.Loaded += OwnerLoaded;
            owner.Closed += OwnerClosed;
        }

        public void Load(XmlNode node)
        {
            Settings = new CrosshairSettings();
            if (node == null) return;
            bool active;
            if (bool.TryParse(Attribute(node, "enabled"), out active)) Settings.Enabled = active;
            CrosshairShape savedShape;
            if (Enum.TryParse(Attribute(node, "shape"), out savedShape)) Settings.Shape = savedShape;
            Settings.ColorHex = Attribute(node, "color") ?? Settings.ColorHex;
            Settings.Size = ReadNumber(node, "size", Settings.Size);
            Settings.Thickness = ReadNumber(node, "thickness", Settings.Thickness);
            Settings.Opacity = ReadNumber(node, "opacity", Settings.Opacity);
            Settings.MonitorDevice = Attribute(node, "monitor") ?? "";
            Settings.Normalize();
        }

        public void Write(XmlWriter writer)
        {
            Settings.Normalize();
            writer.WriteStartElement("crosshair");
            writer.WriteAttributeString("enabled", Settings.Enabled.ToString());
            writer.WriteAttributeString("shape", Settings.Shape.ToString());
            writer.WriteAttributeString("color", Settings.ColorHex);
            writer.WriteAttributeString("size", Settings.Size.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("thickness", Settings.Thickness.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("opacity", Settings.Opacity.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("monitor", Settings.MonitorDevice);
            writer.WriteEndElement();
        }

        private static string Attribute(XmlNode node, string name)
        {
            return node.Attributes == null || node.Attributes[name] == null ? null : node.Attributes[name].Value;
        }

        private static double ReadNumber(XmlNode node, string name, double fallback)
        {
            double value;
            return double.TryParse(Attribute(node, name), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private void OwnerLoaded(object sender, RoutedEventArgs e) { Apply(); }
        private void OwnerClosed(object sender, EventArgs e) { Dispose(); }

        private void InitializeHotkey(object sender, EventArgs e)
        {
            if (testMode || disposed) return;
            hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(owner).Handle);
            if (hotkeySource == null) return;
            // Ctrl + Alt + F8, with repeat suppression while the keys are held.
            HotkeyRegistered = RegisterHotKey(hotkeySource.Handle, HotkeyId, 0x4003, 0x77);
            if (HotkeyRegistered) hotkeySource.AddHook(HandleHotkey);
        }

        private IntPtr HandleHotkey(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312 && wParam.ToInt32() == HotkeyId && !disposed)
            {
                Settings.Enabled = !Settings.Enabled;
                Apply();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Apply()
        {
            if (disposed) return;
            Settings.Normalize();
            lastError = null;
            try
            {
                if (overlay == null && Settings.Enabled)
                {
                    overlay = new CrosshairOverlay();
                    overlay.Failed += OverlayFailed;
                }
                if (overlay != null) overlay.Apply(Settings);
            }
            catch (Win32Exception ex)
            {
                Settings.Enabled = false;
                if (overlay != null) overlay.Hide();
                lastError = "准星显示失败，" + ex.Message;
            }
            var button = ((FrameworkElement)owner.Content).FindName("CrosshairButton") as Button;
            if (button != null)
            {
                button.ToolTip = lastError ?? (Settings.Enabled ? "辅助准星已开启 · Ctrl + Alt + F8 开关" : "辅助准星设置 · Ctrl + Alt + F8 开关");
                button.Background = Settings.Enabled ? UiTheme.Brush("#304A64") : Brushes.Transparent;
            }
            RefreshSettingsUi();
        }

        private void OverlayFailed(string message)
        {
            Settings.Enabled = false;
            lastError = message;
            var button = ((FrameworkElement)owner.Content).FindName("CrosshairButton") as Button;
            if (button != null)
            {
                button.Background = Brushes.Transparent;
                button.ToolTip = message;
            }
            RefreshSettingsUi();
        }

        public void ShowSettings()
        {
            if (disposed) return;
            if (settingsWindow != null)
            {
                settingsWindow.WindowState = WindowState.Normal;
                settingsWindow.Activate();
                return;
            }
            var dialog = new Window {
                Title = "辅助准星", Owner = owner, Width = 470, Height = 660, MinWidth = 430, MinHeight = 590,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = UiTheme.Brush("#101720"), Foreground = UiTheme.Brush("#E9EDF2"), FontFamily = owner.FontFamily
            };
            UiTheme.Attach(dialog);
            var root = (FrameworkElement)XamlReader.Parse(UiTheme.Xaml(Layout));
            root.Resources.MergedDictionaries.Add(((FrameworkElement)owner.Content).Resources);
            dialog.Content = root;
            settingsWindow = dialog;
            enabled = (CheckBox)root.FindName("CrosshairEnabled");
            shape = (ComboBox)root.FindName("CrosshairShape");
            color = (ComboBox)root.FindName("CrosshairColor");
            monitor = (ComboBox)root.FindName("CrosshairMonitor");
            size = (Slider)root.FindName("CrosshairSize");
            thickness = (Slider)root.FindName("CrosshairThickness");
            opacity = (Slider)root.FindName("CrosshairOpacity");
            sizeValue = (TextBlock)root.FindName("SizeValue");
            thicknessValue = (TextBlock)root.FindName("ThicknessValue");
            opacityValue = (TextBlock)root.FindName("OpacityValue");
            hotkeyStatus = (TextBlock)root.FindName("HotkeyStatus");
            preview = new CrosshairVisual { Name = "CrosshairPreview", Width = 88, Height = 88 };
            ((Border)root.FindName("PreviewHost")).Child = preview;
            root.RegisterName(preview.Name, preview);
            shape.ItemsSource = new[] { "十字", "圆点", "圆环" };
            foreach (var entry in new[] {
                new[] { "#7CFF62", "荧光绿" }, new[] { "#4DE6FF", "青色" }, new[] { "#FFE65C", "黄色" },
                new[] { "#FF6262", "红色" }, new[] { "#FFFFFF", "白色" }, new[] { "#FF73D3", "粉色" }
            }) AddColor(entry[0], entry[1]);
            if (!color.Items.Cast<ComboBoxItem>().Any(item => (string)item.Tag == Settings.ColorHex)) AddColor(Settings.ColorHex, "自定义");
            RefreshMonitors();
            enabled.Checked += ChangeSettings;
            enabled.Unchecked += ChangeSettings;
            shape.SelectionChanged += ChangeSettings;
            color.SelectionChanged += ChangeSettings;
            monitor.SelectionChanged += ChangeSettings;
            monitor.DropDownOpened += delegate { RefreshMonitors(); };
            size.ValueChanged += ChangeSettings;
            thickness.ValueChanged += ChangeSettings;
            opacity.ValueChanged += ChangeSettings;
            HwndSource dialogSource = null;
            HwndSourceHook monitorHook = delegate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
                if (message == 0x007E || message == 0x02E0)
                    dialog.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                        if (!disposed && settingsWindow == dialog) RefreshMonitors();
                    }));
                return IntPtr.Zero;
            };
            dialog.SourceInitialized += delegate {
                dialogSource = HwndSource.FromHwnd(new WindowInteropHelper(dialog).Handle);
                if (dialogSource != null) dialogSource.AddHook(monitorHook);
            };
            dialog.Closed += delegate {
                if (dialogSource != null && !dialogSource.IsDisposed) dialogSource.RemoveHook(monitorHook);
                settingsWindow = null; preview = null;
            };
            dialog.Show();
        }

        private void RefreshMonitors()
        {
            updating = true;
            try { monitor.ItemsSource = CrosshairOverlay.GetMonitors(); }
            catch (Win32Exception ex) { lastError = "无法更新显示器列表，" + ex.Message; }
            finally { updating = false; }
            RefreshSettingsUi();
        }

        private void AddColor(string hex, string label)
        {
            var swatch = new StackPanel { Orientation = Orientation.Horizontal };
            swatch.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 9, 0), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) });
            swatch.Children.Add(new TextBlock { Text = label });
            color.Items.Add(new ComboBoxItem { Content = swatch, Tag = hex });
        }

        private void ChangeSettings(object sender, RoutedEventArgs e)
        {
            if (updating || disposed) return;
            Settings.Enabled = enabled.IsChecked == true;
            Settings.Shape = (CrosshairShape)shape.SelectedIndex;
            var selectedColor = color.SelectedItem as ComboBoxItem;
            if (selectedColor != null) Settings.ColorHex = (string)selectedColor.Tag;
            var selectedMonitor = monitor.SelectedItem as CrosshairMonitor;
            if (sender == monitor && selectedMonitor != null) Settings.MonitorDevice = selectedMonitor.DeviceName;
            Settings.Size = size.Value;
            Settings.Thickness = thickness.Value;
            Settings.Opacity = opacity.Value / 100;
            Apply();
        }

        private void RefreshSettingsUi()
        {
            if (settingsWindow == null) return;
            updating = true;
            try
            {
                enabled.IsChecked = Settings.Enabled;
                shape.SelectedIndex = (int)Settings.Shape;
                color.SelectedItem = color.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == Settings.ColorHex);
                var monitors = monitor.Items.Cast<CrosshairMonitor>().ToList();
                monitor.SelectedItem = monitors.FirstOrDefault(item => item.DeviceName == Settings.MonitorDevice)
                    ?? monitors.FirstOrDefault(item => item.IsPrimary) ?? monitors.FirstOrDefault();
                size.Value = Settings.Size;
                thickness.Value = Settings.Thickness;
                opacity.Value = Settings.Opacity * 100;
                sizeValue.Text = Settings.Size.ToString("0", CultureInfo.InvariantCulture) + " px";
                thicknessValue.Text = Settings.Thickness.ToString("0", CultureInfo.InvariantCulture) + " px";
                opacityValue.Text = (Settings.Opacity * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
                hotkeyStatus.Text = lastError ?? (!testMode && owner.IsLoaded && !HotkeyRegistered
                    ? "快捷键已被其他程序占用，可使用上方开关。" : "Ctrl + Alt + F8  显示 / 隐藏准星");
                preview.Apply(Settings);
                preview.Opacity = Settings.Opacity;
            }
            finally { updating = false; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.SourceInitialized -= InitializeHotkey;
            owner.Loaded -= OwnerLoaded;
            owner.Closed -= OwnerClosed;
            if (hotkeySource != null && !hotkeySource.IsDisposed && HotkeyRegistered)
            {
                UnregisterHotKey(hotkeySource.Handle, HotkeyId);
                hotkeySource.RemoveHook(HandleHotkey);
            }
            HotkeyRegistered = false;
            if (settingsWindow != null) settingsWindow.Close();
            if (overlay != null) overlay.Close();
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        private const string Layout = @"
<ScrollViewer xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'>
 <StackPanel Margin='24'>
  <CheckBox x:Name='CrosshairEnabled' Content='显示屏幕准星' FontSize='16' FontWeight='SemiBold' Foreground='#E9EDF2'/>
  <TextBlock x:Name='HotkeyStatus' Foreground='#96A5B6' FontSize='11' Margin='0,8,0,0' TextWrapping='Wrap'/>
  <Border Background='#141F2B' BorderBrush='#2A3441' BorderThickness='1' CornerRadius='14' Padding='16' Margin='0,18,0,18'>
   <Grid Height='96'><Grid.ColumnDefinitions><ColumnDefinition Width='110'/><ColumnDefinition/></Grid.ColumnDefinitions>
    <Border x:Name='PreviewHost' Width='88' Height='88' HorizontalAlignment='Left'/>
    <StackPanel Grid.Column='1' VerticalAlignment='Center'><TextBlock Text='实时预览' FontSize='13' FontWeight='SemiBold'/><TextBlock Text='固定在所选屏幕正中心' Foreground='#96A5B6' FontSize='11' Margin='0,7,0,0'/></StackPanel>
   </Grid>
  </Border>
  <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='16'/><ColumnDefinition/></Grid.ColumnDefinitions>
   <StackPanel><TextBlock Text='样式' Foreground='#96A5B6' FontSize='11' Margin='0,0,0,8'/><ComboBox x:Name='CrosshairShape'/></StackPanel>
   <StackPanel Grid.Column='2'><TextBlock Text='颜色' Foreground='#96A5B6' FontSize='11' Margin='0,0,0,8'/><ComboBox x:Name='CrosshairColor'/></StackPanel>
  </Grid>
  <Grid Margin='0,22,0,2'><TextBlock Text='大小' FontSize='12'/><TextBlock x:Name='SizeValue' HorizontalAlignment='Right' Foreground='#ADCDF9' FontSize='12'/></Grid>
  <Slider x:Name='CrosshairSize' Minimum='8' Maximum='64' TickFrequency='1' IsSnapToTickEnabled='True' IsMoveToPointEnabled='True' Height='30'/>
  <Grid Margin='0,12,0,2'><TextBlock Text='线宽' FontSize='12'/><TextBlock x:Name='ThicknessValue' HorizontalAlignment='Right' Foreground='#ADCDF9' FontSize='12'/></Grid>
  <Slider x:Name='CrosshairThickness' Minimum='1' Maximum='6' TickFrequency='1' IsSnapToTickEnabled='True' IsMoveToPointEnabled='True' Height='30'/>
  <Grid Margin='0,12,0,2'><TextBlock Text='不透明度' FontSize='12'/><TextBlock x:Name='OpacityValue' HorizontalAlignment='Right' Foreground='#ADCDF9' FontSize='12'/></Grid>
  <Slider x:Name='CrosshairOpacity' Minimum='20' Maximum='100' TickFrequency='1' IsSnapToTickEnabled='True' IsMoveToPointEnabled='True' Height='30'/>
  <TextBlock Text='显示器' Foreground='#96A5B6' FontSize='11' Margin='0,16,0,8'/><ComboBox x:Name='CrosshairMonitor'/>
  <TextBlock Text='窗口化 / 无边框全屏下使用。最小化工具后准星继续显示。' Foreground='#8192A5' FontSize='11' TextWrapping='Wrap' Margin='0,16,0,0'/>
 </StackPanel>
</ScrollViewer>";
    }
}
