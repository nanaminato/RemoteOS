# RemoteOS Video Player example

This is a Windows `win-x64` development package that uses `LibVLCSharp.Avalonia` 3.10.0 and the official `VideoLAN.LibVLC.Windows` 3.0.23.1 runtime. The native runtime makes the resulting `.roapp` large (roughly 100 MB), but allows broad codec support without requiring VLC to be installed separately.

Build and package it:

```bash
dotnet run --project Tools/RemoteOS.DevCli -- pack ./examples/VideoPlayer --runtime win-x64 --configuration Release
```

Install it using the Developer Mode CLI, then open a video from RemoteExplorer with **Open with → Video Player**. Grant **读取服务器文件** in Settings → Applications → Video Player before opening remote media.

The example plays the authorized server file through a host-renewed, single-file HTTP media lease. The URL supports HTTP Range requests for seeking and expires automatically when the player closes or the host stops renewing it.
