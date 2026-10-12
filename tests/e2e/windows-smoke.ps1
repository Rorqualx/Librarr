<#
.SYNOPSIS
    Windows end-to-end smoke test for Librarr. The Windows counterpart of
    tests/e2e/smoke.sh, run by the "Windows smoke" job in
    .github/workflows/build.yml.

.DESCRIPTION
    Everything here needs a real Windows machine and, for all but the
    "migrate" stage, an elevated session: it registers a Windows service
    called "Readarr", writes under %ProgramData%\Librarr and (for the
    "installer" stage) installs and uninstalls the product. It is written
    for a throwaway CI runner. Do not run it on a machine that has a Readarr
    or Librarr install you care about.

    Stages:

      console    Start Readarr.Console.exe against a temp data folder, wait
                 for /ping, check /api/v1/system/status, shut it down through
                 the API. Then start it again with its parent process already
                 gone, to see what WindowsServiceHelpers.IsWindowsService()
                 does in that case.

      service    Readarr.Console.exe /i, check the service is RUNNING as
                 LocalService and answering, stop and start it, /u, check it
                 is gone. Then the same install/uninstall through
                 ServiceInstall.exe and ServiceUninstall.exe.

      migrate    Run scripts/migrate-readarr-folder.ps1 against a fake data
                 folder under both Windows PowerShell 5.1 and PowerShell 7
                 and compare the result with the source, file by file.

      installer  Run the Inno Setup installer silently: refused when another
                 "Readarr" service exists, installed beside it without the
                 service task, installed over it with /REPLACESERVICE=1,
                 upgraded in place, uninstalled. Checks the folder ACLs after
                 each install.

.PARAMETER Stage
    console | service | migrate | installer

.PARAMETER App
    Folder holding the packaged Windows build (Readarr.Console.exe and
    friends), e.g. _artifacts\win-x64\net10.0\Readarr.

.PARAMETER Installer
    Path to the installer .exe (installer stage only).

.PARAMETER ExpectedVersion
    Version /api/v1/system/status must report. Optional.

.PARAMETER Output
    Folder that receives logs and diagnostics. Uploaded as a CI artifact.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('console', 'service', 'migrate', 'installer')]
    [string]$Stage,

    [string]$App,
    [string]$Installer,
    [string]$ExpectedVersion,
    [string]$Output = '_smoke'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$BaseUrl     = 'http://localhost:8787'
$ServiceName = 'Readarr'
$DataDir     = Join-Path $env:ProgramData 'Librarr'
$RepoRoot    = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
# RUNNER_TEMP on CI: %TEMP% there is an 8.3 short path (C:\Users\RUNNER~1\...),
# which the app reports back in its long form.
$TempRoot    = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }

New-Item -ItemType Directory -Path $Output -Force | Out-Null
$Output = (Resolve-Path $Output).Path

if ($App) { $App = (Resolve-Path $App).Path }
if ($Installer) { $Installer = (Resolve-Path $Installer).Path }

# -- Helpers -----------------------------------------------------------------

function Step([string]$Message) {
    Write-Host ''
    Write-Host "=== $Message"
}

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw "ASSERTION FAILED: $Message"
    }

    Write-Host "  ok: $Message"
}

function Test-Ping {
    try {
        $response = Invoke-WebRequest -Uri "$BaseUrl/ping" -UseBasicParsing -TimeoutSec 5
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSec, [string]$What) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)

    while ((Get-Date) -lt $deadline) {
        if (& $Condition) {
            Write-Host "  ok: $What"
            return
        }

        Start-Sleep -Seconds 1
    }

    throw "TIMED OUT after ${TimeoutSec}s waiting for: $What"
}

function Get-ApiKey([string]$ConfigPath) {
    Assert (Test-Path -LiteralPath $ConfigPath) "config.xml was generated at $ConfigPath"
    $key = ([xml](Get-Content -LiteralPath $ConfigPath -Raw)).Config.ApiKey
    Assert (-not [string]::IsNullOrWhiteSpace($key)) 'config.xml contains an ApiKey'
    return $key
}

function Get-Status([string]$ApiKey) {
    return Invoke-RestMethod -Uri "$BaseUrl/api/v1/system/status" -Headers @{ 'X-Api-Key' = $ApiKey } -TimeoutSec 30
}

