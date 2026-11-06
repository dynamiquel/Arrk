using System.Reflection;
using Microsoft.AspNetCore.Components;
using Arrk.Ui.Components;

namespace Arrk.Configuration.Tests;

/// <summary>
/// Verifies that the YAML editor wrapper forwards content changes to its parent. Binding
/// <c>@bind-Value="Value"</c> directly on the Monaco editor updates only the wrapper's local
/// parameter, so the parent <c>ConfigEditor</c> would keep stale content when Apply runs.
/// </summary>
public class ConfigYamlEditorTests
{
    [Test]
    public async Task MonacoYamlEditor_Handler_ReRaisesValueChanged()
    {
        MonacoYamlEditor editor = new() { Value = "before" };
        string? received = null;
        editor.ValueChanged = EventCallback.Factory.Create<string>(this, value => received = value);

        MethodInfo handler = typeof(MonacoYamlEditor)
            .GetMethod("OnEditorValueChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // A no-op echo is not re-raised (avoids a feedback loop with the editor).
        await (Task)handler.Invoke(editor, ["before"])!;
        await Assert.That(received).IsNull();

        // A real change updates the parameter and notifies the parent.
        await (Task)handler.Invoke(editor, ["after"])!;
        await Assert.That(editor.Value).IsEqualTo("after");
        await Assert.That(received).IsEqualTo("after");
    }
}
