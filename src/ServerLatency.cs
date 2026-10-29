using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SquadTools
{
    public sealed class ServerLatencyResult
    {
        public string Address { get; private set; }
        public int? Milliseconds { get; private set; }
        public DateTime MeasuredAt { get; private set; }

        internal ServerLatencyResult(string address, int? milliseconds, DateTime measuredAt)
        { Address = address; Milliseconds = milliseconds; MeasuredAt = measuredAt; }
    }

    // EOS does not report the local client's RTT. Measure the host's ICMP RTT;
    // blocked/unsupported ICMP is unknown, never a fabricated zero or game ping.
    public sealed class ServerLatencyClient
    {
        private readonly Func<IPAddress, int, CancellationToken, Task<int?>> probe;
        private readonly Func<DateTime> utcNow;
        private readonly int concurrency, timeoutMilliseconds;
        private readonly SemaphoreSlim slots;
        private readonly object cacheLock = new object();
        private readonly Dictionary<string, ServerLatencyResult> cache = new Dictionary<string, ServerLatencyResult>(StringComparer.Ordinal);
        private static readonly TimeSpan CacheAge = TimeSpan.FromSeconds(60);

        public ServerLatencyClient() : this(PingAddress, () => DateTime.UtcNow, 8, 1000) { }

        internal ServerLatencyClient(Func<IPAddress, int, CancellationToken, Task<int?>> measure, Func<DateTime> clock, int parallelism, int timeout)
        {
            if (measure == null) throw new ArgumentNullException("measure");
            if (clock == null) throw new ArgumentNullException("clock");
            if (parallelism < 1 || parallelism > 32) throw new ArgumentOutOfRangeException("parallelism");
            if (timeout < 1 || timeout > 1200) throw new ArgumentOutOfRangeException("timeout");
            probe = measure; utcNow = clock; concurrency = parallelism; timeoutMilliseconds = timeout;
            slots = new SemaphoreSlim(concurrency, concurrency);
        }

        // Callbacks are serialized on the caller's captured synchronization
        // context. The UI must also reject obsolete snapshot/generation results.
        public async Task Measure(IEnumerable<string> addresses, Action<ServerLatencyResult> onResult, CancellationToken token)
        {
            if (addresses == null) throw new ArgumentNullException("addresses");
            if (onResult == null) throw new ArgumentNullException("onResult");
            token.ThrowIfCancellationRequested();
            var queue = addresses.Select(ServerData.PublicAddress).Where(address => address.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            lock (cacheLock) {
                DateTime now = utcNow();
                foreach (string address in cache.Where(pair => !Fresh(pair.Value, now)).Select(pair => pair.Key).ToArray()) cache.Remove(address);
            }
            int next = 0;
            var pending = new List<Task<ServerLatencyResult>>();
            try {
                while (next < queue.Count || pending.Count > 0) {
                    while (next < queue.Count && pending.Count < concurrency) pending.Add(MeasureOne(queue[next++], token));
                    var completed = await WithCancellation(Task.WhenAny(pending), token);
                    var result = await completed;
                    pending.Remove(completed);
                    token.ThrowIfCancellationRequested();
                    onResult(result);
                }
            } finally {
                // Cancellation returns immediately. Already-sent probes are
                // bounded by their timeout and cannot produce more UI callbacks.
                foreach (var task in pending) Observe(task);
            }
        }

        private bool Fresh(ServerLatencyResult result, DateTime now)
        { return result.MeasuredAt <= now && now - result.MeasuredAt < CacheAge; }

        private bool Cached(string address, out ServerLatencyResult result)
        {
            lock (cacheLock) {
                return cache.TryGetValue(address, out result) && Fresh(result, utcNow());
            }
        }

        private async Task<ServerLatencyResult> MeasureOne(string address, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ServerLatencyResult result;
            if (Cached(address, out result)) return result;
            await slots.WaitAsync(token).ConfigureAwait(false);
            Task<int?> request = null;
            try {
                token.ThrowIfCancellationRequested();
                if (Cached(address, out result)) return result;
                int? milliseconds = null;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                    try {
                        request = probe(IPAddress.Parse(address), timeoutMilliseconds, timeout.Token);
                        if (request == null) throw new InvalidOperationException("The latency probe returned no task.");
                        var delay = Task.Delay(timeoutMilliseconds, timeout.Token);
                        var completed = await Task.WhenAny(request, delay).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (completed == request) {
                            int? measured = await request.ConfigureAwait(false);
                            if (measured.HasValue && measured.Value >= 0) milliseconds = measured;
                        }
                    }
                    catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
                    catch (PingException) { }
                    catch (SocketException) { }
                    catch (NetworkInformationException) { }
                    catch (InvalidOperationException) { }
                    catch (NotSupportedException) { }
                    catch (System.Security.SecurityException) { }
                    finally { timeout.Cancel(); if (request != null) Observe(request); }
                }
                token.ThrowIfCancellationRequested();
                result = new ServerLatencyResult(address, milliseconds, utcNow());
                lock (cacheLock) { cache[address] = result; }
                return result;
            } finally {
                // Cancelling the caller must return promptly, but native ICMP
                // can still be draining until its own timeout. Keep its permit
                // until it settles so a new generation cannot overbook probes.
                if (request != null && !request.IsCompleted)
                    Observe(request.ContinueWith(completed => slots.Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
                else slots.Release();
            }
        }

        private static async Task<int?> PingAddress(IPAddress address, int timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var ping = new Ping()) {
                var reply = await ping.SendPingAsync(address, timeout).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return reply.Status == IPStatus.Success && reply.RoundtripTime >= 0 && reply.RoundtripTime <= int.MaxValue ? (int?)reply.RoundtripTime : null;
            }
        }

        private static async Task<T> WithCancellation<T>(Task<T> operation, CancellationToken token)
        {
            if (!token.CanBeCanceled) return await operation;
            var cancelled = new TaskCompletionSource<bool>();
            using (token.Register(delegate { cancelled.TrySetResult(true); })) {
                if (await Task.WhenAny(operation, cancelled.Task) != operation) throw new OperationCanceledException(token);
                return await operation;
            }
        }

        private static void Observe(Task task)
        { task.ContinueWith(faulted => { var ignored = faulted.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
    }
}
