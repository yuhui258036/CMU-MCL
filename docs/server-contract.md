# PCL-CMU 魔改版 — Local Resource Server Contract

This document is the authoritative description of the local resource server
(`resource-site/`), which is a read-only, HTTPS-only static file server that
accompanies the PCL-CE fork. It is **independent of the skin/auth site**
(`https://csu-mc.magicalsheep.cn`, Blessing Skin 6.0.2) and only ever serves
public, non-secret metadata and artifacts.

---

## 1. Transport

| Property        | Value                                              |
|-----------------|----------------------------------------------------|
| HTTPS bind      | `https://127.0.0.1:5443` (IPv4 loopback only)      |
| HTTP bind       | `http://127.0.0.1:5080` → `308` redirect to HTTPS  |
| Certificate     | self-signed loopback cert (`localhost`, `127.0.0.1`, `::1`), OS-trusted |
| TLS validation  | never disabled on the client                       |

The server binds **only** to `127.0.0.1`. It is not reachable from the LAN or
from `0.0.0.0`. All plain-HTTP traffic is redirected (308) to HTTPS.

---

## 2. Endpoints

| Method | Path                                 | Purpose                              |
|--------|--------------------------------------|--------------------------------------|
| GET    | `/manifest.json`                     | Instance catalog + revision + pack   |
| GET    | `/announcements.json`                | Announcements feed                   |
| GET    | `/friends.json`                      | Friends list (test fixture)          |
| GET    | `/update/windows-x86_64/latest.json` | Launcher update metadata + signature |
| GET    | `/update/windows-x86_64/*.bin`       | Launcher update artifact             |
| GET    | `/packs/test/1.mrpack`               | Modrinth pack artifact               |

Everything else is a 404 (no directory browsing, no default documents, no
unknown-file-type serving).

---

## 3. JSON schemas

### 3.1 `manifest.json`

```json
{
  "formatVersion": 1,
  "instances": [
    {
      "id": "test",
      "name": "PCL-CMU Test Instance",
      "revision": 1,
      "required": true,
      "pack": {
        "url": "https://127.0.0.1:5443/packs/test/1.mrpack",
        "sha1": "e62e6b6aa61d0e376fd206f4c4bfa951b8d9ffa2",
        "size": 312,
        "format": "mrpack"
      }
    }
  ]
}
```

| Field               | Type   | Meaning                                        |
|---------------------|--------|------------------------------------------------|
| `formatVersion`     | int    | Schema version (currently `1`)                 |
| `instances[]`       | array  | One entry per managed instance                 |
| `instances[].id`    | string | Stable machine id (the client looks up `test`) |
| `instances[].revision` | int | Monotonic revision; any change triggers re-download |
| `instances[].required` | bool | If true, must be installed before launch       |
| `instances[].pack.url` | string | HTTPS URL of the pack artifact                 |
| `instances[].pack.sha1` | string | Lowercase hex SHA-1 of the pack bytes          |
| `instances[].pack.size` | int | Exact byte length of the pack                  |
| `instances[].pack.format` | string | `mrpack` (Modrinth)                           |

### 3.2 `announcements.json`

```json
{
  "formatVersion": 1,
  "announcements": [
    {
      "id": "ann-2026-001",
      "title": "…",
      "body": "…",
      "severity": "info",
      "publishedAt": "2026-10-27T00:00:00Z"
    }
  ]
}
```

`severity` is one of `info` | `warning` | `error`.

### 3.3 `friends.json`

```json
{
  "formatVersion": 1,
  "friends": [
    {
      "id": "friend-001",
      "name": "TestFriend",
      "uuid": "00000000-0000-0000-0000-000000000000",
      "skinUrl": "https://csu-mc.magicalsheep.cn/textures/example-skin",
      "capeUrl": null
    }
  ]
}
```

`skinUrl`/`capeUrl` may be `null`; they point back at the skin site, not the
resource server.

### 3.4 `update/windows-x86_64/latest.json`

```json
{
  "formatVersion": 1,
  "platform": "windows-x86_64",
  "version": "0.1.0-test",
  "notes": "Test update artifact for the local resource-site harness.",
  "publishedAt": "2026-10-27T00:00:00Z",
  "url": "https://127.0.0.1:5443/update/windows-x86_64/PCL-CMU-launcher-0.1.0-test.bin",
  "size": 39,
  "sha256": "f122ce302310fec8d4fd8e885a744800c83b5b131478e94a300109dc914aec88",
  "signature": {
    "algorithm": "ECDSA-P256-SHA256",
    "format": "ieee-p1363-base64",
    "value": "<base64 signature>"
  }
}
```

---

## 4. HTTP headers

### 4.1 On every response

| Header                     | Value           |
|----------------------------|-----------------|
| `X-Content-Type-Options`   | `nosniff`       |
| `X-Frame-Options`          | `DENY`          |
| `Referrer-Policy`          | `no-referrer`   |

No `Access-Control-Allow-Origin` header is ever emitted (no CORS wildcard).

### 4.2 Cache-Control by path

