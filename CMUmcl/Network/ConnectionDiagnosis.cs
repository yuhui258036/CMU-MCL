// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using CMUmcl.Auth;
using CMUmcl.Content;

namespace CMUmcl.Network
{
    internal enum ConnectionProblem
    {
        /// <summary>The internet works but the skin site does not answer properly.</summary>
        SkinSiteDown,
        /// <summary>The user's own network (or proxy) cannot reach the internet.</summary>
        LocalNetwork,
        /// <summary>Not a connectivity failure, or it could not be pinned on either side.</summary>
        Unknown,
    }

    /// <summary>
    /// Tells "the skin site is down" apart from "this computer is offline". A failed skin-site
    /// request alone cannot say which side is at fault, so this asks a few unrelated sites
    /// through the same proxy settings, then the skin site once more.
    /// </summary>
    internal static class ConnectionDiagnosis
    {
        internal static Func<Exception, CancellationToken, Task<ConnectionProblem>>? Override;
        internal static HttpMessageHandler? HandlerOverride;
        internal static Func<bool>? NetworkAvailableOverride;

        // Any HTTP answer from these proves the internet (and any configured proxy) works.
        internal static readonly string[] ReferenceUrls =
        {
            "http://www.msftconnecttest.com/connecttest.txt",
            "https://www.baidu.com/",
            ResourceManifestClient.BaseUrl + "/",
        };

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

        internal static async Task<ConnectionProblem> DiagnoseAsync(Exception error, CancellationToken ct = default)
        {
            if (Override != null) return await Override(error, ct).ConfigureAwait(false);

            var failure = error switch
            {
                YggdrasilException auth => auth.Failure,
                HttpRequestException or TaskCanceledException => YggdrasilFailure.Unreachable,
                _ => YggdrasilFailure.Other,
            };
            if (failure == YggdrasilFailure.Other) return ConnectionProblem.Unknown;

            bool adapter;
            try { adapter = NetworkAvailableOverride?.Invoke() ?? NetworkInterface.GetIsNetworkAvailable(); }
            catch { adapter = true; }
            if (!adapter) return ConnectionProblem.LocalNetwork;

            var handler = HandlerOverride ?? LauncherProxy.CreateHandler(allowAutoRedirect: false, useCookies: false);
            using var http = new HttpClient(handler, disposeHandler: HandlerOverride == null) { Timeout = ProbeTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CMUmcl-connectivity");

            if (!await AnyAnswersAsync(http, ct).ConfigureAwait(false)) return ConnectionProblem.LocalNetwork;

            // The internet works. Ask the skin site again so one dropped request is not called an outage.
            return await SkinSiteHealthyAsync(http, ct).ConfigureAwait(false) ? ConnectionProblem.Unknown : ConnectionProblem.SkinSiteDown;
        }

        private static async Task<bool> AnyAnswersAsync(HttpClient http, CancellationToken ct)
        {
            using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var probes = ReferenceUrls.Select(url => AnswersAsync(http, url, done.Token)).ToList();
            try
            {
                while (probes.Count > 0)
                {
                    var finished = await Task.WhenAny(probes).ConfigureAwait(false);
                    probes.Remove(finished);
                    if (await finished.ConfigureAwait(false)) return true;
                }
                return false;
            }
            finally { done.Cancel(); }
        }

        private static async Task<bool> AnswersAsync(HttpClient http, string url, CancellationToken ct)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        private static async Task<bool> SkinSiteHealthyAsync(HttpClient http, CancellationToken ct)
        {
            try
            {
                // The Yggdrasil API root serves the server metadata.
                using var response = await http.GetAsync(YggdrasilClient.ApiRoot, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                return (int)response.StatusCode < 500;
            }
            catch { return false; }
        }
    }
}
