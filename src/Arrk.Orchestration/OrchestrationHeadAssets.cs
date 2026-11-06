using Arrk.Ui;

namespace Arrk.Orchestration;

/// <summary>
/// The orchestration module's own stylesheet (Docker pages/components, service cards and the
/// scheduler), served from <c>_content/Arrk.Orchestration/css/orchestration.css</c>.
/// </summary>
public sealed class OrchestrationHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Arrk.Orchestration/css/orchestration.css", HeadAssetKind.Style, Order: 133)
    ];
}
