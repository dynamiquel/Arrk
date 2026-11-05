using FluentValidation.Results;
using Stackarr.Core.Configuration;

namespace Stackarr.Configuration.Tests;

public class ConfigValidatorTests
{
    readonly StackarrConfigValidator _validator = new();

    [Test]
    public async Task Validate_DefaultConfig_IsValid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_EmptyStorageMedia_IsInvalid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Storage.Media = "";

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains(e => e.PropertyName == "Storage.Media");
    }

    [Test]
    public async Task Validate_EmptyStorageDownloads_IsInvalid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Storage.Downloads = "";

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains(e => e.PropertyName == "Storage.Downloads");
    }

    [Test]
    public async Task Validate_VpnEnabled_EmptyPrivateKey_IsInvalid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Vpn.Enabled = true;
        config.Vpn.WireguardPrivateKey = "";

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains(e => e.PropertyName == "Vpn.WireguardPrivateKey");
    }

    [Test]
    public async Task Validate_VpnEnabled_EmptyAddresses_IsInvalid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Vpn.Enabled = true;
        config.Vpn.WireguardAddresses = "";

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains(e => e.PropertyName == "Vpn.WireguardAddresses");
    }

    [Test]
    public async Task Validate_VpnDisabled_NoKeysRequired()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Vpn.Enabled = false;
        config.Vpn.WireguardPrivateKey = "";
        config.Vpn.WireguardAddresses = "";

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_VpnEnabled_ValidConfig_IsValid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Vpn.Enabled = true;
        config.Vpn.WireguardPrivateKey = "secret";
        config.Vpn.WireguardAddresses = "10.0.0.1/32";

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_NullRoutes_IsInvalid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Routes = null!;

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains(e => e.PropertyName == "Routes");
    }

    [Test]
    public async Task Validate_RoutesPorts_AllowZero_ForTunnelOnly()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Routes.HttpPort = 0;
        config.Routes.HttpsPort = 0;

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_RoutesPort_OutOfRange_IsInvalid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Routes.HttpPort = 70000;

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains(e => e.PropertyName == "Routes.HttpPort");
    }

    [Test]
    public async Task Validate_NullNotifications_IsInvalid()    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Notifications = null!;

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains(e => e.PropertyName == "Notifications");
    }

    [Test]
    public async Task Validate_NullMonitoring_IsInvalid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Monitoring = null!;

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains(e => e.PropertyName == "Monitoring");
    }

    [Test]
    public async Task Validate_AbsoluteRoutingUrls_IsValid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Notifications.Discord.Routing["media"] = "https://discord.example/media";
        config.Notifications.Webhook.Routing["media.requests"] = "https://n8n.example/requests";

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_RelativeRoutingUrl_IsInvalid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Notifications.Discord.Routing["media"] = "/webhooks/media";

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_BlankRoutingUrl_IsValid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Notifications.Discord.Routing["media"] = "";

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_DirectMessagesWithoutBotToken_IsInvalid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Notifications.Discord.DirectMessages.Enabled = true;

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains(e => e.PropertyName.Contains("BotToken"));
    }

    [Test]
    public async Task Validate_DirectMessagesWithToken_IsValid()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Notifications.Discord.DirectMessages.Enabled = true;
        config.Notifications.Discord.DirectMessages.BotToken = "token";

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsTrue();
    }
}
