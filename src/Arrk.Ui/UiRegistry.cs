using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Arrk.Core.Configuration;

namespace Arrk.Ui;

/// <summary>
/// The composed view of everything the modules contribute: navigation, slot components (explicit
/// providers plus <see cref="UiRegionAttribute"/>-decorated components) and head assets. Built once
/// at startup from DI.
/// <para>
/// Availability is resolved live from configuration: every contribution may carry its own
/// <c>EnabledConfigPath</c> (typically the same <c>services:&lt;x&gt;</c> key the service definition
/// uses), and the registry filters it out, and raises <see cref="Changed"/> on reload, when that
/// section is disabled. The shell therefore never references a module to know whether it is on.
/// </para>
/// It also validates that no two components claim the same route, so a module (or an add-on) can
/// never silently break navigation.
/// </summary>
public sealed class UiRegistry : IDisposable
{
    /// <summary>
    /// Assets the shared component library always needs (theme stylesheets and the JS the reusable
    /// components call). Included automatically so any host, or module, that renders a shared
    /// component gets its styles and scripts without wiring them up.
    /// </summary>
    static readonly HeadAsset[] BuiltInHeadAssets =
    [
        // Order matters: app.css (component styles) then site.css (tokens/base + button design
        // system) so the design system wins on equal specificity, matching the original shell.
        new("_content/Arrk.Ui/css/app.css", HeadAssetKind.Style, Order: 10),
        new("_content/Arrk.Ui/css/site.css", HeadAssetKind.Style, Order: 11),
        new("_content/Arrk.Ui/js/scrollRow.js", HeadAssetKind.Script, Order: 10),
        new("_content/Arrk.Ui/js/arrk-sheet.js", HeadAssetKind.Script, Order: 11),
        new("_content/Arrk.Ui/js/arrk-autoscroll.js", HeadAssetKind.Script, Order: 12)
    ];

    readonly IConfiguration _configuration;
    readonly IReadOnlyList<NavigationEntry> _navigation;
    readonly IReadOnlyList<UiSlot> _slots;
    readonly IReadOnlyList<HeadAsset> _headAssets;
    readonly IReadOnlyList<DiscoverSource> _discoverSources;
    readonly IDisposable _reloadSubscription;

