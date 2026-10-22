using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SquadTools;

internal static class MapTests
{
    private static int count;
    private static string fixtureRoot;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            fixtureRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "map-test-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureRoot);
            TestCatalogValidation();
            TestTerrainChannelsAndRectangularSampling();
            if (args.Length > 0) TestBundledMaps(args[0]);
            Console.WriteLine("PASS: " + count + " map assertions.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally
        {
            if (fixtureRoot != null && Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, true);
        }
    }

    private static void TestCatalogValidation()
    {
        Throws<FileNotFoundException>(delegate { MapCatalog.Load(fixtureRoot); }, "missing catalog is explicit");
        WriteFixturePng(Path.Combine(fixtureRoot, "fixture.png"));
        WriteCatalog("<maps />");
        Throws<InvalidDataException>(delegate { MapCatalog.Load(fixtureRoot); }, "empty catalog rejected");
        WriteCatalog("<wrong />");
        Throws<InvalidDataException>(delegate { MapCatalog.Load(fixtureRoot); }, "wrong root rejected");
        WriteCatalog("<!DOCTYPE maps [<!ENTITY e 'fixture'>]><maps />");
        Throws<System.Xml.XmlException>(delegate { MapCatalog.Load(fixtureRoot); }, "DTD rejected");
        string entry = Entry("fixture", "fixture.png", "fixture.png", "200", "300", "0.5", "-10");
        WriteCatalog("<maps>" + entry + entry + "</maps>");
        Throws<InvalidDataException>(delegate { MapCatalog.Load(fixtureRoot); }, "duplicate maps rejected");
        WriteCatalog("<maps>" + entry + Entry("FIXTURE", "fixture.png", "fixture.png", "200", "300", "0.5", "-10") + "</maps>");
        Throws<InvalidDataException>(delegate { MapCatalog.Load(fixtureRoot); }, "case duplicate maps rejected");
        string[] badPaths = { "../fixture.png", "sub/../fixture.png", "./fixture.png", "C:/fixture.png", "/fixture.png", "\\\\server\\fixture.png", "fixture.png:stream" };
        foreach (string path in badPaths)
        {
            WriteCatalog("<maps>" + Entry("fixture", path, "fixture.png", "200", "300", "0.5", "-10") + "</maps>");
            Throws<InvalidDataException>(delegate { MapCatalog.Load(fixtureRoot); }, "unsafe asset path " + path);
        }
        foreach (string badNumber in new[] { "NaN", "Infinity", "-1", "0", "1e500", "abc" })
        {
            WriteCatalog("<maps>" + Entry("fixture", "fixture.png", "fixture.png", badNumber, "300", "0.5", "-10") + "</maps>");
            Throws<InvalidDataException>(delegate { MapCatalog.Load(fixtureRoot); }, "invalid dimensions " + badNumber);
        }
        WriteCatalog("<maps>" + Entry("fixture", "fixture.png", "missing.png", "200", "300", "0.5", "-10") + "</maps>");
        Throws<FileNotFoundException>(delegate { MapCatalog.Load(fixtureRoot); }, "missing terrain is explicit");
        WriteCatalog("<maps>" + Entry("fixture", "fixture.png", "fixture.png", "200", "300", "0", "-10") + "</maps>");
        Throws<InvalidDataException>(delegate { MapCatalog.Load(fixtureRoot); }, "zero height scale rejected");
        WriteCatalog("<maps>" + Entry("fixture", "fixture.png", "fixture.png", "200", "300", "0.5", "NaN") + "</maps>");
        Throws<InvalidDataException>(delegate { MapCatalog.Load(fixtureRoot); }, "nonfinite offset rejected");
        WriteCatalog("<maps>" + Entry("fixture", "fixture.png", "", "200", "300", "0", "0") + "</maps>");
        MapDefinition imageOnly = MapCatalog.Load(fixtureRoot)[0];
        True(!imageOnly.HasTerrainHeight && imageOnly.HeightmapPath == String.Empty, "image-only map declares no terrain");
        Throws<FileNotFoundException>(delegate { TerrainHeightMap.Load(imageOnly); }, "image-only map cannot silently yield zero terrain");
    }

