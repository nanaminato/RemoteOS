param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot 'RemoteOS.Example.NetworkInspector.csproj'
dotnet build $project --configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$output = Join-Path $projectRoot "bin\$Configuration\net10.0"
$staging = Join-Path $output '.roapp-staging'
$package = Join-Path $output 'RemoteOS.Example.NetworkInspector.roapp'
$zipPackage = "$package.zip"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
if (Test-Path -LiteralPath $zipPackage) { Remove-Item -LiteralPath $zipPackage -Force }

New-Item -ItemType Directory -Path (Join-Path $staging 'lib\net10.0') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'manifest.json') -Destination (Join-Path $staging 'manifest.json')
Copy-Item -LiteralPath (Join-Path $output 'RemoteOS.Example.NetworkInspector.dll') -Destination (Join-Path $staging 'lib\net10.0\RemoteOS.Example.NetworkInspector.dll')
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPackage -Force
Move-Item -LiteralPath $zipPackage -Destination $package
Remove-Item -LiteralPath $staging -Recurse -Force
Write-Host "Built $package"
