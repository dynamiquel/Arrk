using FluentValidation.Results;
using Stackarr.Core.Configuration;

namespace Stackarr.Configuration.Tests;

public class VpnConfigValidatorTests
{
    readonly VpnConfigValidator _validator = new();

    [Test]
    public async Task Validate_VpnDisabled_IsValid()
    {
        VpnConfig config = new() { Enabled = false };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_VpnEnabled_ValidConfig_IsValid()
    {
        VpnConfig config = new()
        {
            Enabled = true,
            Provider = "protonvpn",
            Type = "wireguard",
            WireguardPrivateKey = "some-key",
            WireguardAddresses = "10.0.0.1/32"
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_VpnEnabled_EmptyPrivateKey_IsInvalid()
    {
        VpnConfig config = new()
        {
            Enabled = true,
            Provider = "protonvpn",
            Type = "wireguard",
            WireguardPrivateKey = "",
            WireguardAddresses = "10.0.0.1/32"
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_VpnEnabled_EmptyAddresses_IsInvalid()
    {
        VpnConfig config = new()
        {
            Enabled = true,
            Provider = "protonvpn",
            Type = "wireguard",
            WireguardPrivateKey = "some-key",
            WireguardAddresses = ""
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_VpnEnabled_EmptyProvider_IsInvalid()
    {
        VpnConfig config = new()
        {
            Enabled = true,
            Provider = "",
            Type = "wireguard",
            WireguardPrivateKey = "some-key",
            WireguardAddresses = "10.0.0.1/32"
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_VpnEnabled_InvalidType_IsInvalid()
    {
        VpnConfig config = new()
        {
            Enabled = true,
            Provider = "protonvpn",
            Type = "invalid",
            WireguardPrivateKey = "some-key",
            WireguardAddresses = "10.0.0.1/32"
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_VpnEnabled_OpenVpnType_IsValid()
    {
        VpnConfig config = new()
        {
            Enabled = true,
            Provider = "protonvpn",
            Type = "openvpn",
            WireguardPrivateKey = "some-key",
            WireguardAddresses = "10.0.0.1/32"
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_Ports_ValidConfig_IsValid()
    {
        VpnConfig config = new()
        {
            Ports =
            [
                new() { Host = 8080, Container = 8080, Protocol = "tcp" },
                new() { Host = 51413, Container = 51413, Protocol = "udp" }
            ]
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_Ports_InvalidProtocol_IsInvalid()
    {
        VpnConfig config = new()
        {
            Ports = [new() { Host = 8080, Container = 8080, Protocol = "sctp" }]
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_Ports_OutOfRange_IsInvalid()
    {
        VpnConfig config = new()
        {
            Ports = [new() { Host = 0, Container = 70000, Protocol = "tcp" }]
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

}
