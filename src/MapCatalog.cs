using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;

namespace SquadTools
{
    /// <summary>A bundled map, in metres from its north-west corner.</summary>
    public sealed class MapDefinition
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string DisplayName { get; private set; }
        public double Width { get; private set; }
        public double Height { get; private set; }
        public string ImagePath { get; private set; }
        public string HeightmapPath { get; private set; }
        public double HeightScale { get; private set; }
        public double HeightOffset { get; private set; }
        public string SourceCommit { get; private set; }
        public bool HasTerrainHeight { get { return !String.IsNullOrEmpty(HeightmapPath); } }

        internal MapDefinition(string id, string name, string displayName,
            double width, double height, string imagePath, string heightmapPath,
            double heightScale, double heightOffset, string sourceCommit = "")
        {
            Id = id;
            Name = name;
            DisplayName = displayName;
            Width = width;
            Height = height;
            ImagePath = imagePath;
            HeightmapPath = heightmapPath;
            HeightScale = heightScale;
            HeightOffset = heightOffset;
            SourceCommit = sourceCommit;
        }

        public bool Contains(Vec2 position)
        {
            return IsFinite(position.X) && IsFinite(position.Y) &&
                position.X >= 0 && position.X <= Width &&
                position.Y >= 0 && position.Y <= Height;
        }

