# PCL-CMU 魔改版 — PCL-CE Client Integration Points

This document describes **where** the PCL-CE fork plugs in the local resource
protocol. It references real PCL-CE symbols (verified against the `PCL-CE`
tree) but provides **code-style snippets only** — the actual PCL-CE source is
not modified by this package.

> License note: PCL-CMU 魔改版 is a fork of PCL-CE (Plain Craft Launcher CE).
> Before distributing the fork you must comply with PCL/PCL-CE's license
> obligations (preserve copyright/attribution and any license text, state
> changes, and follow the original license terms). Nothing in this package
> modifies PCL-CE; the fork work happens in a separate tree.

---

## 1. Auth gate + revision check + forced update

Hook into `ModLaunch.McLaunchPrecheck()` (currently `private static void` in
`Modules/Minecraft/ModLaunch.cs`). This is the natural single choke point that
runs before the game is launched.

```csharp
// Style only — wire into ModLaunch.McLaunchPrecheck()
private static async Task<bool> CmuPrecheckAsync()
{
    // 1. Auth gate: classic Yggdrasil password login against Blessing Skin.
    //    https://csu-mc.magicalsheep.cn — no OIDC/Janus, no /api/launcher/*.
    var account = await CmuAuth.LoginAsync(user, password);   // Yggdrasil /authserver
    if (account is null) return false;                        // abort launch

    // 2. Revision check against the local resource server.
    var manifest = await CmuResource.GetManifestAsync();       // GET /manifest.json
    var instance = manifest.Instances.First(i => i.Id == "test");
    var localRevision = CmuState.LoadRevision("test");         // SQLite / state.json

    if (localRevision != instance.Revision)
    {
        // 3. Forced update: download + verify before continuing.
        var ok = await CmuResource.DownloadAndVerifyPackAsync(instance);
        if (!ok) return false;                                 // refuse to launch
        CmuState.SaveRevision("test", instance.Revision);
    }

    return true;
}
```

Key behaviours:
* **Auth gate** — launch is blocked until the Yggdrasil password login
  succeeds. Tokens must be persisted via `PCL.Core`'s secret storage (see §2),
  **not** in plain JSON next to the resource server.
* **Revision check** — the client stores the last installed `revision`; any
  remote change forces a re-download of the pack.
* **Forced update** — pack download + SHA-1/size verification must pass before
  `Minecraft` is allowed to launch. On failure, surface an error UI and abort.

---

## 2. PCL.Core: HTTP, SQLite, secure token storage

Use `PCL.Core` for the cross-cutting plumbing rather than hand-rolling it.

### 2.1 Secure token storage

`PCL.Core.Utils.Secret.EncryptHelper` already provides DPAPI-style protection
(`EncryptHelper.SecretEncrypt` / `EncryptHelper.SecretDecrypt`). Use it for the
Yggdrasil access token and the refresh token:

```csharp
// Style only
PCL.Core.Utils.Secret.EncryptHelper.SecretEncrypt(accessToken);   // -> ciphertext string
var token = PCL.Core.Utils.Secret.EncryptHelper.SecretDecrypt(ciphertext);
```

Never store the raw token in `state.json`, `manifest.json`, or any `wwwroot`
file. The resource server and the skin/auth site hold **different** secrets and
must never share them (see `security.md` §2).

### 2.2 HTTP

`PCL.Core`'s HTTP stack (timeout + retry + `HttpClient` reuse) should be used
for both the skin site and the resource server. Points to honour:
* **Do not** disable TLS validation. The resource server uses a trusted
  loopback certificate (or a production CA in deployment).
* Keep `Accept-Ranges`/ETag support so large pack/update downloads can resume.
* Treat `Cache-Control: no-cache` on metadata as a mandate to revalidate.

### 2.3 SQLite

`PCL.Core`'s SQLite layer can hold:
* `local_revision(instance_id, revision)` — for the revision gate.
* `announcement_seen(announcement_id)` — to track read announcements.
* `friends_cache` — cached friends list, refreshed from `/friends.json`.

---

## 3. Update metadata + signature verification

Implement a custom `IUpdateSource` and register it with `UpdateManager`.

Verified interface (PCL-CE `Modules/Updates/IUpdateSource.cs`):

