# 在 RemoteOS 设置 → 应用 → 开发者模式中启用后复制令牌
$env:REMOTEOS_DEV_TOKEN = "8cWM1JRIHoYLkx5po-F-K7t3cJptejVlp2Zq1VlO9uI"

dotnet run --project Tools/RemoteOS.DevCli -- install `
  .\examples\ServerMonitor\bin\Debug\net10.0\RemoteOS.Example.ServerMonitor.roapp