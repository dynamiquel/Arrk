using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Arrk.Core.Configuration;

/// <summary>
/// Overlays an <see cref="IConfiguration"/> section onto an existing options object with
/// <b>replace</b> semantics for lists.
/// </summary>
/// <remarks>
/// <see cref="ConfigurationBinder"/> adds configured items to a collection that already has a
/// value instead of replacing it. Several config POCOs ship non-empty list defaults
/// (<c>qualityProfiles.profiles</c>, each profile's <c>allowedQualities</c>,
/// <c>services.jellyfin.libraries</c>, <c>services.music.profiles</c>), so binding a section into a
/// default-initialised object would produce the defaults plus the configured items.
///
/// This overlay keeps the default when a collection is absent from the config and rebuilds it from
/// the config when the section is present. Objects recurse into the existing instance so their
/// defaults survive. Dictionaries start from the defaults and let configured keys win.
/// </remarks>
public static class ConfigOverlay
{
    public static void Apply(IConfiguration configuration, object target)
    {
        if (target is null || !configuration.GetChildren().Any())
            return;

        foreach (PropertyInfo property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0)
                continue;

            IConfigurationSection child = configuration.GetSection(property.Name);
            if (!child.Exists())
                continue;

            Type propertyType = property.PropertyType;

            if (IsSimple(propertyType))
            {
                object? value = child.Value is null ? null : child.Get(propertyType);
                if (value is not null)
                    property.SetValue(target, value);
            }
            else if (IsDictionary(propertyType))
            {
                property.SetValue(target, BuildDictionary(child, property, target));
            }
            else if (IsList(propertyType))
            {
                property.SetValue(target, BuildList(child, propertyType));
            }
            else
            {
                object? current = property.GetValue(target);
                if (current is null)
                {
                    current = Activator.CreateInstance(propertyType)!;
                    property.SetValue(target, current);
                }

                Apply(child, current);
            }
        }
    }

    static object BuildList(IConfigurationSection section, Type listType)
    {
        Type elementType = listType.IsGenericType ? listType.GetGenericArguments()[0] : typeof(object);
        IList list = (IList)Activator.CreateInstance(listType)!;

        IEnumerable<IConfigurationSection> items = section.GetChildren()
            .OrderBy(item => int.TryParse(item.Key, out int index) ? index : int.MaxValue);

        foreach (IConfigurationSection item in items)
        {
            if (IsSimple(elementType))
            {
                object? value = item.Value is null ? null : item.Get(elementType);
                if (value is not null)
                    list.Add(value);
            }
            else
            {
                object element = Activator.CreateInstance(elementType)!;
                Apply(item, element);
                list.Add(element);
            }
        }

        return list;
    }

    static object BuildDictionary(IConfigurationSection section, PropertyInfo property, object target)
    {
        Type dictionaryType = property.PropertyType;
        Type valueType = dictionaryType.IsGenericType ? dictionaryType.GetGenericArguments()[1] : typeof(object);

        IDictionary result = (IDictionary)Activator.CreateInstance(dictionaryType)!;

        // Keep the POCO defaults for keys the config does not mention, then let config win.
        if (property.GetValue(target) is IDictionary defaults)
        {
            foreach (DictionaryEntry entry in defaults)
                result[entry.Key] = entry.Value;
        }

        foreach (IConfigurationSection entry in section.GetChildren())
        {
            if (IsSimple(valueType))
            {
                object? value = entry.Value is null ? null : entry.Get(valueType);
                if (value is not null)
                    result[entry.Key] = value;
            }
            else if (valueType == typeof(object))
            {
                // Untyped settings hold whatever the config provides, so keep the raw value or the
                // nested children instead of an empty object.
                result[entry.Key] = Materialize(entry);
            }
            else
            {
                object value = Activator.CreateInstance(valueType)!;
                Apply(entry, value);
                result[entry.Key] = value;
            }
        }

        return result;
    }

    static object? Materialize(IConfigurationSection section)
    {
        List<IConfigurationSection> children = [.. section.GetChildren()];
        if (children.Count == 0)
            return section.Value;

        Dictionary<string, object?> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationSection child in children)
            map[child.Key] = Materialize(child);
        return map;
    }

    static bool IsList(Type type) =>
        type != typeof(string) && typeof(IList).IsAssignableFrom(type);

    static bool IsDictionary(Type type) =>
        type != typeof(string) && typeof(IDictionary).IsAssignableFrom(type);

    static bool IsSimple(Type type)
    {
        Type candidate = Nullable.GetUnderlyingType(type) ?? type;
        return candidate.IsPrimitive
            || candidate.IsEnum
            || candidate == typeof(string)
            || candidate == typeof(decimal)
            || candidate == typeof(DateTime)
            || candidate == typeof(DateTimeOffset)
            || candidate == typeof(TimeSpan)
            || candidate == typeof(Guid)
            || candidate == typeof(Uri);
    }
}
