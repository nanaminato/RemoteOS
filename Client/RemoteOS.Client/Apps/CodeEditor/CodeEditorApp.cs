using Client.Apps.Explorer;
using Client.Apps.Explorer.Dialogs;
using Client.Apps.Explorer.ViewModels;
using Client.Apps.Explorer.Views;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.CodeEditor;

/// <summary>A code-focused editor that opens and saves files through the remote file service.</summary>
public sealed class CodeEditorApp : RemoteApplicationBase, IFileOpenApplication
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.codeeditor"),
        DisplayName: "Code Editor",
        Version: "1.0.0",
        IconGlyph: "💻",
        Description: "Syntax-highlighted editor for remote files");

    public override void Activate(AppContext context) => OpenEditor(context, null);

    public void OpenFile(AppContext context, string path) => OpenEditor(context, path);

    private void OpenEditor(AppContext context, string? path)
    {
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var viewModel = new CodeEditorViewModel(files);
        var view = new CodeEditorView { DataContext = viewModel };
        var window = context.ShowWindow("Code Editor", view,
            bounds: new Rect(140, 80, 920, 640),
            iconGlyph: Manifest.IconGlyph);

        viewModel.RequestFileAsync = () => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string>(window, "Select remote file", dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.OpenFile),
                    paths => dialog.Close(paths[0]))
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, GetFilePickerBounds(window));

        viewModel.RequestSavePathAsync = defaultName => context.ShowDialogAsync<string>(window, "Save remote file", dialog =>
        {
            var vm = new TextInputDialogViewModel("Enter a full remote path:", defaultName,
                savePath => dialog.Close(savePath ?? string.Empty), "Save",
                savePath => !string.IsNullOrWhiteSpace(savePath));
            return new TextInputDialogView { DataContext = vm };
        });
        viewModel.RequestSettingsAsync = async () =>
        {
            await context.ShowDialogAsync<bool>(window, "Code Editor settings", dialog =>
            {
                viewModel.CloseSettingsAction = () => dialog.Close(true);
                return new CodeEditorSettingsView { DataContext = viewModel };
            }, new Size(440, 340));
        };

        if (!string.IsNullOrWhiteSpace(path))
            _ = viewModel.OpenPathAsync(path);
    }

    private static Rect GetFilePickerBounds(RemoteOS.WindowManager.ManagedWindow owner)
    {
        var bounds = owner.Info.Bounds;
        const double width = 760;
        const double height = 520;
        var actualWidth = Math.Min(width, Math.Max(480, bounds.Width - 48));
        var actualHeight = Math.Min(height, Math.Max(320, bounds.Height - 56));
        return new Rect(
            bounds.X + Math.Max(24, (bounds.Width - actualWidth) / 2),
            bounds.Y + Math.Max(28, (bounds.Height - actualHeight) / 2),
            actualWidth,
            actualHeight);
    }
}
