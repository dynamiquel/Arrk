using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Arrk.Ai.Tests;

/// <summary>
/// A scripted <see cref="IChatClient"/> for the assistant loop. Each response is consumed in order;
/// a tool-call response makes <c>FunctionInvokingChatClient</c> invoke the matching function and ask
/// for the next response, exactly like a real provider.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    readonly Queue<object> _script = new();

    public List<ScriptedRequest> Requests { get; } = [];

    public ScriptedChatClient ThenAnswer(string text, long inputTokens = 10, long outputTokens = 5)
    {
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens }
        };
        _script.Enqueue(response);
        return this;
    }

    public ScriptedChatClient ThenToolCall(string name, IDictionary<string, object?>? arguments = null, string callId = "call-1")
    {
        ChatMessage message = new(ChatRole.Assistant,
        [
            new FunctionCallContent(callId, name,
                arguments is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(arguments))
        ]);
        _script.Enqueue(new ChatResponse(message)
        {
            Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 }
        });
        return this;
    }

    public ScriptedChatClient ThenThrow(Exception exception)
    {
        _script.Enqueue(exception);
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(new ScriptedRequest(messages.ToList(), options));
        object next = Next();
        return next is Exception exception
            ? Task.FromException<ChatResponse>(exception)
            : Task.FromResult((ChatResponse)next);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(new ScriptedRequest(messages.ToList(), options));
        object next = Next();
        if (next is Exception exception)
            throw exception;

        foreach (ChatResponseUpdate update in ((ChatResponse)next).ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }

        await Task.CompletedTask;
    }

    object Next() => _script.Count > 0
        ? _script.Dequeue()
        : new ChatResponse(new ChatMessage(ChatRole.Assistant, ""));

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

public sealed record ScriptedRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);
