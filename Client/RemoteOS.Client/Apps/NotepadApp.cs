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
        var viewModel = new NotepadViewModel();
        var view = new NotepadView { DataContext = viewModel };
        var window = context.ShowWindow("Notepad", view,
            bounds: new Rect(160, 100, 720, 520),
            iconGlyph: Manifest.IconGlyph);

        viewModel.RequestTextAsync = () => context.ShowDialogAsync<string>(window, "插入文本", dialog =>
        {
            var dialogViewModel = new NotepadInsertDialogViewModel(dialog.Close, dialog.Cancel);
            dialogViewModel.RequestNestedTextAsync = () => dialog.ShowDialogAsync<string>("添加文本", childDialog =>
                new NotepadInsertDialogView
                {
                    DataContext = new NotepadInsertDialogViewModel(childDialog.Close, childDialog.Cancel),
                });
            return new NotepadInsertDialogView { DataContext = dialogViewModel };
        });
        viewModel.RequestFileAsync = () => context.ShowDialogAsync<string>(window, "选择要打开的文件", dialog =>
            new FilePickerView
            {
                DataContext = new FilePickerViewModel(dialog.Close, dialog.Cancel),
            });
    }
}
