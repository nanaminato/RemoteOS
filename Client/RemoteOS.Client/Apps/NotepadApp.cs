using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps;

/// <summary>Built-in Notepad — a minimal text editor to exercise a real application window.</summary>
public sealed class NotepadApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.notepad"),
        DisplayName: "Notepad",
        Version: "1.0.0",
        IconGlyph: "📝",
        Description: "A simple text editor");

    public override void Activate(AppContext context)
    {
        var view = new NotepadView { DataContext = new NotepadViewModel() };
        context.ShowWindow("Notepad", view,
            bounds: new Rect(160, 100, 720, 520),
            iconGlyph: Manifest.IconGlyph);
    }
}