function Assert-Status($Status, [string]$Mode) {
    Write-Host ($Status | Select-Object version, osName, osVersion, isWindows, mode, isAdmin, isUserInteractive, runtimeVersion, appData, startupPath | Format-List | Out-String)

    Assert ($Status.isWindows -eq $true) 'status.isWindows is true'
    Assert ("$($Status.osName)" -match 'Windows') "status.osName names Windows (got '$($Status.osName)')"
    Assert ("$($Status.mode)" -ieq $Mode) "status.mode is '$Mode' (got '$($Status.mode)')"

    if ($ExpectedVersion) {
        Assert ("$($Status.version)" -eq $ExpectedVersion) "status.version is $ExpectedVersion (got '$($Status.version)')"
    }
}

function Get-ServiceOrNull {
    return Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
}

function Get-ServiceImagePath {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"

    if (-not (Test-Path $key)) {
        return $null
    }

    return (Get-ItemProperty -Path $key).ImagePath
}

function Wait-ServiceStatus([string]$Status, [int]$TimeoutSec = 90) {
    Wait-Until { $s = Get-ServiceOrNull; $s -and "$($s.Status)" -eq $Status } $TimeoutSec "service $ServiceName is $Status"
}

function Wait-ServiceGone([int]$TimeoutSec = 60) {
    Wait-Until { -not (Get-ServiceOrNull) } $TimeoutSec "service $ServiceName no longer exists"
}

