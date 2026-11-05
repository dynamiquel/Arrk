using MudBlazor;
using Stackarr.Ui;

namespace Stackarr.Ai;

/// <summary>Shell navigation contributed by the AI module.</summary>
public sealed class AiNavigation : INavigationProvider
{
    // The assistant is an app-bar popup, not a page, so only the admin usage page is navigable. It
    // stays visible regardless of ai.enabled: it explains an off/disabled configuration and links to
    // the editor, which is more useful than hiding it.
    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.System, "AI Usage", "/ai", Icons.Material.Filled.AutoAwesome,
            Order: 60, AdminOnly: true)
    ];
}
