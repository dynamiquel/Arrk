namespace Arrk.Core.Services;

public class Service(IServiceDefinition definition)
{
    public IServiceDefinition Definition { get; } = definition;
    public IServiceState State { get; set; } = new ServiceState { ServiceKey = definition.Key };
}