function Stop-AppProcesses {
    Get-Process -Name 'Readarr', 'Readarr.Console' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  killing leftover $($_.ProcessName) (PID $($_.Id))"
        $_ | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

function Remove-ServiceIfPresent {
    if (Get-ServiceOrNull) {
        Write-Host "  removing leftover service $ServiceName"
        & sc.exe stop $ServiceName | Out-Null
        Start-Sleep -Seconds 3
        & sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    }
}

function Save-Folder([string]$From, [string]$Name) {
    if (Test-Path -LiteralPath $From) {
        $to = Join-Path $Output $Name
        New-Item -ItemType Directory -Path $to -Force | Out-Null
        Copy-Item -Path (Join-Path $From '*') -Destination $to -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Save-Diagnostics([string]$Name) {
    $file = Join-Path $Output "$Name-diagnostics.txt"

    & {
        '--- sc.exe qc'
        & sc.exe qc $ServiceName 2>&1
        '--- sc.exe queryex'
        & sc.exe queryex $ServiceName 2>&1
        '--- processes'
        Get-Process -Name 'Readarr*' -ErrorAction SilentlyContinue | Format-Table Id, ProcessName, StartTime, Path -AutoSize | Out-String
        "--- icacls $DataDir"
        & icacls.exe $DataDir 2>&1
        "--- icacls $DataDir\bin"
        & icacls.exe (Join-Path $DataDir 'bin') 2>&1
        '--- System event log: Service Control Manager'
        Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = (Get-Date).AddMinutes(-30) } -MaxEvents 30 -ErrorAction SilentlyContinue |
            Format-List TimeCreated, Id, LevelDisplayName, Message | Out-String
        '--- Application event log: errors'
        Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 1, 2; StartTime = (Get-Date).AddMinutes(-30) } -MaxEvents 20 -ErrorAction SilentlyContinue |
            Format-List TimeCreated, ProviderName, Id, Message | Out-String
    } *>&1 | Out-File -FilePath $file -Encoding utf8

    Write-Host "  diagnostics written to $file"
}

function Show-LogTail([string]$Folder) {
    $logs = Join-Path $Folder 'logs'

    if (-not (Test-Path -LiteralPath $logs)) {
        Write-Host "  (no logs folder under $Folder)"
        return
    }

    Get-ChildItem -LiteralPath $logs -Filter '*.txt' | ForEach-Object {
        Write-Host "--- tail of $($_.FullName)"
        Get-Content -LiteralPath $_.FullName -Tail 80 | ForEach-Object { Write-Host $_ }
    }
}

function Get-LogText([string]$Folder) {
    $logs = Join-Path $Folder 'logs'

    if (-not (Test-Path -LiteralPath $logs)) {
        return ''
    }

    return (Get-ChildItem -LiteralPath $logs -Filter '*.txt' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
}

function Invoke-Shutdown([string]$ApiKey) {
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/system/shutdown" -Headers @{ 'X-Api-Key' = $ApiKey } -TimeoutSec 30 | Out-Null
}

# -- ACL checks --------------------------------------------------------------

$SidAdministrators = 'S-1-5-32-544'
$SidSystem         = 'S-1-5-18'
$SidUsers          = 'S-1-5-32-545'
$SidLocalService   = 'S-1-5-19'

$WriteRights = [System.Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'

function Get-AllowRules([string]$Path) {
    $acl = Get-Acl -LiteralPath $Path

    return $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.AccessControlType -eq 'Allow' }
}

function Show-Acl([string]$Path) {
    Write-Host "--- icacls $Path"
    & icacls.exe $Path | ForEach-Object { Write-Host "  $_" }
}

# Nobody outside $Writers may hold any right that changes $Path; nobody outside
# $Writers + $Readers may hold any right at all.
function Assert-Acl([string]$Path, [string[]]$Writers, [string[]]$Readers) {
    Show-Acl $Path

    foreach ($rule in (Get-AllowRules $Path)) {
        $sid = $rule.IdentityReference.Value
        $canWrite = ($rule.FileSystemRights -band $WriteRights) -ne 0

        if ($Writers -contains $sid) {
            continue
        }

        if ($Readers -contains $sid) {
            if ($canWrite) {
                throw "ASSERTION FAILED: $sid can modify $Path ($($rule.FileSystemRights))"
            }

            continue
        }

        throw "ASSERTION FAILED: unexpected ACL entry on $Path for $sid ($($rule.FileSystemRights))"
    }

    Write-Host "  ok: only [$($Writers -join ', ')] can modify $Path; additionally readable by [$($Readers -join ', ')]"
}

function Assert-Protected([string]$Path) {
    Assert ((Get-Acl -LiteralPath $Path).AreAccessRulesProtected) "$Path does not inherit permissions from its parent"
}

function Assert-HasModify([string]$Path, [string]$Sid) {
    $modify = [System.Security.AccessControl.FileSystemRights]::Modify
    $found = Get-AllowRules $Path | Where-Object { $_.IdentityReference.Value -eq $Sid -and (($_.FileSystemRights -band $modify) -eq $modify) }
    Assert ([bool]$found) "$Sid has Modify on $Path"
}

# -- Stage: console ----------------------------------------------------------

function Invoke-ConsoleStage {
    $exe = Join-Path $App 'Readarr.Console.exe'
    Assert (Test-Path -LiteralPath $exe) "$exe exists"

    Stop-AppProcesses

    # 1. An ordinary console run.
    Step 'Console: start, ping, status, shut down'
    $data = Join-Path $TempRoot "librarr-console-$PID"
    Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $data | Out-Null

    $stdout = Join-Path $Output 'console-stdout.txt'
    $stderr = Join-Path $Output 'console-stderr.txt'
    $process = Start-Process -FilePath $exe -ArgumentList '-nobrowser', "-data=$data" -PassThru -NoNewWindow `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    try {
        Wait-Until { Test-Ping } 180 "$BaseUrl/ping answers"

        $apiKey = Get-ApiKey (Join-Path $data 'config.xml')
        $status = Get-Status $apiKey
        Assert-Status $status 'console'
        Assert ("$($status.appData)".TrimEnd('\') -ieq $data.TrimEnd('\')) "status.appData is the -data folder (got '$($status.appData)')"

        Invoke-Shutdown $apiKey
        Wait-Until { $process.HasExited } 60 'the console process exits after POST /system/shutdown'
        Wait-Until { -not (Test-Ping) } 30 '/ping stops answering'
    }
    catch {
        if (Test-Path $stdout) { Get-Content $stdout -Tail 120 | ForEach-Object { Write-Host $_ } }
        if (Test-Path $stderr) { Get-Content $stderr -Tail 60 | ForEach-Object { Write-Host $_ } }
        throw
    }
    finally {
        if (-not $process.HasExited) { $process | Stop-Process -Force -ErrorAction SilentlyContinue }
        Save-Folder (Join-Path $data 'logs') 'console-logs'
    }

    # 2. Started by a parent that has already exited by the time the app looks
    #    for it. Bootstrap.GetApplicationMode wraps IsWindowsService() in a
    #    try/catch commented "it can throw sometimes"; this is the cheapest
    #    reproducible candidate for "sometimes".
    Step 'Console: start with the parent process already gone'
    $orphanData = Join-Path $TempRoot "librarr-orphan-$PID"
    Remove-Item -LiteralPath $orphanData -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $orphanData | Out-Null

    # cmd starts the app detached and exits at once.
    $launcher = Start-Process -FilePath $env:ComSpec -ArgumentList '/c', 'start', '""', '/b', "`"$exe`"", '-nobrowser', "-data=$orphanData" -PassThru -WindowStyle Hidden
    $launcher.WaitForExit(30000) | Out-Null

    try {
        Wait-Until { Test-Ping } 180 "$BaseUrl/ping answers from the orphaned process"

        $orphan = @(Get-Process -Name 'Readarr.Console' -ErrorAction SilentlyContinue)
        Assert ($orphan.Count -eq 1) 'exactly one Readarr.Console process is running'

        $parentId = (Get-CimInstance Win32_Process -Filter "ProcessId = $($orphan[0].Id)").ParentProcessId
        $parentAlive = [bool](Get-Process -Id $parentId -ErrorAction SilentlyContinue)
        Write-Host "  parent PID $parentId, still running: $parentAlive"
        Assert (-not $parentAlive) 'the parent process of the app has exited'

        $apiKey = Get-ApiKey (Join-Path $orphanData 'config.xml')
        Assert-Status (Get-Status $apiKey) 'console'

        $log = Get-LogText $orphanData
        Assert ($log -match 'Starting Readarr - ') 'the file log was already being written when the application mode was chosen'
        Assert ($log -notmatch 'Failed to get service status') 'IsWindowsService() did not throw with the parent process gone'

        Invoke-Shutdown $apiKey
        Wait-Until { -not (Get-Process -Name 'Readarr.Console' -ErrorAction SilentlyContinue) } 60 'the orphaned process exits after POST /system/shutdown'
    }
    catch {
        Show-LogTail $orphanData
        throw
    }
    finally {
        Stop-AppProcesses
        Save-Folder (Join-Path $orphanData 'logs') 'console-orphan-logs'
    }
}

# -- Stage: service ----------------------------------------------------------

function Assert-ServiceAnswers {
    Wait-ServiceStatus 'Running'
    Wait-Until { Test-Ping } 180 "$BaseUrl/ping answers from the service"

    $apiKey = Get-ApiKey (Join-Path $DataDir 'config.xml')
    Assert-Status (Get-Status $apiKey) 'service'
}

function Invoke-ServiceStage {
    Stop-AppProcesses
    Remove-ServiceIfPresent

    # Out of the workspace: the service runs as LocalService, and whether that
    # account can read the runner's work folder is not something to bet a CI
    # round on.
    $svcApp = 'C:\LibrarrSmoke\app'
    Step "Service: copy the package to $svcApp"
    Remove-Item -LiteralPath 'C:\LibrarrSmoke' -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $svcApp -Force | Out-Null
    Copy-Item -Path (Join-Path $App '*') -Destination $svcApp -Recurse -Force
    $exe = Join-Path $svcApp 'Readarr.Console.exe'

    # The unit tests that ran before this may have left one behind.
    Remove-Item -LiteralPath $DataDir -Recurse -Force -ErrorAction SilentlyContinue
    Assert (-not (Test-Path -LiteralPath $DataDir)) "$DataDir does not exist yet (a first-ever start as a service)"

    try {
        Step 'Service: Readarr.Console.exe /i'
        & $exe /i /exitimmediately
        Assert ($LASTEXITCODE -eq 0) "Readarr.Console.exe /i exits 0 (got $LASTEXITCODE)"

        Assert ([bool](Get-ServiceOrNull)) "service $ServiceName exists"
        & sc.exe qc $ServiceName | ForEach-Object { Write-Host "  $_" }
        & sc.exe query $ServiceName | ForEach-Object { Write-Host "  $_" }

        $cim = Get-CimInstance Win32_Service -Filter "Name = '$ServiceName'"
        Assert ("$($cim.StartName)" -ieq 'NT AUTHORITY\LocalService') "the service runs as LocalService (got '$($cim.StartName)')"
        Assert ("$($cim.StartMode)" -ieq 'Auto') "the service starts automatically (got '$($cim.StartMode)')"
        Assert ("$($cim.PathName)".Trim('"') -ieq $exe) "the service binary is $exe (got '$($cim.PathName)')"

        Assert-ServiceAnswers
        Assert ((& sc.exe query $ServiceName | Out-String) -match 'RUNNING') 'sc query reports RUNNING'

        # /i ran as an administrator and the service runs as LocalService, both
        # against a data folder neither had seen before. Record who ended up
        # owning what, and that the service can write its own log.
        Show-Acl $DataDir
        Get-ChildItem -LiteralPath $DataDir -Recurse -Force | ForEach-Object {
            Write-Host ("  {0,-28} {1,10} {2}" -f (Get-Acl -LiteralPath $_.FullName).Owner, $(if ($_.PSIsContainer) { '<dir>' } else { $_.Length }), $_.FullName)
        }
        Show-LogTail $DataDir

        Step 'Service: stop'
        & sc.exe stop $ServiceName | ForEach-Object { Write-Host "  $_" }
        Wait-ServiceStatus 'Stopped'
        Wait-Until { -not (Test-Ping) } 30 '/ping stops answering'
        Wait-Until { -not (Get-Process -Name 'Readarr.Console' -ErrorAction SilentlyContinue) } 30 'the service process has exited'

        Step 'Service: start again'
        $startsLogged = [regex]::Matches((Get-LogText $DataDir), 'Starting Readarr - ').Count
        & sc.exe start $ServiceName | ForEach-Object { Write-Host "  $_" }
        Assert-ServiceAnswers
        $startsLoggedNow = [regex]::Matches((Get-LogText $DataDir), 'Starting Readarr - ').Count
        Assert ($startsLoggedNow -gt $startsLogged) "the service (LocalService) can write to the log file ($startsLogged -> $startsLoggedNow start lines)"

        Step 'Service: Readarr.Console.exe /u'
        & $exe /u /exitimmediately
        Assert ($LASTEXITCODE -eq 0) "Readarr.Console.exe /u exits 0 (got $LASTEXITCODE)"
        Wait-ServiceGone
        Wait-Until { -not (Test-Ping) } 30 '/ping stops answering'

        $log = Get-LogText $DataDir
        Assert ($log -notmatch 'Failed to get service status') 'IsWindowsService() never threw'

        # The standalone helpers that ship beside the app.
        Step 'Service: ServiceInstall.exe / ServiceUninstall.exe'
        foreach ($helper in 'ServiceInstall.exe', 'ServiceUninstall.exe') {
            $path = Join-Path $svcApp $helper
            Assert (Test-Path -LiteralPath $path) "$helper ships in the Windows package"
            $bytes = [System.IO.File]::ReadAllBytes($path)
            $text = [System.Text.Encoding]::ASCII.GetString($bytes)
            Assert ($text.Contains('requireAdministrator')) "$helper carries a requireAdministrator manifest"
        }

        $install = Start-Process -FilePath (Join-Path $svcApp 'ServiceInstall.exe') -Wait -PassThru
        Assert ($install.ExitCode -eq 0) "ServiceInstall.exe exits 0 (got $($install.ExitCode))"
        Assert-ServiceAnswers

        $uninstall = Start-Process -FilePath (Join-Path $svcApp 'ServiceUninstall.exe') -Wait -PassThru
        Assert ($uninstall.ExitCode -eq 0) "ServiceUninstall.exe exits 0 (got $($uninstall.ExitCode))"
        Wait-ServiceGone
    }
    catch {
        Save-Diagnostics 'service'
        Show-LogTail $DataDir
        throw
    }
    finally {
        Remove-ServiceIfPresent
        Stop-AppProcesses
        Save-Folder (Join-Path $DataDir 'logs') 'service-logs'
        # The installer stage wants a machine with no Librarr data on it.
        Remove-Item -LiteralPath $DataDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath 'C:\LibrarrSmoke' -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# -- Stage: migrate ----------------------------------------------------------

function New-FakeDataFolder([string]$Path) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Path 'config.xml') -Value '<Config><Port>8787</Port><ApiKey>0123456789abcdef0123456789abcdef</ApiKey></Config>'

    $random = [System.Random]::new(42)
    $db = [byte[]]::new(256KB)
    $random.NextBytes($db)
    [System.IO.File]::WriteAllBytes((Join-Path $Path 'readarr.db'), $db)

    foreach ($dir in 'Backups\scheduled', 'logs', 'MediaCover\12', 'empty-folder') {
        New-Item -ItemType Directory -Path (Join-Path $Path $dir) -Force | Out-Null
    }

    Set-Content -LiteralPath (Join-Path $Path 'Backups\scheduled\readarr_backup_v0.4.18.2805_2025.06.10.zip') -Value 'not really a zip'
    Set-Content -LiteralPath (Join-Path $Path 'logs\readarr.txt') -Value ('log line ' * 500)
    # Names a wildcard-interpreting cmdlet would trip over.
    Set-Content -LiteralPath (Join-Path $Path 'MediaCover\12\poster [1].jpg') -Value 'jpeg'
    Set-Content -LiteralPath (Join-Path $Path 'MediaCover\12\cover & back.jpg') -Value 'jpeg'

    $hidden = Join-Path $Path 'hidden.dat'
    Set-Content -LiteralPath $hidden -Value 'hidden'
    (Get-Item -LiteralPath $hidden -Force).Attributes = 'Hidden'

    # An entry named like the folder itself.
    $leaf = Split-Path -Leaf $Path
    New-Item -ItemType Directory -Path (Join-Path $Path $leaf) -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Path "$leaf\note.txt") -Value 'nested folder with the same name as its parent'
}

function Get-Tree([string]$Root) {
    $rootFull = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')

    return Get-ChildItem -LiteralPath $rootFull -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($rootFull.Length + 1)

        if ($_.PSIsContainer) {
            "D $relative"
        }
        else {
            "F $relative $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
        }
    }
}

