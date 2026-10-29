using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SquadTools;

internal static class ServerBrowserTests
{
    private static int assertions;
    private static readonly string A = new string('a', 32), B = new string('b', 32);
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
    private static object Call(object target, string name, params object[] args) { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); }
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
                await TestEvidenceAndSorting(); await TestReentryAndDispose(); await TestFavorites(); await TestBackgroundFollow(); TestLatencySortingAndFilters(); await TestLatencyUpdates(); await TestLatencyPriority();
                Console.WriteLine("PASS: " + assertions + " WPF browser regression assertions (refresh/filter paths, cancellation, expiry, latency updates/sorting, fixed filter header, favorites and map follow). Mock HTTP and ICMP only.");
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
        public readonly FakeLatency Latency;
        public readonly string FavoritesFile;
        public Harness(string savedFavorites) : this(savedFavorites, new FakeLatency()) { }
        public Harness(string savedFavorites, FakeLatency latency, int parallelism = 2)
        {
            Latency = latency;
            Directory.CreateDirectory(DirectoryPath); FavoritesFile = Path.Combine(DirectoryPath, "favorites.xml");
            if (savedFavorites != null) File.WriteAllText(FavoritesFile, savedFavorites);
            View = new ServerBrowserView(false, new EosClient(Http), FavoritesFile, () => Now, new ServerLatencyClient(Latency.Probe, () => Now, parallelism, 1000));
        }
        public DataGrid Table { get { return Field<DataGrid>(View, "table"); } }
        public ServerRecord First { get { return Field<ServerSnapshot>(View, "snapshot").Servers.First(s => s.SessionId == A); } }
        public Task Pulse() { return (Task)Call(View, "Pulse"); }
        public void Dispose()
        {
            View.Dispose();
            foreach (string file in Directory.GetFiles(DirectoryPath)) File.Delete(file);
            Directory.Delete(DirectoryPath);
        }
    }

    private sealed class FakeLatency
    {
        public int Calls;
        public readonly List<string> Addresses = new List<string>();
        public bool Hold;
        public CancellationToken Token;
        public TaskCompletionSource<int?> Pending;
        public Task<int?> Probe(IPAddress address, int timeout, CancellationToken cancellation)
        {
            Calls++; Token = cancellation; Addresses.Add(address.ToString());
            if (Hold) { Pending = new TaskCompletionSource<int?>(); return Pending.Task; }
            return Task.FromResult<int?>(24);
        }
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        public int Queries, Reported = 100;
        public bool Fail, HoldNext, Disposed;
        public string Map = "BlackCoast_RAAS_v1";
        public TaskCompletionSource<HttpResponseMessage> Pending;
        public CancellationToken PendingToken;
        public static HttpResponseMessage Json(string json) { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)) }; }
        public string Servers()
        {
            return ServerData.Serializer().Serialize(new { sessions = new[] {
                new { id=A, attributes=new { SERVERNAME_s="Alpha", ADDRESS_s="203.0.113.5", GAMESERVER_PORT_l=7787, PLAYERCOUNT_l=Reported, GAMEVERSION_s="v10.6.3.694944.3212", MAPNAME_s=Map } },
                new { id=B, attributes=new { SERVERNAME_s="Bravo", ADDRESS_s="203.0.113.5", GAMESERVER_PORT_l=7788, PLAYERCOUNT_l=5, GAMEVERSION_s="v10.6.3.694944.3212", MAPNAME_s="Narva_RAAS_v1" } }
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

    private static async Task TestBackgroundFollow()
    {
        using (var h = new Harness(null)) {
            await h.View.Refresh();
            int choices = 0, notifications = 0; ServerRecord updated = null;
            h.View.ServerChosen += s => choices++;
            h.View.FollowedServerUpdated += s => { notifications++; updated = s; };
            h.View.FollowServer(A); h.View.SetActive(false); h.Http.Map = "Gorodok_RAAS_v1";
            h.Now = h.Now.AddSeconds(60); await h.Pulse();
            Check(h.Http.Queries == 2 && notifications == 1 && updated.SessionId == A && updated.Map == "Gorodok_RAAS_v1", "selected server continues map updates while shooting page is active");
            Check(choices == 0, "refresh and automatic row selection never imply user choice");
            Call(h.View,"ChooseSelected"); Check(choices == 1, "explicit row activation raises choice even for first preselected row");
            h.View.FollowServer(new string('f',32)); h.Now = h.Now.AddSeconds(60); await h.Pulse();
            Check(notifications == 2 && updated == null, "missing followed session does not fall back to another server");
            h.View.FollowServer(null); h.Now = h.Now.AddSeconds(60); await h.Pulse();
            Check(h.Http.Queries == 3, "stopping follow resumes offline shooting page");
        }
    }

    private static void Pump(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{})); window.UpdateLayout(); }
    private static T Find<T>(DependencyObject root, Func<T,bool> predicate) where T : DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {
            var child=VisualTreeHelper.GetChild(root,i); var typed=child as T;
            if(typed!=null && predicate(typed)) return typed;
            var nested=Find<T>(child,predicate); if(nested!=null) return nested;
        }
        return null;
    }

    private static void TestLatencySortingAndFilters()
    {
        var view=new ServerBrowserView(true); var window=new Window{Content=view,Width=1060,Height=740};
        try {
            window.Show();
            var sample=new ServerSnapshot{RetrievedAt=DateTime.UtcNow};
            sample.Servers.Add(new ServerRecord{SessionId=A,Name="Alpha",LatencyMs=120,LatencyAt=DateTime.UtcNow});
            sample.Servers.Add(new ServerRecord{SessionId=B,Name="Bravo",LatencyMs=20,LatencyAt=DateTime.UtcNow});
            sample.Servers.Add(new ServerRecord{SessionId=new string('c',32),Name="Charlie"});
            view.LoadSmokeSnapshot(sample); Pump(window);
            var table=Field<DataGrid>(view,"table"); var latency=table.Columns.Single(c=>c.SortMemberPath=="LatencyMs");
            Check(((ServerRecord)table.Items[0]).Name=="Bravo" && ((ServerRecord)table.Items[2]).Name=="Charlie","default latency order is low-high with unknown last");
            Check(latency.SortDirection==ListSortDirection.Ascending,"latency sort direction is visible by default");
            var header=Find<DataGridColumnHeader>(table,h=>h.Column==latency);
            Check(header!=null,"latency header is realized");
            var arrow=header.Template.FindName("SortGlyph",header) as TextBlock;
            Check(arrow!=null && arrow.IsVisible && arrow.Text=="↑","ascending arrow is displayed in the latency header");
            table.SelectedItem=sample.Servers[1]; Call(view,"SortColumn",latency); Pump(window);
            Check(((ServerRecord)table.Items[0]).Name=="Alpha" && ((ServerRecord)table.Items[2]).Name=="Charlie","descending latency also leaves unknown last");
            Check(latency.SortDirection==ListSortDirection.Descending && arrow.Text=="↓","same latency header toggles the direction and arrow");
            Check(table.SelectedItem==sample.Servers[1],"sorting retains the selected physical server");
            Field<TextBox>(view,"search").Text="a";
            Check(latency.SortDirection==ListSortDirection.Descending && ((ServerRecord)table.Items[0]).Name=="Alpha","latency order survives applying search");
            Call(view,"SortColumn",table.Columns[1]); Pump(window);
            Check(latency.SortDirection==null && table.Columns[1].SortDirection==ListSortDirection.Ascending,"sorting another header clears the latency arrow");
            Check(((ServerRecord)table.Items[0]).Name=="Alpha","other columns remain sortable");
            var root=(FrameworkElement)view.Child; var expander=(Expander)root.FindName("AdvancedFilters");
            foreach(double width in new[]{1060.0,1280.0,1440.0}) {
                window.Width=width; expander.IsExpanded=false; Pump(window);
                var toggle=Find<ToggleButton>(expander,t=>Convert.ToString(t.Content)=="更多筛选");
                Check(toggle!=null,"advanced filters exposes one stable header toggle");
                var position=toggle.TranslatePoint(new Point(),view);
                expander.IsExpanded=true; Pump(window);
                var expanded=toggle.TranslatePoint(new Point(),view);
                Check(Math.Abs(position.X-expanded.X)<0.1 && Math.Abs(position.Y-expanded.Y)<0.1,"advanced filter header does not move when expanded at width "+width);
                Check(Find<CheckBox>(expander,c=>Convert.ToString(c.Content)=="自动核验前 10 台").IsVisible,"advanced options remain reachable beneath the fixed header");
            }
            Check(!view.HasRequestedNetwork,"sorting and filter layout in test mode never starts ICMP or EOS");
        } finally { window.Close(); view.Dispose(); }
    }

    private static async Task TestLatencyUpdates()
    {
        using(var h=new Harness(null)) {
            Field<CheckBox>(h.View,"autoCheck").IsChecked=false;
            await h.View.Refresh();
            Check(h.Latency.Calls==0,"background map/list refresh does not probe latency while server page is inactive");
            Set(h.View,"active",true); Call(h.View,"StartLatency"); await Field<Task>(h.View,"latencyTask");
            Check(h.Latency.Calls==1 && Field<ServerSnapshot>(h.View,"snapshot").Servers.All(s=>s.LatencyMs==24),"one host probe updates every port/session sharing its IP");
            var info=(TextBlock)((FrameworkElement)h.View.Child).FindName("DetailInfo");
            Check(((TextBlock)info.ToolTip).Text.Contains("24 ms"),"selected server detail tooltip updates after an ICMP result without requiring a new selection");
            h.Table.SelectedItem=h.First;
            Call(h.View,"SortColumn",h.Table.Columns[1]);
            h.Now=h.Now.AddSeconds(61); h.Latency.Hold=true;
            await h.View.Refresh();
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while(h.Latency.Pending==null && DateTime.UtcNow<deadline) await Task.Delay(10);
            Check(h.Latency.Pending!=null,"latency refresh can run asynchronously after the new list is displayed");
            var oldTask=Field<Task>(h.View,"latencyTask"); var late=h.Latency.Pending;
            var previous=Field<ServerSnapshot>(h.View,"snapshot");
            Check(previous.Servers.All(s=>s.LatencyAt!=DateTime.MinValue),"refresh retains expired attempt times for fair host scheduling while hiding stale RTTs");
            h.View.SetActive(false); Check(h.Latency.Token.IsCancellationRequested,"leaving server page cancels its ICMP generation");
            late.SetResult(7); await oldTask;
            Check(previous.Servers.All(s=>!s.LatencyMs.HasValue),"a canceled late ICMP response cannot populate stale rows");
            h.Latency.Hold=false; h.Latency.Pending=null;
            Set(h.View,"active",true); Call(h.View,"StartLatency"); await Field<Task>(h.View,"latencyTask");
            Check(Field<ServerSnapshot>(h.View,"snapshot").Servers.All(s=>s.LatencyMs==24),"returning to server page measures the still-current list");
            Check(((ServerRecord)h.Table.SelectedItem).SessionId==A && h.Table.Columns[1].SortDirection==ListSortDirection.Ascending,"latency update batches preserve user sort and selection");
        }
    }

    private static async Task TestLatencyPriority()
    {
        using(var h=new Harness(null,new FakeLatency(),1)) {
            var sample=new ServerSnapshot{RetrievedAt=h.Now};
            sample.Servers.Add(new ServerRecord{SessionId=A,Name="Old host",Address="203.0.113.6",LatencyMs=20,LatencyAt=h.Now.AddSeconds(-61)});
            sample.Servers.Add(new ServerRecord{SessionId=B,Name="Never attempted",Address="203.0.113.7"});
            Set(h.View,"snapshot",sample); Set(h.View,"active",true); Call(h.View,"ApplyFilter"); Call(h.View,"StartLatency");
            await Field<Task>(h.View,"latencyTask");
            Check(h.Latency.Addresses.SequenceEqual(new[]{"203.0.113.7","203.0.113.6"}),"never-measured hosts are attempted before older hosts when repeated refreshes interrupt a large sweep");
            h.Now=h.Now.AddSeconds(61); h.Http.Fail=true; await h.Pulse();
            Check(sample.Servers.All(s=>!s.LatencyMs.HasValue && s.LatencyAt!=DateTime.MinValue),"expired latency becomes unknown even when the next list refresh fails; attempt times still prevent queue starvation");
        }
    }
}