    private static void TestTerrainChannelsAndRectangularSampling()
    {
        WriteCatalog("<maps>" + Entry("fixture", "fixture.png", "fixture.png", "200", "300", "0.5", "-10") + "</maps>");
        CultureInfo oldCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
        IList<MapDefinition> maps;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            maps = MapCatalog.Load(fixtureRoot);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = oldCulture; }
        True(maps.Count == 1, "one fixture map loaded");
        MapDefinition map = maps[0];
        True(map.Name == "Fixture" && map.ToString() == "测试地图", "names and selector label preserved");
        Near(200, map.Width, "rectangular map width");
        Near(300, map.Height, "rectangular map height");
        Near(0.5, map.HeightScale, "invariant metadata decimals");
        TerrainHeightMap terrain = TerrainHeightMap.Load(map);
        // BGRA pixels are blue, red / black, white / mixed channel values.
        Near(-10, terrain.Sample(new Vec2(0, 0)), "blue means raw zero");
        Near(245, terrain.Sample(new Vec2(100, 0)), "red means raw 510");
        Near(117.5, terrain.Sample(new Vec2(0, 100)), "black means raw 255");
        Near(117.5, terrain.Sample(new Vec2(100, 100)), "white means raw 255");
        Near(132.5, terrain.Sample(new Vec2(0, 200)), "mixed channels R-B decode");
        Near(102.5, terrain.Sample(new Vec2(100, 200)), "negative R-B decode");
        Near(-10, terrain.Sample(new Vec2(49.999, 49.999)), "nearest sample before halfway");
        Near(117.5, terrain.Sample(new Vec2(50, 50)), "nearest sample exact half rounds up");
        Near(102.5, terrain.Sample(new Vec2(200, 300)), "far map edge clamps to final pixel");
        Near(132.5, terrain.Sample(new Vec2(0, 300)), "south map edge preserves x");
        Near(245, terrain.Sample(new Vec2(200, 0)), "east map edge preserves y");
        True(map.Contains(new Vec2(200, 300)) && terrain.Contains(new Vec2(0, 0)), "map contains both edges");
        Vec2[] invalid = { new Vec2(-0.01, 0), new Vec2(0, -0.01), new Vec2(200.01, 0), new Vec2(0, 300.01),
            new Vec2(Double.NaN, 0), new Vec2(0, Double.PositiveInfinity) };
        foreach (Vec2 position in invalid)
        {
            Vec2 captured = position;
            True(!map.Contains(position), "outside coordinates excluded");
            Throws<ArgumentOutOfRangeException>(delegate { terrain.Sample(captured); }, "outside terrain throws rather than zero");
        }
        Throws<ArgumentNullException>(delegate { TerrainHeightMap.Load(null); }, "null terrain map rejected");
        File.Delete(map.HeightmapPath);
        // The loaded raster is independent of an open stream and remains usable.
        Near(245, terrain.Sample(new Vec2(100, 0)), "terrain closes file after loading");
        Throws<FileNotFoundException>(delegate { TerrainHeightMap.Load(map); }, "deleted terrain fails explicitly");
    }

    private static void TestBundledMaps(string directory)
    {
        IList<MapDefinition> maps = MapCatalog.Load(directory);
        True(maps.Count >= 25, "bundled official map catalog coverage");
        HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int terrainCount = 0;
        foreach (MapDefinition map in maps)
        {
            True(ids.Add(map.Id), "unique bundled map " + map.Id);
            using (FileStream stream = File.OpenRead(map.ImagePath))
            {
                BitmapDecoder image = BitmapDecoder.Create(stream,
                    BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                True(image.Frames[0].PixelWidth > 0 && image.Frames[0].PixelHeight > 0, "map image decodes " + map.Id);
            }
            if (!map.HasTerrainHeight)
            {
                True(String.Equals(map.Id, "belaya", StringComparison.OrdinalIgnoreCase), "only legacy Belaya lacks trusted terrain");
                continue;
            }
            terrainCount++;
            TerrainHeightMap terrain = TerrainHeightMap.Load(map);
            Vec2[] points = { new Vec2(0, 0), new Vec2(map.Width / 2, map.Height / 2), new Vec2(map.Width, map.Height) };
            foreach (Vec2 point in points)
            {
                double height = terrain.Sample(point);
                True(!Double.IsNaN(height) && !Double.IsInfinity(height), "finite bundled height " + map.Id);
            }
        }
        True(terrainCount >= 25, "at least 25 bundled maps contain real terrain");
        string[] expected = { "albasrah", "anvil", "blackcoast", "chora", "fallujah", "foolsroad", "goosebay", "gorodok", "harju", "jensen", "kamdesh", "kohat", "kokan", "lashkar", "logar", "manicouagan", "mestia", "mutaha", "narva", "pacific", "sanxian", "skorpo", "sumari", "tallil", "yehorivka" };
        foreach (string id in expected) True(ids.Contains(id), "required official map is included: " + id);
    }

    private static void WriteFixturePng(string path)
    {
        byte[] pixels = {
            255, 0, 0, 255, 0, 0, 255, 255,
            0, 0, 0, 255, 255, 255, 255, 255,
            10, 211, 40, 255, 40, 99, 10, 255
        };
        BitmapSource bitmap = BitmapSource.Create(2, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(path)) encoder.Save(stream);
    }

    private static string Entry(string id, string image, string heightmap, string width, string height, string scale, string offset)
    {
        return "<map id='" + id + "' name='Fixture' displayName='测试地图' width='" + width + "' height='" + height +
            "' image='" + image + "' heightmap='" + heightmap + "' heightScale='" + scale + "' heightOffset='" + offset + "'/>";
    }

    private static void WriteCatalog(string xml)
    {
        File.WriteAllText(Path.Combine(fixtureRoot, "catalog.xml"), xml, new System.Text.UTF8Encoding(false));
    }

    private static void True(bool value, string message)
    {
        count++;
        if (!value) throw new Exception(message);
    }

    private static void Near(double expected, double actual, string message)
    {
        True(!Double.IsNaN(actual) && Math.Abs(expected - actual) < 1e-9, message + " expected=" + expected + " actual=" + actual);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        count++;
        try { action(); }
        catch (T) { return; }
        throw new Exception(message + " did not throw " + typeof(T).Name);
    }
}