```csharp
public interface IUpdateSource
{
    string SourceName { get; set; }
    bool IsAvailable();
    bool RefreshCache();
    VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch);
    bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode);
    VersionAnnouncementDataModel GetAnnouncementList();
}
```

`VersionDataModel` fields (PCL-CE `Modules/Updates/VersionDataModel.cs`):

```csharp
public class VersionDataModel
{
    public string Changelog { get; set; }   // map: latest.json notes
    public string Sha256     { get; set; }   // map: latest.json sha256
    public string Source     { get; set; }   // map: latest.json url
    public int    VersionCode { get; set; }
    public string VersionName { get; set; }  // map: latest.json version
}
```

Custom source (style only):

```csharp
// Style only
public sealed class CmuResourceUpdateSource : IUpdateSource
{
    public string SourceName { get; set; } = "PCL-CMU 魔改版 本地资源";

    public bool IsAvailable() => true;
    public bool RefreshCache() { /* GET /update/windows-x86_64/latest.json */ return true; }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var latest = CmuResource.GetLatest();            // latest.json
        return new VersionDataModel
        {
            VersionName = latest.Version,
            Sha256      = latest.Sha256,
            Source      = latest.Url,
            Changelog   = latest.Notes,
        };
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
        => /* compare currentVersion with latest.Version */ true;

    public VersionAnnouncementDataModel GetAnnouncementList()
        => /* map announcements.json */ new VersionAnnouncementDataModel();
}
```

### 3.1 Signature verification (critical)

The update artifact is signed with **ECDSA P-256 / SHA-256**. `VersionDataModel`
only carries `Sha256`; the ECDSA signature must be verified **before** applying
the downloaded artifact. Add a small verifier next to the update path:

```csharp
// Style only
using System.Security.Cryptography;

static bool VerifyUpdate(byte[] artifactBytes, string signatureBase64, string publicKeyPem)
{
    using var ecdsa = ECDsa.Create();
    ecdsa.ImportFromPem(publicKeyPem);                 // SPKI PEM (out-of-band)
    var sig = Convert.FromBase64String(signatureBase64); // IEEE P1363 r||s
    return ecdsa.VerifyData(artifactBytes, sig, HashAlgorithmName.SHA256);
}
```

Rules:
* The **public key is out-of-band** (shipped with the client), never fetched
  from the resource server in the same channel as the artifact.
* Verify `SHA-256` of the downloaded bytes against `latest.sha256` **and** the
  ECDSA signature. If either fails, discard the artifact and refuse the update.
* The signing private key is **never** shipped in the client or the resource
  server's `wwwroot`.

---

## 4. XAML pages

Suggested page structure for the fork (PCL-CE uses WPF pages):

| Page                  | Responsibility                                                   |
|-----------------------|------------------------------------------------------------------|
| `PageLoginGate`       | Blocking login gate; Yggdrasil password form → `CmuAuth.LoginAsync` |
| `PageOnboarding`      | First-run onboarding; shows `manifest.json` instances + download progress |
| `PageHome` / `HomePage` | Replaces the default home; renders `announcements.json` cards + revision status |
| `PageAnnouncements`   | Announcements list; marks seen ids (SQLite `announcement_seen`)  |
| `PageSkins`           | Skin previews; uses `friends.json` `skinUrl` (skin site, not resource server) |
| `PageFriends`         | Friends list from `/friends.json`; tap-to-open profile          |

Style snippets:

```xml
<!-- PageLoginGate: gate before anything else -->
<Grid>
  <StackPanel>
    <TextBlock Text="请先登录 CSU-MC 皮肤站"/>
    <TextBox x:Name="UserBox"/>
    <PasswordBox x:Name="PassBox"/>
    <Button Content="登录" Click="OnLoginClick"/>
  </StackPanel>
</Grid>
```

```csharp
// PageLoginGate.xaml.cs — style only
private async void OnLoginClick(object sender, RoutedEventArgs e)
{
    var account = await CmuAuth.LoginAsync(UserBox.Text, PassBox.Password);
    if (account is null) { StatusText.Text = "登录失败"; return; }
    await CmuAuth.PersistTokenAsync(account);   // EncryptHelper.SecretEncrypt
    NavigationService.Navigate(new PageHome());
}
```

Remember: the auth/skin site (`csu-mc.magicalsheep.cn`) is the **only** place
that talks to Blessing Skin 6.0.2. The resource server serves public metadata
and artifacts only; it never handles credentials.
