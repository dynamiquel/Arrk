namespace Stackarr.Core.Modules;

public interface IStackarrJobRecord
{
    string JobName { get; }
    Type JobType { get; }
}