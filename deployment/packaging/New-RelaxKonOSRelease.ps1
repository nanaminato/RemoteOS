[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string] $Runtime = 'win-x64',
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [string] $DownloadBaseUri = 'https://downloads.relaxkon.com/relaxkonos/stable',
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$bundleName = "RelaxKonOS-$Version-$Runtime"
$bundle = Join-Path $OutputDirectory $bundleName
$archive = Join-Path $OutputDirectory ($bundleName + '.zip')

if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') { throw 'Version may contain only letters, numbers, dot, underscore, and dash.' }
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Recurse -Force }
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $bundle 'deployment') -Force | Out-Null

$platform = if ($Runtime.StartsWith('win-')) { 'windows' } else { 'linux' }
$extension = if ($platform -eq 'windows') { '.exe' } else { '' }
$publishTargets = @(
    @{ Project = 'Client\RelaxKonOS.Client.Desktop\RelaxKonOS.Client.Desktop.csproj'; Name = 'client'; Executable = "RelaxKonOS.Client.Desktop$extension" },
    @{ Project = 'RelaxKonOS.Server\RelaxKonOS.Server.csproj'; Name = 'server'; Executable = "RelaxKonOS.Server$extension" },
    @{ Project = 'RelaxKonOS.Guardian.Agent\RelaxKonOS.Guardian.Agent.csproj'; Name = 'guardian'; Executable = "RelaxKonOS.Guardian.Agent$extension" },
    @{ Project = 'RelaxKonOS.PrivilegedHelper\RelaxKonOS.PrivilegedHelper.csproj'; Name = 'privileged-helper'; Executable = "RelaxKonOS.PrivilegedHelper$extension" }
)

foreach ($target in $publishTargets) {
    $destination = Join-Path $bundle ("payload\$platform\" + $target.Name)
    & dotnet publish (Join-Path $projectRoot $target.Project) --configuration $Configuration --runtime $Runtime --self-contained true --output $destination
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($target.Name)." }
    if (-not (Test-Path -LiteralPath (Join-Path $destination $target.Executable) -PathType Leaf)) { throw "Publish output did not contain $($target.Executable)." }
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'deployment\bootstrap') -Destination (Join-Path $bundle 'deployment\bootstrap') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot ("deployment\$platform")) -Destination (Join-Path $bundle ("deployment\$platform")) -Recurse -Force
$manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    runtime = $Runtime
    supportedSystems = if ($platform -eq 'windows') { @('windows') } else { @('debian-12', 'ubuntu-22.04', 'ubuntu-24.04', 'ubuntu-26.04') }
    payload = [ordered]@{}
}
$manifest.payload[$platform] = [ordered]@{
    client = "payload/$platform/client/RelaxKonOS.Client.Desktop$extension"
    server = "payload/$platform/server/RelaxKonOS.Server$extension"
    guardian = "payload/$platform/guardian/RelaxKonOS.Guardian.Agent$extension"
    privilegedHelper = "payload/$platform/privileged-helper/RelaxKonOS.PrivilegedHelper$extension"
}
[IO.File]::WriteAllText((Join-Path $bundle 'manifest.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archive + '.sha256'), "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
$downloadBase = $DownloadBaseUri.TrimEnd('/')
$descriptor = [ordered]@{
    schemaVersion = 1
    version = $Version
    runtime = $Runtime
    url = "$downloadBase/$Version/$Runtime/$([IO.Path]::GetFileName($archive))"
    sha256 = $hash
}
[IO.File]::WriteAllText(($archive + '.json'), ($descriptor | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host "Bundle: $bundle"
Write-Host "Archive: $archive"
Write-Host "SHA-256: $hash"
