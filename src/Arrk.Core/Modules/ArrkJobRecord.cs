namespace Arrk.Core.Modules;

public class ArrkJobRecord : IArrkJobRecord
{
    public required string JobName { get; init; }
    public required Type JobType { get; init; }
}