| Path                          | `Cache-Control`                          |
|-------------------------------|------------------------------------------|
| `/packs/**`                   | `public, max-age=31536000, immutable`    |
| everything else               | `no-cache`                               |

Pack artifacts are content-addressed and immutable. All metadata
(manifest/announcements/friends/update) is `no-cache` so clients always
revalidate and can pick up new revisions.

### 4.3 Conditional requests / ranges

* The static-file middleware emits an `ETag` (based on file length + last-write
  time) and `Accept-Ranges: bytes`.
* `If-None-Match` matching the current ETag returns `304 Not Modified`.
* `Range` requests are honoured for resumable pack/update downloads.

---

## 5. Revision rules

1. The client stores the last-installed `revision` per instance locally
   (see `client-harness/state.json`).
2. On launch, the client fetches `manifest.json` and compares `instances[].revision`.
3. If the local revision differs from the remote revision, the client downloads
   the pack at `pack.url` and verifies it (see §6).
4. After a successful verify, the client persists the new revision.
5. A revision **must** be bumped whenever the pack bytes change; the pack URL is
   content-addressed (`1.mrpack`) but the manifest points to it by name for
   simplicity in this test package.

---

## 6. Checksum rules

| Artifact | Algorithm | Field      | Encoding                 |
|----------|-----------|------------|--------------------------|
| Pack     | SHA-1     | `pack.sha1` | lowercase hex (40 chars) |
| Update   | SHA-256   | `sha256`   | lowercase hex (64 chars) |

The client computes the digest over the **exact downloaded bytes** and compares
it to the manifest/latest value (case-insensitive hex). Any mismatch aborts.

---

## 7. Signature rules

* Algorithm: **ECDSA P-256** over **SHA-256** of the update artifact bytes.
* Signature encoding: **IEEE P1363 `r‖s`** (64 bytes for P-256), then **base64**.
* The public key is distributed out-of-band (`client-harness/update-signing-public.pem`,
  SPKI PEM) — **not** inside `wwwroot`.
* The private key lives at `resource-site/keys/update-signing-private.pem`
  (PKCS#8 PEM) — **outside `wwwroot`** and **excluded from the package**.
* Verification: `ECDsa.ImportFromPem(pub).VerifyData(bytes, sig, SHA256)`.
* If verification fails, the client refuses to apply the update.

---

## 8. Path-traversal / directory rules

* Any decoded path or raw request target containing `..` or `%2e` is rejected
  with `404` (not `400`, to avoid leaking internals).
* Directory requests (e.g. `/packs/`) return `404` — directory browsing is
  disabled.
* Unknown paths return `404`.
* `.mrpack` and `.bin` are explicitly mapped to `application/zip` and
  `application/octet-stream`; no other unknown file types are served.

---

## 9. Deployment options

### 9.1 Local (this package)

`dotnet run --project resource-site` (or run the built DLL). Bind to loopback,
use a trusted self-signed cert. See `scripts/verify.ps1` for the full flow.

### 9.2 Cloudflare Pages / R2 (production sketch)

* Put `wwwroot/**` into an R2 bucket; serve via a custom domain.
* Set cache rules: `packs/*` → `Cache-Control: public, max-age=31536000, immutable`;
  everything else → `no-cache` (or a short TTL).
* Cloudflare adds `X-Content-Type-Options: nosniff` and `Referrer-Policy` via a
  transform rule; add `X-Frame-Options: DENY` too.
* Remove the loopback binding and HTTPS-redirect middleware — Cloudflare
  terminates TLS.
* **Never** deploy the private signing key; sign artifacts in CI and commit only
  the public key + `latest.json`.

### 9.3 Nginx (self-hosted)

```nginx
server {
    listen 5443 ssl;
    server_name resource.example;
    ssl_certificate     /etc/ssl/loopback.crt;
    ssl_certificate_key /etc/ssl/loopback.key;

    root /srv/resource-site/wwwroot;
    index off;
    autoindex off;                 # no directory browsing

    add_header X-Content-Type-Options nosniff always;
    add_header X-Frame-Options DENY always;
    add_header Referrer-Policy no-referrer always;

    location ~ ^/packs/ {
        add_header Cache-Control "public, max-age=31536000, immutable";
    }
    location / {
        add_header Cache-Control "no-cache";
    }
}
```

---

## 10. Security checklist

- [x] HTTPS-only; HTTP redirects to HTTPS (308).
- [x] Bound to `127.0.0.1` only.
- [x] No directory browsing; unknown paths → 404.
- [x] Path traversal (`..` / `%2e`) → 404.
- [x] `nosniff`, `DENY`, `no-referrer` on every response.
- [x] `Cache-Control` split: immutable for packs, `no-cache` for metadata.
- [x] `ETag`/304 and `Accept-Ranges: bytes` for resumable downloads.
- [x] No CORS wildcard.
- [x] No secrets (`token`/`password`/`secret`/`Authorization`) in `wwwroot` JSON.
- [x] Private signing key outside `wwwroot`; public key out-of-band.
- [x] Update artifacts signed; client verifies before applying.
