using System;
using System.Collections.Generic;

using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SquadTools;

internal static class ServerTests
{
    private static int assertions;
    private static readonly string A = new string('a',32), B = new string('b',32), C = new string('c',32);
    private static readonly DateTime Now = new DateTime(2026,10,25,5,0,0,DateTimeKind.Utc);
    private static void Check(bool ok, string message) { assertions++; if (!ok) throw new Exception(message); }
    private static void Throws<T>(Action action, string label) where T:Exception { try { action(); } catch(T) { assertions++; return; } throw new Exception("Expected " + typeof(T).Name + ": " + label); }
    private static string Row(string id, int port, object count)
    {
        return ServerData.Serializer().Serialize(new { id=id, totalPlayers=73, settings=new {maxPublicPlayers=100}, attributes=new Dictionary<string,object> {
            {"SERVERNAME_s", "中文服务器 " + id[0]}, {"ADDRESS_s", "203.0.113.5"}, {"ADDRESSBOUND_s", "0.0.0.0:" + port}, {"PLAYERCOUNT_l",count}, {"PUBLICQUEUE_l",4}, {"RESERVEDQUEUE_l",1}, {"LICENSEDSERVER_b",true}, {"GAMEVERSION_s","v10.6.3.694944.3212"}, {"MAPNAME_s","BlackCoast_RAAS_v1"}
        } });
    }
    private static string List(params string[] rows) { return "{\"sessions\":[" + string.Join(",",rows) + "],\"count\":" + rows.Length + "}"; }
    public static int Main()
    {
        try { TestParsing(); TestFilters(); TestRosters(); TestEvidence(); TestSessionIds(); TestTransport().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + assertions + " server assertions (parsing, incomplete results, dedup, conservative counts, time windows, filtering, protocol transport and session validation). No live services or Steam used."); return 0; }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void TestParsing()
    {
        var result=ServerData.ParseServers(List(Row(A,7787,80),Row(A,7787,81),Row(B,7788,0),Row(C,7787,55)),Now,1000);
        Check(result.Servers.Count==3 && result.RepeatedRows==1,"session dedup");
        var one=result.Servers.Single(s=>s.SessionId==A);
        Check(one.Reported==81 && one.Registered==73,"different count sources preserved");
        Check(one.Name=="中文服务器 a" && one.Endpoint=="203.0.113.5:7787","utf8/address parse");
        Check(one.Queue==4 && one.ReservedQueue==1 && one.Capacity==100,"queue and capacity");
        Check(one.PlayersText=="81","reported population is not presented as a fraction of public-only capacity");
        Check(one.DuplicateCount==2 && result.Servers.Single(s=>s.SessionId==B).DuplicateCount==1,"same IP different ports");
        Check(!result.Partial,"complete list");
        Check(ServerData.ParseServers(List(Row(A,7787,80)),Now,1).Partial,"cap marks partial");
        Check(ServerData.ParseServers("{\"count\":7,\"sessions\":["+Row(A,7787,80)+"]}",Now,1000).Partial,"reported total larger than records");
        Check(ServerData.ParseServers(List(Row("invalid",7787,80)),Now,1000).Partial,"discarded record visible as partial");
        Check(ServerData.ParseServers(List(Row(A,99999,-1)),Now,1000).Servers[0].Endpoint=="","invalid port excluded");
        Check(ServerData.ParseServers(List(Row(A,7787,null)),Now,1000).Servers[0].Reported==null,"missing count stays unknown");
        Check(ServerData.ParseServers(List(Row(A,7787,-1)),Now,1000).Servers[0].Reported==null,"negative count stays unknown");
        Check(ServerData.ParseServers(List(Row(A,7787,"NaN")),Now,1000).Servers[0].Reported==null,"invalid count stays unknown");
        Throws<FormatException>(()=>ServerData.ParseServers("{}",Now,1000),"missing sessions");
        Check(ServerData.ParseServers(List(),Now,1000).Servers.Count==0,"empty response");
        foreach (string bad in new[]{"0.0.0.0","::","127.0.0.1","::1","::ffff:127.0.0.1","10.0.0.1","172.16.0.1","192.168.1.2","169.254.1.1","100.64.0.1","224.0.0.1","ff02::1","fc00::1","fe80::1","not-an-address"}) Check(ServerData.PublicAddress(bad)=="","nonpublic placeholder cannot define server identity");
        Check(ServerData.PublicAddress("::ffff:203.0.113.5")=="203.0.113.5","mapped IPv4 normalized");
        var placeholders=ServerData.ParseServers(List(Row(A,7787,80).Replace("203.0.113.5","0.0.0.0"),Row(B,7787,70).Replace("203.0.113.5","0.0.0.0")),Now,1000);
        Check(ServerData.Filter(placeholders.Servers,"","全部",false,false,true,false).Count==2,"different wildcard-address sessions must not merge");
        string dated = Row(A,7787,90).Replace("\"totalPlayers\"", "\"lastUpdated\":\"2026-10-25T04:59:00Z\",\"totalPlayers\"");
        foreach (string untrusted in new[] { Row(C,7787,99), Row(C,7787,99).Replace("\"totalPlayers\"", "\"lastUpdated\":\"invalid\",\"totalPlayers\""), Row(C,7787,99).Replace("\"totalPlayers\"", "\"lastUpdated\":\"2099-01-01T00:00:00Z\",\"totalPlayers\"") }) {
            var ranked = ServerData.ParseServers(List(dated, untrusted), Now, 1000);
            Check(ServerData.Filter(ranked.Servers,"","全部",false,false,true,false)[0].SessionId==A,"unknown or future update time must not outrank known timestamp");
            Check(ServerData.ParseServers(List(dated, untrusted.Replace(C,A)),Now,1000).Servers[0].Reported==90,"same-session duplicate with untrusted timestamp cannot replace dated record");
        }
    }
    private static void TestFilters()
    {
        var rows=ServerData.ParseServers(List(Row(A,7787,90),Row(B,7788,0),Row(C,7787,null)),Now,1000).Servers;
        Check(ServerData.Filter(rows,"","全部",false,false,true,false).Count==2,"fold endpoint alias");
        Check(ServerData.Filter(rows,"","全部",false,false,false,false).Count==3,"reveal aliases");
        Check(ServerData.Filter(rows,"","全部",false,true,false,false).Count==2,"hide empty retains unknown");
        Check(ServerData.Filter(rows," BLACKCOAST ","许可服",false,false,false,false).Count==3,"case insensitive map search");
        Check(ServerData.Filter(rows,"not present","全部",false,false,false,false).Count==0,"empty search");
        Check(ServerData.Filter(rows,"","自定义",false,false,false,false).Count==0,"kind filter");
        rows[0].Favorite=true; Check(ServerData.Filter(rows,"","全部",true,false,false,false).Count==1,"favorite filter");
        rows[0].PersistentMismatch=true; Check(ServerData.Filter(rows,"","全部",false,false,false,true).Count==2,"optional mismatch hiding");
    }
    private static string Presence(string target,string user)
    {
        return ServerData.Serializer().Serialize(new {totalPlayers=16,publicPlayers=new[]{"ignored-party-member"},attributes=new Dictionary<string,object>{{"ADVERTISEDSESSIONID_s","Session:"+target},{"USERID_s",user},{"ADVERTISEDSESSIONCURRENTMEMBERCOUNT_l",100}}});
    }
    private static void TestRosters()
    {
        var json=List(Presence(A,B),Presence(A,B),Presence(A,C),Presence(B,A));
        var counts=ServerData.ParseRosters(json,new[]{A,B,C},1000);
        Check(counts[A].Count==2 && counts[B].Count==1,"unique USERID only, not publicPlayers or advertised totals");
        Check(counts[C].Count==0 && counts[C].Complete,"no matching roster distinguished by caller");
        Check(!ServerData.ParseRosters(json,new[]{A,B},4)[A].Complete,"roster cap");
        Check(!ServerData.ParseRosters(json,new[]{A},1000)[A].Complete,"unexpected target makes incomplete");
        Check(!ServerData.ParseRosters(List(Presence(A,"")),new[]{A},1000)[A].Complete,"missing member ID");
        Throws<FormatException>(()=>ServerData.ParseRosters("{}",new[]{A},1000),"failed result not zero");
    }
    private static void TestEvidence()
    {
        var tracker=new CountEvidence(); var server=new ServerRecord{SessionId=A,Reported=100};
        tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now);
        Check(server.Observed==60 && !server.PersistentMismatch,"first discrepancy is not persistent");
        tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now.AddSeconds(5));
        Check(!server.PersistentMismatch,"rapid double poll not independent");
        tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now.AddSeconds(30));
        Check(server.PersistentMismatch,"two spaced discrepancies");
        tracker.Apply(server,new RosterCount{Count=94,Complete=true},Now.AddSeconds(60));
        Check(!server.PersistentMismatch,"small lag clears anomaly");
        tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now.AddSeconds(90));
        Check(!server.PersistentMismatch,"streak restarted after recovery");
        tracker.Apply(server,new RosterCount{Count=0,Complete=true},Now.AddSeconds(120));
        Check(server.Observed==null && !server.PersistentMismatch,"empty visibility is unknown, not zero");
        tracker.Apply(server,new RosterCount{Count=1,Complete=false},Now.AddSeconds(150));
        Check(server.Observed==null && !server.PersistentMismatch,"truncation never hides server");
        tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now.AddSeconds(180));
        tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now.AddSeconds(400));
        Check(!server.PersistentMismatch,"expired samples not combined");
        tracker.Prune(new[]{B}); tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now.AddSeconds(430));
        Check(!server.PersistentMismatch,"pruned session does not inherit old evidence");
        server.Reported=null; tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now.AddSeconds(460));
        Check(!server.PersistentMismatch && server.CheckState.Contains("无法比较"),"unknown report cannot claim a matching count");
        server.Reported=100; tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now.AddSeconds(490));
        server.Reported=90; tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now.AddSeconds(520));
        Check(!server.PersistentMismatch,"a changed report restarts discrepancy evidence");
        tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now.AddSeconds(550));
        Check(server.PersistentMismatch,"two samples with unchanged report can establish mismatch");
        tracker.Reset(A); tracker.Apply(server,new RosterCount{Count=60,Complete=true},Now.AddSeconds(580));
        Check(!server.PersistentMismatch,"explicit invalidation removes old evidence");
    }
    private static void TestSessionIds()
    {
        foreach(string value in new[]{null,"",A+"\n",A+" & x","../../x",new string('g',32)}) Check(!ServerData.ValidSession(value),"invalid session input");
        Check(ServerData.ValidSession(A),"valid session input");
    }
    private sealed class MockHttp : HttpMessageHandler
    {
        public int AuthCount, Queries; public bool Deny, Limit;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); string path=request.RequestUri.AbsolutePath;
            Check(request.RequestUri.Scheme=="https" && request.RequestUri.Host=="api.epicgames.dev","fixed HTTPS origin");
            string body=await request.Content.ReadAsStringAsync();
            if(path.EndsWith("/deviceid")) { AuthCount++; Check(request.Headers.Authorization.Scheme=="Basic","device auth scheme"); return Json("{\"access_token\":\"test-device\"}"); }
            if(path.EndsWith("/token")) { Check(body.Contains("test-device") && body.Contains("deviceid_access_token"),"token exchange"); return Json("{\"access_token\":\"test-access\",\"expires_in\":3600}"); }
            Queries++; Check(request.Headers.Authorization.Parameter=="test-access","bearer token only in header");
            if(Deny) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if(Limit) return new HttpResponseMessage((HttpStatusCode)429);
            if(path.Contains("/matchmaking/")) return Json(List(Row(A,7787,80)));
            Check(body.Contains("ANY_OF") && body.Contains("Session:"+A),"targeted lobby query");
            return Json(List(Presence(A,B)));
        }
        private static HttpResponseMessage Json(string text) { return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Encoding.UTF8.GetBytes(text))}; }
    }
    private static async Task TestTransport()
    {
        var mock=new MockHttp();
        using(var client=new EosClient(mock)) {
            var first=await client.GetServers(CancellationToken.None); Check(first.Servers[0].Name=="中文服务器 a","UTF8 without HTTP charset");
            await client.GetServers(CancellationToken.None); Check(mock.AuthCount==1,"reuse token");
            var counts=await client.GetRosterCounts(new[]{A},CancellationToken.None); Check(counts[A].Count==1,"lobby integration");
            mock.Deny=true; try { await client.GetServers(CancellationToken.None); throw new Exception("401 accepted"); } catch(IOException) { assertions++; }
            mock.Deny=false; await client.GetServers(CancellationToken.None); Check(mock.AuthCount==2,"expired/rejected token renewed on next request");
            mock.Limit=true; try { await client.GetServers(CancellationToken.None); throw new Exception("429 accepted"); } catch(IOException ex) { Check(ex.Message.Contains("一分钟"),"rate limit explained"); }
            var cts=new CancellationTokenSource(); cts.Cancel();
            try { await client.GetServers(cts.Token); throw new Exception("cancellation ignored"); } catch(OperationCanceledException) { assertions++; }
            try { await client.GetRosterCounts(new[]{"bad"},CancellationToken.None); throw new Exception("bad id accepted"); } catch(ArgumentException) { assertions++; }
        }
    }
}
