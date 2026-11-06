namespace Arrk.Core.Modules;

public interface IArrkJobRecord
{
    string JobName { get; }
    Type JobType { get; }
}