using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SquadTools
{
    // Protocol reference: GameDig (MIT), see third_party/GameDig.LICENSE and docs/SERVER_BROWSER.md.
    public sealed class EosClient : IDisposable
    {
        private const string Deployment = "5dee4062a90b42cd98fcad618b6636c2";
        private const string BaseUrl = "https://api.epicgames.dev";
        // Public game-client OAuth configuration from GameDig; not a user's Steam credential.
        private const string ClientConfig = "xyza7891J7d3GU8ZIwCoC5xdBsdoqVWA:4SLVBqAm09q776SIlQRTD6moM/bnGAWhDSqOxJAIS0s";
        private readonly HttpClient http;
        private string accessToken;
        private DateTime expires;
        public EosClient() : this(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, AllowAutoRedirect = false }) { }
        public EosClient(HttpMessageHandler handler)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SquadTools/0.3");
        }
        private async Task<string> Send(string path, HttpContent body, string scheme, string token, CancellationToken cancellation)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path))
            {
                request.Content = body; request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
                using (var response = await http.SendAsync(request, cancellation).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) {
                        if ((int)response.StatusCode == 401) accessToken = null;
                        string message = (int)response.StatusCode == 429 ? "查询过于频繁，请等待一分钟再刷新。" : (int)response.StatusCode == 401 || (int)response.StatusCode == 403 ? "服务器数据服务拒绝认证，可能需要适配新版本。" : "服务器数据服务暂时不可用（HTTP " + (int)response.StatusCode + "）。";
                        throw new IOException(message);
                    }
                    // Decode explicitly; some EOS responses omit the UTF-8 charset.
                    return Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                }
            }
        }
        private async Task Authenticate(CancellationToken cancellation)
        {
            if (accessToken != null && DateTime.UtcNow < expires) return;
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientConfig));
            string deviceJson = await Send("/auth/v1/accounts/deviceid", new FormUrlEncodedContent(new Dictionary<string, string> { { "deviceModel", "PC" } }), "Basic", basic, cancellation).ConfigureAwait(false);
            string device = ServerData.Text(ServerData.Object(ServerData.Serializer().DeserializeObject(deviceJson)), "access_token");
            if (device.Length == 0) throw new IOException("无法取得查询凭据。");
            var fields = new Dictionary<string, string> { { "grant_type", "external_auth" }, { "external_auth_type", "deviceid_access_token" }, { "external_auth_token", device }, { "nonce", Guid.NewGuid().ToString("N") }, { "deployment_id", Deployment }, { "display_name", "User" } };
            string json = await Send("/auth/v1/oauth/token", new FormUrlEncodedContent(fields), "Basic", basic, cancellation).ConfigureAwait(false);
            var obj = ServerData.Object(ServerData.Serializer().DeserializeObject(json));
            accessToken = ServerData.Text(obj, "access_token");
            if (accessToken.Length == 0) throw new IOException("查询认证未完成。");
            expires = DateTime.UtcNow.AddSeconds(Math.Max(1, Math.Min(1800, ServerData.Count(obj, "expires_in") ?? 300) - 30));
        }
        public async Task<ServerSnapshot> GetServers(CancellationToken cancellation)
        {
            await Authenticate(cancellation).ConfigureAwait(false);
            string json = await Send("/matchmaking/v1/" + Deployment + "/filter", new StringContent("{\"criteria\":[],\"maxResults\":1000}", Encoding.UTF8, "application/json"), "Bearer", accessToken, cancellation).ConfigureAwait(false);
            return ServerData.ParseServers(json, DateTime.UtcNow, 1000);
        }
        public async Task<Dictionary<string, RosterCount>> GetRosterCounts(IEnumerable<string> sessions, CancellationToken cancellation)
        {
            string[] ids = sessions.Distinct().ToArray();
            if (ids.Length < 1 || ids.Length > 5 || ids.Any(id => !ServerData.ValidSession(id))) throw new ArgumentException("人数核验每批需包含 1–5 个有效服务器。");
            await Authenticate(cancellation).ConfigureAwait(false);
            var body = new { criteria = new[] { new { key = "attributes.ADVERTISEDSESSIONID_s", op = "ANY_OF", value = ids.Select(id => "Session:" + id).ToArray() } }, maxResults = 1000 };
            string json = await Send("/lobby/v1/" + Deployment + "/lobbies/filter", new StringContent(ServerData.Serializer().Serialize(body), Encoding.UTF8, "application/json"), "Bearer", accessToken, cancellation).ConfigureAwait(false);
            return ServerData.ParseRosters(json, ids, 1000);
        }
        public void Dispose() { accessToken = null; http.Dispose(); }
    }
}
