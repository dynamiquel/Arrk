namespace Stackarr.Core.Modules;

public class StackarrJobRecord : IStackarrJobRecord
{
    public required string JobName { get; init; }
    public required Type JobType { get; init; }
}