function Invoke-MigrateStage {
    $script = Join-Path $RepoRoot 'scripts\migrate-readarr-folder.ps1'
    $root = Join-Path $TempRoot "librarr-migrate-$PID"
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue

    $src = Join-Path $root 'Readarr'
    New-FakeDataFolder $src
    $expected = @(Get-Tree $src)
    Write-Host ($expected -join "`n")

    $shells = @('powershell.exe', 'pwsh.exe')

    foreach ($shell in $shells) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($shell)
        $dst = Join-Path $root "Librarr-$name"

        Step "Migrate ($shell): dry run copies nothing"
        & $shell -NoProfile -ExecutionPolicy Bypass -File $script -Src $src -Dst $dst -DryRun
        Assert ($LASTEXITCODE -eq 0) "dry run exits 0 (got $LASTEXITCODE)"
        Assert (-not (Test-Path -LiteralPath $dst)) 'dry run did not create the destination'

        Step "Migrate ($shell): copy"
        $transcript = & $shell -NoProfile -ExecutionPolicy Bypass -File $script -Src $src -Dst $dst -Force 2>&1 | Out-String
        $exit = $LASTEXITCODE
        Write-Host $transcript
        Assert ($exit -eq 0) "copy exits 0 (got $exit)"

        $actual = @(Get-Tree $dst)
        $diff = Compare-Object -ReferenceObject $expected -DifferenceObject $actual
        if ($diff) {
            Write-Host ($diff | Format-Table -AutoSize | Out-String)
        }

        Assert (-not $diff) 'the destination tree is identical to the source, file for file'
        Assert (Test-Path -LiteralPath (Join-Path $dst 'config.xml')) 'config.xml sits directly in the destination, not one level down'
        Assert (((Get-Item -LiteralPath (Join-Path $dst 'hidden.dat') -Force).Attributes -band [System.IO.FileAttributes]::Hidden) -ne 0) 'the hidden file is still hidden'
        Assert ($transcript -match 'Copy complete\..*100% match') 'the script reports a 100% size match'
        Assert ($transcript -notmatch '[^\x00-\x7F]') 'the script output is plain ASCII (no mojibake under either shell)'
        Assert (@(Get-Tree $src).Count -eq $expected.Count) 'the source folder is untouched'

        Step "Migrate ($shell): refuses an existing destination"
        $refusal = & $shell -NoProfile -ExecutionPolicy Bypass -File $script -Src $src -Dst $dst -Force 2>&1 | Out-String
        $exit = $LASTEXITCODE
        Assert ($exit -ne 0) "a second run into the same destination fails (exit $exit)"
        Assert ($refusal -match 'already exists') 'and says the destination already exists'
    }

    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

