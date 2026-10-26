using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SquadTools
{
    public sealed class ServerRecord
    {
        public string SessionId { get; set; }
        public string Name { get; set; }
        public string Map { get; set; }
        public string Mode { get; set; }
        public string TeamOne { get; set; }
        public string TeamTwo { get; set; }
        public string Team1Faction { get { return FactionCatalog.Id(TeamOne); } }
        public string Team2Faction { get { return FactionCatalog.Id(TeamTwo); } }
        public string TeamsText { get { return FactionCatalog.Name(TeamOne) + " vs " + FactionCatalog.Name(TeamTwo); } }
        public string Version { get; set; }
        public string Address { get; set; }
        public int Port { get; set; }
        public bool Licensed { get; set; }
        public bool Coop { get; set; }
        public bool Password { get; set; }
        public int? Reported { get; set; }
        public int? Registered { get; set; }
        public int? Capacity { get; set; }
        public int? Queue { get; set; }
        public int? ReservedQueue { get; set; }
        public int? Observed { get; set; }
        public DateTime ObservedAt { get; set; }
        public DateTime RetrievedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string CheckState { get; set; }
        public bool PersistentMismatch { get; set; }
        public int DuplicateCount { get; set; }
        public bool Favorite { get; set; }
        public string FavoriteText { get { return Favorite ? "★" : "☆"; } }
        public string Kind { get { return Coop ? "Fireteam" : Licensed ? "许可服" : "自定义"; } }
        public string Endpoint { get { return string.IsNullOrEmpty(Address) || Port < 1 ? "" : (Address.Contains(":") ? "[" + Address + "]" : Address) + ":" + Port.ToString(CultureInfo.InvariantCulture); } }
        public string FavoriteKey { get { return string.IsNullOrEmpty(Endpoint) ? "session:" + SessionId : Endpoint; } }
        public string PlayersText { get { return Value(Reported); } }
        public string RegisteredText { get { return Value(Registered); } }
        public string QueueText { get { return Value(Queue) + " / " + Value(ReservedQueue); } }
        public string ObservedText { get { return Value(Observed); } }
        public string StatusText { get { return (CheckState ?? "待核验") + (DuplicateCount > 1 ? " · 同入口×" + DuplicateCount : ""); } }
        public string SearchText { get { return (Name + " " + Map + " " + Mode + " " + Address).ToLowerInvariant(); } }
        public static string Value(int? value) { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "—"; }
    }

    public sealed class ServerSnapshot
    {
        public List<ServerRecord> Servers = new List<ServerRecord>();
        public bool Partial;
        public int RepeatedRows;
        public DateTime RetrievedAt;
    }

    public sealed class RosterCount
    {
        public int Count;
        public bool Complete;
    }

    public static class ServerData
    {
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 50 }; }
        public static Dictionary<string, object> Object(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static object Get(Dictionary<string, object> obj, string key) { object value; return obj.TryGetValue(key, out value) ? value : null; }
        public static string Text(Dictionary<string, object> obj, string key) { return Convert.ToString(Get(obj, key), CultureInfo.InvariantCulture) ?? ""; }
        public static int? Count(Dictionary<string, object> obj, string key)
        {
            int value; return int.TryParse(Text(obj, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0 && value <= 1000000 ? (int?)value : null;
        }
        public static bool Flag(Dictionary<string, object> obj, string key) { bool value; return bool.TryParse(Text(obj, key), out value) && value; }
        public static bool ValidSession(string id) { return id != null && Regex.IsMatch(id, "\\A[0-9a-fA-F]{32}\\z"); }
        public static string PublicAddress(string address)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(address, out ip) || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return "";
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4) {
                if (bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 || bytes[0] >= 224 ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168) ||
                    (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)) return "";
            } else if (ip.IsIPv6Multicast || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (bytes[0] & 0xfe) == 0xfc) return "";
            return ip.ToString();
        }
        private static string Clean(string value, int max) { return new string((value ?? "").Where(c => !char.IsControl(c)).Take(max).ToArray()); }
        public static ServerSnapshot ParseServers(string json, DateTime now, int limit)
        {
            var root = Object(Serializer().DeserializeObject(json));
            var rows = Get(root, "sessions") as object[];
            if (rows == null) throw new FormatException("服务器响应缺少列表，请稍后重试。");
            var snapshot = new ServerSnapshot { RetrievedAt = now, Partial = rows.Length >= limit || (Count(root, "count") ?? rows.Length) > rows.Length };
            var byId = new Dictionary<string, ServerRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in rows)
            {
                var row = Object(raw); var attr = Object(Get(row, "attributes")); var settings = Object(Get(row, "settings"));
                string id = Text(row, "id").ToLowerInvariant();
                if (!ValidSession(id) || string.IsNullOrWhiteSpace(Text(attr, "SERVERNAME_s"))) { snapshot.Partial = true; continue; }
                string address = PublicAddress(Text(attr, "ADDRESS_s"));
                int port = Count(attr, "GAMESERVER_PORT_l") ?? 0;
                if (port == 0) { string bound = Text(attr, "ADDRESSBOUND_s"); int index = bound.LastIndexOf(':'); if (index >= 0) int.TryParse(bound.Substring(index + 1), out port); }
                if (port < 1 || port > 65535) port = 0;
                DateTime updated;
                if (!DateTime.TryParse(Text(row, "lastUpdated"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out updated) || updated > now) updated = DateTime.MinValue;
                var server = new ServerRecord {
                    SessionId = id, Name = Clean(Text(attr, "SERVERNAME_s"), 500), Map = Clean(Text(attr, "MAPNAME_s"), 180), Mode = Clean(Text(attr, "GAMEMODE_s"), 80),
                    TeamOne = Clean(Text(attr,"TEAMONE_s"),180), TeamTwo = Clean(Text(attr,"TEAMTWO_s"),180),
                    Version = Clean(Text(attr, "GAMEVERSION_s"), 80), Address = address, Port = port, Licensed = Flag(attr, "LICENSEDSERVER_b"), Coop = Flag(attr, "COOPSERVER_b"), Password = Flag(attr, "PASSWORD_b"),
                    Reported = Count(attr, "PLAYERCOUNT_l"), Registered = Count(row, "totalPlayers"), Capacity = Count(settings, "maxPublicPlayers"), Queue = Count(attr, "PUBLICQUEUE_l"), ReservedQueue = Count(attr, "RESERVEDQUEUE_l"),
                    RetrievedAt = now, UpdatedAt = updated, DuplicateCount = 1
                };
                if (byId.ContainsKey(id)) { snapshot.RepeatedRows++; if (byId[id].UpdatedAt > server.UpdatedAt) continue; }
                byId[id] = server;
            }
            snapshot.Servers = byId.Values.ToList();
            foreach (var group in snapshot.Servers.Where(s => s.Endpoint != "").GroupBy(s => s.Endpoint)) foreach (var server in group) server.DuplicateCount = group.Count();
            return snapshot;
        }

        public static Dictionary<string, RosterCount> ParseRosters(string json, IEnumerable<string> requested, int limit)
        {
            var root = Object(Serializer().DeserializeObject(json)); var rows = Get(root, "sessions") as object[];
            if (rows == null) throw new FormatException("人数核验响应不完整。");
            var ids = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
            var members = ids.ToDictionary(id => id, id => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            bool complete = rows.Length < limit && (Count(root, "count") ?? rows.Length) <= rows.Length;
            foreach (var raw in rows)
            {
                var row = Object(raw); var attr = Object(Get(row, "attributes")); string target = Text(attr, "ADVERTISEDSESSIONID_s");
                if (!target.StartsWith("Session:", StringComparison.Ordinal)) { complete = false; continue; }
                target = target.Substring(8);
                if (!ids.Contains(target)) { complete = false; continue; }
                string member = Text(attr, "USERID_s");
                // Count the advertised user's presence, never the party's membership or reported count.
                if (!ValidSession(member)) { complete = false; continue; }
                members[target].Add(member);
            }
            return members.ToDictionary(x => x.Key, x => new RosterCount { Count = x.Value.Count, Complete = complete }, StringComparer.OrdinalIgnoreCase);
        }

        public static List<ServerRecord> Filter(IEnumerable<ServerRecord> source, string search, string kind, bool favorites, bool populated, bool fold, bool hideMismatch)
        {
            IEnumerable<ServerRecord> rows = source;
            string query = (search ?? "").Trim().ToLowerInvariant();
            if (query.Length > 0) rows = rows.Where(s => s.SearchText.Contains(query));
            if (kind != "全部") rows = rows.Where(s => s.Kind == kind);
            if (favorites) rows = rows.Where(s => s.Favorite);
            if (populated) rows = rows.Where(s => !s.Reported.HasValue || s.Reported.Value > 0);
            if (hideMismatch) rows = rows.Where(s => !s.PersistentMismatch);
            if (fold) rows = rows.GroupBy(s => s.Endpoint == "" ? "session:" + s.SessionId : s.Endpoint).Select(g => g.OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.SessionId, StringComparer.Ordinal).First());
            return rows.OrderByDescending(s => s.Reported ?? -1).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public sealed class CountEvidence
    {
        private sealed class Sample { public DateTime Time; public int Streak; public int Reported; }
        private readonly Dictionary<string, Sample> history = new Dictionary<string, Sample>();
        public void Apply(ServerRecord server, RosterCount roster, DateTime now)
        {
            server.ObservedAt = now; server.PersistentMismatch = false;
            // A successful empty search may still mean hidden/unavailable presence, not an empty game server.
            if (!roster.Complete || roster.Count == 0) { server.Observed = null; server.CheckState = roster.Complete ? "未取得可见成员" : "核验结果不完整"; history.Remove(server.SessionId); return; }
            server.Observed = roster.Count;
            if (!server.Reported.HasValue) { history.Remove(server.SessionId); server.CheckState = "缺少上报人数 · 无法比较"; return; }
            bool mismatch = server.Reported.HasValue && server.Reported.Value - roster.Count >= Math.Max(10, (int)Math.Ceiling(server.Reported.Value * 0.2));
            if (!mismatch) { history.Remove(server.SessionId); server.CheckState = "未见明显差异"; return; }
            Sample previous; int streak = 1;
            if (history.TryGetValue(server.SessionId, out previous) && previous.Reported == server.Reported.Value) {
                var age = (now - previous.Time).TotalSeconds;
                if (age >= 20 && age <= 180) streak = previous.Streak + 1;
                else if (age >= 0 && age < 20) { server.PersistentMismatch = previous.Streak >= 2; server.CheckState = server.PersistentMismatch ? "持续人数差异" : "人数有差异 · 待复核"; return; }
            }
            history[server.SessionId] = new Sample { Time = now, Streak = streak, Reported = server.Reported.Value };
            server.PersistentMismatch = streak >= 2;
            server.CheckState = server.PersistentMismatch ? "持续人数差异" : "人数有差异 · 待复核";
        }
        public void Reset(string sessionId) { history.Remove(sessionId); }
        public void Prune(IEnumerable<string> active) { var keep = new HashSet<string>(active); foreach (var id in history.Keys.Where(k => !keep.Contains(k)).ToArray()) history.Remove(id); }
    }
}
