using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;

namespace SquadTools
{
    public static class SteamJoinBridge
    {
        public static string BuildId(string version)
        {
            var match = Regex.Match(version ?? "", "\\Av?[0-9]+\\.[0-9]+\\.[0-9]+\\.([0-9]{1,10})\\.[0-9]+\\z", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        public static Task<string> Launch(ServerRecord server, string build, CancellationToken cancellation)
        {
            if (!ServerData.ValidSession(server.SessionId) || !Regex.IsMatch(build ?? "", "\\A[0-9]{1,10}\\z")) throw new ArgumentException("Invalid server join parameters.");
            var start = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, "--join-server " + server.SessionId + " " + build) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory };
            return RunHelper(start, cancellation, 25000);
        }

        internal static async Task<string> RunHelper(ProcessStartInfo start, CancellationToken cancellation, int timeoutMs)
        {
            cancellation.ThrowIfCancellationRequested();
            using (var helper = Process.Start(start))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                var read = ReadJoinResult(helper.StandardOutput);
                bool launched = false;
                var gate = new object();
                // Cancellation must terminate preparation even if the WPF dispatcher is closing.
                using (cancellation.Register(delegate { lock (gate) { if (!launched) StopHelper(helper); } })) {
                    try {
                        var deadline = Task.Delay(timeoutMs, timeout.Token);
                        if (await Task.WhenAny(read, deadline).ConfigureAwait(false) != read) {
                            cancellation.ThrowIfCancellationRequested();
                            throw new IOException("Steam 响应超时。");
                        }
                        string result = await read.ConfigureAwait(false);
                        lock (gate) {
                            cancellation.ThrowIfCancellationRequested();
                            launched = result == "LAUNCHED";
                        }
                        return result;
                    }
                    finally {
                        timeout.Cancel();
                        // A launched lobby must stay alive for the game's hand-off. All unfinished helpers are owned here.
                        lock (gate) { if (!launched) StopHelper(helper); }
                        ObserveFailure(read);
                    }
                }
            }
        }

        private static void StopHelper(Process helper)
        {
            try { if (!helper.HasExited) helper.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static void ObserveFailure(Task task)
        {
            // Closing a cancelled helper's pipe may fault its outstanding read.
            task.ContinueWith(t => t.Exception.Handle(ex => true), TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private static async Task<string> ReadJoinResult(StreamReader reader)
        {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) if (line.StartsWith("SQUADTOOLS_JOIN:", StringComparison.Ordinal)) return line.Substring(16);
            return null;
        }

        // Runs only after an explicit Join click, in a short-lived separate process.
        public static int Run(string session, string build)
        {
            if (!ServerData.ValidSession(session) || !Regex.IsMatch(build ?? "", "\\A[0-9]{1,10}\\z")) { Console.WriteLine("SQUADTOOLS_JOIN:ERROR:服务器信息无效，请刷新列表。"); return 1; }
            bool initialized = false; CSteamID lobby = CSteamID.Nil;
            try
            {
                Environment.SetEnvironmentVariable("SteamAppId", "393380");
                Environment.SetEnvironmentVariable("SteamGameId", "393380");
                initialized = SteamAPI.Init();
                if (!initialized) { Console.WriteLine("SQUADTOOLS_JOIN:ERROR:请先启动 Steam，登录拥有 Squad 的账号后再试。"); return 1; }
                if (!SteamApps.BIsSubscribedApp(new AppId_t(393380))) { Console.WriteLine("SQUADTOOLS_JOIN:ERROR:当前 Steam 账号未检测到 Squad 授权。"); return 1; }
                bool finished = false; string error = null;
                using (var result = CallResult<LobbyCreated_t>.Create(delegate(LobbyCreated_t callback, bool failed) {
                    finished = true;
                    if (failed || callback.m_eResult != EResult.k_EResultOK) { error = "Steam 未能建立加入入口，请稍后重试。"; return; }
                    lobby = new CSteamID(callback.m_ulSteamIDLobby);
                }))
                {
                    result.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, 2));
                    var watch = Stopwatch.StartNew();
                    while (!finished && watch.Elapsed < TimeSpan.FromSeconds(15)) { SteamAPI.RunCallbacks(); Thread.Sleep(30); }
                    if (!finished || error != null) { Console.WriteLine("SQUADTOOLS_JOIN:ERROR:" + (error ?? "Steam 加入入口创建超时。")); return 1; }
                }
                string owner = SteamUser.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture);
                var metadata = new Dictionary<string, string> {
                    { "buildid", build }, { "CONMETHOD", "P2P" }, { "P2PPORT", "7777" }, { "P2PADDR", owner },
                    { "OWNINGID", owner }, { "OWNINGNAME", "Squad Tools" }, { "NUMOPENPRIVCONN", "0" }, { "NUMOPENPUBCONN", "1" },
                    { "NUMPRIVCONN", "0" }, { "NUMPUBCONN", "2" }, { "SESSIONFLAGS", "227" },
                    { "RedpointEOSRoomId_s", "Session:" + session.ToLowerInvariant() }, { "RedpointEOSRoomNamespace_s", "Synthetic" }
                };
                foreach (var pair in metadata) if (!SteamMatchmaking.SetLobbyData(lobby, pair.Key, pair.Value)) { Console.WriteLine("SQUADTOOLS_JOIN:ERROR:无法准备服务器连接信息。"); return 1; }
                // Let Steam publish the metadata before the game tries to resolve it.
                var publish = Stopwatch.StartNew();
                while (publish.Elapsed < TimeSpan.FromSeconds(1)) { SteamAPI.RunCallbacks(); Thread.Sleep(30); }
                string uri = "steam://joinlobby/393380/" + lobby.m_SteamID.ToString(CultureInfo.InvariantCulture) + "/" + owner;
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                Console.WriteLine("SQUADTOOLS_JOIN:LAUNCHED"); Console.Out.Flush();
                var lifetime = Stopwatch.StartNew();
                while (lifetime.Elapsed < TimeSpan.FromSeconds(90)) { SteamAPI.RunCallbacks(); Thread.Sleep(100); }
                return 0;
            }
            catch (Exception) { Console.WriteLine("SQUADTOOLS_JOIN:ERROR:Steam 加入入口不可用，请确认 Steam 已启动且组件完整。"); return 1; }
            finally { if (initialized) { if (lobby != CSteamID.Nil) SteamMatchmaking.LeaveLobby(lobby); SteamAPI.Shutdown(); } }
        }
    }
}
