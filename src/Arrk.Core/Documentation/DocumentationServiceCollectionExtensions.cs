using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Arrk.Core.Documentation;

public static class DocumentationServiceCollectionExtensions
{
    /// <summary>Registers only this assembly's Documentation/*.md resources under module-qualified ids.</summary>
    public static IServiceCollection AddModuleDocumentation(this IServiceCollection services,
        string moduleKey, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);
        if (!DocumentationCatalog.IsName(moduleKey))
            throw new ArgumentException("A module key must be a lowercase name using letters, digits and hyphens.", nameof(moduleKey));

        services.TryAddSingleton<DocumentationCatalog>();
        if (!services.Any(d => d.ServiceType == typeof(IDocumentationProvider)
            && d.ImplementationInstance is EmbeddedDocumentationProvider provider
            && provider.ModuleKey == moduleKey && provider.Assembly == assembly))
            services.AddSingleton<IDocumentationProvider>(new EmbeddedDocumentationProvider(moduleKey, assembly));
        return services;
    }

    sealed class EmbeddedDocumentationProvider : IDocumentationProvider
    {
        public string ModuleKey { get; }
        public Assembly Assembly { get; }
        public IReadOnlyList<DocumentationDocument> Documents { get; }

        public EmbeddedDocumentationProvider(string moduleKey, Assembly assembly)
        {
            ModuleKey = moduleKey;
            Assembly = assembly;
            string prefix = assembly.GetName().Name + ".Documentation.";
            List<DocumentationDocument> documents = [];
            foreach (string resource in assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal))
            {
                string stem = resource[prefix.Length..^3];
                if (!DocumentationCatalog.IsName(stem))
                    throw new InvalidOperationException($"Invalid documentation resource stem '{stem}' in '{assembly.GetName().Name}'.");
                using Stream stream = assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException($"Missing documentation resource '{resource}'.");
                using StreamReader reader = new(stream);
                documents.Add(new(moduleKey + "/" + stem, reader.ReadToEnd()));
            }
            Documents = documents.AsReadOnly();
        }
    }
}
