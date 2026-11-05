using System.Net;
using System.Text;

namespace Stackarr.TestSupport;

/// <summary>
/// A request captured by <see cref="FakeHttpMessageHandler"/>.
/// </summary>
public sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    string Body,
    string? ApiKey = null,
    string? SessionId = null,
    string? AuthParameter = null,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// Deterministic <see cref="HttpMessageHandler"/> for exercising HTTP clients without a real
/// server. Responses are dequeued in order; a <see cref="Fallback"/> can serve repeated or
/// concurrent calls. Captures the <c>X-Api-Key</c> and <c>X-Transmission-Session-Id</c> headers
/// plus the basic-auth parameter so it can back any Stackarr client test.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responders = new();

    public List<CapturedRequest> Requests { get; } = [];

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Fallback { get; set; }

    public FakeHttpMessageHandler Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    public FakeHttpMessageHandler EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK)
        => Enqueue((_, _) => Task.FromResult(JsonResponse(json, status)));

    public FakeHttpMessageHandler EnqueueStatus(
        HttpStatusCode status,
        string? sessionId = null,
        string? json = null)
        => Enqueue((_, _) =>
        {
            HttpResponseMessage response = new(status);
            if (sessionId is not null)
                response.Headers.Add("X-Transmission-Session-Id", sessionId);
            if (json is not null)
                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        });

    public FakeHttpMessageHandler EnqueueDelay(TimeSpan delay, string json)
        => Enqueue(async (_, ct) =>
        {
            await Task.Delay(delay, ct);
            return JsonResponse(json, HttpStatusCode.OK);
        });

    public FakeHttpMessageHandler EnqueueThrow(Exception exception)
        => Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);

        string? apiKey = request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? apiKeys)
            ? apiKeys.FirstOrDefault()
            : null;

        string? sessionId = request.Headers.TryGetValues("X-Transmission-Session-Id", out IEnumerable<string>? sessionIds)
            ? sessionIds.FirstOrDefault()
            : null;

        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri!,
            body,
            apiKey,
            sessionId,
            request.Headers.Authorization?.Parameter,
            request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase)));

        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder =
            _responders.Count > 0
                ? _responders.Dequeue()
                : Fallback ?? throw new InvalidOperationException("No HTTP response configured for request");

        return await responder(request, cancellationToken);
    }
}
