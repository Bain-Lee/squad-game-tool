using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using SquadTools;

internal static class CrosshairTests
{
    private static int assertions;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr handle, int index);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition) throw new Exception(label);
    }

    private static void Near(double expected, double actual, double tolerance, string label)
    {
        Check(!Double.IsNaN(actual) && Math.Abs(expected - actual) <= tolerance,
            label + ": expected " + expected + ", actual " + actual);
    }

    private static T Field<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, Private).GetValue(target);
    }

    private static T Named<T>(Window window, string name) where T : class
    {
        return window.FindName(name) as T ?? ((FrameworkElement)window.Content).FindName(name) as T;
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
        window.UpdateLayout();
    }

    private static long ExtendedStyle(IntPtr handle)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(handle, -20).ToInt64() : GetWindowLong32(handle, -20);
    }

    private static void Centered(CrosshairOverlay overlay, CrosshairMonitor monitor)
    {
        Pump(overlay);
        NativeRect rect;
        Check(GetWindowRect(new WindowInteropHelper(overlay).Handle, out rect), "overlay exposes its native pixel bounds");
        Near(monitor.Left + monitor.Width / 2.0, (rect.Left + rect.Right) / 2.0, 1,
            "crosshair uses the full monitor horizontal centre");
        Near(monitor.Top + monitor.Height / 2.0, (rect.Top + rect.Bottom) / 2.0, 1,
            "crosshair uses the full monitor vertical centre");
    }

    private static byte[] Pixels(CrosshairOverlay overlay)
    {
        Pump(overlay);
        int width = Math.Max(1, (int)Math.Ceiling(overlay.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(overlay.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(overlay);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        Check(pixels[3] == 0 && pixels[pixels.Length - 1] == 0, "overlay corners retain a fully transparent background");
        Check(Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4 + 3] > 0),
            "selected crosshair has visible pixels");
        return pixels;
    }

    private static XmlNode SettingsNode(string attributes)
    {
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml("<settings><crosshair " + attributes + "/></settings>");
        return document.SelectSingleNode("/settings/crosshair");
    }

    private static XmlNode Serialize(CrosshairFeature feature)
    {
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text))
        {
            writer.WriteStartElement("settings");
            feature.Write(writer);
            writer.WriteEndElement();
        }
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml(text.ToString());
        return document.SelectSingleNode("/settings/crosshair");
    }

    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo previousLanguage = null;
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            previousLanguage = System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage;
            TestOverlay();
            TestSettings(args[0]);
            TestIntegration(args[0], args[1]);
            Console.WriteLine("PASS: " + assertions + " crosshair assertions. Native click-through/topmost/no-activate styles, full-screen centring, monitor fallback, alpha rendering, settings, live controls, themes and lifecycle.");
            GC.KeepAlive(application);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().FullName + " " + ex.Message + "\n" + ex.StackTrace);
            return 1;
        }
        finally
        {
            if (previousLanguage != null) System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage = previousLanguage;
        }
    }

    private static void TestOverlay()
    {
        var monitors = CrosshairOverlay.GetMonitors();
        Check(monitors.Count > 0 && monitors.Any(monitor => monitor.IsPrimary), "display enumeration includes a primary monitor");
        Check(monitors.All(monitor => monitor.Width > 0 && monitor.Height > 0 && !String.IsNullOrEmpty(monitor.DeviceName)),
            "display bounds and stable device names are available");
        Check(monitors.Select(monitor => monitor.DeviceName).Distinct().Count() == monitors.Count, "display device names are unique");
        var overlay = new CrosshairOverlay();
        var settings = new CrosshairSettings();
        try
        {
            overlay.Apply(settings);
            Check(!overlay.IsVisible, "fresh crosshair remains off by default");
            IntPtr foreground = GetForegroundWindow();
            settings.Enabled = true;
            overlay.Apply(settings); Pump(overlay);
            IntPtr handle = new WindowInteropHelper(overlay).Handle;
            Check(handle != IntPtr.Zero && IsWindowVisible(handle), "enabled crosshair owns a visible native window");
            Check(overlay.Owner == null && !overlay.ShowActivated && !overlay.ShowInTaskbar,
                "overlay is an independent background window without a taskbar entry");
            Check(overlay.WindowStyle == WindowStyle.None && overlay.AllowsTransparency, "overlay uses transparent borderless rendering");
            foreach (long flag in new long[] { 0x8, 0x20, 0x80, 0x80000, 0x8000000 })
                Check((ExtendedStyle(handle) & flag) != 0, "native topmost, transparent, tool, layered and no-activate flags survive Show");
            Check(SendMessage(handle, 0x0084, IntPtr.Zero, IntPtr.Zero).ToInt64() == -1,
                "native hit-testing passes through the overlay");
            Check(SendMessage(handle, 0x0021, IntPtr.Zero, IntPtr.Zero).ToInt64() == 3,
                "mouse activation messages keep the existing application active");
            Check(GetForegroundWindow() != handle && (foreground == IntPtr.Zero || GetForegroundWindow() == foreground),
                "showing the crosshair preserves foreground focus");
            Centered(overlay, monitors.First(monitor => monitor.IsPrimary));
            byte[] previous = null;
            foreach (CrosshairShape shape in Enum.GetValues(typeof(CrosshairShape)))
            {
                settings.Shape = shape; settings.Size = 32; settings.Thickness = 2; settings.Opacity = 0.65;
                overlay.Apply(settings);
                var pixels = Pixels(overlay);
                if (previous != null) Check(!pixels.SequenceEqual(previous), "different crosshair shapes produce different drawings");
                previous = pixels;
                Centered(overlay, monitors.First(monitor => monitor.IsPrimary));
            }
            foreach (var monitor in monitors)
            {
                settings.MonitorDevice = monitor.DeviceName;
                overlay.Apply(settings); Centered(overlay, monitor);
            }
            settings.MonitorDevice = "missing-display-for-regression";
            overlay.Apply(settings); Centered(overlay, monitors.First(monitor => monitor.IsPrimary));
            SendMessage(handle, 0x007E, IntPtr.Zero, IntPtr.Zero);
            Centered(overlay, monitors.First(monitor => monitor.IsPrimary));
            settings.Size = Double.NaN; settings.Thickness = Double.PositiveInfinity; settings.Opacity = Double.NaN;
            settings.ColorHex = "invalid-colour"; settings.Shape = (CrosshairShape)999;
            overlay.Apply(settings); Pump(overlay);
            Check(Double.IsNaN(overlay.ActualWidth) == false && overlay.ActualWidth > 0 && overlay.ActualWidth <= 256,
                "malformed style values keep finite bounded drawing dimensions");
            Check(!Double.IsNaN(overlay.Opacity) && overlay.Opacity >= 0.2 && overlay.Opacity <= 1,
                "malformed opacity keeps a visible valid range");
            settings.ColorHex = "# FF00F";
            overlay.Apply(settings);
            Check(settings.ColorHex == "#7CFF62", "hex colour validation rejects embedded whitespace before rendering");
            Pixels(overlay);
            settings.Enabled = false; overlay.Apply(settings);
            Check(!overlay.IsVisible && !IsWindowVisible(handle), "disabling hides the native overlay immediately");
            settings.Enabled = true; overlay.Apply(settings);
            Check(overlay.IsVisible && new WindowInteropHelper(overlay).Handle == handle, "re-enabling reuses the native window");
        }
        finally { overlay.Close(); }
    }

    private static void TestSettings(string maps)
    {
        var window = new MainWindow(true, maps);
        try
        {
            var feature = Field<CrosshairFeature>(window, "crosshairFeature");
            feature.Load(null);
            Check(!feature.Settings.Enabled && feature.Settings.Shape == CrosshairShape.Cross,
                "legacy settings without a crosshair preserve default disabled state");
            feature.Load(SettingsNode("enabled='true' shape='Circle' color='#ABCDEF' size='32' thickness='3' opacity='0.6' monitor='saved-display'"));
            Check(feature.Settings.Enabled && feature.Settings.Shape == CrosshairShape.Circle && feature.Settings.ColorHex == "#ABCDEF",
                "crosshair settings load enabled state, shape and custom colour");
            Near(32, feature.Settings.Size, 0, "saved size"); Near(3, feature.Settings.Thickness, 0, "saved thickness");
            Near(0.6, feature.Settings.Opacity, 1e-12, "saved opacity");
            Check(feature.Settings.MonitorDevice == "saved-display", "saved display device survives deserialization");
            XmlNode roundTrip = Serialize(feature);
            Check(roundTrip != null, "crosshair serializer writes a child under existing settings");
            feature.Load(null); feature.Load(roundTrip);
            Check(feature.Settings.Enabled && feature.Settings.Shape == CrosshairShape.Circle && feature.Settings.ColorHex == "#ABCDEF",
                "settings round trip preserves state, shape and custom colour");
            Near(32, feature.Settings.Size, 0, "round-trip size"); Near(3, feature.Settings.Thickness, 0, "round-trip thickness");
            Near(0.6, feature.Settings.Opacity, 1e-12, "round-trip opacity");
            feature.Load(SettingsNode("enabled='bad' shape='999' color='bad' size='NaN' thickness='Infinity' opacity='-Infinity'"));
            Check(!feature.Settings.Enabled && feature.Settings.Shape == CrosshairShape.Cross, "malformed bool and enum return to safe defaults");
            Check(feature.Settings.ColorHex == new CrosshairSettings().ColorHex, "malformed colour returns to the visible default");
            Near(24, feature.Settings.Size, 0, "nonfinite size returns to default");
            Near(2, feature.Settings.Thickness, 0, "nonfinite thickness returns to default");
            Near(0.9, feature.Settings.Opacity, 0, "nonfinite opacity returns to default");
            feature.Load(SettingsNode("size='1000000' thickness='-1000000' opacity='0'"));
            Near(64, feature.Settings.Size, 0, "loaded size clamps to upper limit");
            Near(1, feature.Settings.Thickness, 0, "loaded thickness clamps to lower limit");
            Near(0.2, feature.Settings.Opacity, 0, "loaded opacity clamps to lower limit");
            feature.Load(SettingsNode("size='1' thickness='50' opacity='40'"));
            Near(8, feature.Settings.Size, 0, "loaded size clamps to lower limit");
            Near(6, feature.Settings.Thickness, 0, "loaded thickness clamps to upper limit");
            Near(1, feature.Settings.Opacity, 0, "loaded opacity clamps to upper limit");
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                feature.Load(SettingsNode("opacity='0.45'"));
                Near(0.45, feature.Settings.Opacity, 1e-12, "settings use invariant decimal punctuation");
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = previousCulture; }
        }
        finally { window.Close(); }
    }

    private static void TestIntegration(string maps, string output)
    {
        string data = Path.Combine(output, "crosshair-test-settings");
        var window = new MainWindow(true, maps, data);
        CrosshairOverlay overlay = null;
        Window settingsWindow = null;
        IntPtr overlayHandle = IntPtr.Zero;
        try
        {
            window.Show(); Pump(window);
            var feature = Field<CrosshairFeature>(window, "crosshairFeature");
            Check(!feature.HotkeyRegistered, "test-mode feature leaves the global shortcut unregistered");
            var button = Named<Button>(window, "CrosshairButton");
            Check(button != null && button.IsVisible, "main toolbar exposes the auxiliary crosshair entry");
            window.Width = 1060; window.Height = 740; Pump(window);
            var root = (FrameworkElement)window.Content;
            var actions = Named<FrameworkElement>(window, "HeaderActions");
            Check(button.TranslatePoint(new Point(button.ActualWidth, 0), root).X <= actions.TranslatePoint(new Point(), root).X,
                "crosshair entry stays clear of the four right-hand icons at minimum window width");
            window.Width = 1280; window.Height = 850; Pump(window);
            feature.Load(SettingsNode("color='# FF00F'"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
            settingsWindow = Field<Window>(feature, "settingsWindow");
            Check(settingsWindow != null && settingsWindow.IsVisible && settingsWindow.Owner == window,
                "normal entry opens a settings window attached to the current tool");
            foreach (string name in new[] { "CrosshairEnabled", "CrosshairShape", "CrosshairColor", "CrosshairSize", "CrosshairThickness", "CrosshairOpacity", "CrosshairMonitor" })
                Check(Named<FrameworkElement>(settingsWindow, name) != null, "settings exposes the " + name + " control");
            Check(feature.Settings.ColorHex == "#7CFF62", "malformed saved colour can open the actual settings window");
            var displays = Named<ComboBox>(settingsWindow, "CrosshairMonitor");
            displays.ItemsSource = new[] { new CrosshairMonitor { DeviceName = "stale-display", DisplayName = "Stale display" } };
            SendMessage(new WindowInteropHelper(settingsWindow).Handle, 0x007E, IntPtr.Zero, IntPtr.Zero);
            Pump(settingsWindow);
            Check(displays.Items.Cast<CrosshairMonitor>().All(item => item.DeviceName != "stale-display"),
                "an open settings window refreshes display choices on display change messages");
            var enabled = Named<CheckBox>(settingsWindow, "CrosshairEnabled");
            enabled.IsChecked = true; Pump(settingsWindow);
            overlay = Field<CrosshairOverlay>(feature, "overlay");
            Check(feature.Settings.Enabled && overlay != null && overlay.IsVisible, "settings checkbox enables a visible crosshair");
            overlayHandle = new WindowInteropHelper(overlay).Handle;
            Named<ComboBox>(settingsWindow, "CrosshairShape").SelectedIndex = (int)CrosshairShape.Dot;
            Named<Slider>(settingsWindow, "CrosshairSize").Value = 40;
            Named<Slider>(settingsWindow, "CrosshairThickness").Value = 4;
            Named<Slider>(settingsWindow, "CrosshairOpacity").Value = 60;
            var colours = Named<ComboBox>(settingsWindow, "CrosshairColor");
            colours.SelectedIndex = Math.Min(1, colours.Items.Count - 1);
            Pump(settingsWindow);
            Check(feature.Settings.Shape == CrosshairShape.Dot, "shape selection applies live");
            Near(40, feature.Settings.Size, 0, "size slider applies live"); Near(4, feature.Settings.Thickness, 0, "thickness slider applies live");
            Near(0.6, feature.Settings.Opacity, 0.011, "opacity slider applies live");
            Near(0.6, Named<FrameworkElement>(settingsWindow, "CrosshairPreview").Opacity, 0.011,
                "preview includes the selected opacity");
            Check(feature.Settings.ColorHex == (string)((ComboBoxItem)colours.SelectedItem).Tag, "colour selection applies live");
            byte[] drawingBeforeTheme = Pixels(overlay);
            var dark = ((SolidColorBrush)settingsWindow.Background).Color;
            Named<ToggleButton>(window, "ThemeToggle").IsChecked = true; Pump(settingsWindow);
            var light = ((SolidColorBrush)settingsWindow.Background).Color;
            Check(light != dark && light.R >= light.G && light.G > light.B, "an open settings window follows the warm light theme");
            Check(Pixels(overlay).SequenceEqual(drawingBeforeTheme), "theme changes preserve the chosen crosshair colour and opacity");
            Named<ToggleButton>(window, "ThemeToggle").IsChecked = false; Pump(settingsWindow);
            Check(((SolidColorBrush)settingsWindow.Background).Color == dark, "open settings returns to the dark theme");
            Named<Slider>(window, "OpacitySlider").Value = 30; Pump(window);
            Near(0.6, overlay.Opacity, 0.011, "main window transparency leaves crosshair opacity independent");
            Named<Slider>(window, "OpacitySlider").Value = 100; Pump(window);
            window.WindowState = WindowState.Minimized; Pump(window);
            Check(overlay.IsVisible && IsWindowVisible(overlayHandle), "minimizing the main tool keeps the crosshair visible");
            window.WindowState = WindowState.Normal; Pump(window);
            typeof(MainWindow).GetMethod("SaveSettings", Private).Invoke(window, null);
            var document = new XmlDocument { XmlResolver = null };
            document.Load(Path.Combine(data, "settings.xml"));
            Check(document.SelectSingleNode("/settings/crosshair") != null, "normal main settings writer persists the crosshair feature");
            feature.Load(document.SelectSingleNode("/settings/crosshair"));
            Check(feature.Settings.Enabled && feature.Settings.Shape == CrosshairShape.Dot, "normal settings content restores the configured crosshair");
            feature.ShowSettings();
            Check(Field<Window>(feature, "settingsWindow") == settingsWindow, "reopening the entry reuses its settings window");
            enabled.IsChecked = false; Pump(window);
            Check(!feature.Settings.Enabled && !overlay.IsVisible, "settings checkbox disables overlay without closing the tool");
            enabled.IsChecked = true; Pump(window);
        }
        finally { window.Close(); }
        Check(overlay != null && !overlay.IsVisible && !IsWindow(overlayHandle), "closing main window destroys the overlay HWND");
        Check(settingsWindow != null && !settingsWindow.IsVisible, "closing main window also closes crosshair settings");
    }
}
