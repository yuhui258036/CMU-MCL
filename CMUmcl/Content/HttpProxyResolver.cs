using System;
using System.Net;
using System.Net.Sockets;

namespace CMUmcl.Content
{
    /// <summary>
    /// Resolves the outbound HTTP proxy used by the create-flow version-list client.
    /// Some loader endpoints (meta.fabricmc.net, meta.quiltmc.org, maven.neoforged.net)
    /// are only reachable through a local proxy in restricted network environments,
    /// so the version-list <see cref="System.Net.Http.HttpClient"/> opts into one when
    /// available. Resolution order:
    /// <list type="number">
    /// <item><c>CMUMCL_PROXY</c> environment variable (absolute http/https URL).</item>
    /// <item>The system default proxy (<see cref="WebRequest.DefaultWebProxy"/>) when it
    /// is configured and does not bypass the target host.</item>
    /// <item>A reachable local proxy on <c>127.0.0.1:7890</c>/<c>7891</c>/<c>1080</c>
    /// (Clash/Mihomo/V2Ray style), probed with a short timeout.</item>
    /// </list>
    /// Returns <c>null</c> when none of the above applies, in which case the caller keeps
    /// the handler's default proxy behavior.
    /// </summary>
    public static class HttpProxyResolver
    {
        /// <summary>Environment variable for an explicit proxy URL (e.g. <c>http://127.0.0.1:7890</c>).</summary>
        public const string ProxyEnvironmentVariable = "CMUMCL_PROXY";

        /// <summary>Local proxy ports probed as a last resort, in order.</summary>
        private static readonly int[] LocalProxyPorts = { 7890, 7891, 1080 };

        /// <summary>Representative host used to test whether the system proxy applies to loader endpoints.</summary>
        private static readonly Uri ProxyProbeUri = new("https://meta.fabricmc.net/");

        private static readonly TimeSpan LocalProbeTimeout = TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// Returns the proxy to use for the version-list client, or <c>null</c> to keep
        /// the default handler behavior. Never throws.
        /// </summary>
        public static IWebProxy? ResolveProxy()
        {
            // 1. Explicit environment override wins over everything else.
            if (TryResolveEnvironmentProxy(out IWebProxy? envProxy))
                return envProxy;

            // 2. System default proxy, when configured and not bypassed for the host.
            if (TryResolveSystemProxy(out IWebProxy? systemProxy))
                return systemProxy;

            // 3. Local loopback proxy probe (Clash/Mihomo/V2Ray defaults).
            foreach (int port in LocalProxyPorts)
            {
                if (IsPortOpen("127.0.0.1", port))
                    return new WebProxy($"http://127.0.0.1:{port}");
            }

            return null;
        }

        private static bool TryResolveEnvironmentProxy(out IWebProxy? proxy)
        {
            proxy = null;

            string? value = Environment.GetEnvironmentVariable(ProxyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;

            proxy = new WebProxy(uri);
            return true;
        }

        private static bool TryResolveSystemProxy(out IWebProxy? proxy)
        {
            proxy = null;

            try
            {
                IWebProxy? system = WebRequest.DefaultWebProxy;
                if (system is null)
                    return false;

                if (system.IsBypassed(ProxyProbeUri))
                    return false;

                Uri? address = system.GetProxy(ProxyProbeUri);
                if (address is null || string.Equals(address.AbsoluteUri, ProxyProbeUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                    return false;

                proxy = system;
                return true;
            }
            catch
            {
                // Proxy detection is best-effort; any failure falls through to the local probe.
                return false;
            }
        }

        private static bool IsPortOpen(string host, int port)
        {
            try
            {
                using var client = new TcpClient();
                IAsyncResult result = client.BeginConnect(host, port, null, null);
                bool connected = result.AsyncWaitHandle.WaitOne(LocalProbeTimeout);
                if (!connected)
                {
                    client.Close();
                    return false;
                }

                client.EndConnect(result);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
