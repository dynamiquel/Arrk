using Arrk.Ui;

namespace Arrk.Ai;

/// <summary>The AI module's own styles (assistant page layout and bubbles) and thread scroll helper.</summary>
public sealed class AiHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Arrk.Ai/css/ai.css", HeadAssetKind.Style, Order: 130),
        new("_content/Arrk.Ai/js/ai.js", HeadAssetKind.Script, Order: 175)
    ];
}
