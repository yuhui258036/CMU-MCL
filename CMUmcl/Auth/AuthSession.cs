using System;
using System.Text.Json.Serialization;

namespace CMUmcl.Auth
{
    /// <summary>
    /// A persisted or in-memory authentication session for the CSU-MC Yggdrasil skin site.
    /// The password is never stored here or anywhere else.
    /// </summary>
    public sealed class AuthSession
    {
        [JsonPropertyName("clientToken")]
        public string ClientToken { get; set; } = string.Empty;

        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("selectedProfileId")]
        public string SelectedProfileId { get; set; } = string.Empty;

        [JsonPropertyName("selectedProfileName")]
        public string SelectedProfileName { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("savedAt")]
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
