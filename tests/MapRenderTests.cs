using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SquadTools;

internal static class MapRenderTests
{
    private static int assertions;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Near(double expected, double actual, string message) { Check(Math.Abs(expected - actual) < 0.000001, message); }
    private static object Call(MapView view, string method, params object[] args) { return typeof(MapView).GetMethod(method, Private).Invoke(view, args); }
    private static void Set(MapView view, string field, object value) { typeof(MapView).GetField(field, Private).SetValue(view, value); }
    private static object Get(MapView view, string field) { return typeof(MapView).GetField(field, Private).GetValue(view); }
    private static void Update(MapView view) { Call(view, "UpdateViewport"); }
    private static void Layout(MapView view, double width, double height) { view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); Update(view); }
    private static void RectNear(Rect expected, Rect actual, string name)
    {
        Near(expected.X, actual.X, name + " X"); Near(expected.Y, actual.Y, name + " Y");
        Near(expected.Width, actual.Width, name + " width"); Near(expected.Height, actual.Height, name + " height");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error)
        {
            while (error != null) { Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message); Console.Error.WriteLine(error.StackTrace); error = error.InnerException; }
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        MapView view = new MapView();
        view.WorldSize = 4598; view.WorldHeight = 4599;
        view.HasMarkers = true; view.Weapon = new Vec2(2150.125, 2200.75); view.Target = new Vec2(2400.375, 2520.625);
        BitmapImage image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(args[0])); image.EndInit(); image.Freeze(); view.MapImage = image;
        Layout(view, 1100, 760); view.ZoomBy(8); Update(view);
        int builds = view.SceneBuildCount, hudBuilds = view.HudBuildCount;
        DrawingVisual scene = (DrawingVisual)Get(view, "scene"), overlay = (DrawingVisual)Get(view, "overlay");
        DrawingGroup retainedScene = scene.Drawing, retainedOverlay = overlay.Drawing;
        for (int i = 0; i < 300; i++)
        {
            Set(view, "pan", new Vector(Math.Sin(i * .17) * 500, Math.Cos(i * .13) * 350)); Update(view);
            Vec2 world = new Vec2(23.125 + i * 7.123, 65.375 + i * 8.731);
            Point screen = (Point)Call(view, "Screen", world);
            Vec2 inverse = (Vec2)Call(view, "World", screen);
            Near(world.X, inverse.X, "sub-grid X round trip"); Near(world.Y, inverse.Y, "sub-grid Y round trip");
        }
        Check(view.SceneBuildCount == builds, "pan must not rebuild map/grid/marker commands");
        Check(view.HudBuildCount == hudBuilds, "pan must not rebuild HUD text");
        Check(scene.Drawing.Children.Count == retainedScene.Children.Count && overlay.Drawing.Children.Count == retainedOverlay.Children.Count, "pan retains drawing commands");
        Check(view.ClipToBounds, "viewport clips children");
        double scale = (double)typeof(MapView).GetProperty("Scale", Private).GetValue(view, null);
        RectNear(new Rect(0, 0, view.WorldSize * scale, view.WorldHeight * scale), ((RectangleGeometry)scene.Clip).Rect, "rectangular map clip");
        Check(ReferenceEquals(scene.Clip, overlay.Clip), "range and markers share the map clip");
        Point origin = (Point)typeof(MapView).GetProperty("Origin", Private).GetValue(view, null);
        TranslateTransform col = (TranslateTransform)Get(view, "columnOffset"), row = (TranslateTransform)Get(view, "rowOffset");
        Near(origin.X, col.X, "column horizontal pan"); Near(Math.Max(6, origin.Y + 5), col.Y, "column labels pin to top");
        Near(Math.Max(8, origin.X + 6), row.X, "row labels pin to left"); Near(origin.Y, row.Y, "row vertical pan");
        RectNear(new Rect(origin.X, origin.Y, view.WorldSize * scale, view.WorldHeight * scale), col.TransformBounds(((RectangleGeometry)Get(view, "columnClip")).Rect), "column clip follows map");
        RectNear(new Rect(origin.X, origin.Y, view.WorldSize * scale, view.WorldHeight * scale), row.TransformBounds(((RectangleGeometry)Get(view, "rowClip")).Rect), "row clip follows map");
        double[] factors = { 1.2, 1.2, 1 / 1.2, 0.01, 100, 1 / 1.2 };
        foreach (double factor in factors)
        {
            Point anchor = new Point(327.25, 412.875);
            Vec2 before = (Vec2)Call(view, "World", anchor);
            Call(view, "ZoomAt", anchor, factor); Update(view);
            Point after = (Point)Call(view, "Screen", before);
            Near(anchor.X, after.X, "wheel zoom keeps cursor world X"); Near(anchor.Y, after.Y, "wheel zoom keeps cursor world Y");
        }
        builds = view.SceneBuildCount;
        view.Weapon = new Vec2(2150.245, 2200.875); Update(view); Check(view.SceneBuildCount == builds + 1, "marker coordinate change redraws immediately");
        builds = view.SceneBuildCount;
        view.HasMarkers = false; Update(view); Check(view.SceneBuildCount == builds + 1 && (overlay.Drawing == null || overlay.Drawing.Children.Count == 0), "marker reset clears overlay");
        view.HasMarkers = true; Update(view);
        builds = view.SceneBuildCount; Layout(view, 860, 620); Check(view.SceneBuildCount == builds + 1, "resize rebuilds at new scale");
        view.WorldSize = 6000; view.WorldHeight = 3000; builds = view.SceneBuildCount; Update(view);
        Check(view.SceneBuildCount == builds + 1, "rectangular map dimension change redraws");
        view.MapImage = null; builds = view.SceneBuildCount; Update(view); Check(view.SceneBuildCount == builds + 1, "source replacement redraws");
        view.MapImage = image; Update(view);
        // Mouse moves coalesce into a single visual update, then mouse up commits
        // the release position even if the last queued move was never delivered.
        Set(view, "dragging", true); Set(view, "dragStart", new Point(0, 0)); Set(view, "panStart", new Vector(17.25, 41.75));
        int viewportUpdates = view.ViewportUpdateCount;
        Call(view, "OnMove", view, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
        Call(view, "OnMove", view, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
        Check(view.ViewportUpdateCount == viewportUpdates, "mouse moves defer drawing until a frame");
        Check((bool)Get(view, "pendingViewport") && (bool)Get(view, "listeningForFrame"), "moves schedule one frame");
        Call(view, "OnFrame", view, EventArgs.Empty);
        Check(view.ViewportUpdateCount == viewportUpdates + 1 && !(bool)Get(view, "pendingViewport") && !(bool)Get(view, "listeningForFrame"), "frame applies latest pan once and unsubscribes");
        Call(view, "OnFrame", view, EventArgs.Empty); Check(view.ViewportUpdateCount == viewportUpdates + 1, "idle frame does no drawing");
        MouseButtonEventArgs release = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle);
        Vector releasePan = new Vector(17.25, 41.75) + (release.GetPosition(view) - new Point(0, 0));
        Call(view, "OnUp", view, release);
        Vector appliedPan = (Vector)Get(view, "pan"); Near(releasePan.X, appliedPan.X, "mouse up commits release X"); Near(releasePan.Y, appliedPan.Y, "mouse up commits release Y");
        Check(!(bool)Get(view, "dragging") && !(bool)Get(view, "pendingViewport"), "mouse up completes drag");
        // Lost capture must flush the newest pending pan, unhook frame callbacks and restore sharp sampling.
        Set(view, "pan", new Vector(123.25, -77.875)); Set(view, "dragging", true); Set(view, "pendingViewport", true);
        RenderOptions.SetBitmapScalingMode(scene, BitmapScalingMode.LowQuality);
        view.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Check(!(bool)Get(view, "dragging") && !(bool)Get(view, "pendingViewport") && !(bool)Get(view, "listeningForFrame"), "lost capture completes drag");
        Check(RenderOptions.GetBitmapScalingMode(scene) == BitmapScalingMode.HighQuality, "lost capture restores image quality");
        Point finalOrigin = (Point)typeof(MapView).GetProperty("Origin", Private).GetValue(view, null);
        Near(finalOrigin.X, scene.Transform.Value.OffsetX, "lost capture commits final pan X"); Near(finalOrigin.Y, scene.Transform.Value.OffsetY, "lost capture commits final pan Y");
        // A new wheel event during a pending pan uses current logical coordinates.
        Set(view, "pan", new Vector(167.375, -42.125)); Set(view, "pendingViewport", true);
        Point wheelPoint = new Point(183.125, 284.25); Vec2 pendingWorld = (Vec2)Call(view, "World", wheelPoint);
        Call(view, "ZoomAt", wheelPoint, 1.2); Call(view, "EndDrag"); Update(view);
        Point pendingScreen = (Point)Call(view, "Screen", pendingWorld);
        Near(wheelPoint.X, pendingScreen.X, "pending pan wheel X anchor"); Near(wheelPoint.Y, pendingScreen.Y, "pending pan wheel Y anchor");
        Set(view, "pendingViewport", true); Set(view, "dragging", true); view.ResetView(); Update(view);
        Near(1, (double)Get(view, "zoom"), "reset restores zoom"); Check((Vector)Get(view, "pan") == new Vector(), "reset clears pan");
        Check(!(bool)Get(view, "dragging") && !(bool)Get(view, "pendingViewport"), "reset flushes pending drag");
        view.WorldSize = 4598; view.WorldHeight = 4599; Layout(view, 1100, 760); view.ZoomBy(3); Set(view, "pan", new Vector(0, 0)); Update(view);
        if (args.Length > 1)
        {
            RenderTargetBitmap preview = new RenderTargetBitmap(1100, 760, 96, 96, PixelFormats.Pbgra32); preview.Render(view);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
            using (FileStream file = File.Create(args[1])) encoder.Save(file);
        }
        Console.WriteLine("Map render tests passed: " + assertions + " assertions; retained pan, precise coordinates, rectangular clips, pinned labels, wheel anchor, resize, source/marker changes and capture/reset lifecycle.");
        return 0;
    }
}