        public override string ToString()
        {
            return DisplayName;
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }
    }

    public static class MapCatalog
    {
        public static MapDefinition FromServerLayer(IEnumerable<MapDefinition> maps, string layer)
        {
            if (String.IsNullOrWhiteSpace(layer)) return null;
            if (layer.IndexOfAny(new[] { '/', '\\', '.' }) >= 0) return null;
            // Match the map token, never a substring in an unrelated or unknown mod layer.
            string token = Regex.Split(layer.Trim(), @"[_\s]+(?:RAAS|AAS|Invasion|Skirmish|Insurgency|Destruction|TerritoryControl|TC|Seed|Training|TrackAttack)(?:[_\s]|$)", RegexOptions.IgnoreCase)[0];
            token = token.Split('_')[0];
            token = MapToken(token);
            foreach (MapDefinition map in maps) {
                if (token == MapToken(map.Id) || token == MapToken(map.Name.Replace(" (Legacy)", ""))) return map;
                if (map.Id == "jensen" && token == "jensensrange" || map.Id == "belaya" && token == "belayapass") return map;
            }
            return null;
        }

        private static string MapToken(string value) { return Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", ""); }

        public static IList<MapDefinition> Load(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("地图目录不能为空。", "directory");
            string root = Path.GetFullPath(directory);
            string catalogPath = Path.Combine(root, "catalog.xml");
            if (!File.Exists(catalogPath))
                throw new FileNotFoundException("找不到离线地图目录 catalog.xml，请保留软件随附的 maps 文件夹。", catalogPath);

            XmlDocument document = new XmlDocument();
            document.XmlResolver = null;
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            using (XmlReader reader = XmlReader.Create(catalogPath, settings))
                document.Load(reader);
            if (document.DocumentElement == null || document.DocumentElement.Name != "maps")
                throw new InvalidDataException("地图目录根节点必须是 maps。");

            List<MapDefinition> maps = new List<MapDefinition>();
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in document.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                XmlElement item = (XmlElement)node;
                if (item.Name != "map")
                    throw new InvalidDataException("地图目录包含未知节点：" + item.Name);
                string id = Required(item, "id");
                if (!Regex.IsMatch(id, @"\A[a-zA-Z0-9][a-zA-Z0-9_-]*\z") || !ids.Add(id))
                    throw new InvalidDataException("地图标识无效或重复：" + id);

                string heightmap = item.GetAttribute("heightmap").Trim();
                double heightScale = Number(item, "heightScale", heightmap.Length != 0);
                if (heightScale < 0)
                    throw new InvalidDataException("地图 " + id + " 高程比例不得为负数。");
                maps.Add(new MapDefinition(id, Required(item, "name"), Required(item, "displayName"),
                    Number(item, "width", true), Number(item, "height", true),
                    AssetPath(root, Required(item, "image")),
                    heightmap.Length == 0 ? String.Empty : AssetPath(root, heightmap),
                    heightScale, Number(item, "heightOffset", false), document.DocumentElement.GetAttribute("sourceCommit")));
            }
            if (maps.Count == 0)
                throw new InvalidDataException("离线地图目录中没有地图。");
            return maps.AsReadOnly();
        }

        private static string Required(XmlElement item, string attribute)
        {
            string value = item.GetAttribute(attribute).Trim();
            if (value.Length == 0)
                throw new InvalidDataException("地图 " + item.GetAttribute("id") + " 缺少字段：" + attribute);
            return value;
        }

        private static double Number(XmlElement item, string attribute, bool positive)
        {
            double value;
            if (!Double.TryParse(Required(item, attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                Double.IsNaN(value) || Double.IsInfinity(value) || (positive && value <= 0))
                throw new InvalidDataException("地图 " + item.GetAttribute("id") + " 数值无效：" + attribute);
            return value;
        }

        private static string AssetPath(string directory, string relative)
        {
            // Treat both slash styles as separators even on a different host. Colons
            // also disallow drive paths, URI schemes and Windows alternate streams.
            string normal = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normal) || relative.IndexOf(':') >= 0)
                throw new InvalidDataException("地图资源必须使用目录内的相对路径：" + relative);
            string[] parts = normal.Split(Path.DirectorySeparatorChar);
            foreach (string part in parts)
                if (part == ".." || part == "." || String.IsNullOrWhiteSpace(part))
                    throw new InvalidDataException("地图资源路径无效：" + relative);
            string root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, normal));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("地图资源超出地图目录：" + relative);
            if (!File.Exists(full))
                throw new FileNotFoundException("缺少离线地图资源：" + relative + "。请完整解压软件。", full);
            return full;
        }
    }

    /// <summary>Raw PNG red/blue terrain encoding, without colour correction.</summary>
    public sealed class TerrainHeightMap
    {
        private readonly MapDefinition map;
        private readonly byte[] pixels;
        private readonly int pixelWidth;
        private readonly int pixelHeight;
        private readonly int stride;

        private TerrainHeightMap(MapDefinition definition, BitmapSource source)
        {
            map = definition;
            pixelWidth = source.PixelWidth;
            pixelHeight = source.PixelHeight;
            stride = checked(pixelWidth * 4);
            pixels = new byte[checked(stride * pixelHeight)];
            source.CopyPixels(pixels, stride, 0);
        }

        public static TerrainHeightMap Load(MapDefinition map)
        {
            if (map == null) throw new ArgumentNullException("map");
            if (!File.Exists(map.HeightmapPath))
                throw new FileNotFoundException("缺少地图高程，无法自动计算高差：" + map.DisplayName, map.HeightmapPath);
            using (FileStream stream = File.OpenRead(map.HeightmapPath))
            {
                // Preserve raw channel values used by SquadCalc; do not use a
                // colour-managed BitmapImage or premultiplied-alpha conversion.
                PngBitmapDecoder decoder = new PngBitmapDecoder(stream,
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth < 1 || decoder.Frames[0].PixelHeight < 1)
                    throw new InvalidDataException("地图高程 PNG 无效：" + map.DisplayName);
                BitmapSource source = decoder.Frames[0];
                if (source.Format != PixelFormats.Bgra32)
                    source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                return new TerrainHeightMap(map, source);
            }
        }

        public bool Contains(Vec2 position)
        {
            return map.Contains(position);
        }

        public double Sample(Vec2 position)
        {
            if (!Contains(position))
                throw new ArgumentOutOfRangeException("position", "坐标超出当前地图，无法读取地面高度。");
            // Match upstream Math.round for non-negative coordinates. PNG samples
            // span width/worldSize, not (width-1)/worldSize. Clamp the final half
            // pixel and the exact outer boundary to the nearest valid pixel.
            int column = Math.Min(pixelWidth - 1, (int)Math.Floor(position.X / map.Width * pixelWidth + 0.5));
            int row = Math.Min(pixelHeight - 1, (int)Math.Floor(position.Y / map.Height * pixelHeight + 0.5));
            int index = row * stride + column * 4;
            double raw = 255 + pixels[index + 2] - pixels[index];
            double height = raw * map.HeightScale + map.HeightOffset;
            if (Double.IsNaN(height) || Double.IsInfinity(height))
                throw new InvalidDataException("地图高程换算超出数值范围：" + map.DisplayName);
            return height;
        }
    }
}
