namespace Client.Apps;

public sealed record FilePickerEntry(string Name, string FullPath, bool IsDirectory)
{
    public string Icon => IsDirectory ? "📁" : "📄";
}
