using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        view.RangeMeters = 2000; Update(view); Check(view.SceneBuildCount == builds + 1, "changing weapon range redraws retained map layers");
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
        TestThemeRendering(image);
        Console.WriteLine("Map render tests passed: " + assertions + " assertions; retained left pan, left double-click target, right-click mortar, target hover/drag, precise coordinates, rectangular clips, pinned labels, wheel anchor, resize, live warm map themes and capture/reset lifecycle.");
        return 0;
    }

    private static Color Sample(MapView view,Point point)
    {
        view.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),(int)Math.Ceiling(view.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(view); var pixel=new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)point.X,(int)point.Y,1,1),pixel,4,0);
        return Color.FromArgb(pixel[3],pixel[2],pixel[1],pixel[0]);
    }
    private static void WarmSurface(Color color,string label)
    {
        Check(color.R>=color.G && color.G>color.B && color.R-color.B<=22 && color.B>=225,label+": actual="+color);
    }
    private static void Theme(MapView view,bool light)
    {
        UiTheme.Apply(light);
        view.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));
        Update(view); view.InvalidateVisual(); view.UpdateLayout();
    }
    private static void TestThemeRendering(BitmapSource image)
    {
        var view=new MapView();
        try {
            Theme(view,false); Layout(view,900,700);
            Point surround=new Point(2,2); Vec2 bareGround=new Vec2(451,469);
            Color darkSurround=Sample(view,surround), darkGround=Sample(view,(Point)Call(view,"Screen",bareGround));
            Check(darkSurround.R<70 && darkSurround.G<70 && darkSurround.B<70,"dark map surround is dark before theme change");
            int builds=view.SceneBuildCount;
            Theme(view,true);
            WarmSurface(Sample(view,surround),"live light theme updates the map surround");
            WarmSurface(Sample(view,(Point)Call(view,"Screen",bareGround)),"live light theme updates an already-cached blank map");
            Check(view.SceneBuildCount<=builds+1,"changing theme needs at most one scene refresh");
            builds=view.SceneBuildCount;
            for(int i=0;i<20;i++) { Set(view,"pan",new Vector(i*1.125,-i*0.75)); Update(view); }
            Check(view.SceneBuildCount==builds,"light themed retained layers keep the existing pan cache");
            WarmSurface(Sample(view,surround),"panning does not restore the old dark surround");
            Theme(view,false);
            Check(Sample(view,surround)==darkSurround,"switching back restores the original dark map surround");
            Check(Sample(view,(Point)Call(view,"Screen",bareGround))==darkGround,"cached blank map returns to its original dark surface");
            view.ResetView(); view.MapImage=image; Update(view);
            Point texturePoint=(Point)Call(view,"Screen",bareGround); Color gameTexture=Sample(view,texturePoint);
            Theme(view,true);
            Check(Sample(view,texturePoint)==gameTexture,"theme changes preserve the original map image pixels");
            WarmSurface(Sample(view,surround),"image maps also use a light viewport surround");
        } finally { UiTheme.Apply(false); }
    }

    private static void TestPointerGestures()
    {
        var view = new MapView(); Layout(view, 900, 700); view.ZoomBy(3); Update(view);
        int placed = 0; bool lastWeapon = false; Vec2 lastPoint = new Vec2();
        view.PointPlaced += delegate(Vec2 point, bool weapon) {
            placed++; lastPoint = point; lastWeapon = weapon;
            if (weapon) view.Weapon = point; else view.Target = point;
        };
        Point anchor = new Point(450.25, 350.125);
        Point jitter = anchor + new Vector(SystemParameters.MinimumHorizontalDragDistance / 4, SystemParameters.MinimumVerticalDragDistance / 4);
        Point far = anchor + new Vector(SystemParameters.MinimumHorizontalDragDistance + 80, SystemParameters.MinimumVerticalDragDistance + 60);
        var scene = (DrawingVisual)Get(view, "scene");

        Down(view, MouseButton.Left, anchor, 1); Move(view, jitter); Up(view, MouseButton.Left, jitter);
        Check(placed == 0, "left single click does not place a target");
        Check((Vector)Get(view, "pan") == new Vector(), "click jitter does not pan the map");
        Check(RenderOptions.GetBitmapScalingMode(scene) == BitmapScalingMode.HighQuality, "stationary click keeps sharp sampling");
        Vec2 expected = (Vec2)Call(view, "World", jitter);
        Down(view, MouseButton.Left, anchor, 2);
        Check(placed == 0, "target double click waits for release");
        Move(view, jitter); Up(view, MouseButton.Left, jitter);
        Check(placed == 1 && !lastWeapon, "completed left double click places the target");
        Near(expected.X, lastPoint.X, "double left exact X"); Near(expected.Y, lastPoint.Y, "double left exact Y");
        Check((Vector)Get(view, "pan") == new Vector(), "double click keeps viewport stationary");
        Click(view, MouseButton.Left, anchor, 3);
        Check(placed == 1, "third rapid left click starts another pair");
        Click(view, MouseButton.Left, anchor, 4);
        Check(placed == 2 && !lastWeapon, "fourth rapid left click completes another target placement");

        Down(view, MouseButton.Right, anchor, 1);
        Check(placed == 2, "right press waits for a completed click");
        Move(view, jitter); Up(view, MouseButton.Right, jitter);
        Check(placed == 3 && lastWeapon, "right single click places the mortar");
        Near(expected.X, lastPoint.X, "right single exact X"); Near(expected.Y, lastPoint.Y, "right single exact Y");
        Click(view, MouseButton.Right, anchor, 2);
        Check(placed == 4 && lastWeapon, "a repeated right click still places the mortar");
        int before = placed;
        Down(view, MouseButton.Right, anchor, 1); Move(view, far); Move(view, anchor); Up(view, MouseButton.Right, anchor);
        Check(placed == before && (Vector)Get(view, "pan") == new Vector(), "right movement cancels placement without panning");

        view.ResetView(); view.ZoomBy(8); Update(view);
        int builds = view.SceneBuildCount, updates = view.ViewportUpdateCount;
        Down(view, MouseButton.Left, anchor, 1); Move(view, far);
        Check(placed == before, "left pan does not place a marker");
        Check(view.ViewportUpdateCount == updates, "left pan coalesces viewport updates");
        Check(RenderOptions.GetBitmapScalingMode(scene) == BitmapScalingMode.LowQuality, "left pan uses fast sampling");
        Up(view, MouseButton.Right, far); Up(view, MouseButton.Middle, far);
        Check((bool)Get(view, "dragging"), "other button releases do not end left pan");
        Down(view, MouseButton.Right, far, 1);
        Check(placed == before, "right press during left pan does not place a mortar");
        Up(view, MouseButton.Left, far);
        Vector delta = far - anchor, pan = (Vector)Get(view, "pan");
        Near(delta.X, pan.X, "left pan X"); Near(delta.Y, pan.Y, "left pan Y");
        Check(view.SceneBuildCount == builds, "left pan retains map drawing commands");
        Check(!(bool)Get(view, "dragging") && !(bool)Get(view, "listeningForFrame"), "left release clears capture and frame subscription");
        Check(RenderOptions.GetBitmapScalingMode(scene) == BitmapScalingMode.HighQuality, "left release restores sharp sampling");
        Click(view, MouseButton.Left, far, 2);
        Check(placed == before, "pan cannot be the first click of target placement");
        Click(view, MouseButton.Left, far, 3);
        Check(placed == before + 1 && !lastWeapon, "two stationary clicks after pan place a target");

        before = placed; view.ResetView();
        Click(view, MouseButton.Left, anchor, 1);
        Down(view, MouseButton.Left, anchor, 2); Move(view, far); Move(view, anchor); Up(view, MouseButton.Left, anchor);
        Check(placed == before, "moving out and back cancels a double click");
        Click(view, MouseButton.Left, anchor, 1);
        Down(view, MouseButton.Left, anchor, 2); Up(view, MouseButton.Left, far);
        Check(placed == before, "release-only movement pans instead of placing a target");
        pan = (Vector)Get(view, "pan");
        Near(delta.X, pan.X, "release-only pan X"); Near(delta.Y, pan.Y, "release-only pan Y");

        view.ResetView(); Click(view, MouseButton.Left, anchor, 1); Down(view, MouseButton.Left, anchor, 2);
        view.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Up(view, MouseButton.Left, anchor); Click(view, MouseButton.Left, anchor, 2);
        Check(placed == before, "lost capture clears pending target placement");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Click(view, MouseButton.Left, anchor, 2);
        Check(placed == before, "unload clears double-click state");
        view.ResetView(); Click(view, MouseButton.Left, anchor, 2);
        Check(placed == before, "reset clears double-click state");
        view.ZoomBy(1.3); Click(view, MouseButton.Left, anchor, 2);
        Check(placed == before, "toolbar zoom clears double-click state");
        Down(view, MouseButton.Left, anchor, 2); Call(view, "ZoomAt", anchor, 1.2); Up(view, MouseButton.Left, anchor);
        Check(placed == before, "wheel zoom cancels pending double-click placement");
        Click(view, MouseButton.Left, anchor, 1); Click(view, MouseButton.Left, anchor, 1);
        Check(placed == before, "clicks outside the system double-click interval stay single");

        view.ResetView();
        Point outside = (Point)Call(view, "Screen", new Vec2(-1, 100));
        Click(view, MouseButton.Left, outside, 1); Click(view, MouseButton.Left, outside, 2); Click(view, MouseButton.Right, outside, 1);
        Check(placed == before, "neither marker can be placed outside map bounds");
        Down(view, MouseButton.Middle, anchor, 1); Move(view, far); Up(view, MouseButton.Right, far);
        Check((bool)Get(view, "dragging"), "right release does not finish middle pan");
        Up(view, MouseButton.Middle, far);
        Check(placed == before && !(bool)Get(view, "dragging"), "middle pan remains available");
        TestTargetDragging();
    }

    private static void TestTargetDragging()
    {
        var view = new MapView { WorldSize = 6000, WorldHeight = 3000, HasMarkers = true, Target = new Vec2(3000, 1500), Weapon = new Vec2(2300, 1100) };
        Layout(view, 900, 700); view.ZoomBy(2); Update(view);
        int placed = 0; bool lastWeapon = true;
        view.PointPlaced += delegate(Vec2 point, bool weapon) { placed++; lastWeapon = weapon; if (weapon) view.Weapon = point; else view.Target = point; view.InvalidateVisual(); };
        Vec2 original = view.Target, weaponPosition = view.Weapon;
        double scale = (double)typeof(MapView).GetProperty("Scale", Private).GetValue(view, null);
        Point center = (Point)Call(view, "Screen", view.Target);
        Point grab = center + new Vector(5, 3), destination = grab + new Vector(78, 55);
        Call(view, "UpdatePointerCursor", grab); Check(view.Cursor == Cursors.Hand, "hovering the target shows a hand cursor");
        Call(view, "UpdatePointerCursor", center + new Vector(16, 12)); Check(view.Cursor == Cursors.Hand, "the target label also offers target dragging");
        Call(view, "UpdatePointerCursor", center + new Vector(20, 20)); Check(view.Cursor == Cursors.Cross, "away from the target uses the normal map cursor");
        Down(view, MouseButton.Left, grab, 1);
        Check(view.Cursor == Cursors.Hand, "target drag keeps the hand cursor");
        Move(view, grab + new Vector(1, 1)); Check(placed == 0, "target click jitter does not move the target");
        Move(view, destination);
        Check(placed == 1 && !lastWeapon, "held target drag publishes a target update before release");
        Near(original.X + 78 / scale, view.Target.X, "drag preserves grab offset X"); Near(original.Y + 55 / scale, view.Target.Y, "drag preserves grab offset Y");
        Check((Vector)Get(view, "pan") == new Vector(), "target drag keeps the map stationary");
        Check(view.Weapon.X == weaponPosition.X && view.Weapon.Y == weaponPosition.Y, "target drag keeps the mortar fixed");
        Up(view, MouseButton.Right, destination); Check((bool)Get(view, "dragging"), "other button release does not end target drag");
        Point release = grab + new Vector(92, 66); Up(view, MouseButton.Left, release);
        Near(original.X + 92 / scale, view.Target.X, "release commits final target X"); Near(original.Y + 66 / scale, view.Target.Y, "release commits final target Y");
        Check(!(bool)Get(view, "dragging") && !(bool)Get(view, "draggingTarget"), "release completes target drag");

        center = (Point)Call(view, "Screen", view.Target);
        Click(view, MouseButton.Left, center, 1);
        Down(view, MouseButton.Left, center, 2); Move(view, center + new Vector(60, 40)); Up(view, MouseButton.Left, center + new Vector(60, 40));
        Check((Vector)Get(view, "pan") == new Vector() && !lastWeapon, "a second press on the target still drags the target");
        center = (Point)Call(view, "Screen", view.Target);
        Down(view, MouseButton.Left, center, 1);
        Point outside = (Point)Call(view, "Screen", new Vec2(7000, -1000));
        Move(view, outside); Up(view, MouseButton.Left, outside);
        Check(view.Target.X >= 0 && view.Target.X < view.WorldSize && view.Target.Y >= 0 && view.Target.Y < view.WorldHeight, "dragging beyond the map clamps to a valid point");
        Near(view.WorldSize - 0.000001, view.Target.X, "target clamps at the far edge"); Near(0, view.Target.Y, "target clamps at the top edge");

        view.Target = original; center = (Point)Call(view, "Screen", original);
        Down(view, MouseButton.Left, center, 1); Move(view, center + new Vector(30, 20));
        view.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        int before = placed; Up(view, MouseButton.Left, center + new Vector(60, 40));
        Check(placed == before && !(bool)Get(view, "draggingTarget"), "capture loss prevents a later release from moving the target");
        center = (Point)Call(view, "Screen", view.Target);
        Down(view, MouseButton.Left, center, 1); Call(view, "ZoomAt", center, 1.2); Up(view, MouseButton.Left, center + new Vector(60, 40));
        Check(placed == before && !(bool)Get(view, "dragging"), "wheel zoom ends an active target drag cleanly");
        center = (Point)Call(view, "Screen", view.Target);
        Down(view, MouseButton.Left, center, 1); view.PredictionMode = true; Up(view, MouseButton.Left, center + new Vector(60, 40));
        Check(placed == before && !(bool)Get(view, "dragging"), "switching to RAAS cancels a shooting gesture");
    }
}
