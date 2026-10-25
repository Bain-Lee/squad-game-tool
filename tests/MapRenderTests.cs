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
    private static void Down(MapView view, MouseButton button, Point point, int clicks) { Call(view, "HandlePointerDown", button, point, clicks); }
    private static void Move(MapView view, Point point) { Call(view, "MoveDrag", point); }
    private static void Up(MapView view, MouseButton button, Point point) { Call(view, "HandlePointerUp", button, point); }
    private static void Click(MapView view, MouseButton button, Point point, int clicks) { Down(view, button, point, clicks); Up(view, button, point); }
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
        Set(view, "pan", new Vector(17.25, 41.75));
        Down(view, MouseButton.Middle, new Point(100, 100), 1);
        int viewportUpdates = view.ViewportUpdateCount;
        Move(view, new Point(140, 130));
        Move(view, new Point(160, 150));
        Check(view.ViewportUpdateCount == viewportUpdates, "mouse moves defer drawing until a frame");
        Check((bool)Get(view, "pendingViewport") && (bool)Get(view, "listeningForFrame"), "moves schedule one frame");
        Call(view, "OnFrame", view, EventArgs.Empty);
        Check(view.ViewportUpdateCount == viewportUpdates + 1 && !(bool)Get(view, "pendingViewport") && !(bool)Get(view, "listeningForFrame"), "frame applies latest pan once and unsubscribes");
        Call(view, "OnFrame", view, EventArgs.Empty); Check(view.ViewportUpdateCount == viewportUpdates + 1, "idle frame does no drawing");
        Vector releasePan = new Vector(97.25, 111.75);
        Up(view, MouseButton.Middle, new Point(180, 170));
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
        TestPointerGestures();
        Console.WriteLine("Map render tests passed: " + assertions + " assertions; retained pan, right-button drag/double-click isolation, precise coordinates, rectangular clips, pinned labels, wheel anchor, resize, source/marker changes and capture/reset lifecycle.");
        return 0;
    }

    private static void TestPointerGestures()
    {
        MapView view = new MapView(); Layout(view, 900, 700); view.ZoomBy(3); Update(view);
        int placed = 0; bool lastWeapon = false; Vec2 lastPoint = new Vec2();
        view.PointPlaced += delegate(Vec2 point, bool weapon) { placed++; lastPoint = point; lastWeapon = weapon; };
        Point anchor = new Point(450.25, 350.125);
        DrawingVisual scene = (DrawingVisual)Get(view, "scene");
        double jitterX = SystemParameters.MinimumHorizontalDragDistance / 4;
        double jitterY = SystemParameters.MinimumVerticalDragDistance / 4;
        Point jitter = anchor + new Vector(jitterX, jitterY);
        Point far = anchor + new Vector(SystemParameters.MinimumHorizontalDragDistance + 80, SystemParameters.MinimumVerticalDragDistance + 60);

        // A single right click, including normal hand jitter, changes neither point nor pan.
        Down(view, MouseButton.Right, anchor, 1); Move(view, jitter); Up(view, MouseButton.Right, jitter);
        Check(placed == 0, "right single click must not place any marker");
        Check((Vector)Get(view, "pan") == new Vector(), "click jitter must not pan the map");
        Check(RenderOptions.GetBitmapScalingMode(scene) == BitmapScalingMode.HighQuality, "stationary click keeps sharp sampling");

        Vec2 expected = (Vec2)Call(view, "World", jitter);
        Down(view, MouseButton.Right, anchor, 2);
        Check(placed == 0, "double right press defers placement until release");
        Move(view, jitter); Up(view, MouseButton.Right, jitter);
        Check(placed == 1 && lastWeapon, "completed right double click places the mortar once");
        Near(expected.X, lastPoint.X, "double right exact X"); Near(expected.Y, lastPoint.Y, "double right exact Y");
        Check((Vector)Get(view, "pan") == new Vector(), "double click must keep viewport still");
        Click(view, MouseButton.Right, anchor, 3);
        Check(placed == 1, "third rapid click starts a new pair");
        Click(view, MouseButton.Right, anchor, 4);
        Check(placed == 2, "fourth rapid click completes the next pair");

        // Right dragging uses the retained renderer and never inherits left-button mode.
        view.ResetView(); view.ZoomBy(8); Update(view); view.PlaceWeapon = true;
        int builds = view.SceneBuildCount, updates = view.ViewportUpdateCount;
        Down(view, MouseButton.Right, anchor, 1); Move(view, far);
        Check(placed == 2, "right drag never places a mortar in weapon mode");
        Check(view.ViewportUpdateCount == updates, "right drag coalesces moves into a frame");
        Check(RenderOptions.GetBitmapScalingMode(scene) == BitmapScalingMode.LowQuality, "right drag enables fast sampling");
        Up(view, MouseButton.Left, far); Up(view, MouseButton.Middle, far);
        Check((bool)Get(view, "dragging"), "other button releases must not finish right drag");
        Down(view, MouseButton.Left, far, 1);
        Check(placed == 2, "left press during right drag must not place a marker");
        Up(view, MouseButton.Right, far);
        Vector delta = far - anchor, pan = (Vector)Get(view, "pan");
        Near(delta.X, pan.X, "right drag moves pan X"); Near(delta.Y, pan.Y, "right drag moves pan Y");
        Check(view.SceneBuildCount == builds, "right drag retains the scene at eight times zoom");
        Check(!(bool)Get(view, "dragging") && !(bool)Get(view, "listeningForFrame"), "right release finishes drag and frame subscription");
        Check(RenderOptions.GetBitmapScalingMode(scene) == BitmapScalingMode.HighQuality, "right release restores sharp sampling");
        Click(view, MouseButton.Right, far, 2);
        Check(placed == 2, "a completed drag cannot be the first half of a double click");
        Click(view, MouseButton.Right, far, 3);
        Check(placed == 3, "two fresh stationary clicks after dragging can place a mortar");

        // A second press that starts moving is a drag, even if it returns to its origin.
        view.ResetView();
        Click(view, MouseButton.Right, anchor, 1);
        Down(view, MouseButton.Right, anchor, 2); Move(view, far); Move(view, anchor); Up(view, MouseButton.Right, anchor);
        Check(placed == 3, "dragging out and back during second click cancels placement");
        Click(view, MouseButton.Right, anchor, 1);
        Down(view, MouseButton.Right, anchor, 2); Up(view, MouseButton.Right, far);
        Check(placed == 3, "release-only movement must also cancel double-click placement");
        pan = (Vector)Get(view, "pan");
        Near(delta.X, pan.X, "release-only pan X"); Near(delta.Y, pan.Y, "release-only pan Y");

        // Capture loss, unload, reset and zoom cancel incomplete click sequences.
        view.ResetView(); Click(view, MouseButton.Right, anchor, 1); Down(view, MouseButton.Right, anchor, 2);
        view.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Up(view, MouseButton.Right, anchor); Click(view, MouseButton.Right, anchor, 2);
        Check(placed == 3, "lost capture clears both the active gesture and previous click");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Click(view, MouseButton.Right, anchor, 2);
        Check(placed == 3, "unload clears the previous right click");
        view.ResetView(); Click(view, MouseButton.Right, anchor, 2);
        Check(placed == 3, "reset clears the previous right click");
        view.ZoomBy(1.3); Click(view, MouseButton.Right, anchor, 2);
        Check(placed == 3, "toolbar zoom clears the previous right click");
        Down(view, MouseButton.Right, anchor, 2); Call(view, "ZoomAt", anchor, 1.2); Up(view, MouseButton.Right, anchor);
        Check(placed == 3, "wheel zoom during a second right press cancels marker placement");
        Click(view, MouseButton.Right, anchor, 1); Click(view, MouseButton.Right, anchor, 1);
        Check(placed == 3, "clicks outside system double-click limits remain single clicks");

        view.ResetView();
        Point outside = (Point)Call(view, "Screen", new Vec2(-1, 100));
        Click(view, MouseButton.Right, outside, 1); Click(view, MouseButton.Right, outside, 2);
        Check(placed == 3, "right double click outside map bounds must not place a mortar");
        view.PlaceWeapon = false; Click(view, MouseButton.Left, anchor, 1);
        Check(placed == 4 && !lastWeapon, "left click still places the target");
        expected = (Vec2)Call(view, "World", anchor);
        Near(expected.X, lastPoint.X, "left exact X"); Near(expected.Y, lastPoint.Y, "left exact Y");
        view.PlaceWeapon = true; Click(view, MouseButton.Left, anchor, 1);
        Check(placed == 5 && lastWeapon, "left click still follows the toolbar weapon mode");
        Down(view, MouseButton.Middle, anchor, 1); Move(view, far); Up(view, MouseButton.Right, far);
        Check((bool)Get(view, "dragging"), "right release must not finish middle drag");
        Up(view, MouseButton.Middle, far);
        Check(placed == 5 && !(bool)Get(view, "dragging"), "middle-button dragging remains available without placing markers");
    }
}