# -- Stage: installer --------------------------------------------------------

$InstallerRun = 0

function Invoke-Setup([string[]]$Arguments, [string]$Label) {
    $script:InstallerRun++
    $log = Join-Path $Output ("installer-{0:d2}-{1}.log" -f $script:InstallerRun, $Label)
    $all = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/LOG=`"$log`"") + $Arguments
    Write-Host "  > $(Split-Path -Leaf $Installer) $($all -join ' ')"

    $process = Start-Process -FilePath $Installer -ArgumentList $all -Wait -PassThru
    Write-Host "  exit code $($process.ExitCode)"

    return [pscustomobject]@{ ExitCode = $process.ExitCode; Log = $log }
}

function Show-SetupLog($Run) {
    if (Test-Path -LiteralPath $Run.Log) {
        Write-Host "--- $($Run.Log)"
        Get-Content -LiteralPath $Run.Log | ForEach-Object { Write-Host "  $_" }
    }
}

function Get-UninstallString {
    foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
        $key = Join-Path $root '{FA3F8C19-9179-4676-B7A9-B8F9B411711A}_is1'

        if (Test-Path -LiteralPath $key) {
            return "$((Get-ItemProperty -LiteralPath $key).UninstallString)".Trim('"')
        }
    }

    return $null
}

