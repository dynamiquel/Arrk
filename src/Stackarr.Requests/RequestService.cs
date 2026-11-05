using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Auth.Abstractions;
using Stackarr.Core.Enums;
using Stackarr.Core.Events;
using Stackarr.Core.Notifications;
using Stackarr.Core.Requests;
using Stackarr.Media.Abstractions;
using Stackarr.Requests.Models;

namespace Stackarr.Requests;

/// <summary>Filters for querying requests.</summary>
public sealed class MediaRequestQuery
{
    public int? UserId { get; init; }
    public RequestStatus? Status { get; init; }
    public MediaDomain? Domain { get; init; }
    public int Limit { get; init; } = 500;
}

/// <summary>Headline counts for the Requests page tiles.</summary>
public sealed record RequestsSummary(int Open, int Blacklisted, int Accepted, int Denied, int Total);

/// <summary>Outcome of a request operation, with a user-facing message.</summary>
public sealed record RequestOperationResult(bool Success, string? Message = null, MediaRequest? Request = null, RequestTargetState? TargetState = null)
{
    public static RequestOperationResult Ok(MediaRequest? request = null, string? message = null) => new(true, message, request);
    public static RequestOperationResult Fail(string message, RequestTargetState? state = null) => new(false, message, null, state);
}

/// <summary>
/// The request lifecycle. Validates and persists requests, routes approval to the appropriate
/// <see cref="IMediaRequestHandler"/> (Tvarr, Lidarr, an alternative, the service never
/// references a concrete backend), resolves auto-approval, and emits targeted notifications.
/// </summary>
public sealed class RequestService(
    IDbContextFactory<StackarrDbContext> dbContextFactory,
    IMediaRequestRegistry registry,
    INotificationService notifications,
    NotificationSubscriptionService subscriptions,
    IEventStore events,
    IUserDirectory users,
    IOptionsMonitor<RequestsConfig> config,
    TimeProvider time,
    ILogger<RequestService> logger)
{
    public bool Enabled => config.CurrentValue.Enabled;

    public IMediaRequestRegistry Registry => registry;

    // Creation

    public async Task<RequestOperationResult> CreateAsync(
        int userId,
        string username,
        RequestTarget target,
        string? qualityKey = null,
        CancellationToken ct = default)
    {
        RequestsConfig cfg = config.CurrentValue;
        if (!cfg.Enabled)
            return RequestOperationResult.Fail("The request system is disabled.");

        if (!IsKindAllowed(cfg, target.Kind))
            return RequestOperationResult.Fail($"Requests of this type are not allowed.");

        IMediaRequestHandler? handler = registry.GetHandler(target.Domain, target.Kind);
        if (handler is null)
            return RequestOperationResult.Fail("No media backend is configured for this request.");
        if (!handler.IsAvailable)
            return RequestOperationResult.Fail($"{handler.DisplayName} is not available right now.");

        // Already blacklisted?
        if (await IsBlacklistedAsync(target, ct))
            return RequestOperationResult.Fail($"\"{target.Title}\" is blacklisted and cannot be requested.");

        // Already present?
        RequestTargetState state = await handler.CheckAsync(target, ct);
        if (state.Exists)
            return RequestOperationResult.Fail($"\"{target.Title}\" is already in the library.", state);

        await using (StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            // Duplicate from the same user for the same scope? Compare provider ids structurally
            // (JSON key order is not stable, so string comparison would be brittle).
            List<MediaRequest> sameScope = await db.Set<MediaRequest>().AsNoTracking()
                .Where(r =>
                    r.RequestedByUserId == userId &&
                    r.Status == RequestStatus.Pending &&
                    r.Domain == target.Domain &&
                    r.Kind == target.Kind &&
                    r.SeasonNumber == target.SeasonNumber &&
                    r.EpisodeNumber == target.EpisodeNumber)
                .ToListAsync(ct);

            bool duplicate = sameScope.Any(r =>
                ProviderIdMap.Overlaps(ProviderIdMap.Deserialize(r.ProviderIds), target.ProviderIds));
            if (duplicate)
                return RequestOperationResult.Fail("You have already requested this.");

            if (cfg.MaxOpenPerUser > 0)
            {
                int open = await db.Set<MediaRequest>().CountAsync(r =>
                    r.RequestedByUserId == userId && r.Status == RequestStatus.Pending, ct);
                if (open >= cfg.MaxOpenPerUser)
                    return RequestOperationResult.Fail($"You already have {open} open requests (limit {cfg.MaxOpenPerUser}).");
            }
        }

        MediaEntityRef? entity = handler.ResolveEntity(target);
        DateTime now = time.GetUtcNow().UtcDateTime;
        string? qualityName = null;
        if (!string.IsNullOrWhiteSpace(qualityKey))
        {
            try
            {
                qualityName = (await handler.GetQualityOptionsAsync(ct))
                    .FirstOrDefault(o => string.Equals(o.Key, qualityKey, StringComparison.OrdinalIgnoreCase))?.Name;
            }
            catch
            {
                /* name is cosmetic; the key is what fulfils */
            }
        }

        MediaRequest request = new()
        {
            RequestedByUserId = userId,
            RequestedByUsername = username,
            RequestedAt = now,
            UpdatedAt = now,
            Domain = target.Domain,
            Kind = target.Kind,
            ProviderIds = ProviderIdMap.Serialize(target.ProviderIds),
            Title = target.Title,
            Year = target.Year,
            PosterUrl = target.PosterUrl,
            SeasonNumber = target.SeasonNumber,
            EpisodeNumber = target.EpisodeNumber,
            HandlerKey = handler.Key,
            QualityKey = qualityKey,
            QualityName = qualityName,
            ProviderKey = entity?.ProviderKey,
            EntityKey = entity?.ItemKey,
            Status = RequestStatus.Pending
        };

        await using (StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            db.Set<MediaRequest>().Add(request);
            await db.SaveChangesAsync(ct);
        }

        if (entity is not null)
            await SubscribeRequestAsync(request, entity, ct);

        bool autoApprove = await ShouldAutoApproveAsync(userId, username, ct);
        if (autoApprove)
        {
            await ApproveInternalAsync(request, userId, qualityKey, auto: true, ct);
            return RequestOperationResult.Ok(request, "Request approved automatically.");
        }

        await NotifyAdminsOfNewRequestAsync(request, ct);
        logger.LogInformation("Request {Id} for {Title} created by {User}", request.Id, request.Title, username);
        return RequestOperationResult.Ok(request);
    }

    static bool IsKindAllowed(RequestsConfig cfg, RequestKind kind) => kind switch
    {
        RequestKind.Series => cfg.AllowSeriesRequests,
        RequestKind.Season => cfg.AllowSeasonRequests,
        RequestKind.Episode => cfg.AllowEpisodeRequests,
        _ => true
    };

    async Task<bool> ShouldAutoApproveAsync(int userId, string username, CancellationToken ct)
    {
        RequestsConfig cfg = config.CurrentValue;

        if (cfg.AutoApproveUsers.Any(u => string.Equals(u, username, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (cfg.AutoApproveRoles.Count > 0)
        {
            StackarrUser? user = await users.GetByIdAsync(userId, ct);
            if (user is not null)
            {
                IReadOnlyList<string> roles = await users.GetRolesAsync(user);
                if (roles.Any(r => cfg.AutoApproveRoles.Contains(r, StringComparer.OrdinalIgnoreCase)))
                    return true;
            }
        }

        return false;
    }

    // Decisions

    public async Task<RequestOperationResult> ApproveAsync(
        long id,
        int adminUserId,
        string? qualityOverride = null,
        CancellationToken ct = default)
    {
        MediaRequest? request = await GetByIdAsync(id, ct);
        if (request is null)
            return RequestOperationResult.Fail("Request not found.");
        if (request.Status != RequestStatus.Pending)
            return RequestOperationResult.Fail($"Request is already {request.Status}.", null);

        return await ApproveInternalAsync(request, adminUserId, qualityOverride, auto: false, ct);
    }

    async Task<RequestOperationResult> ApproveInternalAsync(
        MediaRequest request,
        int actingUserId,
        string? qualityOverride,
        bool auto,
        CancellationToken ct)
    {
        IMediaRequestHandler? handler = registry.GetHandler(request.Domain, request.Kind);
        if (handler is null)
            return RequestOperationResult.Fail("No media backend is configured for this request.");
        if (!handler.IsAvailable)
            return RequestOperationResult.Fail($"{handler.DisplayName} is not available right now.");

        // Older requests predate the stored entity key; resolve it now so imports and
        // notifications can be matched.
        MediaEntityRef? entity = handler.ResolveEntity(request.ToTarget());
        if (entity is not null)
        {
            if (string.IsNullOrWhiteSpace(request.ProviderKey) || string.IsNullOrWhiteSpace(request.EntityKey))
            {
                request.ProviderKey = entity.ProviderKey;
                request.EntityKey = entity.ItemKey;
            }

            if (!await subscriptions.IsSubscribedAsync(
                    request.RequestedByUserId, entity.ProviderKey, entity.ItemKey, ct))
                await SubscribeRequestAsync(request, entity, ct);
        }

        string? qualityKey = qualityOverride;
        if (string.IsNullOrWhiteSpace(qualityKey))
            qualityKey = request.QualityKey;
        if (string.IsNullOrWhiteSpace(qualityKey) && !string.IsNullOrWhiteSpace(config.CurrentValue.DefaultQualityProfile))
            qualityKey = config.CurrentValue.DefaultQualityProfile;

        FulfilmentResult result;
        try
        {
            result = await handler.FulfilAsync(new FulfilmentRequest(request.ToTarget(), qualityKey, SearchNow: true), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fulfilment of request {Id} failed", request.Id);
            result = FulfilmentResult.Fail(ex.Message);
        }

        DateTime now = time.GetUtcNow().UtcDateTime;
        request.UpdatedAt = now;
        request.DecidedByUserId = actingUserId;
        request.DecidedAt = now;

        if (!result.Success)
        {
            request.Status = RequestStatus.Failed;
            request.DecisionReason = result.Message;
        }
        else
        {
            request.Status = RequestStatus.Approved;
            request.LibraryId = result.LibraryId;
            if (!string.IsNullOrWhiteSpace(qualityKey))
                request.QualityKey = qualityKey;
        }

        await UpdateAsync(request, ct);

        if (!result.Success)
        {
            await RecordAsync(RequestsNotificationTopics.Approved, "RequestFailed",
                $"Failed to fulfil request for {request.Title}: {result.Message}", EventSeverity.Error, request, ct);
            return RequestOperationResult.Fail(result.Message ?? "Fulfilment failed.");
        }

        await NotifyRequesterAsync(request, RequestsNotificationTopics.Approved,
            "Request approved", $"\"{request.Title}\" was approved and is being added to the library.", ct);
        await RecordAsync(RequestsNotificationTopics.Approved, "RequestApproved",
            $"Request for {request.Title} approved" + (auto ? " (auto)" : ""), EventSeverity.Success, request, ct);

        logger.LogInformation("Request {Id} for {Title} approved (auto={Auto})", request.Id, request.Title, auto);
        return RequestOperationResult.Ok(request, "Request approved.");
    }

    public async Task<RequestOperationResult> DenyAsync(long id, int adminUserId, string? reason = null, CancellationToken ct = default)
    {
        MediaRequest? request = await GetByIdAsync(id, ct);
        if (request is null)
            return RequestOperationResult.Fail("Request not found.");
        if (request.Status != RequestStatus.Pending)
            return RequestOperationResult.Fail($"Request is already {request.Status}.");

        DateTime now = time.GetUtcNow().UtcDateTime;
        request.Status = RequestStatus.Denied;
        request.DecidedByUserId = adminUserId;
        request.DecidedAt = now;
        request.DecisionReason = reason;
        request.UpdatedAt = now;
        await UpdateAsync(request, ct);
        await RemoveRequestSubscriptionAsync(request, ct);

        if (config.CurrentValue.BlacklistOnDeny)
            await AddBlacklistAsync(request, adminUserId, reason, ct);

        await NotifyRequesterAsync(request, RequestsNotificationTopics.Denied,
            "Request denied", $"\"{request.Title}\" was denied." + (string.IsNullOrWhiteSpace(reason) ? "" : $" Reason: {reason}"), ct);
        await RecordAsync(RequestsNotificationTopics.Denied, "RequestDenied",
            $"Request for {request.Title} denied", EventSeverity.Warning, request, ct);

        return RequestOperationResult.Ok(request, "Request denied.");
    }

    public async Task<RequestOperationResult> BlacklistAsync(long id, int adminUserId, string? reason = null, CancellationToken ct = default)
    {
        MediaRequest? request = await GetByIdAsync(id, ct);
        if (request is null)
            return RequestOperationResult.Fail("Request not found.");

        await AddBlacklistAsync(request, adminUserId, reason, ct);

        DateTime now = time.GetUtcNow().UtcDateTime;
        request.DecisionReason = reason;
        request.UpdatedAt = now;
        if (request.Status == RequestStatus.Pending)
        {
            request.Status = RequestStatus.Blacklisted;
            request.DecidedByUserId = adminUserId;
            request.DecidedAt = now;
            await UpdateAsync(request, ct);
        }
        else
        {
            await UpdateAsync(request, ct);
        }

        await RemoveRequestSubscriptionAsync(request, ct);

        await NotifyRequesterAsync(request, RequestsNotificationTopics.Blacklisted,
            "Request blacklisted", $"\"{request.Title}\" was blacklisted." + (string.IsNullOrWhiteSpace(reason) ? "" : $" Reason: {reason}"), ct);
        await RecordAsync(RequestsNotificationTopics.Blacklisted, "RequestBlacklisted",
            $"{request.Title} blacklisted", EventSeverity.Warning, request, ct);

        return RequestOperationResult.Ok(request, "Title blacklisted.");
    }

    async Task AddBlacklistAsync(MediaRequest request, int adminUserId, string? reason, CancellationToken ct)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        bool exists = await db.Set<RequestBlacklist>().AnyAsync(b =>
            b.Domain == request.Domain && b.ProviderIds == request.ProviderIds, ct);
        if (exists)
            return;

        db.Set<RequestBlacklist>().Add(new RequestBlacklist
        {
            Domain = request.Domain,
            ProviderIds = request.ProviderIds,
            Title = request.Title,
            Reason = reason,
            BlacklistedByUserId = adminUserId,
            BlacklistedAt = time.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Withdraws a pending request. Only the requesting user may revoke their own request.</summary>
    public async Task<RequestOperationResult> RevokeAsync(long id, int userId, CancellationToken ct = default)
    {
        MediaRequest? request = await GetByIdAsync(id, ct);
        if (request is null)
            return RequestOperationResult.Fail("Request not found.");
        if (request.RequestedByUserId != userId)
            return RequestOperationResult.Fail("You can only revoke your own requests.");
        if (request.Status != RequestStatus.Pending)
            return RequestOperationResult.Fail($"Only pending requests can be revoked (this one is {request.Status}).");

        request.Status = RequestStatus.Cancelled;
        request.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await UpdateAsync(request, ct);
        await RemoveRequestSubscriptionAsync(request, ct);

        logger.LogInformation("Request {Id} for {Title} revoked by {User}", request.Id, request.Title, request.RequestedByUsername);
        return RequestOperationResult.Ok(request, "Request revoked.");
    }

    /// <summary>
    /// Cancels any active (non-denied, non-cancelled) request for a title that has been removed
    /// from the library, so it becomes requestable again. Returns the number updated.
    /// </summary>
    public async Task<int> CancelForMediaAsync(
        MediaDomain domain,
        IReadOnlyDictionary<string, string> providerIds,
        CancellationToken ct = default)
    {
        if (providerIds.Count == 0)
            return 0;

        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        List<MediaRequest> candidates = await db.Set<MediaRequest>()
            .Where(r => r.Domain == domain &&
                        r.Status != RequestStatus.Cancelled &&
                        r.Status != RequestStatus.Denied)
            .ToListAsync(ct);

        DateTime now = time.GetUtcNow().UtcDateTime;
        List<MediaRequest> changedRequests = [];
        foreach (MediaRequest request in candidates)
        {
            if (!ProviderIdMap.Overlaps(ProviderIdMap.Deserialize(request.ProviderIds), providerIds))
                continue;

            request.Status = RequestStatus.Cancelled;
            request.UpdatedAt = now;
            changedRequests.Add(request);
        }

        if (changedRequests.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (MediaRequest request in changedRequests)
                await subscriptions.RemoveSourceAsync("request", request.Id.ToString(), ct);
        }

        return changedRequests.Count;
    }

    // Queries
    public async Task<MediaRequest?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<MediaRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<MediaRequest>> QueryAsync(MediaRequestQuery query, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        IQueryable<MediaRequest> q = db.Set<MediaRequest>().AsNoTracking();

        if (query.UserId is { } userId)
            q = q.Where(r => r.RequestedByUserId == userId);
        if (query.Status is { } status)
            q = q.Where(r => r.Status == status);
        if (query.Domain is { } domain)
            q = q.Where(r => r.Domain == domain);

        int limit = query.Limit <= 0 ? 500 : Math.Min(query.Limit, 5000);
        return await q.OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<RequestsSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        int open = await db.Set<MediaRequest>().CountAsync(r => r.Status == RequestStatus.Pending, ct);
        int accepted = await db.Set<MediaRequest>().CountAsync(
            r => r.Status == RequestStatus.Approved || r.Status == RequestStatus.Available, ct);
        int denied = await db.Set<MediaRequest>().CountAsync(r => r.Status == RequestStatus.Denied, ct);
        int blacklisted = await db.Set<MediaRequest>().CountAsync(r => r.Status == RequestStatus.Blacklisted, ct)
                          + await db.Set<RequestBlacklist>().CountAsync(ct);
        int total = await db.Set<MediaRequest>().CountAsync(ct);
        return new RequestsSummary(open, blacklisted, accepted, denied, total);
    }

    public async Task<bool> IsBlacklistedAsync(RequestTarget target, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, string> ids = target.ProviderIds;
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        List<RequestBlacklist> candidates = await db.Set<RequestBlacklist>().AsNoTracking()
            .Where(b => b.Domain == target.Domain).ToListAsync(ct);

        return candidates.Any(b => ProviderIdMap.Overlaps(ProviderIdMap.Deserialize(b.ProviderIds), ids));
    }

    public async Task<IReadOnlyList<RequestBlacklist>> GetBlacklistAsync(CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<RequestBlacklist>().AsNoTracking()
            .OrderByDescending(b => b.BlacklistedAt).ToListAsync(ct);
    }

    /// <summary>
    /// Marks approved requests as available when an import matches their entity. A series request
    /// matches its first imported episode/season; a season request matches any episode in that
    /// season; an episode/movie request matches exactly.
    /// </summary>
    public async Task<int> MarkAvailableAsync(MediaEntityRef entity, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        List<MediaRequest> approved = await db.Set<MediaRequest>()
            .Where(r => r.Status == RequestStatus.Approved && r.EntityKey != null)
            .ToListAsync(ct);

        List<MediaRequest> matched = [];
        DateTime now = time.GetUtcNow().UtcDateTime;
        foreach (MediaRequest request in approved)
        {
            if (request.NotifiedAt is not null || !MatchesEntity(request, entity))
                continue;

            request.Status = RequestStatus.Available;
            request.NotifiedAt = now;
            request.UpdatedAt = now;
            matched.Add(request);
        }

        if (matched.Count == 0)
            return 0;

        await db.SaveChangesAsync(ct);

        foreach (MediaRequest request in matched)
        {
            if (await StatusNotificationsEnabledAsync(request, ct))
            {
                NotificationMessage message = new()
                {
                    Key = RequestsNotificationTopics.Available,
                    Title = "Request available",
                    Body = $"\"{request.Title}\" is now available in your library.",
                    Severity = EventSeverity.Success,
                    Source = request.Id.ToString(),
                    SourceName = request.Title,
                    Url = "/requests",
                    Style = string.IsNullOrWhiteSpace(request.PosterUrl)
                        ? null
                        : new NotificationStyle { ThumbnailUrl = request.PosterUrl }
                };

                try
                {
                    await notifications.PublishToUserAsync(request.RequestedByUserId, message, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to publish available notification for request {Id}", request.Id);
                }
            }

            await RecordAsync(RequestsNotificationTopics.Available, "RequestAvailable",
                $"{request.Title} is now available", EventSeverity.Success, request, ct);
        }

        return matched.Count;
    }

    // Helpers

    async Task<bool> StatusNotificationsEnabledAsync(MediaRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderKey) || string.IsNullOrWhiteSpace(request.EntityKey))
            return true;

        NotificationSubscription? subscription = await subscriptions.GetAsync(
            request.RequestedByUserId, request.ProviderKey, request.EntityKey, ct);
        return subscription is null || subscription.NotifyStatus;
    }

    Task SubscribeRequestAsync(MediaRequest request, MediaEntityRef entity, CancellationToken ct) =>
        subscriptions.SubscribeAsync(
            request.RequestedByUserId,
            entity,
            new NotificationSubscriptionOptions
            {
                IncludeDescendants = request.Kind is RequestKind.Series or RequestKind.Season
            },
            source: "request",
            sourceId: request.Id.ToString(),
            ct: ct);

    Task RemoveRequestSubscriptionAsync(MediaRequest request, CancellationToken ct) =>
        subscriptions.RemoveSourceAsync("request", request.Id.ToString(), ct);

    static bool MatchesEntity(MediaRequest request, MediaEntityRef entity)
    {
        if (string.IsNullOrWhiteSpace(request.EntityKey))
            return false;
        if (!string.Equals(request.ProviderKey, entity.ProviderKey, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(request.EntityKey, entity.ItemKey, StringComparison.OrdinalIgnoreCase))
            return true;

        if (entity.ItemKey.StartsWith(request.EntityKey + ":", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (string ancestor in entity.Ancestors)
        {
            if (string.Equals(request.EntityKey, ancestor, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    async Task UpdateAsync(MediaRequest request, CancellationToken ct)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        db.Set<MediaRequest>().Update(request);
        await db.SaveChangesAsync(ct);
    }

    async Task NotifyAdminsOfNewRequestAsync(MediaRequest request, CancellationToken ct)
    {
        // Broadcast so admins receive it once (and external channels post once). Users may also
        // see it, which is consistent with the Requests page being visible to everyone.
        NotificationMessage message = new()
        {
            Key = RequestsNotificationTopics.New,
            Title = "New media request",
            Body = $"{request.RequestedByUsername} requested \"{request.Title}\" ({request.ScopeLabel}).",
            Severity = EventSeverity.Info,
            Source = request.Id.ToString(),
            SourceName = request.Title,
            Url = "/requests",
            Style = string.IsNullOrWhiteSpace(request.PosterUrl)
                ? null
                : new NotificationStyle { ThumbnailUrl = request.PosterUrl }
        };

        try
        {
            await notifications.PublishAsync(message, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish new-request notification");
        }

        await RecordAsync(RequestsNotificationTopics.New, "RequestCreated",
            $"{request.RequestedByUsername} requested {request.Title}", EventSeverity.Info, request, ct);
    }

    async Task NotifyRequesterAsync(MediaRequest request, string topic, string title, string body, CancellationToken ct)
    {
        if (!await StatusNotificationsEnabledAsync(request, ct))
            return;

        NotificationMessage message = new()
        {
            Key = topic,
            Title = title,
            Body = body,
            Severity = topic == RequestsNotificationTopics.Approved ? EventSeverity.Success : EventSeverity.Warning,
            Source = request.Id.ToString(),
            SourceName = request.Title,
            Url = "/requests"
        };
        await notifications.PublishToUserAsync(request.RequestedByUserId, message, ct);
    }

    async Task RecordAsync(string topic, string eventType, string message, EventSeverity severity, MediaRequest request, CancellationToken ct)
    {
        try
        {
            await events.RecordAsync(new SystemEventRecord
            {
                Category = EventCategory.Media,
                EventType = eventType,
                Message = message,
                Severity = severity,
                Source = request.Id.ToString(),
                SourceName = request.Title,
                Topic = topic
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record request event {EventType}", eventType);
        }
    }
}
