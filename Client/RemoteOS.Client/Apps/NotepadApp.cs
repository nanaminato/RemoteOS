using Client.Apps.Explorer;
using Client.Apps.Explorer.Dialogs;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps;

/// <summary>内置 Notebook：可编辑远程文本文件，并支持指定编码打开与保存。</summary>
public sealed class NotepadApp : RemoteApplicationBase, IFileOpenApplication
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.notepad"),
        DisplayName: "Notebook",
        Version: "1.0.0",
        IconGlyph: "📝",
        Description: "Text editor for remote files");

    public override void Activate(AppContext context)
        => OpenEditor(context, null);

    public void OpenFile(AppContext context, string path)
        => OpenEditor(context, path);

    private void OpenEditor(AppContext context, string? path)
    {
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var viewModel = new NotepadViewModel(files);
        var view = new NotepadView { DataContext = viewModel };
        var window = context.ShowWindow("Notebook", view,
            bounds: new Rect(160, 100, 820, 580),
            iconGlyph: Manifest.IconGlyph);

        viewModel.RequestFileAsync = () => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string>(window, "Open remote file", dialog => new RemoteFilePickerView
            {
                DataContext = new RemoteFilePickerViewModel(files, path => dialog.Close(path), dialog.Cancel),
            });
        viewModel.RequestSavePathAsync = defaultName => context.ShowDialogAsync<string>(window, "Save remote file", dialog =>
        {
            var vm = new TextInputDialogViewModel("Enter a full remote path:", defaultName, path => dialog.Close(path ?? string.Empty), "Save",
                path => !string.IsNullOrWhiteSpace(path));
            return new TextInputDialogView { DataContext = vm };
        });
        if (!string.IsNullOrWhiteSpace(path))
            _ = viewModel.OpenPathAsync(path);
    }
}
