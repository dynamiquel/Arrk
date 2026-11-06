using Arrk.Core.Configuration;

namespace Arrk.Configuration.Tests;

public class YamlContextResolverTests
{
    [Test]
    public async Task GetContext_EmptyText_ReturnsKeyContext()
    {
        YamlContext context = YamlContextResolver.GetContext("", 0);
        await Assert.That(context.IsKeyContext).IsTrue();
        await Assert.That(context.IndentLevel).IsEqualTo(0);
        await Assert.That(context.ParentKeys).IsEmpty();
    }

    [Test]
    public async Task GetContext_AtTopLevel_ReturnsKeyContext()
    {
        string yaml = "storage:";
        YamlContext context = YamlContextResolver.GetContext(yaml, yaml.Length);
        await Assert.That(context.IsAfterColon).IsTrue();
        await Assert.That(context.CurrentLineText).IsEqualTo("storage:");
    }

    [Test]
    public async Task GetContext_AfterColonWithValue_IsValueContext()
    {
        string yaml = "storage:\n  base: /mnt";
        YamlContext context = YamlContextResolver.GetContext(yaml, yaml.Length);
        await Assert.That(context.IsKeyContext).IsFalse();
        await Assert.That(context.CurrentKey).IsEqualTo("base");
    }

    [Test]
    public async Task GetContext_AfterColonNoValue_IsValueContext()
    {
        string yaml = "storage:\n  base:";
        YamlContext context = YamlContextResolver.GetContext(yaml, yaml.Length);
        await Assert.That(context.IsKeyContext).IsFalse();
        await Assert.That(context.IsAfterColon).IsTrue();
        await Assert.That(context.CurrentKey).IsEqualTo("base");
    }

    [Test]
    public async Task GetContext_NestedKey_HasCorrectParentKeys()
    {
        string yaml = "storage:\n  base: /mnt\n  tv:";
        YamlContext context = YamlContextResolver.GetContext(yaml, yaml.Length);
        await Assert.That(context.ParentKeys).Contains("storage");
        await Assert.That(context.CurrentLineText).IsEqualTo("tv:");
    }

    [Test]
    public async Task GetContext_DeeplyNested_HasAllParentKeys()
    {
        string yaml = "monitoring:\n  speedtest:\n    enabled:";
        YamlContext context = YamlContextResolver.GetContext(yaml, yaml.Length);
        await Assert.That(context.ParentKeys).Contains("monitoring");
        await Assert.That(context.ParentKeys).Contains("speedtest");
        await Assert.That(context.IsKeyContext).IsFalse();
        await Assert.That(context.CurrentKey).IsEqualTo("enabled");
    }

    [Test]
    public async Task GetContext_EmptyLine_IsKeyContext()
    {
        string yaml = "storage:\n  base: /mnt\n";
        YamlContext context = YamlContextResolver.GetContext(yaml, yaml.Length);
        await Assert.That(context.IsKeyContext).IsTrue();
    }

    [Test]
    public async Task GetContext_IndentLevel_MatchesSpaces()
    {
        string yaml = "storage:\n    base:";
        YamlContext context = YamlContextResolver.GetContext(yaml, yaml.Length);
        await Assert.That(context.IndentLevel).IsEqualTo(4);
    }

    [Test]
    public async Task GetContext_ServicesSection_HasCorrectPath()
    {
        string yaml = "services:\n  tv:\n    enabled:";
        YamlContext context = YamlContextResolver.GetContext(yaml, yaml.Length);
        await Assert.That(context.ParentKeys).Contains("services");
        await Assert.That(context.ParentKeys).Contains("tv");
        await Assert.That(context.SectionPath).IsEqualTo("services:tv");
    }

    [Test]
    public async Task GetContext_MultipleKeysAtSameLevel_DoesNotCrossContaminate()
    {
        string yaml = "storage:\n  base: /mnt\n  downloads: /data\nvpn:\n  enabled:";
        YamlContext context = YamlContextResolver.GetContext(yaml, yaml.Length);
        // vpn is the parent of enabled, storage should NOT be in parent keys
        await Assert.That(context.ParentKeys).Contains("vpn");
        await Assert.That(context.ParentKeys).DoesNotContain("storage");
        await Assert.That(context.CurrentKey).IsEqualTo("enabled");
    }

    [Test]
    public async Task GetContext_LineInMiddle_CorrectContext()
    {
        string yaml = "storage:\n  base: /mnt\n  tv:\nvpn:\n  enabled: true";
        // Cursor at end of "  tv:" line (line index 2)
        int offset = "storage:\n  base: /mnt\n  tv:".Length;
        YamlContext context = YamlContextResolver.GetContext(yaml, offset);
        await Assert.That(context.ParentKeys).Contains("storage");
        await Assert.That(context.CurrentLineText).IsEqualTo("tv:");
    }

    [Test]
    public async Task GetContext_ListItem_IsKeyContext()
    {
        string yaml = "indexers:\n  entries:\n    - name:";
        YamlContext context = YamlContextResolver.GetContext(yaml, yaml.Length);
        await Assert.That(context.CurrentLineText).Contains("name:");
    }

    [Test]
    public async Task GetContext_ComplexYaml_AllParentKeysCorrect()
    {
        string yaml = @"storage:
  base: /data
  tv: tv
vpn:
  enabled: false
  type: wireguard
services:
  tv:
    enabled: true
    qualities:
      min: 1080p";
        YamlContext context = YamlContextResolver.GetContext(yaml, yaml.Length);
        await Assert.That(context.ParentKeys).Contains("services");
        await Assert.That(context.ParentKeys).Contains("tv");
        await Assert.That(context.ParentKeys).Contains("qualities");
        await Assert.That(context.CurrentKey).IsEqualTo("min");
        await Assert.That(context.IsKeyContext).IsFalse();
    }
}
