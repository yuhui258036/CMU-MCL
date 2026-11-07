using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMUmcl.Content;

namespace CMUmcl.Auth
{
    /// <summary>
    /// Persists the authentication session to <c>%LOCALAPPDATA%\CMUmcl\auth.dat</c>,
    /// encrypted with DPAPI (current user scope). The password is never stored.
    /// The <c>clientToken</c> is a stable GUID that is generated once and kept
    /// across runs even when the user chooses not to remember their login.
    /// </summary>
    public sealed class AuthStore
    {
        private static readonly object Sync = new object();
        private static string DirectoryPath => LauncherPaths.RootDirectory;
        private static string FilePath => Path.Combine(DirectoryPath, "auth.dat");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        /// <summary>Internal on-disk shape (encrypted). Keeps clientToken stable even when the session is cleared.</summary>
        private sealed class StoredData
        {
            public string ClientToken { get; set; } = string.Empty;
            public AuthSession? Session { get; set; }
        }

        /// <summary>Gets the persisted client token, or creates and persists a new stable GUID if missing.</summary>
        public string GetOrCreateClientToken()
        {
            lock (Sync)
            {
                var data = LoadStoredData();
                if (!string.IsNullOrWhiteSpace(data?.ClientToken))
                    return data.ClientToken!;

                string token = Guid.NewGuid().ToString("D");
                var updated = data ?? new StoredData();
                updated.ClientToken = token;
                SaveStoredData(updated);
                return token;
            }
        }

        /// <summary>Loads the saved session, or null if there is none or it is unreadable.</summary>
        public AuthSession? LoadSession()
        {
            lock (Sync)
            {
                return LoadStoredData()?.Session;
            }
        }

        /// <summary>Saves the session (remember-login). Also persists the current client token.</summary>
        public void SaveSession(AuthSession session)
        {
            lock (Sync)
            {
                var data = LoadStoredData() ?? new StoredData();
                data.ClientToken = string.IsNullOrWhiteSpace(data.ClientToken) ? session.ClientToken : data.ClientToken;
                data.Session = session;
                SaveStoredData(data);
            }
        }

        /// <summary>Clears any saved session while preserving the stable client token.</summary>
        public void ClearSession()
        {
            lock (Sync)
            {
                var data = LoadStoredData() ?? new StoredData();
                data.Session = null;
                SaveStoredData(data);
            }
        }

        /// <summary>Clears the saved session only if it is still <paramref name="expected"/>.</summary>
        internal bool TryClearSession(AuthSession expected)
        {
            lock (Sync)
            {
                var data = LoadStoredData();
                if (data?.Session == null || data.Session.AccessToken != expected.AccessToken
                    || data.Session.ClientToken != expected.ClientToken)
                    return false;
                data.Session = null;
                SaveStoredData(data);
                return true;
            }
        }

        internal bool TryReplaceSession(AuthSession expected, AuthSession updated)
        {
            lock (Sync)
            {
                var data = LoadStoredData();
                if (data?.Session == null || data.Session.AccessToken != expected.AccessToken
                    || data.Session.ClientToken != expected.ClientToken
                    || data.Session.SelectedProfileId != expected.SelectedProfileId)
                    return false;
                data.Session = updated;
                SaveStoredData(data);
                return true;
            }
        }

        private static StoredData? LoadStoredData()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;

                byte[] protectedBytes = File.ReadAllBytes(FilePath);
                byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(plainBytes);
                return JsonSerializer.Deserialize<StoredData>(json, JsonOptions);
            }
            catch
            {
                // Corrupt or undecryptable file: treat as absent rather than crashing startup.
                return null;
            }
        }

        private static void SaveStoredData(StoredData data)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string json = JsonSerializer.Serialize(data, JsonOptions);
                byte[] plainBytes = Encoding.UTF8.GetBytes(json);
                byte[] protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(FilePath, protectedBytes);
            }
            catch
            {
                // Failing to persist is non-fatal; the in-memory session still works for this run.
            }
        }
    }
}
