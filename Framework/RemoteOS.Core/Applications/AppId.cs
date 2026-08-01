namespace RemoteOS.Core.Applications;

/// <summary>Strongly typed identifier for a RemoteOS application.</summary>
public readonly record struct AppId(string Value)
{
    public static AppId From(Type type) => new(type.FullName ?? type.Name);

    public override string ToString() => Value;
}
