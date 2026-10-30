using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Content
{
    /// <summary>
    /// Fetches and verifies the signed server-pack manifest from the resource site.
    /// Fails closed: the manifest is only parsed after its detached ECDSA P-256/SHA-256
    /// signature verifies against the embedded public key.
    /// </summary>
    public sealed class ResourceManifestClient : IDisposable
    {
        public const string BaseUrl = "https://csumcl.mcapcs.cn";
        public const string AllowedPackHost = "csumcl.mcapcs.cn";
        private const string LegacyAllowedPackHost = "103.24.216.15";
        private const string UserAgent = "CMUmcl/0.1.0";
        private const string SignatureAlgorithm = "ECDSA-P256-SHA256";

        // Embedded manifest-signing public key (SPKI PEM). Out-of-band; never fetched.
        private const string PublicKeyPem =
            "-----BEGIN PUBLIC KEY-----\n" +
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4oPSKR6LHNu1iifTLCpw8NGfzTiw\n" +
            "anmgQl/6+zHvodFc6NNGbKkL2GcilT0DdpZQAu/1Dy3iomuoGLrS5QhDvw==\n" +
            "-----END PUBLIC KEY-----";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly HttpClient _http;

        public ResourceManifestClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            };

            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20),
                BaseAddress = new Uri(BaseUrl + "/"),
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }

        /// <summary>
        /// Downloads the manifest and its detached signature, verifies the signature
        /// over the exact bytes, then parses and allowlist-checks the result.
        /// </summary>
        public async Task<ResourceManifest> GetManifestAsync(CancellationToken ct = default)
        {
            byte[] manifestBytes;
            byte[] sigBytes;

            try
            {
                manifestBytes = await _http.GetByteArrayAsync("manifest.json", ct).ConfigureAwait(false);
                sigBytes = await _http.GetByteArrayAsync("manifest.json.sig", ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ResourceException("网络错误，无法连接资源站，请检查网络连接。", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new ResourceException("连接资源站超时，请稍后再试。", ex);
            }

            ManifestSignature signature;
            try
            {
                signature = JsonSerializer.Deserialize<ManifestSignature>(sigBytes, JsonOptions)
                    ?? throw new ResourceException("签名文件为空或格式无效。");
            }
            catch (JsonException ex)
            {
                throw new ResourceException("签名文件格式无效。", ex);
            }

            if (!string.Equals(signature.Algorithm, SignatureAlgorithm, StringComparison.OrdinalIgnoreCase))
            {
                throw new ResourceException("不支持的签名算法，已拒绝加载。");
            }

            if (!VerifySignature(manifestBytes, signature.Signature))
            {
                throw new ResourceException("清单签名验证失败，已拒绝加载。");
            }

            ResourceManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ResourceManifest>(manifestBytes, JsonOptions)
                    ?? throw new ResourceException("清单内容为空或格式无效。");
            }
            catch (JsonException ex)
            {
                throw new ResourceException("清单内容格式无效。", ex);
            }

            // Allowlist: every pack URL must be absolute HTTPS on the allowed host.
            foreach (var instance in manifest.Instances)
            {
                if (!IsAllowedPackUrl(instance.Pack.Url))
                {
                    throw new ResourceException($"整合包下载地址不被允许，已拒绝加载（实例：{instance.Id}）。");
                }
            }

            return manifest;
        }

        private static bool VerifySignature(byte[] data, string signatureBase64)
        {
            try
            {
                byte[] signature = Convert.FromBase64String(signatureBase64);
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(PublicKeyPem);
                return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsAllowedPackUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;
            return string.Equals(uri.Host, AllowedPackHost, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, LegacyAllowedPackHost, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose() => _http.Dispose();
    }
}
