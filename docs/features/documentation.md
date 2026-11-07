# Module documentation

Documentation is a product capability, not an AI feature. Modules and plugins own their
end-user instructions. `Arrk.Core.Documentation` aggregates them without referencing AI, UI,
a model provider, a network service or a vector database.

## Ownership and contributions

Keep user instructions in the owning module's `Documentation/*.md`. Write short explanations,
task-based headings, prerequisites, numbered steps, expected results and troubleshooting.
Include actual configuration keys when readers need them to set up a feature. Implementation
contracts, file maps and internal invariants stay in `docs/features/`, not the public guides.
Related workflows can mention other modules, but should not copy their detailed instructions.

The repository's `Directory.Build.props` embeds these files. A third-party plugin outside this
repository must include its own project item:

```xml
<ItemGroup>
  <EmbeddedResource Include="Documentation/*.md" />
</ItemGroup>
```

Register the module's own assembly in `ConfigureServices`:

```csharp
services.AddModuleDocumentation("my-plugin", typeof(MyModule).Assembly);
```

This produces ids such as `my-plugin/start`. Module keys and filenames use lowercase
letters/digits separated by hyphens. Manifest resources must use the assembly's default
`<AssemblyName>.Documentation.<stem>.md` naming; set `LogicalName` explicitly if overriding
the project's root namespace. Registrations are idempotent per module/assembly. Modules can
instead implement `IDocumentationProvider` and register it in DI; providers contribute trusted
Markdown with module-qualified ids, not user files or URLs. The sample plugin demonstrates
contribution without any AI dependency.

Use one level-one title, a short introductory paragraph and level-two headings for searchable
sections. Section ids are generated from headings; duplicate document or section ids fail
fast rather than silently hiding another module's content. Headings inside fenced examples
are not indexed. The website builder currently accepts paragraphs, level-two headings,
numbered/bulleted lists and inline code, and rejects unsupported blocks instead of silently
dropping text. Plugin docs rendered only in the app can use standard Markdown.

## Consumers

- The app's `/help` page renders the original Markdown and searches all installed providers.
  It remains available with AI disabled or the AI module absent. Search results link directly
  to the matching section using catalog section ids, with space below the app header.
  Human previews render section text without Markdown syntax, capped at 240 characters
  plus an ellipsis when shortened, without changing the AI search payload or ranking.
  At widths up to 900px, the guide list starts collapsed behind a keyboard-accessible
  Browse all guides button and closes when a guide is selected. Search stays visible;
  wider layouts always show the guide list.
- `Arrk.Ai` supplies only a search/read tool adapter over the Core catalog. It owns the
  instructions for its own AI feature, not the other modules' documentation.
- MCP offers the same catalog as resources. Client apps choose which resources to include
  in their context; search/read tools also support clients that do not surface resources.
- `python3 site/build-guides.py` publishes built-in `src/*/Documentation/*.md` as
  `site/product-guides.html`. Module-prefixed anchors avoid filename collisions.
  `--check` detects stale generated output; Pages regenerates on source changes.

The website describes the built-in product, not a user's installed plugins. A plugin ships its
own source with its release, and that source joins Help and AI retrieval at startup. There is
no need to upload it to GitHub Pages or maintain a separate AI copy. The catalog is a startup
snapshot; changed installed assemblies require a restart, just like plugin loading.

## Retrieval contract

Search is local, deterministic and bounded: queries are capped at 200 characters and 12
distinct terms, results at eight excerpts of 240 characters. It ignores common English
question words, weights headings above prose and rare terms above terms shared by many
sections. Each term counts once per field rather than gaining weight through repetition.
Matching uses word-boundary prefixes (whole words for short acronyms), with limited English
query suffix reduction for `-ed`, `-ing` and plurals. This is not a synonym or translation engine.
AI should translate/extract keywords in the documentation's language (English for built-ins),
retain named plugins, retry weak matches or browse the index, and acknowledge missing information.
Search matches are candidates: read their source before asserting that a feature is supported.

`Read` returns one section at a time, capped at 4,000 characters with continuation offsets.
`GetDocument` and `ReadSection` retain full original Markdown for deliberate human/MCP
reads. An omitted section selects the overview, not the entire topic. Lookups accept only
registered ids; they never fetch caller-supplied paths or network addresses.

This is retrieval-augmented generation when consumed by AI; RAG does not require vectors.
Do not stuff the corpus into every prompt. Add semantic/hybrid retrieval only when measured
misses justify embedding models, versioned indexes and their operational/privacy costs.
Documentation describes supported behavior, not current state or permission to act: live
state and writes must still use authorized service tools.

The reproducible [retrieval evaluation](../../scripts/DocumentationEvaluation/README.md) covers
88 cases, six query languages, overlapping plugin fixtures and unsupported questions, and records
baseline/candidate rankings and limitations. Raw multilingual/paraphrase search still has measured
gaps. Manually translated keyword controls are not evidence of real model adherence. The current
decision is to use the existing assistant's query planning, not add an embedding service; a local
multilingual hybrid experiment is warranted if actual assistant queries or required standalone
multilingual Help fail the documented quality checks.

## Tests

`Arrk.Core.Tests/DocumentationTests` covers provider aggregation, namespacing, ranking,
bounds/continuation, exact source preservation, fenced examples and invalid ids.
`Arrk.App.Tests` exercises real embedded module resources with the AI module absent and the
drop-in sample plugin's contribution. `Arrk.Ai.Tests` covers bounded adapters and authenticated
MCP resource listing, pagination, reads and denial. `python3 site/test-product-guides.py`
checks generated content, safe HTML, links, lists and namespaced anchors.
