using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Client.Localization;
using RemoteOS.Protocol.Docker;

namespace Client.Apps.Docker.Views;

/// <summary>Resource-list subviews, kept separate from workspace layout for easy column tuning.</summary>
internal static class DockerManagerTables
{
    public static DataGrid Containers(DockerManagerViewModel vm)
    {
        var table = Table(vm.Containers); table.Columns.Add(Column("docker.table.name", nameof(DockerContainerDto.Names), 180)); table.Columns.Add(Column("docker.table.image", nameof(DockerContainerDto.Image), 210)); table.Columns.Add(Column("docker.table.status", nameof(DockerContainerDto.Status), 260)); table.Columns.Add(Column("docker.table.state", nameof(DockerContainerDto.State), 100)); table.SelectionChanged += (_, _) => vm.SelectedContainer = table.SelectedItem as DockerContainerDto; return table;
    }
    public static DataGrid Stacks(DockerManagerViewModel vm)
    {
        var table = Table(vm.Stacks); table.Columns.Add(Column("docker.table.name", nameof(DockerStackDto.Name), 220)); table.Columns.Add(Column("docker.table.status", nameof(DockerStackDto.Status), 300)); table.Columns.Add(Column("docker.table.config_files", nameof(DockerStackDto.ConfigFiles), 420)); return table;
    }
    public static DataGrid Images(DockerManagerViewModel vm)
    {
        var table = Table(vm.Images); table.Columns.Add(Column("docker.table.repository", nameof(DockerImageDto.Repository), 240)); table.Columns.Add(Column("docker.table.tag", nameof(DockerImageDto.Tag), 120)); table.Columns.Add(Column("docker.table.size", nameof(DockerImageDto.Size), 100)); table.Columns.Add(Column("docker.table.created", nameof(DockerImageDto.CreatedSince), 180)); table.SelectionChanged += (_, _) => vm.SelectedImage = table.SelectedItem as DockerImageDto; return table;
    }
    public static DataGrid Networks(DockerManagerViewModel vm)
    {
        var table = Table(vm.Networks); table.Columns.Add(Column("docker.table.name", nameof(DockerNetworkDto.Name), 240)); table.Columns.Add(Column("docker.table.driver", nameof(DockerNetworkDto.Driver), 160)); table.Columns.Add(Column("docker.table.scope", nameof(DockerNetworkDto.Scope), 140)); table.SelectionChanged += (_, _) => vm.SelectedNetwork = table.SelectedItem as DockerNetworkDto; return table;
    }
    public static DataGrid Volumes(DockerManagerViewModel vm)
    {
        var table = Table(vm.Volumes); table.Columns.Add(Column("docker.table.name", nameof(DockerVolumeDto.Name), 220)); table.Columns.Add(Column("docker.table.driver", nameof(DockerVolumeDto.Driver), 140)); table.Columns.Add(Column("docker.table.mount_point", nameof(DockerVolumeDto.Mountpoint), 400)); table.SelectionChanged += (_, _) => vm.SelectedVolume = table.SelectedItem as DockerVolumeDto; return table;
    }
    private static DataGrid Table(System.Collections.IEnumerable source) => new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserReorderColumns = false, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, SelectionMode = DataGridSelectionMode.Single, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 230, ItemsSource = source };
    private static DataGridTextColumn Column(string key, string property, double width) => new() { Header = LocalizedText.Get(key), Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Pixel) };
}
