<#
.SYNOPSIS
    Authenticode-sign Librarr's Windows binaries and installers.

.DESCRIPTION
    Signs the first-party executables and assemblies produced by the build --
    Readarr.exe, Readarr.Console.exe, Readarr.Update.exe and the Readarr.*.dll
    set -- plus any installer .exe passed directly.

    Third-party dependencies are deliberately left alone. Re-signing someone
    else's binary with our certificate asserts that we published it, which is
    not true, and most of them already carry their vendor's signature.

    With no certificate configured this exits 0 after saying so. That is the
    point: a fork without a code-signing certificate should still be able to
    cut a release, it just ships unsigned binaries. Once a certificate IS
    configured, any failure to sign is fatal -- quietly publishing unsigned
    artifacts from a pipeline that claims to sign them is the worse outcome.

.PARAMETER Path
    Files and/or directories to sign. Directories are searched recursively for
    the first-party patterns above; files are signed as given.

.PARAMETER CertificateBase64
    Base64-encoded PKCS#12 (.pfx). Defaults to $env:WINDOWS_CERT_PFX.
    Base64 because GitHub Actions secrets are text.

.PARAMETER CertificatePassword
    Password for that .pfx. Defaults to $env:WINDOWS_CERT_PASSWORD.

.PARAMETER TimestampUrl
    RFC3161 timestamp authority. A timestamp is what keeps signatures valid
    after the signing certificate expires, so this is not optional.

.EXAMPLE
    ./distribution/windows/sign.ps1 -Path _artifacts

.EXAMPLE
    ./distribution/windows/sign.ps1 -Path distribution/windows/setup/output/Librarr.1.1.0-beta.win-x64.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $Path,

    [string] $CertificateBase64 = $env:WINDOWS_CERT_PFX,

    [string] $CertificatePassword = $env:WINDOWS_CERT_PASSWORD,

    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

# Keep this file pure ASCII. Windows PowerShell 5.1 reads a BOM-less script as
# Windows-1252, so a UTF-8 em dash arrives as the three characters "a EUR "
# -- and that last one is U+201D, which PowerShell honours as a string
# delimiter. The result is a parse error hundreds of lines away from the
# actual character. pwsh 7 (what CI runs) decodes UTF-8 correctly and never
# sees this, so it only breaks for whoever runs the script locally.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Anything we build. Everything else in the publish directory belongs to
# someone else -- see the note in the header about not signing it.
#
# The two service helpers are ours as well, and matter more than most: they
# carry a requireAdministrator manifest, so Windows shows a UAC prompt for
# them, and that prompt reads "Publisher: Unknown" for an unsigned file.
$FirstPartyPatterns = @('Readarr*.exe', 'Readarr*.dll', 'ServiceInstall.exe', 'ServiceInstall.dll', 'ServiceUninstall.exe', 'ServiceUninstall.dll')

