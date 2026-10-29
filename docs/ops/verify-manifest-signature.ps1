param(
    [Parameter(Mandatory = $true)]
    [string]$PublicKey,

    [string]$Manifest = (Join-Path $PWD "manifest.json"),

    [string]$Signature = (Join-Path $PWD "manifest.json.sig"),

    [string]$OpenSsl = "C:\Program Files\Git\usr\bin\openssl.exe"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PublicKey)) { throw "Public key not found: $PublicKey" }
if (-not (Test-Path -LiteralPath $Manifest)) { throw "Manifest not found: $Manifest" }
if (-not (Test-Path -LiteralPath $Signature)) { throw "Signature not found: $Signature" }
if (-not (Test-Path -LiteralPath $OpenSsl)) { throw "OpenSSL not found: $OpenSsl" }

function Convert-P1363ToDer {
    param([byte[]]$Raw)

    if ($Raw.Length -ne 64) {
        throw "Expected a 64-byte ECDSA P-256 P1363 signature, got $($Raw.Length) bytes."
    }

    function NormalizeInteger {
        param([byte[]]$Value)

        $i = 0
        while ($i -lt ($Value.Length - 1) -and $Value[$i] -eq 0) {
            $i++
        }

        $result = New-Object System.Collections.Generic.List[byte]
        for (; $i -lt $Value.Length; $i++) {
            $result.Add($Value[$i])
        }

        # DER integers are signed two's-complement; add a leading zero if the
        # high bit is set.
        if (($result[0] -band 0x80) -ne 0) {
            $result.Insert(0, 0)
        }

        return ,$result.ToArray()
    }

    [byte[]]$r = NormalizeInteger $Raw[0..31]
    [byte[]]$s = NormalizeInteger $Raw[32..63]

    $seqLength = $r.Length + $s.Length + 4
    $der = New-Object byte[] ($seqLength + 2)
    $der[0] = 0x30
    $der[1] = [byte]$seqLength
    $der[2] = 0x02
    $der[3] = [byte]$r.Length
    [Array]::Copy($r, 0, $der, 4, $r.Length)

    $offset = 4 + $r.Length
    $der[$offset] = 0x02
    $der[$offset + 1] = [byte]$s.Length
    [Array]::Copy($s, 0, $der, $offset + 2, $s.Length)

    return ,$der
}

$signatureDocument = Get-Content -LiteralPath $Signature -Raw | ConvertFrom-Json
$rawSignature = [Convert]::FromBase64String([string]$signatureDocument.signature)
$derSignature = Convert-P1363ToDer $rawSignature

$tempDer = [IO.Path]::GetTempFileName()
try {
    [IO.File]::WriteAllBytes($tempDer, $derSignature)
    & $OpenSsl dgst -sha256 -verify $PublicKey -signature $tempDer $Manifest
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Remove-Item -LiteralPath $tempDer -Force -ErrorAction SilentlyContinue
}
