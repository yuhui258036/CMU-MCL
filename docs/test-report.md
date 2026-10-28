# PCL-CMU 魔改版 — Local Resource Test Report

Actual commands and results from this run.

**Environment**

* OS: Windows 11 (PowerShell 5.1, build 26100)
* SDK: .NET 9.0.308 (no .NET 10 required)
* Target frameworks: `net9.0`

---

## 1. Commands run

```powershell
# (execution policy on this machine blocks unsigned scripts; bypass per-process)
Set-ExecutionPolicy -Scope Process Bypass -Force

# Generate pack + manifest, signing key pair, and signed update metadata
powershell -File scripts\build-pack.ps1
powershell -File scripts\generate-keys.ps1
powershell -File scripts\sign-update.ps1

# Run the full protocol/security suite (builds + starts server + tests + stops)
powershell -File scripts\verify.ps1
```

`scripts/generate-keys.ps1` output:

```
wrote private key: D:\新启动器\resource-site\keys\update-signing-private.pem
wrote public key:  D:\新启动器\client-harness\update-signing-public.pem
```

`scripts/build-pack.ps1` output:

```
built  D:\新启动器\resource-site\wwwroot\packs\test\1.mrpack
  size=312
  sha1=e62e6b6aa61d0e376fd206f4c4bfa951b8d9ffa2
updated D:\新启动器\resource-site\wwwroot\manifest.json
```

`scripts/sign-update.ps1` output:

```
signed D:\新启动器\resource-site\wwwroot\update\windows-x86_64\PCL-CMU-launcher-0.1.0-test.bin
wrote  D:\新启动器\resource-site\wwwroot\update\windows-x86_64\latest.json
```

---

## 2. `scripts/verify.ps1` output

```
=== building ===
=== TLS certificate ===
loopback cert already trusted (thumbprint 4F7D58484430D515E1799057A0405AEC96842057)
=== reset state ===
=== starting server ===
[PASS] listen only on loopback — listeners=127.0.0.1:5443, 127.0.0.1:5080
[PASS] GET /manifest.json -> 200 JSON no-cache ETag — status=200 cache-control='no-cache' etag="1dd662cd3dc3233"
[PASS] conditional GET -> 304 — status=304
[PASS] GET /packs/test/1.mrpack -> 200 len ranges immutable — status=200 content-length=312 accept-ranges='bytes' cache-control='public, max-age=31536000, immutable'
[PASS] pack sha1 matches manifest — actual=e62e6b6aa61d0e376fd206f4c4bfa951b8d9ffa2 expected=e62e6b6aa61d0e376fd206f4c4bfa951b8d9ffa2
[PASS] pack size matches manifest — actual=312 expected=312
[PASS] client-harness protocol run — exit=0
revision: 1
download: ok
sha1: ok
size: ok
server: https://127.0.0.1:5443
update: ok
signature: ok
[PASS] traversal paths -> 404 — /../appsettings.json=404 /packs/../manifest.json=404 /%2e%2e/manifest.json=404
[PASS] GET /packs/ -> 404 (no listing) — status=404
[PASS] GET /unknown -> 404 — status=404
[PASS] HTTP -> HTTPS redirect (301/308) — status=308 location=https://127.0.0.1:5443/manifest.json
[PASS] no secrets in wwwroot JSON
[PASS] private signing key outside wwwroot — privateKey=D:\新启动器\resource-site\keys\update-signing-private.pem keyFilesInWwwroot=
[PASS] server running during tests — pid=16616
[PASS] no server process left after stop — listeners=

=== SUMMARY ===
total=15 passed=15 failed=0
ALL TESTS PASSED
```

`client-harness` (run independently) output:

```
revision: 1
download: ok
sha1: ok
size: ok
server: https://127.0.0.1:5443
update: ok
signature: ok
```

---

## 3. Results table

| # | Check | Result |
|---|-------|--------|
| 1 | Server listens only on loopback (`127.0.0.1:5443`, `127.0.0.1:5080`) | PASS |
| 2 | `GET /manifest.json` → 200, JSON, `Cache-Control: no-cache`, ETag present | PASS |
| 3 | Conditional GET `If-None-Match` → 304 | PASS |
| 4 | `GET /packs/test/1.mrpack` → 200, Content-Length 312, `Accept-Ranges: bytes`, immutable | PASS |
| 5 | Pack SHA-1 == manifest `sha1`; size == manifest `size` | PASS |
| 6 | `client-harness` run (revision/download/sha1/size/update/signature) | PASS |
| 7 | `GET /../appsettings.json`, `/packs/../manifest.json`, `/%2e%2e/manifest.json` → 404 | PASS |
| 8 | `GET /packs/` → 404 (no directory listing) | PASS |
| 9 | `GET /unknown` → 404 | PASS |
| 10 | `http://127.0.0.1:5080/manifest.json` → 308 to HTTPS | PASS |
| 11 | Scan `wwwroot` JSON for `token`/`password`/`secret`/`Authorization` → none | PASS |
| 12 | Private signing key outside `wwwroot` | PASS |
| 13 | No server process left after tests | PASS |

---

## 4. SSL trust caveat

* The default `dotnet dev-certs https` certificate covers only `localhost`;
  because the endpoint is `https://127.0.0.1:5443`, the harness generates a
  self-signed loopback certificate (`DNS:localhost`, `IP:127.0.0.1`, `IP:::1`)
  and trusts it in `CurrentUser\Root` (no UI prompt). Certificate validation is
  **not** disabled on the client.
* Manual trust equivalent: `dotnet dev-certs https --trust` (for `localhost`),
  or import `resource-site/certs/loopback.cer` into `Cert:\CurrentUser\Root`.

## 5. Local run command

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
powershell -File .\scripts\verify.ps1
```

On success it prints `ALL TESTS PASSED` and exits 0; it leaves no server
process behind.