function Find-SignTool {
    if ($env:SIGNTOOL_PATH -and (Test-Path -LiteralPath $env:SIGNTOOL_PATH)) {
        return $env:SIGNTOOL_PATH
    }

    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    # The Windows SDK installs one signtool per architecture under a
    # version-named directory, and puts none of them on PATH:
    #   ...\Windows Kits\10\bin\10.0.22621.0\{arm,arm64,x64,x86}\signtool.exe
    # Take the newest SDK, preferring the x64 build.
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

    $candidates = foreach ($root in $roots) {
        Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            ForEach-Object {
                $parsed = $null
                if ([Version]::TryParse($_.Name, [ref] $parsed)) {
                    foreach ($arch in @('x64', 'x86', 'arm64', 'arm')) {
                        $exe = Join-Path $_.FullName "$arch\signtool.exe"
                        if (Test-Path -LiteralPath $exe) {
                            [pscustomobject]@{
                                Version = $parsed
                                Rank    = [array]::IndexOf(@('x64', 'x86', 'arm64', 'arm'), $arch)
                                Exe     = $exe
                            }
                        }
                    }
                }
            }
    }

    # Newest SDK first, then the preferred architecture. One sort with two
    # keys rather than two sorts, because Sort-Object -Stable does not exist
    # on the Windows PowerShell 5.1 a developer machine is likely to have.
    $best = $candidates | Sort-Object -Property `
        @{ Expression = 'Version'; Descending = $true },
        @{ Expression = 'Rank'; Descending = $false } |
        Select-Object -First 1

    if (-not $best) {
        throw 'signtool.exe not found. Install the Windows SDK or set SIGNTOOL_PATH.'
    }

    return $best.Exe
}

function Get-FilesToSign {
    param([string[]] $Roots)

    $files = foreach ($root in $Roots) {
        if (-not (Test-Path -LiteralPath $root)) {
            throw "Path not found: $root"
        }

        if (Test-Path -LiteralPath $root -PathType Container) {
            foreach ($pattern in $FirstPartyPatterns) {
                Get-ChildItem -LiteralPath $root -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue
            }
        }
        else {
            Get-Item -LiteralPath $root
        }
    }

    # A publish directory holds the same assembly under several RIDs, but each
    # copy is a distinct file and each needs its own signature. Only collapse
    # exact duplicate paths, which the two patterns above can produce.
    @($files | Sort-Object -Property FullName -Unique)
}

if ([string]::IsNullOrWhiteSpace($CertificateBase64)) {
    Write-Host 'No code-signing certificate configured (WINDOWS_CERT_PFX is empty).'
    Write-Host 'Skipping Authenticode signing -- the artifacts will be unsigned.'
    exit 0
}

$files = @(Get-FilesToSign -Roots $Path)
if ($files.Count -eq 0) {
    throw "Nothing to sign under: $($Path -join ', ')"
}

$signTool = Find-SignTool
Write-Host "signtool: $signTool"
Write-Host "Signing $($files.Count) file(s)."

# Keep the .pfx out of the workspace so a later artifact-upload step cannot
# scoop it up, and make sure it is gone even if signing throws.
$pfxPath = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName() + '.pfx')

try {
    [System.IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($CertificateBase64))

    # signtool takes many files per call, so batch them: each invocation costs
    # one round trip to the timestamp authority, and those are rate-limited.
    $batchSize = 50
    for ($i = 0; $i -lt $files.Count; $i += $batchSize) {
        $batch = $files[$i..([Math]::Min($i + $batchSize, $files.Count) - 1)]

        $signArgs = @(
            'sign',
            '/fd', 'SHA256',
            '/f', $pfxPath,
            '/tr', $TimestampUrl,
            '/td', 'SHA256'
        )
        if ($CertificatePassword) {
            $signArgs += @('/p', $CertificatePassword)
        }
        $signArgs += $batch.FullName

        # Timestamp authorities go down and rate-limit; a failure here is
        # almost never about the certificate. Retry before giving up.
        $attempt = 0
        while ($true) {
            $attempt++
            & $signTool @signArgs
            if ($LASTEXITCODE -eq 0) {
                break
            }
            if ($attempt -ge 3) {
                throw "signtool failed with exit code $LASTEXITCODE after $attempt attempts."
            }
            Write-Host "signtool exit $LASTEXITCODE -- retrying in $($attempt * 10)s (attempt $attempt of 3)."
            Start-Sleep -Seconds ($attempt * 10)
        }
    }
}
finally {
    if (Test-Path -LiteralPath $pfxPath) {
        Remove-Item -LiteralPath $pfxPath -Force
    }
}

# Verify by reading the signature back off disk rather than trusting the exit
# code above. Chain trust is deliberately not asserted: a self-signed test
# certificate will not chain to a trusted root, and that says nothing about
# whether the signing itself worked. What must hold is that every file now
# carries a signature.
$unsigned = @()
$statuses = @{}
foreach ($file in $files) {
    $sig = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if (-not $sig.SignerCertificate) {
        $unsigned += $file.FullName
        continue
    }
    $key = $sig.Status.ToString()
    $statuses[$key] = 1 + $(if ($statuses.ContainsKey($key)) { $statuses[$key] } else { 0 })
}

foreach ($key in $statuses.Keys) {
    Write-Host "  $($statuses[$key]) file(s): $key"
}

if ($unsigned.Count -gt 0) {
    Write-Host 'Files left without a signature:'
    $unsigned | ForEach-Object { Write-Host "  $_" }
    throw "$($unsigned.Count) file(s) were not signed."
}

Write-Host "Signed and verified $($files.Count) file(s)."
