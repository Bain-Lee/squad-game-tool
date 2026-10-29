using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using SquadTools;

internal static class ServerLatencyTests
{
    private static int assertions;
    private static readonly DateTime Now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string label) { assertions++; if (!value) throw new Exception(label); }
    private static void Throws<T>(Action action, string label) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception("Expected " + typeof(T).Name + ": " + label); }

    public static int Main()
    {
        try {
            TestModels(); TestValidation();
            TestAddressAndCache().GetAwaiter().GetResult();
            TestBoundsAndFailures().GetAwaiter().GetResult();
            TestConcurrency().GetAwaiter().GetResult();
            TestCancellation().GetAwaiter().GetResult();
            Console.WriteLine("PASS: " + assertions + " server-latency assertions: public IP validation/dedup, real zero vs unknown, cache expiry, timeout/failure handling, global concurrency, responsive cancellation and no obsolete callbacks. Mock probes only.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void TestModels()
    {
        var record = new ServerRecord(); var changed = new List<string>();
        record.PropertyChanged += delegate(object sender, System.ComponentModel.PropertyChangedEventArgs e) { changed.Add(e.PropertyName); };
        Check(record.LatencyText == "—" && record.LatencyToolTip.Contains("尚未测量"), "missing latency is explicitly unknown");
        record.LatencyMs = 0;
        Check(record.LatencyText == "0 ms", "a successful zero-millisecond response remains valid");
        Check(changed.Contains("LatencyMs") && changed.Contains("LatencyText") && changed.Contains("LatencyToolTip"), "measured values notify bindings and derived display text");
        changed.Clear(); record.LatencyAt = Now;
        Check(changed.Contains("LatencyAt") && changed.Contains("LatencyToolTip"), "measurement timestamps notify the hover explanation");
        Check(record.LatencyToolTip.Contains("ICMP") && record.LatencyToolTip.Contains("不等于游戏内"), "host RTT is not described as a game protocol measurement");
        changed.Clear(); record.LatencyAt = Now; record.LatencyMs = 0;
        Check(changed.Count == 0, "unchanged latency does not churn UI bindings");
        record.LatencyMs = null;
        Check(record.LatencyText == "—" && record.LatencyToolTip.Contains("未测得响应"), "failed probes never display zero");
        record.LatencyMs = -1; Check(record.LatencyText == "—", "invalid negative latency cannot become a displayed reading");
    }

    private static void TestValidation()
    {
        Func<IPAddress, int, CancellationToken, Task<int?>> probe = (address, timeout, token) => Task.FromResult<int?>(1);
        Throws<ArgumentNullException>(() => new ServerLatencyClient(null, () => Now, 1, 10), "missing probe");
        Throws<ArgumentNullException>(() => new ServerLatencyClient(probe, null, 1, 10), "missing clock");
        foreach (int value in new[] { 0, 33 }) Throws<ArgumentOutOfRangeException>(() => new ServerLatencyClient(probe, () => Now, value, 10), "unbounded parallelism");
        foreach (int value in new[] { 0, 1201 }) Throws<ArgumentOutOfRangeException>(() => new ServerLatencyClient(probe, () => Now, 1, value), "unbounded timeout");
        var client = new ServerLatencyClient(probe, () => Now, 1, 10);
        Throws<ArgumentNullException>(() => client.Measure(null, result => { }, CancellationToken.None).GetAwaiter().GetResult(), "missing IP sequence");
        Throws<ArgumentNullException>(() => client.Measure(new string[0], null, CancellationToken.None).GetAwaiter().GetResult(), "missing callback");
    }

    private static async Task TestAddressAndCache()
    {
        DateTime now = Now; int requests = 0; var measured = new List<string>();
        var client = new ServerLatencyClient((address, timeout, token) => {
            requests++; measured.Add(address.ToString()); return Task.FromResult<int?>(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 19 : 0);
        }, () => now, 2, 100);
        var results = new List<ServerLatencyResult>();
        await client.Measure(new[] { null, "", "not-a-host", "localhost", "127.0.0.1", "::1", "10.0.0.1", "172.16.0.1", "192.168.1.1", "169.254.1.1", "100.64.0.1", "0.0.0.0", "::", "ff02::1", "fe80::1", "fc00::1", "203.0.113.5", "203.0.113.5", "::ffff:203.0.113.5", "2001:db8::5", "2001:0db8:0000:0000::5" }, results.Add, CancellationToken.None);
        Check(requests == 2 && results.Count == 2, "only normalized, unique public IP literals are probed; DNS and local addresses are excluded");
        Check(results.Single(result => result.Address == "203.0.113.5").Milliseconds == 0, "mapped IPv4 dedup preserves a measured zero");
        Check(results.Single(result => result.Address == "2001:db8::5").Milliseconds == 19, "IPv6 is normalized and measured");
        Check(results.All(result => result.MeasuredAt == Now), "results carry their observation time");
        results.Clear(); now = Now.AddSeconds(59);
        await client.Measure(new[] { "203.0.113.5" }, results.Add, CancellationToken.None);
        Check(requests == 2 && results[0].MeasuredAt == Now, "within a minute cached results retain the original observation time");
        results.Clear(); now = Now.AddSeconds(60);
        await client.Measure(new[] { "203.0.113.5" }, results.Add, CancellationToken.None);
        Check(requests == 3 && results[0].MeasuredAt == now, "expired cache is replaced with a current measurement");
        results.Clear(); now = Now.AddSeconds(20);
        await client.Measure(new[] { "203.0.113.5" }, results.Add, CancellationToken.None);
        Check(requests == 4 && results[0].MeasuredAt == now, "clock rollback cannot make future-dated evidence stay fresh");
        await client.Measure(new string[0], results.Add, CancellationToken.None);
        Check(requests == 4, "an empty address list performs no network operations");
    }

    private static async Task TestBoundsAndFailures()
    {
        DateTime now = Now; int calls = 0;
        var client = new ServerLatencyClient((address, timeout, token) => {
            calls++;
            switch (address.GetAddressBytes().Last()) {
                case 1: throw new PingException("fixture error");
                case 2: return Task.FromResult<int?>(null);
                case 3: return Task.FromResult<int?>(-1);
                default: return Task.FromResult<int?>(35);
            }
        }, () => now, 2, 40);
        var results = new List<ServerLatencyResult>();
        await client.Measure(new[] { "203.0.113.1", "203.0.113.2", "203.0.113.3", "203.0.113.4" }, results.Add, CancellationToken.None);
        Check(results.Count == 4 && results.Count(result => !result.Milliseconds.HasValue) == 3, "probe errors, nonresponses and invalid negative results remain unknown");
        Check(results.Single(result => result.Address.EndsWith(".4", StringComparison.Ordinal)).Milliseconds == 35, "one failed host does not cancel another host's valid measurement");
        await client.Measure(new[] { "203.0.113.1", "203.0.113.2" }, result => { }, CancellationToken.None);
        Check(calls == 4, "failed hosts are cached briefly to avoid retry storms");
        now = now.AddSeconds(60);
        await client.Measure(new[] { "203.0.113.1" }, result => { }, CancellationToken.None);
        Check(calls == 5, "a failed host becomes eligible for a new probe after expiry");

        var tokens = new List<CancellationToken>();
        var bounded = new ServerLatencyClient(async (address, timeout, token) => { tokens.Add(token); await Task.Delay(60).ConfigureAwait(false); return (int?)null; }, () => Now, 1, 20);
        results.Clear(); var watch = Stopwatch.StartNew();
        await bounded.Measure(new[] { "203.0.113.7", "203.0.113.8" }, results.Add, CancellationToken.None);
        Check(watch.ElapsedMilliseconds < 1500 && results.Count == 2 && results.All(result => result.Milliseconds == null), "a stalled probe cannot block the list beyond the bounded timeout");
        Check(tokens.All(token => token.IsCancellationRequested), "timed-out probes receive cancellation requests");
    }

    private static async Task TestConcurrency()
    {
        int active = 0, maximum = 0, calls = 0;
        var client = new ServerLatencyClient(async (address, timeout, token) => {
            int running = Interlocked.Increment(ref active); Interlocked.Increment(ref calls);
            int previous;
            do { previous = maximum; if (previous >= running) break; } while (Interlocked.CompareExchange(ref maximum, running, previous) != previous);
            try { await Task.Delay(15, token).ConfigureAwait(false); return (int?)14; }
            finally { Interlocked.Decrement(ref active); }
        }, () => Now, 3, 1000);
        var first = new List<ServerLatencyResult>(); var second = new List<ServerLatencyResult>();
        await Task.WhenAll(client.Measure(Enumerable.Range(20, 10).Select(index => "203.0.113." + index), first.Add, CancellationToken.None),
            client.Measure(Enumerable.Range(40, 10).Select(index => "203.0.113." + index), second.Add, CancellationToken.None));
        Check(calls == 20 && first.Count == 10 && second.Count == 10, "overlapping batches finish each independent host exactly once");
        Check(maximum <= 3 && maximum > 1 && active == 0, "client-wide concurrency stays bounded even when refresh generations overlap");
    }

    private static async Task TestCancellation()
    {
        int calls = 0; var stalled = new TaskCompletionSource<int?>();
        var client = new ServerLatencyClient((address, timeout, token) => {
            return Interlocked.Increment(ref calls) <= 2 ? stalled.Task : Task.FromResult<int?>(64);
        }, () => Now, 2, 60);
        var oldResults = new List<ServerLatencyResult>();
        using (var cancel = new CancellationTokenSource()) {
            var old = client.Measure(Enumerable.Range(100, 10).Select(index => "203.0.113." + index), oldResults.Add, cancel.Token);
            Check(calls == 2, "only the bounded first batch has started");
            var watch = Stopwatch.StartNew(); cancel.Cancel();
            try { await old; throw new Exception("expected cancellation"); } catch (OperationCanceledException) { assertions++; }
            Check(watch.ElapsedMilliseconds < 500 && oldResults.Count == 0, "cancellation returns promptly without publishing old results");
            var current = new List<ServerLatencyResult>();
            var next = client.Measure(new[] { "203.0.113.100" }, current.Add, CancellationToken.None);
            await Task.Delay(90);
            Check(calls == 2 && oldResults.Count == 0 && !next.IsCompleted, "cancelled native probes retain their slots until they actually settle");
            stalled.TrySetResult(25); await next;
            Check(calls == 3 && current.Single().Milliseconds == 64, "cancelled observations cannot overwrite a new generation via the cache");
        }
        using (var already = new CancellationTokenSource()) {
            already.Cancel();
            try { await client.Measure(new[] { "203.0.113.101" }, result => { throw new Exception("unexpected callback"); }, already.Token); throw new Exception("expected cancellation"); }
            catch (OperationCanceledException) { assertions++; }
            Check(calls == 3, "an already-cancelled operation never probes a host");
        }
    }
}
