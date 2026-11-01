using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Auth
{
    /// <summary>Yggdrasil player profile (role / skin character).</summary>
    public sealed class YggdrasilProfile
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        public override string ToString() => string.IsNullOrEmpty(Name) ? Id : Name;
    }

    /// <summary>Result of a successful /authserver/authenticate call.</summary>
    public sealed class AuthenticateResult
    {
        public string AccessToken { get; set; } = string.Empty;
        public string ClientToken { get; set; } = string.Empty;
        public IReadOnlyList<YggdrasilProfile> AvailableProfiles { get; set; } = Array.Empty<YggdrasilProfile>();
        public YggdrasilProfile? SelectedProfile { get; set; }
        public YggdrasilUser? User { get; set; }
    }

    /// <summary>Result of a successful /authserver/refresh call.</summary>
    public sealed class RefreshResult
    {
        public string AccessToken { get; set; } = string.Empty;
        public string ClientToken { get; set; } = string.Empty;
        public YggdrasilProfile? SelectedProfile { get; set; }
        public YggdrasilUser? User { get; set; }
    }

    /// <summary>Minimal user descriptor returned by the Yggdrasil server.</summary>
    public sealed class YggdrasilUser
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;
    }

    /// <summary>Raised when the Yggdrasil server returns an error or an unexpected status.</summary>
    public sealed class YggdrasilException : Exception
    {
        public YggdrasilException(string message) : base(message) { }
        public YggdrasilException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// HTTP client for the CSU-MC classic Yggdrasil API.
    /// Only classic Yggdrasil is supported (no OIDC / Janus).
    /// </summary>
    public sealed class YggdrasilClient : IDisposable
    {
        public const string ApiRoot = "https://csu-mc.magicalsheep.cn/api/yggdrasil";
        public const string RegisterUrl = "https://csu-mc.magicalsheep.cn/auth/register";
        public const string LoginPageUrl = "https://csu-mc.magicalsheep.cn/auth/login";
        private const string UserAgent = "CMUmcl/0.1.0";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly HttpClient _http;

        public YggdrasilClient()
        {
            var handler = CMUmcl.Network.LauncherProxy.CreateHandler();

            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20),
                BaseAddress = new Uri(ApiRoot + "/"),
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }

        /// <summary>
        /// Authenticates with the Yggdrasil API and returns the parsed result.
        /// Throws <see cref="YggdrasilException"/> with a friendly Chinese message on failure.
        /// </summary>
        public async Task<AuthenticateResult> AuthenticateAsync(
            string username, string password, string clientToken, CancellationToken ct = default)
        {
            var payload = new
            {
                agent = new { name = "Minecraft", version = 1 },
                username,
                password,
                clientToken,
                requestUser = true,
            };

            string body = await PostAsync("authserver/authenticate", payload, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var result = new AuthenticateResult
            {
                AccessToken = GetString(root, "accessToken"),
                ClientToken = GetString(root, "clientToken"),
            };

            if (root.TryGetProperty("availableProfiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
            {
                var list = new List<YggdrasilProfile>();
                foreach (var p in profiles.EnumerateArray())
                {
                    list.Add(new YggdrasilProfile
                    {
                        Id = GetString(p, "id"),
                        Name = GetString(p, "name"),
                    });
                }
                result.AvailableProfiles = list;
            }

            if (root.TryGetProperty("selectedProfile", out var sel) && sel.ValueKind == JsonValueKind.Object)
            {
                result.SelectedProfile = new YggdrasilProfile
                {
                    Id = GetString(sel, "id"),
                    Name = GetString(sel, "name"),
                };
            }

            if (root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                result.User = new YggdrasilUser
                {
                    Id = GetString(user, "id"),
                    Username = GetString(user, "username"),
                };
            }

            return result;
        }

        /// <summary>
        /// Validates an access token. Returns true for 204 (valid), false for 403 (invalid).
        /// </summary>
        public async Task<bool> ValidateAsync(string accessToken, string clientToken, CancellationToken ct = default)
        {
            var payload = new { accessToken, clientToken };

            try
            {
                using var resp = await SendAsync(HttpMethod.Post, "authserver/validate", payload, ct)
                    .ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.NoContent) return true;
                if (resp.StatusCode == HttpStatusCode.Forbidden) return false;

                // Any other status (e.g. 5xx) is treated as an unavailable/error state.
                throw new YggdrasilException("皮肤站暂时不可用，请稍后再试。");
            }
            catch (YggdrasilException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                throw new YggdrasilException("网络错误，无法连接皮肤站，请检查网络连接。");
            }
            catch (TaskCanceledException)
            {
                throw new YggdrasilException("连接皮肤站超时，请稍后再试。");
            }
        }

        /// <summary>
        /// Refreshes an access token. Returns the new token and selected profile.
        /// </summary>
        public async Task<RefreshResult> RefreshAsync(string accessToken, string clientToken, CancellationToken ct = default)
        {
            var payload = new
            {
                accessToken,
                clientToken,
                requestUser = true,
            };

            string body = await PostAsync("authserver/refresh", payload, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var result = new RefreshResult
            {
                AccessToken = GetString(root, "accessToken"),
                ClientToken = GetString(root, "clientToken"),
            };

            if (root.TryGetProperty("selectedProfile", out var sel) && sel.ValueKind == JsonValueKind.Object)
            {
                result.SelectedProfile = new YggdrasilProfile
                {
                    Id = GetString(sel, "id"),
                    Name = GetString(sel, "name"),
                };
            }

            if (root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                result.User = new YggdrasilUser
                {
                    Id = GetString(user, "id"),
                    Username = GetString(user, "username"),
                };
            }

            return result;
        }

        /// <summary>POSTs JSON and returns the response body string, translating errors into <see cref="YggdrasilException"/>.</summary>
        private async Task<string> PostAsync(string path, object payload, CancellationToken ct)
        {
            using var resp = await SendAsync(HttpMethod.Post, path, payload, ct).ConfigureAwait(false);

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                return body;
            }

            throw BuildException(resp.StatusCode, body);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object payload, CancellationToken ct)
        {
            try
            {
                var json = JsonSerializer.Serialize(payload, JsonOptions);
                using var req = new HttpRequestMessage(method, path)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                return await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new YggdrasilException("网络错误，无法连接皮肤站，请检查网络连接。", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new YggdrasilException("连接皮肤站超时，请稍后再试。", ex);
            }
        }

        /// <summary>Builds a friendly Chinese exception from the Yggdrasil error JSON or status code.</summary>
        private static YggdrasilException BuildException(HttpStatusCode status, string body)
        {
            string? error = null;
            string? errorMessage = null;

            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("error", out var e))
                        error = e.GetString();
                    if (doc.RootElement.TryGetProperty("errorMessage", out var m))
                        errorMessage = m.GetString();
                }
                catch (JsonException)
                {
                    // Fall through to status-based mapping.
                }
            }

            string friendly = MapError(error, errorMessage, status);
            return new YggdrasilException(friendly);
        }

        private static string MapError(string? error, string? errorMessage, HttpStatusCode status)
        {
            string e = error ?? string.Empty;
            string m = errorMessage ?? string.Empty;

            // Explicit Yggdrasil error codes first.
            if (e.Equals("ForbiddenOperationException", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase) ||
                    m.Contains("用户名或密码", StringComparison.OrdinalIgnoreCase))
                    return "账号或密码错误，请重新输入。";
                if (m.Contains("Invalid token", StringComparison.OrdinalIgnoreCase))
                    return "登录状态已过期，请重新登录。";
                return "皮肤站拒绝了本次请求：" + (string.IsNullOrEmpty(m) ? e : m);
            }

            if (e.Equals("IllegalArgumentException", StringComparison.OrdinalIgnoreCase))
                return "请求参数有误，请检查输入后重试。";

            if (status == HttpStatusCode.NotFound)
                return "皮肤站接口不存在（404），请确认站点可用。";
            if (status == HttpStatusCode.Forbidden)
                return "登录状态无效，请重新登录。";
            if ((int)status >= 500)
                return "皮肤站暂时不可用，请稍后再试。";
            if (status == HttpStatusCode.Unauthorized)
                return "账号或密码错误，请重新输入。";

            return "登录失败：" + (string.IsNullOrEmpty(m) ? $"HTTP {(int)status}" : m);
        }

        private static string GetString(JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? string.Empty;
            return string.Empty;
        }

        public void Dispose() => _http.Dispose();
    }
}
