param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot 'RemoteOS.Example.VideoPlayer.csproj'
dotnet build $project --configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$output = Join-Path $projectRoot "bin\$Configuration\net10.0\win-x64"
$staging = Join-Path $output '.roapp-staging'
$package = Join-Path $output 'RemoteOS.Example.VideoPlayer.roapp'
$zipPackage = "$package.zip"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
if (Test-Path -LiteralPath $zipPackage) { Remove-Item -LiteralPath $zipPackage -Force }

$library = Join-Path $staging 'lib\net10.0'
New-Item -ItemType Directory -Path $library -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $library 'libvlc') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'manifest.json') -Destination (Join-Path $staging 'manifest.json')
Get-ChildItem -LiteralPath $output -Force | Where-Object {
    $_.Name -notin @('.roapp-staging', 'RemoteOS.Example.VideoPlayer.roapp', 'RemoteOS.Example.VideoPlayer.roapp.zip') -and
    $_.Name -ne 'libvlc' -and
    $_.Extension -notin @('.pdb', '.deps.json', '.runtimeconfig.json')
} | Copy-Item -Destination $library -Recurse -Force
Copy-Item -LiteralPath (Join-Path $output 'libvlc\win-x64') -Destination (Join-Path $library 'libvlc\win-x64') -Recurse -Force
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPackage -Force
Move-Item -LiteralPath $zipPackage -Destination $package
Remove-Item -LiteralPath $staging -Recurse -Force
Write-Host "Built $package"
