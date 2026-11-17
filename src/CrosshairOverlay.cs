using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SquadTools
{
    internal enum CrosshairShape { Cross, Dot, Circle }

    internal sealed class CrosshairSettings
    {
        public bool Enabled;
        public CrosshairShape Shape = CrosshairShape.Cross;
        public string ColorHex = "#7CFF62";
        public double Size = 24;
        public double Thickness = 2;
        public double Opacity = 0.9;
        public string MonitorDevice = "";

        public void Normalize()
        {
            if (!Enum.IsDefined(typeof(CrosshairShape), Shape)) Shape = CrosshairShape.Cross;
            byte red, green, blue;
            if (ColorHex == null || ColorHex.Length != 7 || ColorHex[0] != '#'
                || !Byte.TryParse(ColorHex.Substring(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out red)
                || !Byte.TryParse(ColorHex.Substring(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out green)
                || !Byte.TryParse(ColorHex.Substring(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out blue))
                ColorHex = "#7CFF62";
            ColorHex = ColorHex.ToUpperInvariant();
            Size = Limit(Size, 8, 64, 24);
            Thickness = Limit(Thickness, 1, 6, 2);
            Opacity = Limit(Opacity, 0.2, 1, 0.9);
            MonitorDevice = MonitorDevice ?? "";
        }

        private static double Limit(double value, double minimum, double maximum, double fallback)
        {
            return Double.IsNaN(value) || Double.IsInfinity(value) ? fallback : Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal sealed class CrosshairMonitor
    {
        public string DeviceName, DisplayName;
        public int Left, Top, Width, Height;
        public bool IsPrimary;

        public override string ToString() { return DisplayName; }
    }

    internal sealed class CrosshairVisual : FrameworkElement
    {
        private CrosshairShape shape;
        private double size = 24, thickness = 2;
        private Brush color = new SolidColorBrush(Color.FromRgb(124, 255, 98));

        public void Apply(CrosshairSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            settings.Normalize();
            shape = settings.Shape; size = settings.Size; thickness = settings.Thickness;
            color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.ColorHex));
            color.Freeze();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext context)
        {
            base.OnRender(context);
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            double radius = size / 2;
            var outline = new Pen(Brushes.Black, thickness + 2);
            var stroke = new Pen(color, thickness);
            if (shape == CrosshairShape.Dot)
            {
                context.DrawEllipse(Brushes.Black, null, center, radius + 1, radius + 1);
                context.DrawEllipse(color, null, center, radius, radius);
            }
            else if (shape == CrosshairShape.Circle)
            {
                context.DrawEllipse(null, outline, center, radius, radius);
                context.DrawEllipse(null, stroke, center, radius, radius);
            }
            else
            {
                var left = new Point(center.X - radius, center.Y); var right = new Point(center.X + radius, center.Y);
                var top = new Point(center.X, center.Y - radius); var bottom = new Point(center.X, center.Y + radius);
                context.DrawLine(outline, left, right); context.DrawLine(outline, top, bottom);
                context.DrawLine(stroke, left, right); context.DrawLine(stroke, top, bottom);
            }
        }
    }

    // Window styles and full-monitor centering adapted from Crux (MIT).
    // Copyright (c) 2026 nowaaak. See third_party/Crux.LICENSE.
    internal sealed class CrosshairOverlay : Window
    {
        private const int ExtendedStyleIndex = -20;
        private const long OverlayStyles = 0x00080000L | 0x00000020L | 0x08000000L | 0x00000080L;
        private static readonly IntPtr TopmostHandle = new IntPtr(-1);
        private readonly CrosshairVisual drawing = new CrosshairVisual();
        private HwndSource source;
        private IntPtr handle;
        private string monitorDevice = "";
        private bool enabled, closed, positionQueued;

        public event Action<string> Failed;

        public CrosshairOverlay()
        {
            Title = "Squad Tools · 辅助准星";
            Width = 80; Height = 80;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            Focusable = false;
            IsHitTestVisible = false;
            Content = drawing;
            SourceInitialized += delegate {
                handle = new WindowInteropHelper(this).Handle;
                source = HwndSource.FromHwnd(handle);
                source.AddHook(WindowMessage);
                SetOverlayStyles();
            };
            SizeChanged += delegate { QueuePosition(); };
        }

        public void Apply(CrosshairSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (closed) throw new ObjectDisposedException("CrosshairOverlay");
            settings.Normalize();
            monitorDevice = settings.MonitorDevice;
            drawing.Apply(settings);
            Opacity = settings.Opacity;
            enabled = settings.Enabled;
            if (!enabled) { Hide(); return; }
            if (!IsVisible) Show();
            PositionOnMonitor();
        }

        public static List<CrosshairMonitor> GetMonitors()
        {
            var monitors = new List<CrosshairMonitor>();
            MonitorCallback callback = delegate(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data) {
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var bounds = info.Bounds;
                    monitors.Add(new CrosshairMonitor {
                        DeviceName = info.Device,
                        DisplayName = String.Format(CultureInfo.InvariantCulture, "{0}{1} · {2} × {3}",
                            (info.Flags & 1) != 0 ? "主显示器 · " : "显示器 · ", info.Device,
                            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
                        Left = bounds.Left, Top = bounds.Top,
                        Width = bounds.Right - bounds.Left, Height = bounds.Bottom - bounds.Top,
                        IsPrimary = (info.Flags & 1) != 0
                    });
                }
                return true;
            };
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取显示器列表。");
            monitors.Sort(delegate(CrosshairMonitor first, CrosshairMonitor second) {
                int primary = second.IsPrimary.CompareTo(first.IsPrimary);
                if (primary != 0) return primary;
                int left = first.Left.CompareTo(second.Left);
                return left != 0 ? left : first.Top.CompareTo(second.Top);
            });
            return monitors;
        }

        private void SetOverlayStyles()
        {
            NativeSetLastError(0);
            IntPtr style = IntPtr.Size == 8 ? GetWindowLongPtr(handle, ExtendedStyleIndex) : new IntPtr(GetWindowLong(handle, ExtendedStyleIndex));
            int error = Marshal.GetLastWin32Error();
            if (style == IntPtr.Zero && error != 0) throw new Win32Exception(error);
            NativeSetLastError(0);
            long extended = style.ToInt64() | OverlayStyles;
            IntPtr previous = IntPtr.Size == 8 ? SetWindowLongPtr(handle, ExtendedStyleIndex, new IntPtr(extended))
                : new IntPtr(SetWindowLong(handle, ExtendedStyleIndex, unchecked((int)extended)));
            error = Marshal.GetLastWin32Error();
            if (previous == IntPtr.Zero && error != 0) throw new Win32Exception(error);
        }

        private void PositionOnMonitor()
        {
            if (closed || !enabled || handle == IntPtr.Zero) return;
            List<CrosshairMonitor> monitors = GetMonitors();
            CrosshairMonitor selected = monitors.Find(delegate(CrosshairMonitor monitor) {
                return String.Equals(monitor.DeviceName, monitorDevice, StringComparison.OrdinalIgnoreCase);
            }) ?? monitors.Find(delegate(CrosshairMonitor monitor) { return monitor.IsPrimary; });
            if (selected == null && monitors.Count > 0) selected = monitors[0];
            if (selected == null) { Hide(); return; }
            if (!IsVisible) Show();
            NativeRect bounds;
            if (!GetWindowRect(handle, out bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Native bounds and window pixels share the same coordinate space,
            // including screens left of the primary display and mixed DPI.
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            int left = (int)Math.Round(selected.Left + (selected.Width - width) / 2.0);
            int top = (int)Math.Round(selected.Top + (selected.Height - height) / 2.0);
            if (!SetWindowPos(handle, TopmostHandle, left, top, 0, 0, 0x0001 | 0x0010 | 0x0200))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位屏幕准星。");
        }

        private void QueuePosition()
        {
            if (closed || positionQueued) return;
            positionQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                positionQueued = false;
                try { PositionOnMonitor(); }
                catch (Win32Exception ex)
                {
                    enabled = false;
                    Hide();
                    if (Failed != null) Failed(ex.Message);
                }
            }));
        }

        private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0021) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE / MA_NOACTIVATE
            if (message == 0x0084) { handled = true; return new IntPtr(-1); } // WM_NCHITTEST / HTTRANSPARENT
            if (message == 0x007C && wParam.ToInt64() == ExtendedStyleIndex && lParam != IntPtr.Zero)
            {
                var change = (StyleChange)Marshal.PtrToStructure(lParam, typeof(StyleChange));
                change.NewStyle |= (uint)OverlayStyles;
                Marshal.StructureToPtr(change, lParam, false);
            }
            if (message == 0x007E || message == 0x02E0) QueuePosition(); // Display bounds or DPI changed.
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            closed = true;
            enabled = false;
            if (source != null && !source.IsDisposed) source.RemoveHook(WindowMessage);
            source = null; handle = IntPtr.Zero;
            base.OnClosed(e);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct StyleChange { public uint OldStyle, NewStyle; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Bounds, WorkArea;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
        private static extern void NativeSetLastError(uint error);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr window, int index, int value);
    }
}