    public UiRegistry(
        IEnumerable<INavigationProvider> navigation,
        IEnumerable<IUiSlotProvider> slotProviders,
        IEnumerable<IHeadAssetProvider> headAssets,
        IEnumerable<IDiscoverSourceProvider> discoverSources,
        IUiAssemblyProvider assemblies,
        IConfiguration configuration)
    {
        _configuration = configuration;
        _navigation = navigation.SelectMany(p => p.Entries).ToList();
        _headAssets = [.. BuiltInHeadAssets.Concat(headAssets.SelectMany(a => a.Assets)).OrderBy(a => a.Order)];
        _discoverSources = discoverSources.SelectMany(p => p.Sources).OrderBy(s => s.Order).ToList();

        List<Type> uiTypes = EnumerateUiAssemblies(assemblies).SelectMany(SafeTypes).ToList();
        List<UiSlot> slots = slotProviders.SelectMany(p => p.Slots).ToList();
        slots.AddRange(DiscoverRegionComponents(uiTypes));
        _slots = slots;

        IReadOnlyList<string> conflicts = FindRouteConflicts(uiTypes);
        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                "Duplicate Blazor routes detected across the host and its modules. " +
                "Every route template must be unique:\n  " + string.Join("\n  ", conflicts));
        }

        // Any configuration reload may flip an enabled flag; let the shell re-render.
        _reloadSubscription = ChangeToken.OnChange(configuration.GetReloadToken, () => Changed?.Invoke());
    }

    /// <summary>Raised when configuration reloads and a contribution may have changed availability.</summary>
    public event Action? Changed;

    /// <summary>Every available navigation entry, ordered. Live: re-read after a config change.</summary>
    public IReadOnlyList<NavigationEntry> Navigation =>
        _navigation.Where(IsAvailable).OrderBy(e => e.Order).ToList();

    /// <summary>Every head asset (styles then scripts), ordered. Head assets are always loaded.</summary>
    public IReadOnlyList<HeadAsset> HeadAssets => _headAssets;

    /// <summary>Every available discovery section, ordered. Live: re-read after a config change.</summary>
    public IReadOnlyList<DiscoverSource> DiscoverSources =>
        _discoverSources.Where(s => IsAvailable(s.EnabledConfigPath)).OrderBy(s => s.Order).ToList();

    /// <summary>The available discovery section with <paramref name="id"/>, or null.</summary>
    public DiscoverSource? FindDiscoverSource(string? id) =>
        id is null
            ? null
            : DiscoverSources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    /// <summary>Navigation entries in <paramref name="section"/>, in order.</summary>
    public IReadOnlyList<NavigationEntry> NavigationIn(string section) =>
        Navigation.Where(e => string.Equals(e.Section, section, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Order)
            .ToList();

    /// <summary>
    /// The available slots for a region, ordered for rendering. If any <see cref="UiSlotMode.Replace"/>
    /// slot targets the region, only those are returned (the host content is replaced); otherwise
    /// prepends render before appends.
    /// </summary>
    public IReadOnlyList<UiSlot> SlotsFor(string region)
    {
        List<UiSlot> matching = _slots
            .Where(s => IsAvailable(s) && RegionMatches(s.Region, region))
            .ToList();

        List<UiSlot> replaces = matching
            .Where(s => s.Mode == UiSlotMode.Replace)
            .OrderBy(s => s.Order)
            .ToList();
        if (replaces.Count > 0)
        {
            return replaces;
        }

        return matching
            .OrderBy(s => s.Mode == UiSlotMode.Prepend ? 0 : 1)
            .ThenBy(s => s.Order)
            .ToList();
    }

    /// <summary>Whether a config-gated contribution is currently enabled (no path = always enabled).</summary>
    public bool IsAvailable(string? enabledConfigPath) =>
        enabledConfigPath is null || _configuration.IsServiceEnabled(enabledConfigPath);

    bool IsAvailable(NavigationEntry entry) => IsAvailable(entry.EnabledConfigPath);

    bool IsAvailable(UiSlot slot) => IsAvailable(slot.EnabledConfigPath);

    static bool RegionMatches(string pattern, string region) =>
        pattern.Contains('*')
            ? Regex.IsMatch(region, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase)
            : string.Equals(pattern, region, StringComparison.OrdinalIgnoreCase);

    /// <summary>Slots declared by <see cref="UiRegionAttribute"/>-decorated components.</summary>
    internal static IReadOnlyList<UiSlot> DiscoverRegionComponents(IEnumerable<Type> types)
    {
        List<UiSlot> slots = [];
        foreach (Type type in types)
        {
            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition
                || !typeof(IComponent).IsAssignableFrom(type))
            {
                continue;
            }

            foreach (UiRegionAttribute attribute in type.GetCustomAttributes<UiRegionAttribute>(inherit: false))
            {
                slots.Add(new UiSlot(
                    attribute.Region,
                    type,
                    Order: attribute.Order,
                    AdminOnly: attribute.AdminOnly,
                    EnabledConfigPath: attribute.EnabledConfigPath,
                    Mode: attribute.Mode));
            }
        }

        return slots;
    }

    /// <summary>Route templates claimed by more than one component, as human-readable descriptions.</summary>
    internal static IReadOnlyList<string> FindRouteConflicts(IEnumerable<Type> types)
    {
        Dictionary<string, Type> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> conflicts = [];
        foreach (Type type in types)
        {
            foreach (RouteAttribute route in type.GetCustomAttributes<RouteAttribute>(inherit: false))
            {
                if (seen.TryGetValue(route.Template, out Type? existing) && existing != type)
                {
                    conflicts.Add($"'{route.Template}' in {existing.FullName} and {type.FullName}");
                }
                else
                {
                    seen[route.Template] = type;
                }
            }
        }

        return conflicts;
    }

    static IEnumerable<Assembly> EnumerateUiAssemblies(IUiAssemblyProvider assemblies)
    {
        yield return assemblies.HostAssembly;
        foreach (Assembly assembly in assemblies.AdditionalAssemblies.Distinct())
        {
            yield return assembly;
        }
    }

    static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    public void Dispose() => _reloadSubscription.Dispose();
}
