using System.Xml.Linq;

namespace Stackarr.Jellyfin;

/// <summary>
/// Reads and writes Jellyfin's <c>network.xml</c> so Stackarr can set the server's Base URL
/// (the documented way to run Jellyfin behind a reverse proxy on a subpath). Jellyfin has no
/// environment variable for this setting, so the config file must be edited directly.
/// </summary>
public static class JellyfinNetworkConfig
{
    public const string NetworkFileName = "network.xml";
    const string Namespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// Upserts the <c>&lt;BaseUrl&gt;</c> value inside every <c>network.xml</c> found under
    /// <paramref name="configDirectory"/>. When no file exists, one is created at
    /// <paramref name="defaultFileName"/> (the image's standard location). Jellyfin will
    /// pick up the change on its next restart.
    /// </summary>
    public static void SetBaseUrl(string configDirectory, string? baseUrl, string? defaultFileName = null)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
            return;

        if (!Directory.Exists(configDirectory))
        {
            // Only create the directory when we intend to seed a new file; otherwise there is
            // nothing to update and we should not create empty config folders.
            if (string.IsNullOrWhiteSpace(defaultFileName))
                return;

            Directory.CreateDirectory(configDirectory);
        }

        string normalized = NormalizeBaseUrl(baseUrl);

        List<string> files = [.. Directory.EnumerateFiles(configDirectory, NetworkFileName, SearchOption.AllDirectories)];

        if (files.Count == 0 && !string.IsNullOrWhiteSpace(defaultFileName))
        {
            string target = Path.Combine(configDirectory, defaultFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            WriteDocument(target, normalized);
            return;
        }

        foreach (string file in files)
        {
            UpdateDocument(file, normalized);
        }
    }

    static string NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl == "/")
            return string.Empty;

        string value = baseUrl.Trim();
        if (!value.StartsWith('/'))
            value = "/" + value;
        return value.TrimEnd('/');
    }

    static void UpdateDocument(string path, string baseUrl)
    {
        try
        {
            XDocument doc = XDocument.Load(path);
            XElement root = doc.Root ?? throw new InvalidDataException("network.xml has no root element");

            XElement? existing = root.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseUrl");
            if (string.IsNullOrEmpty(baseUrl))
            {
                existing?.Remove();
            }
            else if (existing != null)
            {
                existing.Value = baseUrl;
            }
            else
            {
                root.Add(new XElement("BaseUrl", baseUrl));
            }

            doc.Save(path);
        }
        catch (Exception)
        {
            // Best effort: a malformed or locked file should never break provisioning.
        }
    }

    static void WriteDocument(string path, string baseUrl)
    {
        XElement root = new("NetworkConfiguration",
            new XAttribute(XNamespace.Xmlns + "xsi", Namespace));

        if (!string.IsNullOrEmpty(baseUrl))
        {
            root.Add(new XElement("BaseUrl", baseUrl));
        }

        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
    }
}
