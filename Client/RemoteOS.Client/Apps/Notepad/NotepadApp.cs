using Client.Apps.Explorer;
using Client.Apps.Explorer.Dialogs;
using Client.Apps.Explorer.ViewModels;
using Client.Apps.Explorer.Views;
using Client.Localization;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Notepad;

/// <summary>内置 Notebook：可编辑远程文本文件，并支持指定编码打开与保存。</summary>
public sealed class NotepadApp : RemoteApplicationBase, IFileOpenApplication
{
    public static IReadOnlyList<string> SupportedExtensions { get; } =
    [
        ".txt", ".text", ".md", ".markdown", ".mdx", ".rst", ".adoc", ".asciidoc", ".log", ".nfo",
        ".csv", ".tsv", ".tab", ".ini", ".cfg", ".conf", ".config", ".properties", ".yaml", ".yml", ".toml",
        ".xml", ".xsd", ".xsl", ".xslt", ".json", ".jsonc", ".json5", ".html", ".htm", ".xhtml",
        ".css", ".scss", ".sass", ".less", ".tex", ".bib", ".srt", ".vtt", ".ics", ".vcf", ".diff", ".patch",
        ".asc", ".pem", ".crt", ".cer", ".pub",
    ];

    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.notepad"),
        DisplayName: "Notebook",
        Version: "1.0.0",
        IconGlyph: "📝",
        Description: "Text editor for remote files",
        SupportedFileExtensions: SupportedExtensions,
        SupportsExtensionlessFiles: true);

    public override void Activate(AppContext context)
        => OpenEditor(context, null);

    public void OpenFile(AppContext context, string path)
        => OpenEditor(context, path);

    private void OpenEditor(AppContext context, string? path)
    {
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var viewModel = new NotepadViewModel(files);
        var view = new NotepadView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("application.remoteos.notepad.display_name"), view,
            bounds: new Rect(160, 100, 820, 580),
            iconGlyph: Manifest.IconGlyph);

        viewModel.RequestFileAsync = () => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string>(window, LocalizedText.Get("notepad.open_remote_file"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, Filters: [
                        new ExplorerFileFilter(LocalizedText.Get("notepad.text_file_filter"), SupportedExtensions.Select(extension => $"*{extension}").ToArray(),
                            IncludeExtensionlessFiles: true),
                    ]),
                    paths => dialog.Close(paths[0]))
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, GetFilePickerBounds(window));
        viewModel.RequestSavePathAsync = defaultName => context.ShowDialogAsync<string>(window, LocalizedText.Get("notepad.save_remote_file"), dialog =>
        {
            var vm = new TextInputDialogViewModel(LocalizedText.Get("notepad.remote_path_prompt"), defaultName, path => dialog.Close(path ?? string.Empty), LocalizedText.Get("common.save"),
                path => !string.IsNullOrWhiteSpace(path));
            return new TextInputDialogView { DataContext = vm };
        });
        viewModel.RequestSettingsAsync = async () =>
        {
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("notepad.settings.title"), dialog =>
            {
                viewModel.CloseSettingsAction = () => dialog.Close(true);
                return new NotepadSettingsView { DataContext = viewModel };
            }, new Size(420, 300));
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
