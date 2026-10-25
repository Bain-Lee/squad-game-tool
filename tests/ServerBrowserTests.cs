using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using SquadTools;

internal static class ServerBrowserTests
{
    private static int assertions;
    private static readonly string A = new string('a', 32), B = new string('b', 32);
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
    private static object Call(object target, string name) { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null); }
    private static async Task WaitForRequest(Harness h)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.Http.Pending == null && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(h.Http.Pending != null, "refresh is actually pending");
    }

    [STAThread]
    public static int Main()
    {
        int exitCode = 0;
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(new Action(async delegate {
            try {
                await TestEvidenceAndSorting(); await TestReentryAndDispose(); await TestFavorites(); await TestJoinLifecycle();
                Console.WriteLine("PASS: " + assertions + " WPF browser regression assertions (real refresh/filter paths, cancellation, expiry, sorting, favorites and join state). Mock HTTP and join launcher only.");
            } catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        return exitCode;
    }

    private sealed class Harness : IDisposable
    {
        public DateTime Now = DateTime.UtcNow;
        public readonly FakeHttp Http = new FakeHttp();
        public readonly string DirectoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "browser-test-" + Guid.NewGuid().ToString("N"));
        public readonly ServerBrowserView View;
        public readonly string FavoritesFile;
        public int JoinCalls;
        public TaskCompletionSource<string> PendingJoin;
        public CancellationToken JoinCancellation;
        public Harness(string savedFavorites)
        {
            Directory.CreateDirectory(DirectoryPath); FavoritesFile = Path.Combine(DirectoryPath, "favorites.xml");
            if (savedFavorites != null) File.WriteAllText(FavoritesFile, savedFavorites);
            View = new ServerBrowserView(false, new EosClient(Http), FavoritesFile, () => Now, (server, build, token) => {
                JoinCalls++; JoinCancellation = token;
                return PendingJoin == null ? Task.FromResult("LAUNCHED") : PendingJoin.Task;
            });
        }
        public DataGrid Table { get { return Field<DataGrid>(View, "table"); } }
        public ServerRecord First { get { return Field<ServerSnapshot>(View, "snapshot").Servers.First(s => s.SessionId == A); } }
        public Task Pulse() { return (Task)Call(View, "Pulse"); }
        public Task Join() { return (Task)Call(View, "JoinSelected"); }
        public void Dispose()
        {
            View.Dispose();
            foreach (string file in Directory.GetFiles(DirectoryPath)) File.Delete(file);
            Directory.Delete(DirectoryPath);
        }
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        public int Queries, Reported = 100;
        public bool Fail, HoldNext, Disposed;
        public TaskCompletionSource<HttpResponseMessage> Pending;
        public CancellationToken PendingToken;
        public static HttpResponseMessage Json(string json) { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)) }; }
        public string Servers()
        {
            return ServerData.Serializer().Serialize(new { sessions = new[] {
                new { id=A, attributes=new { SERVERNAME_s="Alpha", ADDRESS_s="203.0.113.5", GAMESERVER_PORT_l=7787, PLAYERCOUNT_l=Reported, GAMEVERSION_s="v10.6.3.694944.3212" } },
                new { id=B, attributes=new { SERVERNAME_s="Bravo", ADDRESS_s="203.0.113.5", GAMESERVER_PORT_l=7788, PLAYERCOUNT_l=5, GAMEVERSION_s="v10.6.3.694944.3212" } }
            } });
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/deviceid") || path.EndsWith("/token")) return Task.FromResult(Json("{\"access_token\":\"fake\",\"expires_in\":1800}"));
            if (path.Contains("/matchmaking/")) {
                Queries++;
                if (HoldNext) { HoldNext = false; PendingToken = cancellation; Pending = new TaskCompletionSource<HttpResponseMessage>(); return Pending.Task; }
                if (Fail) throw new IOException("模拟网络中断");
                return Task.FromResult(Json(Servers()));
            }
            var sessions = Enumerable.Range(1, 60).Select(i => new { attributes = new { ADVERTISEDSESSIONID_s="Session:"+A, USERID_s=i.ToString("x32") } }).ToArray();
            return Task.FromResult(Json(ServerData.Serializer().Serialize(new { sessions=sessions })));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static async Task TestEvidenceAndSorting()
    {
        using (var h = new Harness(null)) {
            await h.View.Refresh(); Check(h.First.Observed == 60 && !h.First.PersistentMismatch, "first real refresh has only one mismatch sample");
            h.Now = h.Now.AddSeconds(30); h.Http.Reported = 90; await h.View.Refresh();
            Check(!h.First.PersistentMismatch, "changed reported count does not reuse earlier streak");
            Field<CheckBox>(h.View,"autoCheck").IsChecked = false;
            h.Now = h.Now.AddSeconds(30); h.Http.Reported = 100; await h.View.Refresh();
            Field<CheckBox>(h.View,"autoCheck").IsChecked = true;
            h.Now = h.Now.AddSeconds(30); h.Http.Reported = 90; await h.View.Refresh();
            Check(!h.First.PersistentMismatch, "intermediate refresh without verification also resets evidence");
            h.Now = h.Now.AddSeconds(30); await h.View.Refresh();
            Check(h.First.PersistentMismatch, "two current matching report samples establish discrepancy");
            Field<CheckBox>(h.View,"hideMismatch").IsChecked = true;
            Check(h.Table.Items.Count == 1, "persistent discrepancy is hidden only on request");
            h.Http.Fail = true; h.Now = h.Now.AddSeconds(181); Set(h.View,"active",true); await h.Pulse();
            Check(h.First.Observed == null && !h.First.PersistentMismatch && h.First.CheckState == "核验已过期", "timer expires evidence even when refresh fails");
            Check(h.Table.Items.Count == 2, "expired discrepancy is automatically revealed during outage");
            Check(Field<TextBlock>(h.View,"status").Text.Contains("模拟网络中断"), "failed refresh is visible");
        }
        using (var h = new Harness(null)) {
            await h.View.Refresh();
            h.Table.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending)); h.Table.Columns[1].SortDirection = ListSortDirection.Descending;
            h.Table.SelectedItem = h.First;
            h.Now = h.Now.AddSeconds(30); await h.View.Refresh();
            Check(((ServerRecord)h.Table.Items[0]).Name == "Bravo", "user sorting survives refresh and verification");
            Check(h.Table.Columns[1].SortDirection == ListSortDirection.Descending, "sort indicator stays consistent");
            Check(((ServerRecord)h.Table.SelectedItem).SessionId == A, "selection survives reordering");
        }
    }

    private static async Task TestReentryAndDispose()
    {
        using (var h = new Harness(null)) {
            h.Http.HoldNext = true; var refresh = h.View.Refresh();
            await WaitForRequest(h);
            h.View.SetActive(false); h.View.SetActive(true);
            Check(h.Http.PendingToken.IsCancellationRequested, "leaving cancels in-flight HTTP");
            h.Http.Pending.SetResult(FakeHttp.Json(h.Http.Servers())); await refresh;
            Check(h.Table.Items.Count == 0, "late cancelled response does not replace snapshot");
            h.Now = h.Now.AddSeconds(10); await h.Pulse();
            Check(h.Http.Queries == 2 && h.Table.Items.Count == 2, "rapid return retries after throttle without waiting a full minute");
        }
        using (var h = new Harness(null)) {
            h.Http.HoldNext = true; var refresh = h.View.Refresh(); await WaitForRequest(h); h.View.Dispose();
            Check(h.Http.PendingToken.IsCancellationRequested && !h.Http.Disposed, "close cancels before disposing pending client");
            h.Http.Pending.SetResult(FakeHttp.Json(h.Http.Servers())); await refresh;
            Check(h.Http.Disposed && h.Table.Items.Count == 0, "client is disposed and late response ignored after close");
        }
    }

    private static async Task TestFavorites()
    {
        foreach (string damaged in new[] { "<favorites><server>broken", "<unexpected/>" }) {
            using (var h = new Harness(damaged)) {
                await h.View.Refresh();
                Check(!Field<Button>(h.View,"favorite").IsEnabled, "unreadable or wrong-root favorites disables writes");
                Call(h.View,"ToggleFavorite");
                Check(File.ReadAllText(h.FavoritesFile) == damaged && !h.First.Favorite, "corrupt favorites never overwritten by a new favorite");
            }
        }
        using (var h = new Harness(null)) {
            await h.View.Refresh(); Call(h.View,"ToggleFavorite");
            Check(h.First.Favorite && File.ReadAllText(h.FavoritesFile).Contains("203.0.113.5:7787"), "favorite saves to disk");
            Call(h.View,"ToggleFavorite");
            Check(!h.First.Favorite && !File.ReadAllText(h.FavoritesFile).Contains("203.0.113.5:7787"), "existing favorite file replaced atomically");
            Check(Directory.GetFiles(h.DirectoryPath,"*.tmp").Length == 0, "no temporary favorite files remain");
        }
    }

    private static async Task TestJoinLifecycle()
    {
        using (var h = new Harness(null)) {
            await h.View.Refresh(); h.PendingJoin = new TaskCompletionSource<string>(); var join = h.Join();
            Check(h.JoinCalls == 1 && !Field<Button>(h.View,"join").IsEnabled, "pending join disables button");
            await h.Join(); Check(h.JoinCalls == 1, "duplicate join is guarded inside action");
            h.Now = h.Now.AddSeconds(15); h.PendingJoin.SetResult("LAUNCHED"); await join;
            h.Now = h.Now.AddSeconds(76); Set(h.View,"active",true); Set(h.View,"lastAttempt",h.Now); await h.Pulse();
            Check(!Field<Button>(h.View,"join").IsEnabled, "cooldown starts after helper launch rather than click");
            await h.Join(); Check(h.JoinCalls == 1, "action cannot bypass cooldown");
            h.Now = h.Now.AddSeconds(14); await h.Pulse();
            Check(Field<Button>(h.View,"join").IsEnabled, "cooldown expires automatically without refresh or selection change");
        }
        foreach (bool close in new[] { false, true }) {
            using (var h = new Harness(null)) {
                await h.View.Refresh(); h.PendingJoin = new TaskCompletionSource<string>(); var join = h.Join();
                if (close) h.View.Dispose(); else h.View.SetActive(false);
                Check(h.JoinCancellation.IsCancellationRequested, "page exit or window close cancels pending join");
                h.PendingJoin.SetCanceled(); await join;
                Check(!Field<bool>(h.View,"joining"), "cancelled join releases operation state");
                if (!close) { h.PendingJoin = null; await h.Join(); Check(h.JoinCalls == 2, "cancelled preparation does not impose success cooldown"); }
            }
        }
        using (var h = new Harness(null)) {
            await h.View.Refresh(); h.PendingJoin = new TaskCompletionSource<string>(); var join = h.Join(); h.PendingJoin.SetResult("ERROR:测试失败"); await join;
            Check(Field<TextBlock>(h.View,"status").Text == "测试失败" && Field<Button>(h.View,"join").IsEnabled, "helper error is shown and permits retry");
        }
    }
}
