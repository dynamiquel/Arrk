namespace Stackarr.Ui;

public enum HeadAssetKind
{
    Style,
    Script
}

/// <summary>
/// A stylesheet or script a module wants the shell to load (e.g. its own
/// <c>_content/&lt;Assembly&gt;/css/...</c>). The shell renders them in <see cref="Order"/>.
/// </summary>
public sealed record HeadAsset(string Href, HeadAssetKind Kind, int Order = 100);

/// <summary>Contributes head assets (styles/scripts). Modules register it in DI.</summary>
public interface IHeadAssetProvider
{
    IReadOnlyList<HeadAsset> Assets { get; }
}
