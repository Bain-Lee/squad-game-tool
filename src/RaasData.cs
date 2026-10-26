using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SquadTools
{
    public sealed class RaasPoint
    {
        public string Id, Name;
        public Vec2 Position;
        public List<string> CandidateIds = new List<string>();
        public bool IsMain;
        public override string ToString() { return Name; }
    }
    public sealed class RaasCluster { public string Id; public List<string> Ids = new List<string>(); }
    public sealed class RaasLink { public string From, To; }
    public sealed class RaasLayer
    {
        public string MapId, RawName, SourceCommit, Team1, Team2;
        public List<RaasPoint> Mains = new List<RaasPoint>(), Flags = new List<RaasPoint>();
        public List<RaasCluster> Clusters = new List<RaasCluster>();
        public List<RaasLink> Links = new List<RaasLink>();
    }
    public sealed class RaasLayerInfo
    {
        public string RawName, Name;
        public override string ToString() { return Name; }
    }
    public sealed class RaasMapInfo
    {
        public string Id, Name, Note;
        public List<RaasLayerInfo> Layers = new List<RaasLayerInfo>();
        public override string ToString() { return Name; }
    }
    public sealed class RaasRepository
    {
        private readonly string directory;
        public readonly List<RaasMapInfo> Maps = new List<RaasMapInfo>();
        public string SourceCommit { get; private set; }
        public string Updated { get; private set; }
        public RaasRepository(string root)
        {
            directory = Path.GetFullPath(root);
            var data = Read(Path.Combine(directory,"catalog.json"));
            Schema(data); SourceCommit = ServerData.Text(data,"sourceCommit"); Updated = ServerData.Text(data,"fetchedAt");
            if(!Regex.IsMatch(SourceCommit,"\\A[a-f0-9]{40}\\z")) throw new FormatException("RAAS 底图来源版本无效。");
            var mapIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Rows(data,"maps")) {
                var map = new RaasMapInfo { Id = Text(item,"id"), Name = Text(item,"name"), Note = ServerData.Text(item,"note") };
                if (!Identifier(map.Id) || !mapIds.Add(map.Id)) throw new FormatException("RAAS 地图标识无效或重复。");
                foreach (var entry in Rows(item,"layers")) {
                    var layer = new RaasLayerInfo { RawName = Text(entry,"rawName"), Name = Text(entry,"name") };
                    if (!Identifier(layer.RawName) || !layerIds.Add(layer.RawName)) throw new FormatException("RAAS 图层标识无效或重复。");
                    map.Layers.Add(layer);
                }
                Maps.Add(map);
            }
            if (Maps.Count == 0) throw new FormatException("RAAS 地图目录为空。");
        }
        public RaasMapInfo FindMap(string id) { return Maps.Find(m => string.Equals(m.Id,id,StringComparison.OrdinalIgnoreCase)); }
        public RaasLayer Load(string mapId, string rawName)
        {
            var map = FindMap(mapId);
            if (map == null || !map.Layers.Any(l => string.Equals(l.RawName,rawName,StringComparison.OrdinalIgnoreCase))) throw new FormatException("未收录此 RAAS 图层。");
            rawName = map.Layers.First(l => string.Equals(l.RawName,rawName,StringComparison.OrdinalIgnoreCase)).RawName;
            var data = Read(Path.Combine(directory,rawName+".json")); Schema(data);
            var layer = new RaasLayer { MapId = Text(data,"mapId"), RawName = Text(data,"rawName"), SourceCommit = Text(data,"sourceCommit") };
            layer.Team1=FactionCatalog.Id(ServerData.Text(data,"team1")); layer.Team2=FactionCatalog.Id(ServerData.Text(data,"team2"));
            if (layer.MapId != map.Id || layer.RawName != rawName || layer.SourceCommit != SourceCommit || !new[] {"RAAS","RVAAS"}.Contains(Text(data,"mode"))) throw new FormatException("RAAS 图层与目录不一致。");
            var nodes = new HashSet<string>(StringComparer.Ordinal); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Rows(data,"mains")) {
                var point = Point(entry); point.IsMain = true;
                if (!nodes.Add(point.Id)) throw new FormatException("RAAS 主基地标识重复。");
                layer.Mains.Add(point);
            }
            foreach (var entry in Rows(data,"clusters")) {
                var cluster = new RaasCluster { Id = Text(entry,"id") };
                if (!nodes.Add(cluster.Id)) throw new FormatException("RAAS 点簇标识重复。");
                foreach (var item in Rows(entry,"points")) {
                    var point = Point(item);
                    if (!ids.Add(point.Id)) throw new FormatException("RAAS 候选标识重复。");
                    cluster.Ids.Add(point.Id);
                    var flag = layer.Flags.Find(f => Squared(f.Position,point.Position) < 9);
                    if (flag == null) { flag = point; layer.Flags.Add(flag); }
                    flag.CandidateIds.Add(point.Id);
                }
                if (cluster.Ids.Count == 0) throw new FormatException("RAAS 点簇没有候选点。");
                layer.Clusters.Add(cluster);
            }
            var links = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Rows(data,"links")) {
                var link = new RaasLink { From = Text(entry,"from"), To = Text(entry,"to") };
                if (!nodes.Contains(link.From) || !nodes.Contains(link.To) || link.From == link.To || !links.Add(link.From+"\n"+link.To)) throw new FormatException("RAAS 路线链接无效。");
                layer.Links.Add(link);
            }
            if (layer.Mains.Count != 2 || !layer.Flags.Any() || layer.Flags.Count > 1000 || layer.Clusters.Count > 1000 || layer.Links.Count > 5000) throw new FormatException("RAAS 图层不完整或超出限制。");
            return layer;
        }
        private static RaasPoint Point(Dictionary<string,object> data)
        {
            double x, y;
            if (!double.TryParse(ServerData.Text(data,"x"),NumberStyles.Float,CultureInfo.InvariantCulture,out x) || !double.TryParse(ServerData.Text(data,"y"),NumberStyles.Float,CultureInfo.InvariantCulture,out y) || double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y) || Math.Abs(x)>100000 || Math.Abs(y)>100000) throw new FormatException("RAAS 点位坐标无效。");
            return new RaasPoint { Id = Text(data,"id"), Name = Text(data,"name"), Position = new Vec2(x,y) };
        }
        private static Dictionary<string,object> Read(string file)
        {
            if (!File.Exists(file)) throw new IOException("找不到 RAAS 数据，请保留程序旁的 layers 文件夹。");
            if (new FileInfo(file).Length > 4*1024*1024) throw new FormatException("RAAS 数据文件过大。");
            return ServerData.Object(ServerData.Serializer().DeserializeObject(File.ReadAllText(file)));
        }
        private static void Schema(Dictionary<string,object> data) { if (ServerData.Count(data,"schema") != 1) throw new FormatException("不支持此 RAAS 数据版本。"); }
        private static bool Identifier(string id) { return Regex.IsMatch(id,"\\A[A-Za-z0-9_-]{1,150}\\z"); }
        private static string Text(Dictionary<string,object> data,string key)
        {
            string value = ServerData.Text(data,key);
            if (string.IsNullOrWhiteSpace(value) || value.Length > 300 || value.Any(char.IsControl)) throw new FormatException("RAAS 字段无效："+key);
            return value;
        }
        private static IEnumerable<Dictionary<string,object>> Rows(Dictionary<string,object> data,string key)
        {
            var values = ServerData.Get(data,key) as object[];
            if (values == null || values.Length > 5000) throw new FormatException("RAAS 列表无效："+key);
            return values.Select(ServerData.Object);
        }
        private static double Squared(Vec2 a,Vec2 b) { return (a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y); }
    }
}
