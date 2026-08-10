[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'RemoteOS'),
    [string] $ServerExecutable,
    [string] $GuardianExecutable,
    [int] $ServerPort = 5000,
    [string] $ServerServiceName = 'RemoteOSServer',
    [string] $GuardianServiceName = 'RemoteOSGuardian'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this signed deployment script from an elevated Administrator session.'
}

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$ServerExecutable = if ([string]::IsNullOrWhiteSpace($ServerExecutable)) { Join-Path $InstallRoot 'server\RemoteOS.Server.exe' } else { $ServerExecutable }
$GuardianExecutable = if ([string]::IsNullOrWhiteSpace($GuardianExecutable)) { Join-Path $InstallRoot 'guardian\RemoteOS.Guardian.Agent.exe' } else { $GuardianExecutable }
$ServerExecutable = [IO.Path]::GetFullPath($ServerExecutable)
$GuardianExecutable = [IO.Path]::GetFullPath($GuardianExecutable)
if (-not (Test-Path -LiteralPath $ServerExecutable -PathType Leaf) -or -not (Test-Path -LiteralPath $GuardianExecutable -PathType Leaf)) {
    throw 'ServerExecutable and GuardianExecutable must both exist.'
}
if ($ServerPort -lt 1 -or $ServerPort -gt 65535) { throw 'ServerPort must be between 1 and 65535.' }

$guardianData = Join-Path $env:ProgramData 'RemoteOS\guardian'
$guardianConfig = Join-Path $guardianData 'guardian.json'
$serverHostConfig = Join-Path (Split-Path -Parent $ServerExecutable) 'appsettings.host.json'
New-Item -ItemType Directory -Force -Path $guardianData | Out-Null
$secretBytes = New-Object byte[] 48
[Security.Cryptography.RandomNumberGenerator]::Fill($secretBytes)
$sharedSecret = [Convert]::ToBase64String($secretBytes)

$agentSettings = [ordered]@{
    pipeName = 'remoteos-guardian'
    sharedSecret = $sharedSecret
    dataDirectory = $guardianData
    protectedServerMonitor = [ordered]@{
        serviceName = $ServerServiceName
        healthUrl = "http://127.0.0.1:$ServerPort/healthz"
        intervalSeconds = 15
        timeoutSeconds = 5
        failureThreshold = 3
    }
}
$serverSettings = [ordered]@{
    GuardianAgent = [ordered]@{
        PipeName = 'remoteos-guardian'
        SharedSecret = $sharedSecret
    }
}
[IO.File]::WriteAllText($guardianConfig, ($agentSettings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($serverHostConfig, ($serverSettings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

# The Agent is privileged only to restart the one installer-declared Server service.
# Keep secret-bearing files readable by SYSTEM and Administrators only.
& icacls $guardianData /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
& icacls $serverHostConfig /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' | Out-Null

function Install-OrUpdateService([string] $Name, [string] $BinaryPath) {
    if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
        & sc.exe config $Name ("binPath= " + $BinaryPath) start= auto | Out-Null
    } else {
        New-Service -Name $Name -DisplayName $Name -BinaryPathName $BinaryPath -StartupType Automatic | Out-Null
    }
    & sc.exe failure $Name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
}

Install-OrUpdateService $GuardianServiceName ('"' + $GuardianExecutable + '" --config "' + $guardianConfig + '"')
Install-OrUpdateService $ServerServiceName ('"' + $ServerExecutable + '"')

if ($PSCmdlet.ShouldProcess('RemoteOS services', 'Start or restart')) {
    Restart-Service -Name $GuardianServiceName -Force
    Restart-Service -Name $ServerServiceName -Force
}

Write-Host "Installed $GuardianServiceName and $ServerServiceName. No manual Guardian service configuration is required."
