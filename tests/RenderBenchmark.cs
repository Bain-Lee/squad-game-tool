using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SquadTools;

// Measures UI-thread scene construction/update only. This is not a displayed FPS test:
// no compositor, monitor, mouse dispatch, or per-frame bitmap rasterization is timed.
internal static class RenderBenchmark
{
    [STAThread]
    private static int Main(string[] args)
    {
        MapView view = new MapView();
        view.WorldSize = 4598;
        view.WorldHeight = 4599;
        BitmapImage bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(Path.GetFullPath(args[0]));
        bitmap.EndInit(); bitmap.Freeze();
        view.MapImage = bitmap;
        view.Weapon = new Vec2(2000, 2100);
        view.Target = new Vec2(2350, 2550);
        view.HasMarkers = true;
        view.Measure(new Size(1100, 760));
        view.Arrange(new Rect(0, 0, 1100, 760));
        view.ZoomBy(8);
        FieldInfo pan = typeof(MapView).GetField("pan", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo render = typeof(MapView).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic);
        DrawingVisual frame = new DrawingVisual();
        Action<int> update = delegate(int i)
        {
            pan.SetValue(view, new Vector(Math.Sin(i * 0.017) * 440, Math.Cos(i * 0.013) * 270));
            using (DrawingContext dc = frame.RenderOpen()) render.Invoke(view, new object[] { dc });
        };
        for (int i = 0; i < 30; i++) update(i);
        const int rounds = 5, frames = 240;
        PropertyInfo builds = typeof(MapView).GetProperty("SceneBuildCount", BindingFlags.Instance | BindingFlags.NonPublic);
        int initialBuilds = builds == null ? -1 : (int)builds.GetValue(view, null);
        double[] times = new double[rounds];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int collections = GC.CollectionCount(0);
        for (int round = 0; round < rounds; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++) update(round * frames + i);
            clock.Stop(); times[round] = clock.Elapsed.TotalMilliseconds / frames;
        }
        Array.Sort(times);
        Console.WriteLine("Scenario: Black Coast {0}x{1}px, rectangular 4598x4599m, viewport 1100x760, zoom 8x, 1200 pan updates", bitmap.PixelWidth, bitmap.PixelHeight);
        Console.WriteLine("Metric: UI-thread scene update CPU; excludes compositor/rasterization/display FPS.");
        Console.WriteLine("Median ms/update: " + times[rounds / 2].ToString("F6", CultureInfo.InvariantCulture));
        Console.WriteLine("Round min/max ms/update: " + times[0].ToString("F6", CultureInfo.InvariantCulture) + " / " + times[rounds - 1].ToString("F6", CultureInfo.InvariantCulture));
        Console.WriteLine("Gen0 collections in timed rounds: " + (GC.CollectionCount(0) - collections));
        Console.WriteLine("Map/grid/marker scene rebuilds while panning: " + (builds == null ? "1200 (legacy OnRender rebuilds each update)" : ((int)builds.GetValue(view, null) - initialBuilds).ToString(CultureInfo.InvariantCulture)));
        return 0;
    }
}
