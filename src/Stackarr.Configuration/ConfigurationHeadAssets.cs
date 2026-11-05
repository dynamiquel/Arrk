using Stackarr.Ui;

namespace Stackarr.Configuration;

/// <summary>
/// The configuration module's own script. The Monaco editor integration is served from
/// <c>_content/Stackarr.Configuration/js/configEditor.js</c>.
/// </summary>
public sealed class ConfigurationHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Stackarr.Configuration/css/config.css", HeadAssetKind.Style, Order: 130),
        new("_content/Stackarr.Configuration/js/configEditor.js", HeadAssetKind.Script, Order: 100)
    ];
}
