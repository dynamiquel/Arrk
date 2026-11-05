using System.Xml.Linq;
using Stackarr.Jellyfin;

namespace Stackarr.Orchestration.Tests;

public class JellyfinNetworkConfigTests
{
    [Test]
    public async Task SetBaseUrl_CreatesFile_WhenMissing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-jellyfin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            JellyfinNetworkConfig.SetBaseUrl(dir, "/jellyfin", defaultFileName: JellyfinNetworkConfig.NetworkFileName);

            string path = Path.Combine(dir, JellyfinNetworkConfig.NetworkFileName);
            await Assert.That(File.Exists(path)).IsTrue();

            XDocument doc = XDocument.Load(path);
            await Assert.That(doc.Root!.Element("BaseUrl")?.Value).IsEqualTo("/jellyfin");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task SetBaseUrl_UpdatesExistingFile_PreservingOtherValues()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-jellyfin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, JellyfinNetworkConfig.NetworkFileName);
            File.WriteAllText(path,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<NetworkConfiguration>\n  <EnableIPv6>true</EnableIPv6>\n</NetworkConfiguration>\n");

            JellyfinNetworkConfig.SetBaseUrl(dir, "/jellyfin");

            XDocument doc = XDocument.Load(path);
            await Assert.That(doc.Root!.Element("EnableIPv6")?.Value).IsEqualTo("true");
            await Assert.That(doc.Root!.Element("BaseUrl")?.Value).IsEqualTo("/jellyfin");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task SetBaseUrl_Null_RemovesBaseUrl()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-jellyfin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, JellyfinNetworkConfig.NetworkFileName);
            File.WriteAllText(path,
                "<NetworkConfiguration>\n  <BaseUrl>/jellyfin</BaseUrl>\n</NetworkConfiguration>\n");

            JellyfinNetworkConfig.SetBaseUrl(dir, null);

            XDocument doc = XDocument.Load(path);
            await Assert.That(doc.Root!.Element("BaseUrl")).IsNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task SetBaseUrl_NormalizesMissingLeadingSlash()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-jellyfin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            JellyfinNetworkConfig.SetBaseUrl(dir, "jellyfin/", defaultFileName: JellyfinNetworkConfig.NetworkFileName);

            XDocument doc = XDocument.Load(Path.Combine(dir, JellyfinNetworkConfig.NetworkFileName));
            await Assert.That(doc.Root!.Element("BaseUrl")?.Value).IsEqualTo("/jellyfin");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
