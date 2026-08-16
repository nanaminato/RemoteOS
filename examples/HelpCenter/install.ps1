param([string]$Configuration = 'Debug')

$env:REMOTEOS_DEV_TOKEN = Read-Host 'RemoteOS developer token'
dotnet run --project Tools/RemoteOS.DevCli -- install ".\examples\HelpCenter\bin\$Configuration\net10.0\RemoteOS.Example.HelpCenter.roapp"
