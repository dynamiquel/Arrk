using FluentValidation;
using Stackarr.Core.Configuration;

namespace Stackarr.Configuration;

public class StackarrConfigValidator : AbstractValidator<StackarrConfig>
{
    public StackarrConfigValidator()
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
        When(x => x.Notifications is not null, () =>
        {
            RuleForEach(x => x.Notifications.Discord.Routing)
                .Must(entry => IsBlankOrAbsoluteHttpUrl(entry.Value))
                .WithMessage("Discord per-category webhook URLs must be absolute http(s) URLs.");
            RuleForEach(x => x.Notifications.Webhook.Routing)
                .Must(entry => IsBlankOrAbsoluteHttpUrl(entry.Value))
                .WithMessage("Webhook per-category URLs must be absolute http(s) URLs.");

            When(x => x.Notifications.Discord.DirectMessages.Enabled, () =>
            {
                RuleFor(x => x.Notifications.Discord.DirectMessages.BotToken)
                    .NotEmpty()
                    .WithMessage("A Discord bot token is required when direct messages are enabled.");
                RuleFor(x => x.Notifications.Discord.DirectMessages.ApiBaseUrl)
                    .Must(IsBlankOrAbsoluteHttpUrl)
                    .WithMessage("The Discord API base URL must be an absolute http(s) URL.");
            });
        });
        RuleFor(x => x.Monitoring).NotEmpty();
    }

    static bool IsBlankOrAbsoluteHttpUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
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
