using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>规划和执行 vector lifecycle metadata sidecar 回填；不修改业务源对象。</summary>
public sealed class VectorLifecycleMetadataBackfillPlanner
{
    public const string PolicyVersion = "vector-lifecycle-metadata-backfill-v1";
    private const string MetadataSource = "vector_lifecycle_metadata_backfill";
    private readonly VectorSourceLifecycleMetadataResolver _resolver;

    public VectorLifecycleMetadataBackfillPlanner(VectorSourceLifecycleMetadataResolver? resolver = null)
    {
        _resolver = resolver ?? new VectorSourceLifecycleMetadataResolver();
    }

    public VectorLifecycleMetadataBackfillPlan CreatePlan(
        string planId,
        string workspaceId,
        string collectionId,
        EmbeddingProviderOptions provider,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IReadOnlyList<VectorIndexEntry> entries,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(entries);

        var entryByItem = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ItemId))
            .GroupBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entry => entry.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var candidates = sourceItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                entryByItem.TryGetValue(item.ItemId, out var entry);
                return BuildCandidate(item, entry);
            })
            .ToArray();
        var total = candidates.Length;
        var unknownBefore = candidates.Count(item => item.Action != VectorLifecycleMetadataBackfillActions.AlreadyKnown);
        var auto = candidates.Count(item => item.Action == VectorLifecycleMetadataBackfillActions.AutoResolve);
        var knownBefore = total - unknownBefore;
        var expectedKnown = knownBefore + auto;
        var warnings = BuildWarnings(candidates, entries).ToArray();

        return new VectorLifecycleMetadataBackfillPlan
        {
            PlanId = planId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ProviderId = provider.ProviderId,
            EmbeddingModel = provider.EmbeddingModel,
            Dimension = provider.Dimension,
            TotalVectorSourceItems = total,
            UnknownLifecycleBefore = unknownBefore,
            AutoResolvableCount = auto,
            ManualReviewRequiredCount = candidates.Count(item => item.Action == VectorLifecycleMetadataBackfillActions.ManualReviewRequired),
            CannotResolveCount = candidates.Count(item => item.Action == VectorLifecycleMetadataBackfillActions.CannotResolve),
            ExpectedKnownLifecycleAfter = expectedKnown,
            ExpectedCoverageAfter = total == 0 ? 0 : expectedKnown / (double)total,
            RiskImpact = ResolveRiskImpact(candidates),
            RecallRecoveryEstimate = unknownBefore == 0 ? 0 : auto / (double)unknownBefore,
            DryRun = dryRun,
            Candidates = candidates,
            Warnings = warnings,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<VectorLifecycleMetadataBackfillResult> ApplyAsync(
        VectorLifecycleMetadataBackfillPlan plan,
        IVectorIndexStore store,
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);
        var startedAt = DateTimeOffset.UtcNow;
        if (!confirm)
        {
            return NewResult(plan, applied: false, startedAt, startedAt, 0, 0, 0, ["vector lifecycle metadata backfill apply 需要 --confirm。"], []);
        }

        var updated = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<string>();
        var applicable = plan.Candidates
            .Where(item => item.Action == VectorLifecycleMetadataBackfillActions.AutoResolve && item.CanApply)
            .ToArray();

        foreach (var candidate in applicable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entries = await store.GetByItemIdAsync(
                    plan.WorkspaceId,
                    plan.CollectionId,
                    candidate.ItemId,
                    cancellationToken).ConfigureAwait(false);
                var target = entries
                    .Where(entry => string.Equals(entry.EntryId, candidate.EntryId, StringComparison.OrdinalIgnoreCase)
                                    || string.IsNullOrWhiteSpace(candidate.EntryId))
                    .OrderByDescending(entry => entry.UpdatedAt)
                    .FirstOrDefault();
                if (target is null)
                {
                    failed++;
                    errors.Add($"{candidate.ItemId}: vector entry missing during lifecycle metadata backfill。");
                    continue;
                }

                await store.UpsertAsync(WithBackfilledMetadata(target, candidate), cancellationToken)
                    .ConfigureAwait(false);
                updated++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{candidate.ItemId}: {ex.Message}");
            }
        }

        skipped = plan.Candidates.Count - updated - failed;
        var completedAt = DateTimeOffset.UtcNow;
        return NewResult(plan, applied: failed == 0, startedAt, completedAt, updated, skipped, failed, [], errors);
    }

    public string ToMarkdown(VectorLifecycleMetadataBackfillPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Backfill Plan");
        builder.AppendLine();
        builder.AppendLine($"Generated: {plan.CreatedAt:O}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Workspace: `{plan.WorkspaceId}`");
        builder.AppendLine($"- Collection: `{plan.CollectionId}`");
        builder.AppendLine($"- Provider: `{plan.ProviderId}`");
        builder.AppendLine($"- Model: `{plan.EmbeddingModel}`");
        builder.AppendLine($"- TotalVectorSourceItems: `{plan.TotalVectorSourceItems}`");
        builder.AppendLine($"- UnknownLifecycleBefore: `{plan.UnknownLifecycleBefore}`");
        builder.AppendLine($"- AutoResolvableCount: `{plan.AutoResolvableCount}`");
        builder.AppendLine($"- ManualReviewRequiredCount: `{plan.ManualReviewRequiredCount}`");
        builder.AppendLine($"- CannotResolveCount: `{plan.CannotResolveCount}`");
        builder.AppendLine($"- ExpectedKnownLifecycleAfter: `{plan.ExpectedKnownLifecycleAfter}`");
        builder.AppendLine($"- ExpectedCoverageAfter: `{plan.ExpectedCoverageAfter:P2}`");
        builder.AppendLine($"- RecallRecoveryEstimate: `{plan.RecallRecoveryEstimate:P2}`");
        builder.AppendLine($"- RiskImpact: `{plan.RiskImpact}`");
        builder.AppendLine();
        builder.AppendLine("## Candidates");
        builder.AppendLine();
        builder.AppendLine("| Action | ItemId | Kind | Layer | ProposedLifecycle | Confidence | Reason | EvidenceMetadataKeys |");
        builder.AppendLine("|---|---|---|---|---|---:|---|---|");
        foreach (var candidate in plan.Candidates
                     .Where(item => item.Action != VectorLifecycleMetadataBackfillActions.AlreadyKnown)
                     .Take(100))
        {
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {candidate.Action} | {candidate.ItemId} | {candidate.ItemKind} | {candidate.Layer} | {Display(candidate.ProposedLifecycle)} | {candidate.Confidence:F2} | {Escape(candidate.Reason)} | {Escape(string.Join(", ", candidate.EvidenceMetadataKeys))} |"));
        }

        if (plan.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Warnings");
            foreach (var warning in plan.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public string ToMarkdown(VectorLifecycleMetadataBackfillResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Backfill Result");
        builder.AppendLine();
        builder.AppendLine($"- ResultId: `{result.ResultId}`");
        builder.AppendLine($"- OperationId: `{result.OperationId}`");
        builder.AppendLine($"- Applied: `{result.Applied}`");
        builder.AppendLine($"- UpdatedEntries: `{result.UpdatedEntries}`");
        builder.AppendLine($"- SkippedEntries: `{result.SkippedEntries}`");
        builder.AppendLine($"- ManualReviewRequiredCount: `{result.ManualReviewRequiredCount}`");
        builder.AppendLine($"- CannotResolveCount: `{result.CannotResolveCount}`");
        builder.AppendLine($"- FailedCount: `{result.FailedCount}`");
        if (result.Errors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Errors");
            foreach (var error in result.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        builder.AppendLine();
        builder.Append(ToMarkdown(result.Plan));
        return builder.ToString();
    }

    private VectorLifecycleMetadataBackfillCandidate BuildCandidate(
        VectorReindexSourceItem source,
        VectorIndexEntry? entry)
    {
        var metadata = MergeMetadata(source.Metadata, entry?.Metadata);
        var sourceForResolve = new VectorReindexSourceItem
        {
            ItemId = source.ItemId,
            ItemKind = source.ItemKind,
            Layer = source.Layer,
            Text = source.Text,
            UpdatedAt = source.UpdatedAt,
            Metadata = metadata
        };
        var current = _resolver.Resolve(sourceForResolve);
        if (current.IsKnownLifecycle)
        {
            return NewCandidate(source, entry, current, VectorLifecycleMetadataBackfillActions.AlreadyKnown, current.Lifecycle, current.ReviewStatus, 1.0, "source 已具备 lifecycle metadata。", []);
        }

        var decision = InferLifecycle(source.Layer, source.ItemKind, metadata);
        if (decision is null)
        {
            return NewCandidate(source, entry, current, VectorLifecycleMetadataBackfillActions.ManualReviewRequired, string.Empty, string.Empty, 0, "没有足够运行时 metadata 支撑自动 lifecycle backfill。", []);
        }

        var action = entry is null
            ? VectorLifecycleMetadataBackfillActions.CannotResolve
            : VectorLifecycleMetadataBackfillActions.AutoResolve;
        var reason = entry is null
            ? "存在可推断 lifecycle，但没有对应 vector entry，无法写入 sidecar metadata。"
            : decision.Reason;
        return NewCandidate(source, entry, current, action, decision.Lifecycle, decision.ReviewStatus, decision.Confidence, reason, decision.EvidenceKeys);
    }

    private static VectorLifecycleMetadataBackfillCandidate NewCandidate(
        VectorReindexSourceItem source,
        VectorIndexEntry? entry,
        VectorSourceLifecycleMetadata current,
        string action,
        string proposedLifecycle,
        string proposedReviewStatus,
        double confidence,
        string reason,
        IReadOnlyList<string> evidenceKeys)
    {
        var backfilled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (action == VectorLifecycleMetadataBackfillActions.AutoResolve && !string.IsNullOrWhiteSpace(proposedLifecycle))
        {
            backfilled[VectorSourceLifecycleMetadataResolver.BackfilledLifecycleKey] = proposedLifecycle;
            backfilled[VectorSourceLifecycleMetadataResolver.BackfilledReviewStatusKey] = string.IsNullOrWhiteSpace(proposedReviewStatus)
                ? "Inferred"
                : proposedReviewStatus;
            backfilled[VectorSourceLifecycleMetadataResolver.BackfilledMetadataSourceKey] = MetadataSource;
            backfilled[VectorSourceLifecycleMetadataResolver.BackfilledReasonKey] = reason;
            backfilled[VectorSourceLifecycleMetadataResolver.BackfilledEvidenceKeysKey] = string.Join(",", evidenceKeys);
            backfilled[VectorSourceLifecycleMetadataResolver.BackfilledPolicyVersionKey] = PolicyVersion;
            backfilled["vectorLifecycleBackfill.confidence"] = confidence.ToString("F2", CultureInfo.InvariantCulture);
        }

        return new VectorLifecycleMetadataBackfillCandidate
        {
            ItemId = source.ItemId,
            EntryId = entry?.EntryId ?? string.Empty,
            ItemKind = source.ItemKind,
            Layer = source.Layer,
            SourceType = current.SourceType,
            CurrentLifecycle = current.Lifecycle,
            ProposedLifecycle = proposedLifecycle,
            ProposedReviewStatus = proposedReviewStatus,
            Action = action,
            Reason = reason,
            Confidence = confidence,
            CanApply = action == VectorLifecycleMetadataBackfillActions.AutoResolve && entry is not null,
            EvidenceMetadataKeys = evidenceKeys,
            BackfilledMetadata = backfilled,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static BackfillDecision? InferLifecycle(
        string layer,
        string itemKind,
        IReadOnlyDictionary<string, string> metadata)
    {
        var evidence = new List<string>();

        if (AnyMetadataValue(metadata, out var replacementKey, "supersededBy", "replacedBy", "replacementItemId", "replacementId", "superseded_by", "replaced_by"))
        {
            evidence.Add(replacementKey);
            return new BackfillDecision("Superseded", "Inferred", 0.95, "存在 replacement / supersede runtime metadata，可推断为 Superseded。", evidence);
        }

        var lifecycleMarker = ResolveLifecycleMarkerFromMetadata(metadata, evidence);
        if (lifecycleMarker is not null)
        {
            return lifecycleMarker;
        }

        if (EqualsAny(layer, "deprecated_evidence"))
        {
            return new BackfillDecision("Deprecated", "Inferred", 0.9, "layer 表示 deprecated evidence。", ["layer"]);
        }

        if (EqualsAny(layer, "historical_context"))
        {
            return new BackfillDecision("Historical", "Inferred", 0.9, "layer 表示 historical context。", ["layer"]);
        }

        if (EqualsAny(layer, "Stable", "stable_context", "stable_memory"))
        {
            return new BackfillDecision("Active", "Inferred", 0.85, "layer 表示 stable source，且没有历史/拒绝/替代 metadata。", ["layer"]);
        }

        if (EqualsAny(layer, "Candidate", "candidate_memory", "candidate_context"))
        {
            return new BackfillDecision("Candidate", "Inferred", 0.8, "layer 表示 candidate source，且没有历史/拒绝/替代 metadata。", ["layer"]);
        }

        if (EqualsAny(Get(metadata, "sourceKind"), "context")
            && EqualsAny(layer, "context"))
        {
            return new BackfillDecision("Active", "Inferred", 0.75, "sourceKind/layer 表示 normal context source，且没有历史/拒绝/替代 metadata。", ["sourceKind", "layer"]);
        }

        if (EqualsAny(Get(metadata, "sourceKind"), "memory"))
        {
            return new BackfillDecision("Active", "Inferred", 0.75, "sourceKind 表示 memory source，且没有历史/拒绝/替代 metadata。", ["sourceKind"]);
        }

        if (!string.IsNullOrWhiteSpace(itemKind) && EqualsAny(Get(metadata, "sourceType"), "stable", "active"))
        {
            return new BackfillDecision("Active", "Inferred", 0.75, "sourceType 表示 active/stable source。", ["sourceType"]);
        }

        return null;
    }

    private static BackfillDecision? ResolveLifecycleMarkerFromMetadata(
        IReadOnlyDictionary<string, string> metadata,
        ICollection<string> evidence)
    {
        foreach (var (key, lifecycle) in EnumerateLifecycleMarkerValues(metadata))
        {
            if (TryMapLifecycleMarker(lifecycle, out var normalized))
            {
                evidence.Add(key);
                return new BackfillDecision(
                    normalized,
                    "Inferred",
                    0.9,
                    $"{key} runtime metadata 包含生命周期标记，可推断为 {normalized}。",
                    evidence.ToArray());
            }
        }

        return null;
    }

    private static IEnumerable<(string Key, string Value)> EnumerateLifecycleMarkerValues(
        IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var key in new[] { "sourceType", "sourceKind", "source", "sourceMode", "tags", "sourceTags" })
        {
            var value = Get(metadata, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return (key, value);
            }
        }
    }

    private static bool TryMapLifecycleMarker(string value, out string lifecycle)
    {
        foreach (var token in value.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (EqualsAny(token, "deprecated"))
            {
                lifecycle = "Deprecated";
                return true;
            }

            if (EqualsAny(token, "historical", "legacy"))
            {
                lifecycle = "Historical";
                return true;
            }

            if (EqualsAny(token, "rejected"))
            {
                lifecycle = "Rejected";
                return true;
            }

            if (EqualsAny(token, "superseded", "replaced"))
            {
                lifecycle = "Superseded";
                return true;
            }

            if (EqualsAny(token, "active", "current", "stable"))
            {
                lifecycle = "Active";
                return true;
            }

            if (EqualsAny(token, "candidate"))
            {
                lifecycle = "Candidate";
                return true;
            }
        }

        lifecycle = string.Empty;
        return false;
    }

    private static VectorIndexEntry WithBackfilledMetadata(
        VectorIndexEntry entry,
        VectorLifecycleMetadataBackfillCandidate candidate)
    {
        var metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in candidate.BackfilledMetadata)
        {
            metadata[pair.Key] = pair.Value;
        }

        metadata[VectorSourceLifecycleMetadataResolver.BackfilledAppliedAtKey] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        return new VectorIndexEntry
        {
            EntryId = entry.EntryId,
            ItemId = entry.ItemId,
            ItemKind = entry.ItemKind,
            Layer = entry.Layer,
            WorkspaceId = entry.WorkspaceId,
            CollectionId = entry.CollectionId,
            ContentHash = entry.ContentHash,
            EmbeddingModel = entry.EmbeddingModel,
            EmbeddingProvider = entry.EmbeddingProvider,
            Dimension = entry.Dimension,
            Vector = entry.Vector.ToArray(),
            CreatedAt = entry.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = metadata
        };
    }

    private static Dictionary<string, string> MergeMetadata(
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string>? entry)
    {
        var metadata = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        if (entry is null)
        {
            return metadata;
        }

        foreach (var pair in entry.Where(pair => pair.Key.StartsWith(VectorSourceLifecycleMetadataResolver.BackfillPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            metadata[pair.Key] = pair.Value;
        }

        return metadata;
    }

    private static IEnumerable<string> BuildWarnings(
        IReadOnlyList<VectorLifecycleMetadataBackfillCandidate> candidates,
        IReadOnlyList<VectorIndexEntry> entries)
    {
        if (entries.Count == 0)
        {
            yield return "vector index 没有 provider-scoped entry，backfill apply 无法写入 sidecar metadata。";
        }

        if (candidates.Any(item => item.Action == VectorLifecycleMetadataBackfillActions.ManualReviewRequired))
        {
            yield return "存在缺少运行时 lifecycle 证据的 source；这些项只进入人工复核，不自动 backfill。";
        }

        if (candidates.Any(item => item.Action == VectorLifecycleMetadataBackfillActions.CannotResolve))
        {
            yield return "存在可推断但缺少 vector entry 的 source；需先 reindex。";
        }
    }

    private static string ResolveRiskImpact(IReadOnlyList<VectorLifecycleMetadataBackfillCandidate> candidates)
    {
        var auto = candidates.Count(item => item.Action == VectorLifecycleMetadataBackfillActions.AutoResolve);
        var manual = candidates.Count(item => item.Action == VectorLifecycleMetadataBackfillActions.ManualReviewRequired);
        return auto == 0
            ? "没有可自动回填项；风险策略保持保守。"
            : $"将仅回填 {auto} 个有运行时 metadata 证据的 source；{manual} 个无证据 source 仍需人工复核。";
    }

    private static VectorLifecycleMetadataBackfillResult NewResult(
        VectorLifecycleMetadataBackfillPlan plan,
        bool applied,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        int updated,
        int skipped,
        int failed,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors)
    {
        return new VectorLifecycleMetadataBackfillResult
        {
            ResultId = Guid.NewGuid().ToString("N"),
            OperationId = plan.PlanId,
            WorkspaceId = plan.WorkspaceId,
            CollectionId = plan.CollectionId,
            Applied = applied,
            UpdatedEntries = updated,
            SkippedEntries = skipped,
            ManualReviewRequiredCount = plan.ManualReviewRequiredCount,
            CannotResolveCount = plan.CannotResolveCount,
            FailedCount = failed,
            Plan = plan,
            Warnings = warnings,
            Errors = errors,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };
    }

    private static bool AnyMetadataValue(
        IReadOnlyDictionary<string, string> metadata,
        out string matchedKey,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!string.IsNullOrWhiteSpace(Get(metadata, key)))
            {
                matchedKey = key;
                return true;
            }
        }

        matchedKey = string.Empty;
        return false;
    }

    private static string Get(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
    }

    private static bool EqualsAny(string value, params string[] expected)
    {
        return expected.Any(item => string.Equals(value, item, StringComparison.OrdinalIgnoreCase));
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "/", StringComparison.Ordinal);
    }

    private static string Display(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private sealed record BackfillDecision(
        string Lifecycle,
        string ReviewStatus,
        double Confidence,
        string Reason,
        IReadOnlyList<string> EvidenceKeys);
}
