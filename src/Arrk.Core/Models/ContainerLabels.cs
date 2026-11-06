namespace Arrk.Core.Models;

public static class ContainerLabels
{
    // Stable on-disk Docker metadata: retain ownership of resources created before the rename.
    public const string Managed = "stackarr.managed";
    public const string InstanceId = "stackarr.instance-id";
    public const string Service = "stackarr.service";
    public const string Network = "stackarr.network";

    public const string ManagedValue = "true";
}
