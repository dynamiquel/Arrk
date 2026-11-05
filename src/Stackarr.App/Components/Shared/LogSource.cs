namespace Stackarr.App.Components.Shared;

public readonly record struct LogSource
{
    public enum SourceType { Stackarr, Container }
    public SourceType Type { get; init; }
    public string ServiceKey { get; init; }
    public string ContainerName { get; init; }

    public LogSource(SourceType type, string serviceKey = "", string container = "")
    {
        Type = type;
        ServiceKey = serviceKey;
        ContainerName = container;
    }

    public static LogSource Stackarr => new(SourceType.Stackarr);
    public static LogSource Container(string serviceKey, string containerName) => new(SourceType.Container, serviceKey, containerName);

    public bool IsStackarr => Type == SourceType.Stackarr;
    public bool IsContainer => Type == SourceType.Container;
    public override string ToString() => Type == SourceType.Stackarr ? "Stackarr" : ContainerName;
}