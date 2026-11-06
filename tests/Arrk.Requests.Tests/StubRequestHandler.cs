using Arrk.Core.Enums;
using Arrk.Core.Requests;
using Arrk.Media.Abstractions;

namespace Arrk.Requests.Tests;

/// <summary>
/// A test double for the request capability contract (not a client). Lets us exercise the request
/// lifecycle without a real media backend.
/// </summary>
internal sealed class StubRequestHandler(string key = "tv") : IMediaRequestHandler
{
    public string Key { get; } = key;
    public string DisplayName => "Stub";
    public bool IsAvailable { get; set; } = true;

    public List<RequestCapability> SupportedCapabilities { get; } =
    [
        new(new MediaDomain("movie"), RequestKind.Movie),
        new(new MediaDomain("tv"), RequestKind.Series),
        new(new MediaDomain("tv"), RequestKind.Season),
        new(new MediaDomain("tv"), RequestKind.Episode),
    ];

    public IReadOnlyList<RequestCapability> Capabilities => SupportedCapabilities;

    public RequestTargetState CheckResult { get; set; } = RequestTargetState.Missing();
    public FulfilmentResult FulfilResult { get; set; } = FulfilmentResult.Ok("42", "added");
    public MediaEntityRef? Entity { get; set; }
    public List<FulfilmentRequest> Fulfilled { get; } = [];
    public List<RequestQualityOption> QualityOptions { get; } = [new("1", "Best")];

    public Task<IReadOnlyList<RequestQualityOption>> GetQualityOptionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RequestQualityOption>>(QualityOptions);

    public Task<RequestTargetState> CheckAsync(RequestTarget target, CancellationToken ct = default) =>
        Task.FromResult(CheckResult);

    public MediaEntityRef? ResolveEntity(RequestTarget target) => Entity;

    public Task<FulfilmentResult> FulfilAsync(FulfilmentRequest request, CancellationToken ct = default)
    {
        Fulfilled.Add(request);
        return Task.FromResult(FulfilResult);
    }
}
