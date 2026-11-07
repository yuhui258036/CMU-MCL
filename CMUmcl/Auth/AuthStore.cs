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
    /// encrypted with DPAPI (current user scope). Password storage is explicit opt-in.
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
            public StoredCredentials? Credentials { get; set; }
        }

        private sealed class StoredCredentials
        {
            public string Username { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public string ProfileId { get; set; } = string.Empty;
            public string Generation { get; set; } = string.Empty;
        }

        // Kept out of AuthSession, game arguments, diagnostics and public settings.
        internal sealed class RememberedLogin
        {
            internal string Username = string.Empty, Password = string.Empty, ProfileId = string.Empty;
            internal string Generation = string.Empty, SessionToken = string.Empty, ClientToken = string.Empty;
        }

        internal bool HasRememberedPassword => LoadRememberedLogin(LoadSession()) != null;

        internal bool SaveLogin(AuthSession session, string username, string password, bool rememberPassword)
        {
            lock (Sync)
            {
                var data = LoadStoredData() ?? new StoredData();
                data.ClientToken = string.IsNullOrWhiteSpace(data.ClientToken) ? session.ClientToken : data.ClientToken;
                data.Session = session;
                data.Credentials = rememberPassword ? new StoredCredentials
                {
                    Username = username, Password = password, ProfileId = session.SelectedProfileId,
                    Generation = Guid.NewGuid().ToString("N"),
                } : null;
                return SaveStoredData(data);
            }
        }

        internal RememberedLogin? LoadRememberedLogin(AuthSession? expected)
        {
            lock (Sync)
            {
                var data = LoadStoredData();
                var credential = data?.Credentials;
                if (expected == null || data?.Session == null || credential == null
                    || string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrEmpty(credential.Password)
                    || string.IsNullOrEmpty(credential.Generation)
                    || !string.Equals(credential.ProfileId, expected.SelectedProfileId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(data.Session.SelectedProfileId, expected.SelectedProfileId, StringComparison.OrdinalIgnoreCase))
                    return null;
                return new RememberedLogin
                {
                    Username = credential.Username, Password = credential.Password, ProfileId = credential.ProfileId,
                    Generation = credential.Generation, SessionToken = data.Session.AccessToken, ClientToken = data.Session.ClientToken,
                };
            }
        }

        internal bool TrySaveAutomaticLogin(RememberedLogin expected, AuthSession updated)
        {
            lock (Sync)
            {
                var data = LoadStoredData();
                if (data?.Credentials?.Generation != expected.Generation || data.Session?.AccessToken != expected.SessionToken
                    || !string.Equals(updated.SelectedProfileId, expected.ProfileId, StringComparison.OrdinalIgnoreCase))
                    return false;
                data.Session = updated;
                SaveStoredData(data);
                return true;
            }
        }

        internal void ForgetRejectedPassword(RememberedLogin expected)
        {
            lock (Sync)
            {
                var data = LoadStoredData();
                if (data?.Credentials?.Generation != expected.Generation) return;
                data.Credentials = null;
                SaveStoredData(data);
            }
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
                if (data.Credentials != null && !string.Equals(data.Credentials.ProfileId, session.SelectedProfileId, StringComparison.OrdinalIgnoreCase))
                    data.Credentials = null;
                SaveStoredData(data);
            }
        }

        /// <summary>Clears any saved session while preserving the stable client token.</summary>
        public void ClearSession() => TryClearSession();

        internal bool TryClearSession()
        {
            lock (Sync)
            {
                var data = LoadStoredData() ?? new StoredData();
                data.Session = null;
                data.Credentials = null;
                return SaveStoredData(data);
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
                data.Credentials = null;
                return SaveStoredData(data);
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

        private static bool SaveStoredData(StoredData data)
        {
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string json = JsonSerializer.Serialize(data, JsonOptions);
                byte[] plainBytes = Encoding.UTF8.GetBytes(json);
                byte[] protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, protectedBytes);
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                else File.Move(temporary, FilePath);
                return true;
            }
            catch
            {
                // Failing to persist is non-fatal; the in-memory session still works for this run.
                return false;
            }
            finally { try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
    }
}
