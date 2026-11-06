namespace Arrk.App.Components.Shared;

public readonly record struct LogSource
{
    public enum SourceType { Arrk, Container }
    public SourceType Type { get; init; }
    public string ServiceKey { get; init; }
    public string ContainerName { get; init; }

    public LogSource(SourceType type, string serviceKey = "", string container = "")
    {
        Type = type;
        ServiceKey = serviceKey;
        ContainerName = container;
    }

    public static LogSource Arrk => new(SourceType.Arrk);
    public static LogSource Container(string serviceKey, string containerName) => new(SourceType.Container, serviceKey, containerName);

    public bool IsArrk => Type == SourceType.Arrk;
    public bool IsContainer => Type == SourceType.Container;
    public override string ToString() => Type == SourceType.Arrk ? "Arrk" : ContainerName;
}