using System.ComponentModel;
using Avalonia.Controls;
using AvaloniaEdit.Highlighting;

namespace Client.Apps;

public partial class CodeEditorView : UserControl
{
    private CodeEditorViewModel? _viewModel;

    public CodeEditorView()
    {
        InitializeComponent();
        Editor.Document.TextChanged += OnEditorTextChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as CodeEditorViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Editor.Text = _viewModel.Text;
            UpdateSyntaxHighlighting(_viewModel.CurrentPath);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(CodeEditorViewModel.Text) && Editor.Text != _viewModel?.Text)
            Editor.Text = _viewModel?.Text ?? string.Empty;
        else if (eventArgs.PropertyName == nameof(CodeEditorViewModel.CurrentPath))
            UpdateSyntaxHighlighting(_viewModel?.CurrentPath);
    }

    private void OnEditorTextChanged(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is not null && _viewModel.Text != Editor.Text)
            _viewModel.Text = Editor.Text;
    }

    private void UpdateSyntaxHighlighting(string? path)
    {
        Editor.SyntaxHighlighting = string.IsNullOrWhiteSpace(path)
            ? null
            : HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
    }
}