function Invoke-Uninstall {
    $uninstaller = Get-UninstallString
    Assert ([bool]$uninstaller) 'the install is registered in Add/Remove Programs'
    Assert ($uninstaller -ieq (Join-Path $DataDir 'uninstall\unins000.exe')) "the uninstaller lives in the uninstall folder (got '$uninstaller')"
    Assert-Acl $uninstaller @($SidAdministrators, $SidSystem) @($SidUsers)

    $log = Join-Path $Output ("uninstall-{0:d2}.log" -f $script:InstallerRun)
    $process = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"" -Wait -PassThru
    Write-Host "  uninstaller exit code $($process.ExitCode)"
    Wait-Until { -not (Test-Path -LiteralPath (Join-Path $DataDir 'bin\Readarr.Console.exe')) } 120 'the binaries are removed'
    Wait-Until { -not (Get-UninstallString) } 60 'the Add/Remove Programs entry is removed'

    if (Test-Path -LiteralPath $log) {
        Get-Content -LiteralPath $log | Select-String -Pattern 'service', 'net stop', 'sc delete' -SimpleMatch | ForEach-Object { Write-Host "  $_" }
    }
}

function Assert-ProgramFolders {
    foreach ($folder in 'bin', 'uninstall') {
        $path = Join-Path $DataDir $folder
        Assert-Protected $path
        Assert-Acl $path @($SidAdministrators, $SidSystem) @($SidUsers)
    }

    # And a file inside, to see what was actually inherited.
    Assert-Acl (Join-Path $DataDir 'bin\Readarr.Console.exe') @($SidAdministrators, $SidSystem) @($SidUsers)
    Assert (-not (Test-Path -LiteralPath (Join-Path $DataDir 'unins000.exe'))) 'no uninstaller is left in the data folder'
}

