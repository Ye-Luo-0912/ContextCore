using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Stable memory 生命周期人工 review 服务，不做内容编辑、自动合并或检索链路变更。</summary>
public sealed class StableLifecycleReviewService
{
    private readonly IMemoryStore? _memoryStore;
    private readonly IConstraintStore? _constraintStore;
    private readonly IGlobalContextStore? _globalContextStore;
    private readonly IStableLifecycleReviewStore? _reviewStore;
    private readonly IRelationStore? _relationStore;
    private readonly StableMemoryGovernanceService _governanceService;

    public StableLifecycleReviewService(
        IMemoryStore? memoryStore,
        IConstraintStore? constraintStore,
        IGlobalContextStore? globalContextStore,
        IStableLifecycleReviewStore? reviewStore,
        IRelationStore? relationStore,
        StableMemoryGovernanceService governanceService)
    {
        _memoryStore = memoryStore;
        _constraintStore = constraintStore;
        _globalContextStore = globalContextStore;
        _reviewStore = reviewStore;
        _relationStore = relationStore;
        _governanceService = governanceService;
    }

    public Task<IReadOnlyList<StableLifecycleReviewRecord>> GetReviewsAsync(
        string stableItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableItemId);
        EnsureReviewStore();
        return _reviewStore!.QueryReviewsAsync(stableItemId, cancellationToken);
    }

    public Task<StableLifecycleReviewResult?> DeprecateAsync(
        string stableItemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(stableItemId, StableLifecycleReviewActions.Deprecate, request, cancellationToken);
    }

    public Task<StableLifecycleReviewResult?> SupersedeAsync(
        string stableItemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(stableItemId, StableLifecycleReviewActions.Supersede, request, cancellationToken);
    }

    public Task<StableLifecycleReviewResult?> RejectAsync(
        string stableItemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(stableItemId, StableLifecycleReviewActions.Reject, request, cancellationToken);
    }

    private async Task<StableLifecycleReviewResult?> ReviewAsync(
        string stableItemId,
        string action,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableItemId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        EnsureReviewStore();

        var source = await FindStableAsync(stableItemId, request.WorkspaceId, request.CollectionId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        ValidateTransition(source, action, request);
        StableSource? replacement = null;
        if (string.Equals(action, StableLifecycleReviewActions.Supersede, StringComparison.OrdinalIgnoreCase))
        {
            EnsureRelationStore();
            replacement = await ResolveReplacementAsync(source, request, cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId.Trim();
        var reviewer = string.IsNullOrWhiteSpace(request.Reviewer) ? "manual" : request.Reviewer.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? action : request.Reason.Trim();
        var reviewId = $"slr-{BuildShortHash($"{source.Id}\u001f{action}\u001f{now:O}")}";
        var fromLifecycle = source.Lifecycle;
        var toStatus = ResolveTargetStatus(source, action);
        var toLifecycle = ResolveTargetLifecycle(action);
        var warnings = BuildWarnings(source).ToList();

        var updated = await SaveUpdatedAsync(
            source,
            action,
            toStatus,
            toLifecycle,
            reviewer,
            reason,
            reviewId,
            operationId,
            replacement?.Id,
            request.Metadata,
            now,
            cancellationToken).ConfigureAwait(false);

        if (replacement is not null)
        {
            await SaveReplacementAsync(
                replacement,
                source.Id,
                reviewer,
                reason,
                reviewId,
                operationId,
                request.Metadata,
                now,
                cancellationToken).ConfigureAwait(false);
            await SaveReplacementRelationsAsync(
                source,
                replacement,
                reviewId,
                operationId,
                reviewer,
                reason,
                request.Metadata,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        var review = new StableLifecycleReviewRecord
        {
            ReviewId = reviewId,
            StableItemId = source.Id,
            StableKind = source.StableKind,
            WorkspaceId = source.WorkspaceId,
            CollectionId = source.CollectionId,
            Action = action,
            FromStatus = source.Status,
            ToStatus = toStatus,
            FromLifecycle = fromLifecycle,
            ToLifecycle = toLifecycle,
            Reviewer = reviewer,
            Reason = reason,
            ReplacementItemId = replacement?.Id,
            EvidenceRefs = source.EvidenceRefs,
            SourceRefs = source.SourceRefs,
            CreatedAt = now,
            ReviewedAt = now,
            Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["operationId"] = operationId,
                ["reviewedAt"] = now.ToString("O")
            },
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };
        await _reviewStore!.AppendReviewAsync(review, cancellationToken).ConfigureAwait(false);

        return new StableLifecycleReviewResult
        {
            OperationId = operationId,
            StableItemId = source.Id,
            StableKind = source.StableKind,
            Action = action,
            FromStatus = source.Status,
            ToStatus = toStatus,
            FromLifecycle = fromLifecycle,
            ToLifecycle = toLifecycle,
            ReviewId = reviewId,
            Reviewer = reviewer,
            Reason = reason,
            ReviewedAt = now,
            ReplacementItemId = replacement?.Id,
            StableItem = updated,
            Review = review,
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };
    }

    private async Task<StableSource?> FindStableAsync(
        string stableItemId,
        string workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        if (_memoryStore is not null)
        {
            var memory = await _memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Layer = ContextMemoryLayer.Stable,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            var match = memory.FirstOrDefault(item => string.Equals(item.Id, stableItemId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return StableSource.FromMemory(match);
            }
        }

        if (_constraintStore is not null)
        {
            var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            var match = constraints
                .Where(static item => item.Status != ContextMemoryStatus.Candidate)
                .FirstOrDefault(item => string.Equals(item.Id, stableItemId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return StableSource.FromConstraint(match);
            }
        }

        if (_globalContextStore is not null)
        {
            var global = await _globalContextStore.QueryAsync(new ContextGlobalQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            var match = global.FirstOrDefault(item => string.Equals(item.Id, stableItemId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return StableSource.FromGlobal(match);
            }
        }

        return null;
    }

    private async Task<StableSource> ResolveReplacementAsync(
        StableSource source,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ReplacementItemId))
        {
            throw new ArgumentException("Supersede 需要 replacementItemId。", nameof(request));
        }

        if (string.Equals(request.ReplacementItemId, source.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("replacementItemId 不能是当前 stable item。", nameof(request));
        }

        var replacement = await FindStableAsync(
            request.ReplacementItemId,
            request.WorkspaceId,
            request.CollectionId,
            cancellationToken).ConfigureAwait(false);
        if (replacement is null)
        {
            throw new ArgumentException($"replacement item 不存在：{request.ReplacementItemId}", nameof(request));
        }

        if (replacement.Status is ContextMemoryStatus.Rejected or ContextMemoryStatus.Deprecated
            || string.Equals(replacement.Lifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(replacement.Lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)
            || string.Equals(replacement.Lifecycle, StableMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("replacement item 不能是 rejected / deprecated / superseded。", nameof(request));
        }

        return replacement;
    }

    private async Task<StableMemoryRecord> SaveUpdatedAsync(
        StableSource source,
        string action,
        ContextMemoryStatus toStatus,
        string toLifecycle,
        string reviewer,
        string reason,
        string reviewId,
        string operationId,
        string? replacementItemId,
        IReadOnlyDictionary<string, string> requestMetadata,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var metadata = CreateReviewMetadata(
            source.Metadata,
            action,
            toLifecycle,
            reviewer,
            reason,
            reviewId,
            operationId,
            requestMetadata,
            now);
        if (string.Equals(action, StableLifecycleReviewActions.Supersede, StringComparison.OrdinalIgnoreCase))
        {
            metadata["supersededBy"] = replacementItemId ?? string.Empty;
        }

        if (source.MemoryItem is not null)
        {
            var item = source.MemoryItem;
            var updated = new ContextMemoryItem
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Layer = item.Layer,
                Status = toStatus,
                Type = item.Type,
                Content = item.Content,
                ContentFormat = item.ContentFormat,
                Tags = item.Tags.ToArray(),
                SourceRefs = item.SourceRefs.ToArray(),
                RelationRefs = item.RelationRefs.ToArray(),
                Importance = item.Importance,
                Confidence = item.Confidence,
                Version = item.Version + 1,
                Metadata = metadata,
                CreatedAt = item.CreatedAt,
                UpdatedAt = now
            };
            await _memoryStore!.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        else if (source.Constraint is not null)
        {
            var item = source.Constraint;
            var updated = new ContextConstraint
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Scope = item.Scope,
                Level = item.Level,
                Content = item.Content,
                AppliesToRefs = item.AppliesToRefs.ToArray(),
                SourceRefs = item.SourceRefs.ToArray(),
                Status = toStatus,
                Confidence = item.Confidence,
                Metadata = metadata,
                CreatedAt = item.CreatedAt,
                UpdatedAt = now
            };
            await _constraintStore!.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var item = source.GlobalItem!;
            metadata["status"] = toStatus.ToString();
            var updated = new ContextGlobalItem
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Scope = item.Scope,
                Type = item.Type,
                Content = item.Content,
                ContentFormat = item.ContentFormat,
                Tags = item.Tags.ToArray(),
                SourceRefs = item.SourceRefs.ToArray(),
                Importance = item.Importance,
                Version = item.Version + 1,
                Metadata = metadata,
                CreatedAt = item.CreatedAt,
                UpdatedAt = now
            };
            await _globalContextStore!.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        }

        var explanation = await _governanceService.ExplainAsync(source.Id, source.WorkspaceId, source.CollectionId, cancellationToken)
            .ConfigureAwait(false);
        return explanation?.StableItem ?? source.ToRecord(toStatus, toLifecycle, metadata, now);
    }

    private async Task SaveReplacementAsync(
        StableSource replacement,
        string oldItemId,
        string reviewer,
        string reason,
        string reviewId,
        string operationId,
        IReadOnlyDictionary<string, string> requestMetadata,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var metadata = CreateReviewMetadata(
            replacement.Metadata,
            "Replaces",
            replacement.Lifecycle,
            reviewer,
            reason,
            reviewId,
            operationId,
            requestMetadata,
            now);
        metadata["replaces"] = AppendMetadataList(metadata, "replaces", oldItemId);

        if (replacement.MemoryItem is not null)
        {
            var item = replacement.MemoryItem;
            await _memoryStore!.SaveAsync(new ContextMemoryItem
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Layer = item.Layer,
                Status = item.Status,
                Type = item.Type,
                Content = item.Content,
                ContentFormat = item.ContentFormat,
                Tags = item.Tags.ToArray(),
                SourceRefs = item.SourceRefs.ToArray(),
                RelationRefs = item.RelationRefs.ToArray(),
                Importance = item.Importance,
                Confidence = item.Confidence,
                Version = item.Version + 1,
                Metadata = metadata,
                CreatedAt = item.CreatedAt,
                UpdatedAt = now
            }, cancellationToken).ConfigureAwait(false);
        }
        else if (replacement.Constraint is not null)
        {
            var item = replacement.Constraint;
            await _constraintStore!.SaveAsync(new ContextConstraint
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Scope = item.Scope,
                Level = item.Level,
                Content = item.Content,
                AppliesToRefs = item.AppliesToRefs.ToArray(),
                SourceRefs = item.SourceRefs.ToArray(),
                Status = item.Status,
                Confidence = item.Confidence,
                Metadata = metadata,
                CreatedAt = item.CreatedAt,
                UpdatedAt = now
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var item = replacement.GlobalItem!;
            await _globalContextStore!.SaveAsync(new ContextGlobalItem
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Scope = item.Scope,
                Type = item.Type,
                Content = item.Content,
                ContentFormat = item.ContentFormat,
                Tags = item.Tags.ToArray(),
                SourceRefs = item.SourceRefs.ToArray(),
                Importance = item.Importance,
                Version = item.Version + 1,
                Metadata = metadata,
                CreatedAt = item.CreatedAt,
                UpdatedAt = now
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SaveReplacementRelationsAsync(
        StableSource source,
        StableSource replacement,
        string reviewId,
        string operationId,
        string reviewer,
        string reason,
        IReadOnlyDictionary<string, string> requestMetadata,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.CollectionId)
            || string.IsNullOrWhiteSpace(replacement.CollectionId))
        {
            throw new ArgumentException("relation-aware supersede 需要 source 和 replacement 都具备 CollectionId。", nameof(source));
        }

        var evidenceRefs = source.EvidenceRefs
            .Concat(replacement.EvidenceRefs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sourceRefs = source.SourceRefs
            .Concat(replacement.SourceRefs)
            .Append(reviewId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var policyVersion = requestMetadata.TryGetValue("policyVersion", out var configuredPolicyVersion)
            && !string.IsNullOrWhiteSpace(configuredPolicyVersion)
            ? configuredPolicyVersion
            : "stable-lifecycle-review-v1";
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "stable_lifecycle_review",
            ["reviewId"] = reviewId,
            ["reviewer"] = reviewer,
            ["reason"] = reason,
            ["createdAt"] = now.ToString("O"),
            ["sourceOperationId"] = operationId,
            ["sourceItemId"] = source.Id,
            ["createdBy"] = reviewer,
            ["createdFrom"] = "stable_lifecycle_review",
            ["confidence"] = "1.0",
            ["confidenceReason"] = "stable_lifecycle_review",
            ["lifecycle"] = StableMemoryLifecycle.Active,
            ["reviewStatus"] = "Reviewed",
            ["policyVersion"] = policyVersion,
            ["sourceRefs"] = string.Join(',', sourceRefs),
            ["evidenceRefs"] = string.Join(',', evidenceRefs)
        };
        var supersededBy = new ContextRelation
        {
            Id = $"rel-{BuildShortHash($"{reviewId}\u001fsuperseded_by")}",
            WorkspaceId = source.WorkspaceId,
            CollectionId = source.CollectionId,
            SourceId = source.Id,
            TargetId = replacement.Id,
            RelationType = ContextRelationTypes.SupersededBy,
            Weight = 1.0,
            Confidence = 1.0,
            SourceRefs = sourceRefs,
            Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
            CreatedAt = now
        };
        var replaces = new ContextRelation
        {
            Id = $"rel-{BuildShortHash($"{reviewId}\u001freplaces")}",
            WorkspaceId = replacement.WorkspaceId,
            CollectionId = replacement.CollectionId,
            SourceId = replacement.Id,
            TargetId = source.Id,
            RelationType = ContextRelationTypes.Replaces,
            Weight = 1.0,
            Confidence = 1.0,
            SourceRefs = sourceRefs,
            Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
            CreatedAt = now
        };

        return _relationStore!.SaveManyAsync([supersededBy, replaces], cancellationToken);
    }

    private static Dictionary<string, string> CreateReviewMetadata(
        IReadOnlyDictionary<string, string> source,
        string action,
        string lifecycle,
        string reviewer,
        string reason,
        string reviewId,
        string operationId,
        IReadOnlyDictionary<string, string> requestMetadata,
        DateTimeOffset now)
    {
        var metadata = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase)
        {
            ["lifecycle"] = lifecycle,
            ["stableLifecycleReviewAction"] = action,
            ["lastStableLifecycleReviewId"] = reviewId,
            ["reviewer"] = reviewer,
            ["reviewReason"] = reason,
            ["reviewedAt"] = now.ToString("O"),
            ["operationId"] = operationId
        };
        foreach (var pair in requestMetadata)
        {
            metadata[$"reviewMetadata.{pair.Key}"] = pair.Value;
        }

        return metadata;
    }

    private static void ValidateTransition(
        StableSource source,
        string action,
        StableLifecycleReviewRequest request)
    {
        if (source.Status == ContextMemoryStatus.Candidate)
        {
            throw new ArgumentException("Candidate item 不允许通过 stable lifecycle endpoint 修改。", nameof(source));
        }

        if (string.Equals(action, StableLifecycleReviewActions.Reject, StringComparison.OrdinalIgnoreCase)
            && source.Status == ContextMemoryStatus.Rejected)
        {
            throw new ArgumentException("Stable item 已经是 Rejected。", nameof(source));
        }

        if (string.Equals(action, StableLifecycleReviewActions.Deprecate, StringComparison.OrdinalIgnoreCase))
        {
            if (source.Status == ContextMemoryStatus.Deprecated)
            {
                throw new ArgumentException("Stable item 已经是 Deprecated。", nameof(source));
            }

            if (string.Equals(source.Lifecycle, StableMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase)
                && !request.AllowDeprecatedSupersededDeprecation)
            {
                throw new ArgumentException("Stable item 已经是 Superseded，默认不允许再 Deprecate。", nameof(source));
            }
        }
    }

    private static ContextMemoryStatus ResolveTargetStatus(StableSource source, string action)
    {
        return action switch
        {
            StableLifecycleReviewActions.Deprecate => ContextMemoryStatus.Deprecated,
            StableLifecycleReviewActions.Supersede => ContextMemoryStatus.Deprecated,
            StableLifecycleReviewActions.Reject => ContextMemoryStatus.Rejected,
            _ => source.Status
        };
    }

    private static string ResolveTargetLifecycle(string action)
    {
        return action switch
        {
            StableLifecycleReviewActions.Deprecate => StableMemoryLifecycle.Deprecated,
            StableLifecycleReviewActions.Supersede => StableMemoryLifecycle.Superseded,
            StableLifecycleReviewActions.Reject => StableMemoryLifecycle.Rejected,
            StableLifecycleReviewActions.MarkNeedsMoreEvidence => "NeedsMoreEvidence",
            _ => StableMemoryLifecycle.Current
        };
    }

    private static IEnumerable<string> BuildWarnings(StableSource source)
    {
        if (source.EvidenceRefs.Count == 0)
        {
            yield return "stable item has no evidence refs.";
        }

        if (source.SourceRefs.Count == 0
            && !source.Metadata.ContainsKey("sourceStableReviewCandidateId")
            && !source.Metadata.ContainsKey("sourcePromotionCandidateId")
            && !source.Metadata.ContainsKey("sourceCandidateId")
            && !source.Metadata.ContainsKey("sourceFeedbackId")
            && !source.Metadata.ContainsKey("sourceLearningCaseId")
            && !source.Metadata.ContainsKey("sourceWorkingItemId"))
        {
            yield return "stable item provenance links are missing.";
        }
    }

    private void EnsureReviewStore()
    {
        if (_reviewStore is null)
        {
            throw new InvalidOperationException("当前 provider 未注册 StableLifecycle review 存储。");
        }
    }

    private void EnsureRelationStore()
    {
        if (_relationStore is null)
        {
            throw new InvalidOperationException("当前 provider 未注册 RelationStore，不能执行 relation-aware supersede。");
        }
    }

    private static string AppendMetadataList(
        IDictionary<string, string> metadata,
        string key,
        string value)
    {
        var values = new List<string>();
        if (metadata.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            values.AddRange(existing.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        values.Add(value);
        return string.Join(",", values.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsDecision(string type, IReadOnlyDictionary<string, string> metadata)
    {
        return string.Equals(type, "decision", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ReadMetadata(metadata, "suggestedTargetLayer", "targetLayer"), "DecisionRecord", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ReadMetadata(metadata, "stableTargetKind"), "DecisionRecord", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLifecycle(ContextMemoryStatus status, IReadOnlyDictionary<string, string> metadata)
    {
        var metadataLifecycle = ReadMetadata(metadata, "lifecycle", "processState");
        if (!string.IsNullOrWhiteSpace(metadataLifecycle))
        {
            return metadataLifecycle;
        }

        return status switch
        {
            ContextMemoryStatus.Active => StableMemoryLifecycle.Active,
            ContextMemoryStatus.Deprecated => StableMemoryLifecycle.Deprecated,
            ContextMemoryStatus.Rejected => StableMemoryLifecycle.Rejected,
            _ => StableMemoryLifecycle.Current
        };
    }

    private static IReadOnlyList<string> ResolveEvidenceRefs(
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, string> metadata)
    {
        var refs = new List<string>();
        var value = ReadMetadata(metadata, "evidenceRefs");
        if (!string.IsNullOrWhiteSpace(value))
        {
            refs.AddRange(value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        refs.AddRange(sourceRefs.Where(static reference =>
            !reference.StartsWith("src-", StringComparison.OrdinalIgnoreCase)
            && !reference.StartsWith("stpc-", StringComparison.OrdinalIgnoreCase)
            && !reference.StartsWith("clc-", StringComparison.OrdinalIgnoreCase)));
        return refs
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ReadMetadata(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string ResolveTitle(string content)
    {
        var first = (content ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(first)
            ? "(untitled stable item)"
            : first.Length <= 120 ? first : first[..120];
    }

    private static string ResolveSummary(string content)
    {
        var normalized = (content ?? string.Empty).Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    private static double ParseDouble(string? value)
    {
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0.0;
    }

    private static string BuildShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private sealed class StableSource
    {
        public string Id { get; init; } = string.Empty;

        public string WorkspaceId { get; init; } = string.Empty;

        public string? CollectionId { get; init; }

        public string StableKind { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public string Content { get; init; } = string.Empty;

        public ContextMemoryStatus Status { get; init; }

        public string Lifecycle { get; init; } = StableMemoryLifecycle.Current;

        public double Importance { get; init; }

        public double Confidence { get; init; }

        public ContextScope? Scope { get; init; }

        public ConstraintLevel? ConstraintLevel { get; init; }

        public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

        public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public ContextMemoryItem? MemoryItem { get; init; }

        public ContextConstraint? Constraint { get; init; }

        public ContextGlobalItem? GlobalItem { get; init; }

        public static StableSource FromMemory(ContextMemoryItem item)
        {
            return new StableSource
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                StableKind = IsDecision(item.Type, item.Metadata) ? StableMemoryKinds.DecisionRecord : StableMemoryKinds.StableMemory,
                Type = item.Type,
                Content = item.Content,
                Status = item.Status,
                Lifecycle = ResolveLifecycle(item.Status, item.Metadata),
                Importance = item.Importance,
                Confidence = item.Confidence,
                EvidenceRefs = ResolveEvidenceRefs(item.SourceRefs, item.Metadata),
                SourceRefs = item.SourceRefs,
                Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
                MemoryItem = item
            };
        }

        public static StableSource FromConstraint(ContextConstraint item)
        {
            return new StableSource
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                StableKind = StableMemoryKinds.StableConstraint,
                Type = item.Level.ToString(),
                Content = item.Content,
                Status = item.Status,
                Lifecycle = ResolveLifecycle(item.Status, item.Metadata),
                Importance = item.Level == ContextCore.Abstractions.Models.ConstraintLevel.Hard ? 1.0 : 0.8,
                Confidence = item.Confidence,
                Scope = item.Scope,
                ConstraintLevel = item.Level,
                EvidenceRefs = ResolveEvidenceRefs(item.SourceRefs, item.Metadata),
                SourceRefs = item.SourceRefs,
                Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
                Constraint = item
            };
        }

        public static StableSource FromGlobal(ContextGlobalItem item)
        {
            var status = Enum.TryParse<ContextMemoryStatus>(ReadMetadata(item.Metadata, "status"), ignoreCase: true, out var parsed)
                ? parsed
                : ContextMemoryStatus.Stable;
            return new StableSource
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                StableKind = StableMemoryKinds.GlobalMemory,
                Type = item.Type,
                Content = item.Content,
                Status = status,
                Lifecycle = ResolveLifecycle(status, item.Metadata),
                Importance = item.Importance,
                Confidence = ParseDouble(ReadMetadata(item.Metadata, "confidence")),
                Scope = item.Scope,
                EvidenceRefs = ResolveEvidenceRefs(item.SourceRefs, item.Metadata),
                SourceRefs = item.SourceRefs,
                Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
                GlobalItem = item
            };
        }

        public StableMemoryRecord ToRecord(
            ContextMemoryStatus status,
            string lifecycle,
            IReadOnlyDictionary<string, string> metadata,
            DateTimeOffset now)
        {
            return new StableMemoryRecord
            {
                Id = Id,
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                StableKind = StableKind,
                Type = Type,
                Title = ResolveTitle(Content),
                Summary = ResolveSummary(Content),
                Content = Content,
                Status = status,
                Lifecycle = lifecycle,
                Importance = Importance,
                Confidence = Confidence,
                Scope = Scope,
                ConstraintLevel = ConstraintLevel,
                EvidenceRefs = EvidenceRefs,
                SourceRefs = SourceRefs,
                StableReviewCandidateId = ReadMetadata(metadata, "sourceStableReviewCandidateId", "stableReviewCandidateId"),
                PromotionCandidateId = ReadMetadata(metadata, "sourcePromotionCandidateId", "sourceCandidateId"),
                LearningCaseId = ReadMetadata(metadata, "sourceLearningCaseId"),
                FeedbackId = ReadMetadata(metadata, "sourceFeedbackId", "feedbackId"),
                WorkingItemId = ReadMetadata(metadata, "sourceWorkingItemId"),
                CreatedAt = MemoryItem?.CreatedAt ?? Constraint?.CreatedAt ?? GlobalItem?.CreatedAt ?? now,
                UpdatedAt = now,
                Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
