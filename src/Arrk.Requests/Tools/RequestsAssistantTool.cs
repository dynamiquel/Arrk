using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Arrk.Ai;
using Arrk.Core.Requests;
using Arrk.Requests.Models;

namespace Arrk.Requests.Tools;

/// <summary>
/// The request system's assistant pack. Any signed-in user can see their own requests (and an admin
/// can list everyone's); only an admin can approve or deny. The service has no internal role check,
/// so the pack enforces it before calling.
/// </summary>
public sealed class RequestsAssistantTool(
    IOptionsMonitor<RequestsConfig> config) : IAssistantTool
{
    public string Key => "requests";
    public string Name => "Requests";
    public string Description => "Read requests and (admin) approve or deny them, one at a time or all pending.";
    public AssistantAccess Access => AssistantAccess.User;

    public IReadOnlyList<string> Keywords =>
    [
        "request", "requests", "pending", "approve", "approved", "deny", "denied", "revoke",
        "moderation", "queue", "waiting"
    ];

    public bool IsAvailable => config.CurrentValue.Enabled;

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string? status = "pending", string? scope = "mine", int limit = 25,
                    CancellationToken ct = default) =>
                    await ListAsync(context, status, scope, limit, ct),
                "requests_list",
                "Lists requests. status=pending|approved|denied|blacklisted|cancelled|failed|all; " +
                "scope=mine (default) or all (administrators only). Returns id, title, scope, status, requester and dates."),

            AIFunctionFactory.Create(
                async (CancellationToken ct = default) => await SummaryAsync(context, ct),
                "requests_summary",
                "Returns the instance-wide request counts (open, approved, denied, blacklisted, total)."),

            AIFunctionFactory.Create(
                async (long id, string? quality = null, CancellationToken ct = default) =>
                    await DecideAsync(context, id, approve: true, qualityOrReason: quality, ct),
                "requests_approve",
                "Approves one pending request by id (from requests_list), optionally with a quality profile key. Administrator only."),

            AIFunctionFactory.Create(
                async (long id, string? reason = null, CancellationToken ct = default) =>
                    await DecideAsync(context, id, approve: false, qualityOrReason: reason, ct),
                "requests_deny",
                "Denies one pending request by id, optionally with a reason. Administrator only."),

            AIFunctionFactory.Create(
                async (int limit = 25, CancellationToken ct = default) =>
                    await ApproveAllAsync(context, limit, ct),
                "requests_approve_all",
                "Approves every pending request (up to the limit) one by one. Administrator only; use only when " +
                "the administrator explicitly asks to approve all."),

            AIFunctionFactory.Create(
                async (long id, CancellationToken ct = default) => await RevokeAsync(context, id, ct),
                "requests_revoke",
                "Cancels the caller's own pending request by id.")
        ];
    }

    static async Task<JsonElement> ListAsync(
        AssistantToolContext context, string? status, string? scope, int limit, CancellationToken ct)
    {
        RequestService? requests = context.Services.GetService<RequestService>();
        if (requests is null || !requests.Enabled)
            return Json(new { error = "Requests are turned off for this Arrk instance." });

        bool all = context.IsAdmin
            && string.Equals(scope?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        MediaRequestQuery query = new()
        {
            UserId = all ? null : context.UserId,
            Status = ParseStatus(status),
            Limit = Math.Clamp(limit <= 0 ? 25 : limit, 1, 100)
        };

        try
        {
            IReadOnlyList<MediaRequest> rows = await requests.QueryAsync(query, ct);
            List<object> items = rows.Select(r => (object)new
            {
                id = r.Id,
                title = r.Title,
                year = r.Year,
                domain = r.Domain.ToString(),
                kind = r.Kind.ToString(),
                scope = r.ScopeLabel,
                status = r.Status.ToString(),
                requester = r.RequestedByUsername,
                mine = r.RequestedByUserId == context.UserId,
                requestedAt = r.RequestedAt,
                decidedAt = r.DecidedAt,
                decisionReason = r.DecisionReason,
                quality = r.QualityName
            }).ToList();

            return Json(new { count = items.Count, scope = all ? "all" : "mine", status = status ?? "pending", items });
        }
        catch (Exception ex)
        {
            return Json(new { error = $"Could not read requests: {ex.Message}" });
        }
    }

    static async Task<JsonElement> SummaryAsync(AssistantToolContext context, CancellationToken ct)
    {
        RequestService? requests = context.Services.GetService<RequestService>();
        if (requests is null || !requests.Enabled)
            return Json(new { error = "Requests are turned off for this Arrk instance." });

        try
        {
            RequestsSummary summary = await requests.GetSummaryAsync(ct);
            return Json(new { open = summary.Open, approved = summary.Accepted, denied = summary.Denied, blacklisted = summary.Blacklisted, total = summary.Total });
        }
        catch (Exception ex)
        {
            return Json(new { error = $"Could not read the request summary: {ex.Message}" });
        }
    }

    static async Task<JsonElement> DecideAsync(
        AssistantToolContext context, long id, bool approve, string? qualityOrReason, CancellationToken ct)
    {
        if (!context.IsAdmin)
            return Json(new { ok = false, message = "Only an administrator can approve or deny requests." });

        RequestService? requests = context.Services.GetService<RequestService>();
        if (requests is null || !requests.Enabled)
            return Json(new { ok = false, message = "Requests are turned off for this Arrk instance." });

        try
        {
            RequestOperationResult result = approve
                ? await requests.ApproveAsync(id, context.UserId, qualityOrReason, ct)
                : await requests.DenyAsync(id, context.UserId, qualityOrReason, ct);

            return Json(new
            {
                ok = result.Success,
                message = result.Message ?? (result.Success ? "Done." : "That request could not be updated."),
                id,
                status = result.Request?.Status.ToString(),
                title = result.Request?.Title
            });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, message = $"Request update failed: {ex.Message}" });
        }
    }

    static async Task<JsonElement> ApproveAllAsync(
        AssistantToolContext context, int limit, CancellationToken ct)
    {
        if (!context.IsAdmin)
            return Json(new { ok = false, message = "Only an administrator can approve requests." });

        RequestService? requests = context.Services.GetService<RequestService>();
        if (requests is null || !requests.Enabled)
            return Json(new { ok = false, message = "Requests are turned off for this Arrk instance." });

        int take = Math.Clamp(limit <= 0 ? 25 : limit, 1, 50);
        try
        {
            IReadOnlyList<MediaRequest> pending = await requests.QueryAsync(
                new MediaRequestQuery { Status = RequestStatus.Pending, Limit = take }, ct);

            int approved = 0;
            List<object> failures = [];
            foreach (MediaRequest request in pending)
            {
                RequestOperationResult result = await requests.ApproveAsync(request.Id, context.UserId, null, ct);
                if (result.Success && result.Request?.Status == RequestStatus.Approved)
                    approved++;
                else
                    failures.Add(new { id = request.Id, title = request.Title, message = result.Message });
            }

            return Json(new
            {
                ok = true,
                pending = pending.Count,
                approved,
                failed = failures.Count,
                failures
            });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, message = $"Bulk approval failed: {ex.Message}" });
        }
    }

    static async Task<JsonElement> RevokeAsync(
        AssistantToolContext context, long id, CancellationToken ct)
    {
        RequestService? requests = context.Services.GetService<RequestService>();
        if (requests is null || !requests.Enabled)
            return Json(new { ok = false, message = "Requests are turned off for this Arrk instance." });

        try
        {
            RequestOperationResult result = await requests.RevokeAsync(id, context.UserId, ct);
            return Json(new
            {
                ok = result.Success,
                message = result.Message ?? (result.Success ? "Request cancelled." : "That request could not be cancelled."),
                id
            });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, message = $"Could not cancel that request: {ex.Message}" });
        }
    }

    static RequestStatus? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "pending" or null or "" => RequestStatus.Pending,
        "approved" or "accepted" => RequestStatus.Approved,
        "denied" => RequestStatus.Denied,
        "blacklisted" => RequestStatus.Blacklisted,
        "cancelled" or "canceled" => RequestStatus.Cancelled,
        "failed" => RequestStatus.Failed,
        "all" => null,
        _ => RequestStatus.Pending
    };

    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