function Invoke-InstallerStage {
    Assert (Test-Path -LiteralPath $Installer) "$Installer exists"

    Stop-AppProcesses
    Remove-ServiceIfPresent
    Remove-Item -LiteralPath $DataDir -Recurse -Force -ErrorAction SilentlyContinue
    Assert (-not (Test-Path -LiteralPath $DataDir)) "$DataDir does not exist before the first install"

    $bin = Join-Path $DataDir 'bin'
    $foreignPath = 'C:\LibrarrSmoke\OtherReadarr\bin\Readarr.Console.exe'

    try {
        # 1. Another product owns the service name.
        Step "Installer: a foreign '$ServiceName' service exists; an unattended service install must refuse"
        & sc.exe create $ServiceName binPath= $foreignPath start= demand | ForEach-Object { Write-Host "  $_" }
        Assert ($LASTEXITCODE -eq 0) 'created a stand-in for a stock Readarr service'
        $foreignImagePath = Get-ServiceImagePath

        $run = Invoke-Setup @('/TASKS="windowsService"') 'refuse-foreign-service'
        Show-SetupLog $run
        Assert ($run.ExitCode -ne 0) "setup refuses (exit code $($run.ExitCode))"
        Assert ((Get-ServiceImagePath) -eq $foreignImagePath) 'the foreign service is untouched'
        Assert (-not (Test-Path -LiteralPath (Join-Path $bin 'Readarr.Console.exe'))) 'nothing was installed'

        # 2. Installed beside it, without the service.
        Step "Installer: install with no service task; the foreign service must survive install and uninstall"
        $run = Invoke-Setup @('/TASKS="none"') 'no-service'
        if ($run.ExitCode -ne 0) { Show-SetupLog $run }
        Assert ($run.ExitCode -eq 0) "setup succeeds (exit code $($run.ExitCode))"
        Assert (Test-Path -LiteralPath (Join-Path $bin 'Readarr.Console.exe')) 'the binaries are installed'
        Assert (Test-Path -LiteralPath (Join-Path $bin 'Readarr.exe')) 'the tray application is installed'
        Assert ((Get-ServiceImagePath) -eq $foreignImagePath) 'the foreign service is untouched by the install'

        Assert-ProgramFolders
        Assert-Protected $DataDir
        Assert-Acl $DataDir @($SidAdministrators, $SidSystem, $SidUsers) @()
        Assert-HasModify $DataDir $SidUsers

        Invoke-Uninstall
        Assert ((Get-ServiceImagePath) -eq $foreignImagePath) 'the foreign service is untouched by the uninstall'

        # 3. Taking the service over, on request.
        Step 'Installer: /REPLACESERVICE=1 replaces the foreign service'
        $run = Invoke-Setup @('/TASKS="windowsService"', '/REPLACESERVICE=1') 'replace-foreign-service'
        if ($run.ExitCode -ne 0) { Show-SetupLog $run }
        Assert ($run.ExitCode -eq 0) "setup succeeds (exit code $($run.ExitCode))"

        $imagePath = "$(Get-ServiceImagePath)".Trim('"')
        Assert ($imagePath -ieq (Join-Path $bin 'Readarr.Console.exe')) "the service now runs the installed binary (got '$imagePath')"
        Assert-ServiceAnswers
        & sc.exe query $ServiceName | ForEach-Object { Write-Host "  $_" }

        Assert-ProgramFolders
        Assert-Protected $DataDir
        Assert-Acl $DataDir @($SidAdministrators, $SidSystem, $SidLocalService) @()
        Assert-HasModify $DataDir $SidLocalService
        # What the service created for itself must have inherited the same.
        Assert-Acl (Join-Path $DataDir 'config.xml') @($SidAdministrators, $SidSystem, $SidLocalService) @()

        $url = Join-Path $bin 'Librarr.url'
        Assert (Test-Path -LiteralPath $url) 'the web UI shortcut target exists'
        Write-Host (Get-Content -LiteralPath $url -Raw)
        Assert ((Get-Content -LiteralPath $url -Raw) -match 'URL=http://localhost:8787\s') 'it points at http://localhost:8787'

        $log = Get-LogText $DataDir
        Assert ($log -notmatch "Coudn't set app folder permission") 'the service did not try (and fail) to re-open the data folder to Everyone'
        Assert ($log -notmatch 'Failed to get service status') 'IsWindowsService() never threw'

        # 4. Upgrade in place: our own service is replaced without being asked.
        Step 'Installer: run again over the existing install'
        $apiKeyBefore = Get-ApiKey (Join-Path $DataDir 'config.xml')
        $run = Invoke-Setup @('/TASKS="windowsService"') 'upgrade-in-place'
        if ($run.ExitCode -ne 0) { Show-SetupLog $run }
        Assert ($run.ExitCode -eq 0) "setup succeeds without /REPLACESERVICE (exit code $($run.ExitCode))"
        Assert-ServiceAnswers
        Assert ((Get-ApiKey (Join-Path $DataDir 'config.xml')) -eq $apiKeyBefore) 'config.xml survived the upgrade'
        Assert-ProgramFolders
        Assert-Acl $DataDir @($SidAdministrators, $SidSystem, $SidLocalService) @()

        # 5. Uninstall.
        Step 'Installer: uninstall'
        Invoke-Uninstall
        Wait-ServiceGone
        Assert (Test-Path -LiteralPath (Join-Path $DataDir 'config.xml')) 'the data (config.xml) is left in place'
        Assert (Test-Path -LiteralPath (Join-Path $DataDir 'readarr.db')) 'the data (readarr.db) is left in place'
    }
    catch {
        Save-Diagnostics 'installer'
        Show-LogTail $DataDir
        throw
    }
    finally {
        Save-Folder (Join-Path $DataDir 'logs') 'installer-logs'
        Remove-ServiceIfPresent
        Stop-AppProcesses
    }
}

# -- Main --------------------------------------------------------------------

try {
    switch ($Stage) {
        'console'   { Invoke-ConsoleStage }
        'service'   { Invoke-ServiceStage }
        'migrate'   { Invoke-MigrateStage }
        'installer' { Invoke-InstallerStage }
    }
}
catch {
    Write-Host ''
    Write-Host "FAILED ($Stage): $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    exit 1
}

Write-Host ''
Write-Host "Windows smoke stage '$Stage' passed."
exit 0
