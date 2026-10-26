using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SpotifyMini
{
    public class ApiException : Exception
    {
        public int Status { get; }
        public ApiException(int status, string message) : base(message) { Status = status; }
    }

    public record Playlist(string Id, string Name, string Uri, bool Mine, bool Liked);
    public record SearchItem(string Kind, string Name, string Sub, string Uri);
    public record PlayerState(string TrackUri, bool Shuffle, string Repeat, int? Volume,
                              long ProgressMs, long DurationMs, bool Playing);

    // Spotify Web API with the user's own developer app (PKCE, no client secret).
    // Same calls as spotify-lists.py in the Linux applet.
    public class SpotifyApi
    {
        public const int RedirectPort = 8898;
        public static readonly string RedirectUri = $"http://127.0.0.1:{RedirectPort}/callback";
        const string Api = "https://api.spotify.com/v1";
        static readonly string TokenFile = Path.Combine(Config.Dir, "token.json");
        static readonly string CacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyMini", "playlists.json");
        static readonly string[] Scopes =
        {
            "playlist-read-private", "playlist-read-collaborative",
            "playlist-modify-private", "playlist-modify-public",
            "user-library-read", "user-library-modify",
            "user-read-playback-state", "user-modify-playback-state",
        };

        readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        JsonObject _token;
        string _me;

        public SpotifyApi()
        {
            try { _token = JsonNode.Parse(File.ReadAllText(TokenFile)) as JsonObject; }
            catch { _token = null; }
        }

        public bool LoggedIn => _token?["refresh_token"] != null;

        // ---------- auth ----------

        public async Task<string> Login(string clientId, CancellationToken cancel)
        {
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomNumberGenerator.GetBytes(16));
            string url = "https://accounts.spotify.com/authorize?" + Query(new()
            {
                ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = RedirectUri,
                ["scope"] = string.Join(' ', Scopes), ["state"] = state,
                ["code_challenge_method"] = "S256", ["code_challenge"] = challenge,
            });

            // tiny one-shot HTTP server on 127.0.0.1 (HttpListener would need admin rights)
            var listener = new TcpListener(IPAddress.Loopback, RedirectPort);
            listener.Start();
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                    using var stream = client.GetStream();
                    var buf = new byte[8192];
                    int n = await stream.ReadAsync(buf, timeout.Token);
                    string line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];   // GET /callback?... HTTP/1.1
                    string path = line.Split(' ').ElementAtOrDefault(1) ?? "";
                    if (!path.StartsWith("/callback"))
                    {
                        await Respond(stream, 404, "");
                        continue;
                    }
                    var q = ParseQuery(path);
                    bool ok = q.GetValueOrDefault("state") == state && q.ContainsKey("code");
                    await Respond(stream, 200, ok ? "Cuenta conectada. Ya puedes cerrar esta pestaña."
                                                  : "No se pudo conectar: " + q.GetValueOrDefault("error", "bad-state"));
                    if (!ok) throw new ApiException(400, q.GetValueOrDefault("error", "bad-state"));

                    var tok = await TokenRequest(new()
                    {
                        ["grant_type"] = "authorization_code", ["code"] = q["code"],
                        ["redirect_uri"] = RedirectUri, ["client_id"] = clientId, ["code_verifier"] = verifier,
                    });
                    StoreToken(tok, clientId);
                    var me = await Get("/me");
                    return (string)me["display_name"] ?? (string)me["id"];
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        static async Task Respond(NetworkStream stream, int status, string message)
        {
            string body = "<html><body style=\"font-family:sans-serif;background:#121212;color:#fff;display:flex;" +
                          "align-items:center;justify-content:center;height:100vh\"><h2>" +
                          WebUtility.HtmlEncode(message) + "</h2></body></html>";
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            string head = $"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                          $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(bytes);
        }

        public void Logout()
        {
            _token = null;
            _me = null;
            foreach (var f in new[] { TokenFile, CacheFile })
                if (File.Exists(f)) File.Delete(f);
        }

        async Task<JsonObject> TokenRequest(Dictionary<string, string> form)
        {
            var res = await _http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
            string text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, text);
            return (JsonObject)JsonNode.Parse(text);
        }

        void StoreToken(JsonObject tok, string clientId)
        {
            _token = new JsonObject
            {
                ["client_id"] = clientId,
                ["access_token"] = (string)tok["access_token"],
                ["refresh_token"] = (string)tok["refresh_token"] ?? (string)_token?["refresh_token"],
                ["expires_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ((int?)tok["expires_in"] ?? 3600),
            };
            Directory.CreateDirectory(Config.Dir);
            File.WriteAllText(TokenFile, _token.ToJsonString());
        }

        async Task<string> AccessToken()
        {
            if (!LoggedIn) throw new ApiException(401, "not-logged-in");
            if ((long)_token["expires_at"] - 60 < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                var tok = await TokenRequest(new()
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = (string)_token["refresh_token"],
                    ["client_id"] = (string)_token["client_id"],
                });
                StoreToken(tok, (string)_token["client_id"]);
            }
            return (string)_token["access_token"];
        }

        // ---------- API ----------

        async Task<JsonNode> Call(HttpMethod method, string path, Dictionary<string, string> query = null, object body = null)
        {
            string url = path.StartsWith("http") ? path : Api + path;
            if (query != null) url += "?" + Query(query);
            for (int attempt = 0; ; attempt++)
            {
                var req = new HttpRequestMessage(method, url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await AccessToken());
                if (body != null)
                    req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                else if (method != HttpMethod.Get)
                    req.Content = new StringContent("");
                var res = await _http.SendAsync(req);
                string text = await res.Content.ReadAsStringAsync();
                if ((int)res.StatusCode == 429 && attempt < 2)
                {
                    double wait = res.Headers.RetryAfter?.Delta?.TotalSeconds ?? 2;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, 10)));
                    continue;
                }
                if (!res.IsSuccessStatusCode)
                {
                    string msg = text;
                    try { msg = (string)JsonNode.Parse(text)?["error"]?["message"] ?? text; } catch { }
                    throw new ApiException((int)res.StatusCode, msg);
                }
                if (string.IsNullOrWhiteSpace(text)) return null;
                try { return JsonNode.Parse(text); }
                catch (JsonException) { return null; }   // some player endpoints answer with plain text
            }
        }

        Task<JsonNode> Get(string path, Dictionary<string, string> q = null) => Call(HttpMethod.Get, path, q);

        async Task<List<JsonNode>> Paged(string path, Dictionary<string, string> q)
        {
            var items = new List<JsonNode>();
            string url = path;
            bool first = true;
            while (url != null)
            {
                var page = await Get(url, first ? q : null);
                first = false;
                items.AddRange((page?["items"] as JsonArray ?? new JsonArray()).Where(i => i != null));
                url = (string)page?["next"];
            }
            return items;
        }

        async Task<string> Me() => _me ??= (string)(await Get("/me"))["id"];

        // Me gusta + every playlist in the library
        public async Task<List<Playlist>> Library()
        {
            string me = await Me();
            var list = new List<Playlist> { new("", "Canciones que te gustan", $"spotify:user:{me}:collection", true, true) };
            foreach (var p in await Paged("/me/playlists", new() { ["limit"] = "50" }))
            {
                bool mine = (string)p["owner"]?["id"] == me || (bool?)p["collaborative"] == true;
                list.Add(new((string)p["id"], (string)p["name"], (string)p["uri"], mine, false));
            }
            return list;
        }

        // which of my playlists contain the track, cached by snapshot_id
        public async Task<(bool liked, List<(Playlist list, bool contains)> lists)> TrackState(string track)
        {
            JsonObject cache;
            try { cache = JsonNode.Parse(File.ReadAllText(CacheFile)) as JsonObject ?? new JsonObject(); }
            catch { cache = new JsonObject(); }

            string me = await Me();
            var result = new List<(Playlist, bool)>();
            foreach (var p in await Paged("/me/playlists", new() { ["limit"] = "50" }))
            {
                if ((string)p["owner"]?["id"] != me && (bool?)p["collaborative"] != true) continue;
                string id = (string)p["id"], snap = (string)p["snapshot_id"];
                HashSet<string> uris;
                if (cache[id] is JsonObject entry && (string)entry["snapshot"] == snap)
                    uris = entry["uris"].AsArray().Select(u => (string)u).ToHashSet();
                else
                {
                    uris = new HashSet<string>();
                    foreach (var it in await Paged($"/playlists/{id}/items", new() { ["limit"] = "50" }))
                    {
                        string u = (string)(it["item"] ?? it["track"])?["uri"];
                        if (u != null) uris.Add(u);
                    }
                    cache[id] = new JsonObject { ["snapshot"] = snap, ["uris"] = new JsonArray(uris.Select(u => (JsonNode)u).ToArray()) };
                }
                result.Add((new Playlist(id, (string)p["name"], (string)p["uri"], true, false), uris.Contains(track)));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile));
            File.WriteAllText(CacheFile, cache.ToJsonString());
            var liked = await Get("/me/library/contains", new() { ["uris"] = track }) as JsonArray;
            return (liked != null && liked.Count > 0 && (bool)liked[0], result);
        }

        public async Task<bool> IsLiked(string track)
        {
            var liked = await Get("/me/library/contains", new() { ["uris"] = track }) as JsonArray;
            return liked != null && liked.Count > 0 && (bool)liked[0];
        }

        public async Task SetInPlaylist(string playlistId, string track, bool add)
        {
            if (add)
                await Call(HttpMethod.Post, $"/playlists/{playlistId}/items", body: new { uris = new[] { track } });
            else
                await Call(HttpMethod.Delete, $"/playlists/{playlistId}/items", body: new { items = new[] { new { uri = track } } });
            if (File.Exists(CacheFile)) File.Delete(CacheFile);   // simplest: rebuild on next look
        }

        public Task SetLiked(string track, bool liked) =>
            Call(liked ? HttpMethod.Put : HttpMethod.Delete, "/me/library", new() { ["uris"] = track });

        public async Task<List<SearchItem>> Search(string text)
        {
            var r = await Get("/search", new() { ["q"] = text, ["type"] = "track,album,artist,playlist", ["limit"] = "5" });
            string Names(JsonNode arr) => string.Join(", ", (arr as JsonArray ?? new JsonArray()).Select(a => (string)a?["name"]));
            IEnumerable<JsonNode> Items(string k) => (r?[k]?["items"] as JsonArray ?? new JsonArray()).Where(i => i != null);
            var list = new List<SearchItem>();
            list.AddRange(Items("tracks").Take(5).Select(t => new SearchItem("track", (string)t["name"], Names(t["artists"]), (string)t["uri"])));
            list.AddRange(Items("albums").Take(3).Select(a => new SearchItem("album", (string)a["name"], Names(a["artists"]), (string)a["uri"])));
            list.AddRange(Items("artists").Take(3).Select(a => new SearchItem("artist", (string)a["name"], "", (string)a["uri"])));
            list.AddRange(Items("playlists").Take(3).Select(p => new SearchItem("playlist", (string)p["name"],
                (string)p["owner"]?["display_name"] ?? "", (string)p["uri"])));
            return list;
        }

        public async Task<PlayerState> State()
        {
            var s = await Get("/me/player");
            if (s == null) return null;
            var item = s["item"];
            return new PlayerState((string)item?["uri"], (bool?)s["shuffle_state"] == true, (string)s["repeat_state"] ?? "off",
                (int?)s["device"]?["volume_percent"], (long?)s["progress_ms"] ?? 0, (long?)item?["duration_ms"] ?? 0,
                (bool?)s["is_playing"] == true);
        }

        // this computer's Spotify: the one named like the PC, else the active one, else any computer
        async Task<string> PickDevice()
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                var devices = (await Get("/me/player/devices"))?["devices"] as JsonArray ?? new JsonArray();
                var d = devices.FirstOrDefault(x => string.Equals((string)x["name"], Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                     ?? devices.FirstOrDefault(x => (bool?)x["is_active"] == true)
                     ?? devices.FirstOrDefault(x => (string)x["type"] == "Computer");
                if (d != null) return (string)d["id"];
                await Task.Delay(1000);   // Spotify just started: give it time to show up
            }
            return null;
        }

        async Task<int> ContextSize(string uri)
        {
            var parts = uri.Split(':');
            string path = uri.EndsWith(":collection") ? "/me/tracks"
                        : parts[1] == "playlist" ? $"/playlists/{parts[2]}/items"
                        : parts[1] == "album" ? $"/albums/{parts[2]}/tracks" : null;
            if (path == null) return 0;
            return (int?)(await Get(path, new() { ["limit"] = "1" }))?["total"] ?? 0;
        }

        // A list with shuffle on starts at a random song (Spotify always starts at the first one).
        // A single song plays alone and then similar ones, like the app.
        public async Task Play(string uri, bool shuffle)
        {
            string dev = await PickDevice() ?? throw new ApiException(404, "no-device");
            object body;
            string kind = uri.Split(':')[1];
            if (kind == "track" || kind == "episode")
                body = new { uris = new[] { uri } };
            else
            {
                int size = shuffle ? await ContextSize(uri) : 0;
                body = size > 1 ? new { context_uri = uri, offset = new { position = Random.Shared.Next(size) } }
                                : (object)new { context_uri = uri };
            }
            await Call(HttpMethod.Put, "/me/player/play", new() { ["device_id"] = dev }, body);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await SetShuffle(shuffle, dev);
                    break;
                }
                catch (ApiException)
                {
                    await Task.Delay(700);
                }
            }
        }

        public Task SetShuffle(bool on, string device = null)
        {
            var q = new Dictionary<string, string> { ["state"] = on ? "true" : "false" };
            if (device != null) q["device_id"] = device;
            return Call(HttpMethod.Put, "/me/player/shuffle", q);
        }

        public Task SetRepeat(string state) => Call(HttpMethod.Put, "/me/player/repeat", new() { ["state"] = state });

        public Task SetVolume(int percent) =>
            Call(HttpMethod.Put, "/me/player/volume", new() { ["volume_percent"] = percent.ToString() });

        public Task Seek(long ms) => Call(HttpMethod.Put, "/me/player/seek", new() { ["position_ms"] = ms.ToString() });

        // ---------- helpers ----------

        static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        static string Query(Dictionary<string, string> q) =>
            string.Join("&", q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

        static Dictionary<string, string> ParseQuery(string path)
        {
            var d = new Dictionary<string, string>();
            int i = path.IndexOf('?');
            if (i < 0) return d;
            foreach (var part in path.Substring(i + 1).Split('&'))
            {
                var kv = part.Split('=', 2);
                d[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            }
            return d;
        }

        // open.spotify.com/playlist/ID?si=… → spotify:playlist:ID
        public static string ToSpotifyUri(string text)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("spotify:")) return text;
            var m = System.Text.RegularExpressions.Regex.Match(text,
                @"open\.spotify\.com/(?:intl-[a-z-]+/)?(track|album|playlist|artist|show|episode)/([A-Za-z0-9]+)");
            return m.Success ? $"spotify:{m.Groups[1].Value}:{m.Groups[2].Value}" : null;
        }
    }
}
