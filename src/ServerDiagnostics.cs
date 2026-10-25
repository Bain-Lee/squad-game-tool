using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SquadTools
{
    public static class ServerDiagnostics
    {
        public static int Probe(string path)
        {
            path = Path.GetFullPath(path);
            try {
                using (var client = new EosClient()) using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55))) {
                    var snapshot = client.GetServers(timeout.Token).GetAwaiter().GetResult();
                    var selected = snapshot.Servers.Where(s => s.Reported.GetValueOrDefault() > 0).OrderByDescending(s => s.Reported).Take(10).ToList();
                    var evidence = new CountEvidence();
                    for (int i = 0; i < selected.Count; i += 5) {
                        var batch = selected.Skip(i).Take(5).ToList(); var counts = client.GetRosterCounts(batch.Select(s => s.SessionId), timeout.Token).GetAwaiter().GetResult();
                        foreach (var server in batch) evidence.Apply(server, counts[server.SessionId], DateTime.UtcNow);
                    }
                    File.WriteAllText(path, ServerData.Serializer().Serialize(snapshot), new UTF8Encoding(false));
                    File.WriteAllText(Path.ChangeExtension(path, ".txt"), "EOS live query: " + snapshot.Servers.Count + " records; partial=" + snapshot.Partial + "; checked=" + selected.Count + "; observed=" + selected.Count(s => s.Observed.HasValue) + ". No tokens or player identifiers persisted. Join not executed.\r\n", Encoding.UTF8);
                    return 0;
                }
            } catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(path, ".txt"), "Query failed: " + ex.GetType().Name + ". No credentials saved.\r\n", Encoding.UTF8); return 1; }
        }
        public static ServerSnapshot SmokeSample(string path)
        {
            if (path != null) return ServerData.Serializer().Deserialize<ServerSnapshot>(File.ReadAllText(path, Encoding.UTF8));
            return new ServerSnapshot { RetrievedAt = DateTime.MinValue, Servers = new List<ServerRecord> {
                new ServerRecord { SessionId = new string('a',32), Name = "离线测试样本 · 非真实服务器", Map = "Yehorivka_RAAS_v1", Mode = "RAAS", Address = "192.0.2.1", Port = 7787, Reported = 80, Capacity = 100, Registered = 79, Queue = 0, ReservedQueue = 0, Licensed = true, Version = "v10.6.3.694944.3212", CheckState = "测试数据", DuplicateCount = 1 },
                new ServerRecord { SessionId = new string('b',32), Name = "离线空服样本 · 非真实服务器", Map = "JensensRange", Address = "192.0.2.2", Port = 7787, Reported = 0, Capacity = 100, DuplicateCount = 1 }
            } };
        }
    }
}
