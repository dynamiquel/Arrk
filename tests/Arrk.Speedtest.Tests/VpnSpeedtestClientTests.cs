using System.Net;
using Microsoft.Extensions.Configuration;
using Arrk.Core;
using Arrk.Core.Services;

namespace Arrk.Speedtest.Tests;

public class VpnSpeedtestClientTests
{
    static ArrkInstance Instance() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = "demo" })
        .Build());

    [Test]
    public async Task ProxyAddress_TargetsGluetunHttpProxyPort()
    {
        VpnSpeedtestClient client = new(Instance());

        await Assert.That(client.ProxyAddress).IsEqualTo($"http://stackarr-demo-gluetun:{GluetunServiceDefinition.HttpProxyPort}");
    }

    [Test]
    public async Task CreateClient_UsesProxy()
    {
        VpnSpeedtestClient client = new(Instance());

        using HttpClient http = client.CreateClient();
        using SocketsHttpHandler handler = client.CreateHandler();

        await Assert.That(http).IsNotNull();
        await Assert.That(http.Timeout).IsEqualTo(TimeSpan.FromSeconds(120));
        await Assert.That(handler.UseProxy).IsTrue();
        await Assert.That(handler.Proxy).IsNotNull();
        Uri proxyUri = handler.Proxy!.GetProxy(new Uri("https://speed.cloudflare.com"));
        await Assert.That(proxyUri.Host).IsEqualTo("stackarr-demo-gluetun");
        await Assert.That(proxyUri.Port).IsEqualTo(GluetunServiceDefinition.HttpProxyPort);
    }
}
