using FluentValidation;
using Arrk.Core.Configuration;

namespace Arrk.Configuration;

public class ArrkConfigValidator : AbstractValidator<ArrkConfig>
{
    public ArrkConfigValidator()
    {
        RuleFor(x => x.Storage).NotEmpty();
        RuleFor(x => x.Storage.Media).NotEmpty().WithMessage("Storage base path is required");
        RuleFor(x => x.Storage.Downloads).NotEmpty().WithMessage("Storage downloads path is required");

        RuleFor(x => x.Vpn).NotEmpty();
        When(x => x.Vpn.Enabled, () =>
        {
            RuleFor(x => x.Vpn.WireguardPrivateKey).NotEmpty()
                .WithMessage("WireGuard private key is required when VPN is enabled");
            RuleFor(x => x.Vpn.WireguardAddresses).NotEmpty()
                .WithMessage("WireGuard addresses are required when VPN is enabled");
        });

        RuleFor(x => x.Routes).NotEmpty();
        When(x => x.Routes is not null, () =>
        {
            RuleFor(x => x.Routes.HttpPort).InclusiveBetween(0, 65535);
            RuleFor(x => x.Routes.HttpsPort).InclusiveBetween(0, 65535);
        });

        RuleFor(x => x.Notifications).NotEmpty();
        RuleFor(x => x.Monitoring).NotEmpty();
    }
}

public class StorageConfigValidator : AbstractValidator<StorageConfig>
{
    public StorageConfigValidator()
    {
        RuleFor(x => x.Media).NotEmpty();
        RuleFor(x => x.Downloads).NotEmpty();
    }
}

public class VpnConfigValidator : AbstractValidator<VpnConfig>
{
    public VpnConfigValidator()
    {
        When(x => x.Enabled, () =>
        {
            RuleFor(x => x.WireguardPrivateKey).NotEmpty();
            RuleFor(x => x.WireguardAddresses).NotEmpty();
            RuleFor(x => x.Provider).NotEmpty();
            RuleFor(x => x.Type).Must(t => t is "wireguard" or "openvpn")
                .WithMessage("VPN type must be 'wireguard' or 'openvpn'");
        });

        RuleForEach(x => x.Ports).ChildRules(port =>
        {
            port.RuleFor(p => p.Host).InclusiveBetween(1, 65535);
            port.RuleFor(p => p.Container).InclusiveBetween(1, 65535);
            port.RuleFor(p => p.Protocol).Must(p => p is "tcp" or "udp")
                .WithMessage("VPN port protocol must be 'tcp' or 'udp'");
        });
    }
}
