# RemoteOS Video Player example

This is a Windows `win-x64` development package that uses `LibVLCSharp.Avalonia` 3.10.0 and the official `VideoLAN.LibVLC.Windows` 3.0.23.1 runtime. The native runtime makes the resulting `.roapp` large (roughly 100 MB), but allows broad codec support without requiring VLC to be installed separately.

Build and package it:

```powershell
.\examples\VideoPlayer\build-package.ps1
```

Install it using the Developer Mode CLI, then open a video from RemoteExplorer with **Open with → Video Player**. Grant **读取服务器文件** in Settings → Applications → Video Player before opening remote media.

The example downloads the authorized server file to a per-user temporary file while it is playing, and removes that temporary file when the player window closes.
