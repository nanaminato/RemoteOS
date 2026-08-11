using Avalonia.Controls;
using RemoteOS.Core;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;

namespace RemoteOS.WindowManager;

/// <summary>Parameters used to create a managed desktop window.</summary>
public sealed record WindowCreateOptions(
    AppId OwnerAppId,
    string Title,
    Control Content,
    Rect? Bounds = null,
    string? IconGlyph = null,
    bool CanResize = true,
    bool CanMinimize = true,
    bool CanMaximize = true,
    bool IsModalDialog = false,
    WindowInitialPlacement InitialPlacement = WindowInitialPlacement.Explicit);
