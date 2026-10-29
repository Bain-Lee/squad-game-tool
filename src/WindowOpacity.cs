using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SquadTools
{
    // Use native whole-window alpha so the game remains visible behind a normal
    // WPF window, including its standard system title bar and resize frame.
    internal static class WindowOpacity
    {
        private const int ExtendedStyleIndex = -20;
        private const long LayeredStyle = 0x00080000;
        private const uint AlphaFlag = 0x00000002;
        private const int StyleChangingMessage = 0x007C;
        private static readonly DependencyProperty HookInstalledProperty = DependencyProperty.RegisterAttached("HookInstalled", typeof(bool), typeof(WindowOpacity), new PropertyMetadata(false));

        [StructLayout(LayoutKind.Sequential)]
        private struct StyleChange
        {
            public uint OldStyle, NewStyle;
        }

        public static void Apply(Window window, double opacity)
        {
            if (window == null) throw new ArgumentNullException("window");
            if (double.IsNaN(opacity) || double.IsInfinity(opacity) || opacity < 0 || opacity > 1)
                throw new ArgumentOutOfRangeException("opacity", "Window opacity must be a finite value between zero and one.");

            // Handle returns zero before SourceInitialized and does not create
            // the native window. Restore the preference when that event fires.
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            NativeSetLastError(0);
            IntPtr extendedStyle = GetExtendedStyle(handle);
            int error = Marshal.GetLastWin32Error();
            if (extendedStyle == IntPtr.Zero && error != 0) throw new Win32Exception(error, "Unable to read the window style.");
            // A window that has always been opaque needs no layered rendering.
            if (opacity == 1 && (extendedStyle.ToInt64() & LayeredStyle) == 0) return;
            PreserveLayeredStyle(window, handle);

            if ((extendedStyle.ToInt64() & LayeredStyle) == 0) {
                NativeSetLastError(0);
                IntPtr previousStyle = SetExtendedStyle(handle, new IntPtr(extendedStyle.ToInt64() | LayeredStyle));
                error = Marshal.GetLastWin32Error();
                if (previousStyle == IntPtr.Zero && error != 0) throw new Win32Exception(error, "Unable to enable window transparency.");
            }

            byte alpha = (byte)Math.Round(opacity * 255, MidpointRounding.AwayFromZero);
            if (!SetLayeredWindowAttributes(handle, 0, alpha, AlphaFlag))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to apply window transparency.");
        }

        private static void PreserveLayeredStyle(Window window, IntPtr handle)
        {
            if ((bool)window.GetValue(HookInstalledProperty)) return;
            var source = HwndSource.FromHwnd(handle);
            if (source == null) throw new InvalidOperationException("The window does not have a WPF native source.");

            // HwndSource would remove WS_EX_LAYERED for its opaque render target,
            // both now and during Show/resize/pin changes. Preserve the native
            // whole-window-alpha flag while accepting every other requested bit.
            HwndSourceHook preserveLayered = delegate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
                if (message == StyleChangingMessage && wParam.ToInt64() == ExtendedStyleIndex && lParam != IntPtr.Zero) {
                    var change = (StyleChange)Marshal.PtrToStructure(lParam, typeof(StyleChange));
                    change.NewStyle |= (uint)LayeredStyle;
                    Marshal.StructureToPtr(change, lParam, false);
                    handled = true;
                }
                return IntPtr.Zero;
            };
            source.AddHook(preserveLayered);
            window.SetValue(HookInstalledProperty, true);
            window.Closed += delegate { if (!source.IsDisposed) source.RemoveHook(preserveLayered); };
        }

        private static IntPtr GetExtendedStyle(IntPtr handle)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr(handle, ExtendedStyleIndex) : new IntPtr(GetWindowLong(handle, ExtendedStyleIndex));
        }

        private static IntPtr SetExtendedStyle(IntPtr handle, IntPtr value)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr(handle, ExtendedStyleIndex, value) : new IntPtr(SetWindowLong(handle, ExtendedStyleIndex, value.ToInt32()));
        }

        [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
        private static extern void NativeSetLastError(uint error);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr handle, int index, int value);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr handle, uint colorKey, byte alpha, uint flags);
    }
}
