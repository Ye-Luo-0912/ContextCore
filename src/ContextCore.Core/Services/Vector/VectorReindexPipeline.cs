using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>构建 vector reindex 计划；只读扫描 source 和 index，不写入存储。</summary>
public sealed class VectorReindexPlanner
{
    private readonly IContextStore? _contextStore;
    private readonly IMemoryStore? _memoryStore;
    private readonly IVectorIndexStore? _vectorStore;
    private readonly IEmbeddingGenerator? _generator;

    public VectorReindexPlanner(
        IContextStore? contextStore,
        IMemoryStore? memoryStore,
        IVectorIndexStore? vectorStore,
        IEmbeddingGenerator? generator)
    {
        _contextStore = contextStore;
        _memoryStore = memoryStore;
        _vectorStore = vectorStore;
        _generator = generator;
    }

    public async Task<VectorReindexPlan> CreatePlanAsync(
        VectorReindexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        var warnings = new List<string>();
        if (_vectorStore is null)
        {
            warnings.Add("当前 provider 未注册 V1 vector index store。");
        }

        if (_generator is null)
        {
            warnings.Add("当前 provider 未注册 embedding generator。");
        }

        var sources = await LoadSourceItemsAsync(request, cancellationToken).ConfigureAwait(false);
        var entries = _vectorStore is null
            ? Array.Empty<VectorIndexEntry>()
            : await _vectorStore.ListAsync(new VectorIndexQuery
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                Take = 100_000,
                IncludeVector = false
            }, cancellationToken).ConfigureAwait(false);

        var sourceById = sources.ToDictionary(item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        var entriesByItem = entries
            .GroupBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.UpdatedAt).ToArray(), StringComparer.OrdinalIgnoreCase);

        var items = new List<VectorReindexPlanItem>();
        foreach (var source in sources)
        {
            var currentHash = VectorIndexContentHasher.Hash(source.Text);
            if (!entriesByItem.TryGetValue(source.ItemId, out var existingEntries) || existingEntries.Length == 0)
            {
                items.Add(NewPlanItem(source, null, "Create", currentHash, null, true, "source item 尚未建立 embedding。"));
                continue;
            }

            var descriptor = _generator is null ? null : EmbeddingGeneratorDescriptor.From(_generator);
            var matchingEntries = descriptor is null
                ? existingEntries
                : existingEntries
                    .Where(entry => string.Equals(entry.EmbeddingModel, descriptor.Model, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(entry.EmbeddingProvider, descriptor.Provider, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(entry => entry.UpdatedAt)
                    .ToArray();
            var latest = matchingEntries.FirstOrDefault() ?? existingEntries[0];
            var isStale = !string.Equals(latest.ContentHash, currentHash, StringComparison.OrdinalIgnoreCase);
            var compatibilityChanges = descriptor is null
                ? Array.Empty<string>()
                : GetCompatibilityChanges(latest, descriptor);

            if (request.Force || isStale || compatibilityChanges.Length > 0)
            {
                var reason = request.Force
                    ? "force=true，强制重新生成 embedding。"
                    : isStale
                        ? "source item 内容 hash 已变化。"
                        : $"embedding 配置变化，需要 reindex：{string.Join(", ", compatibilityChanges)}。";
                items.Add(NewPlanItem(source, latest, "Update", currentHash, latest.ContentHash, true, reason, planMetadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["requiresReindex"] = "true",
                    ["changeReasons"] = string.Join(",", compatibilityChanges)
                }));
            }
            else
            {
                items.Add(NewPlanItem(source, latest, "Skip", currentHash, latest.ContentHash, false, "embedding 已是当前版本。"));
            }

            foreach (var duplicate in existingEntries
                         .GroupBy(DuplicateKey, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1)
                         .SelectMany(group => group.OrderByDescending(entry => entry.UpdatedAt).Skip(1)))
            {
                items.Add(NewPlanItem(source, duplicate, "Duplicate", currentHash, duplicate.ContentHash, false, "同一 item/model/provider 存在重复 entry。", isDuplicate: true));
            }
        }

        foreach (var orphan in entries.Where(entry => !sourceById.ContainsKey(entry.ItemId)))
        {
            items.Add(new VectorReindexPlanItem
            {
                ItemId = orphan.ItemId,
                EntryId = orphan.EntryId,
                ItemKind = orphan.ItemKind,
                Layer = orphan.Layer,
                Action = "DeleteOrphan",
                ExistingContentHash = orphan.ContentHash,
                IsOrphan = true,
                Reason = "vector entry 没有对应 source item；V2 只报告，不自动删除。"
            });
        }

        var staleItems = items
            .Where(item => item.Action == "Update")
            .Select(item => item.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missingItems = items
            .Where(item => item.Action == "Create")
            .Select(item => item.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var duplicateItems = items
            .Where(item => item.IsDuplicate)
            .Select(item => item.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var orphanItems = items
            .Where(item => item.IsOrphan)
            .Select(item => item.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new VectorReindexPlan
        {
            PlanId = string.IsNullOrWhiteSpace(request.OperationId) ? Guid.NewGuid().ToString("N") : request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            LayerFilter = request.Layer ?? (request.Layers.Count > 0 ? string.Join(",", request.Layers) : null),
            ItemKindFilter = request.ItemKind,
            TotalCandidates = sources.Length,
            ToCreate = items.Count(item => item.Action == "Create"),
            ToUpdate = items.Count(item => item.Action == "Update"),
            ToSkip = items.Count(item => item.Action == "Skip"),
            ToDeleteOrphan = items.Count(item => item.Action == "DeleteOrphan"),
            EstimatedEmbeddingCount = items.Count(item => item.NeedsEmbedding),
            DryRun = request.DryRun || !request.Apply,
            StaleItems = staleItems,
            MissingItems = missingItems,
            DuplicateItems = duplicateItems,
            OrphanItems = orphanItems,
            Items = items,
            Warnings = warnings,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    internal async Task<SourceItem[]> LoadSourceItemsAsync(
        VectorReindexRequest request,
        CancellationToken cancellationToken)
    {
        var maxItems = request.MaxItems > 0 ? request.MaxItems : 200;
        var sources = new List<SourceItem>();

        if (request.IncludeContextItems && _contextStore is not null)
        {
            var contextItems = await _contextStore.QueryAsync(new ContextQuery
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                IncludeContent = true,
                Take = maxItems
            }, cancellationToken).ConfigureAwait(false);
            sources.AddRange(contextItems.Select(item => new SourceItem(
                item.Id,
                item.Type,
                "context",
                item.Content ?? string.Empty,
                item.WorkspaceId,
                item.CollectionId,
                item.UpdatedAt,
                item.Metadata)));
        }

        if (request.IncludeMemoryItems && _memoryStore is not null)
        {
            var memoryItems = await _memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                Take = maxItems
            }, cancellationToken).ConfigureAwait(false);
            sources.AddRange(memoryItems.Select(item => new SourceItem(
                item.Id,
                item.Type,
                item.Layer.ToString(),
                item.Content ?? string.Empty,
                item.WorkspaceId,
                item.CollectionId,
                item.UpdatedAt,
                item.Metadata)));
        }

        if (request.SourceItems.Count > 0)
        {
            sources.AddRange(request.SourceItems
                .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .Select(item => new SourceItem(
                    item.ItemId,
                    string.IsNullOrWhiteSpace(item.ItemKind) ? "unknown" : item.ItemKind,
                    string.IsNullOrWhiteSpace(item.Layer) ? "external" : item.Layer,
                    item.Text,
                    request.WorkspaceId,
                    request.CollectionId,
                    item.UpdatedAt == default ? DateTimeOffset.UtcNow : item.UpdatedAt,
                    item.Metadata)));
        }

        var layers = request.Layers
            .Where(layer => !string.IsNullOrWhiteSpace(layer))
            .Select(layer => layer.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return sources
            .Where(item => string.IsNullOrWhiteSpace(request.Layer)
                || string.Equals(item.Layer, request.Layer, StringComparison.OrdinalIgnoreCase))
            .Where(item => layers.Count == 0 || layers.Contains(item.Layer))
            .Where(item => string.IsNullOrWhiteSpace(request.ItemKind)
                || string.Equals(item.ItemKind, request.ItemKind, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToArray();
    }

    private static VectorReindexPlanItem NewPlanItem(
        SourceItem source,
        VectorIndexEntry? entry,
        string action,
        string currentHash,
        string? existingHash,
        bool needsEmbedding,
        string reason,
        bool isDuplicate = false,
        IReadOnlyDictionary<string, string>? planMetadata = null)
    {
        var metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase);
        if (planMetadata is not null)
        {
            foreach (var pair in planMetadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        return new VectorReindexPlanItem
        {
            ItemId = source.ItemId,
            EntryId = entry?.EntryId,
            ItemKind = source.ItemKind,
            Layer = source.Layer,
            Action = action,
            CurrentContentHash = currentHash,
            ExistingContentHash = existingHash,
            NeedsEmbedding = needsEmbedding,
            IsDuplicate = isDuplicate,
            Reason = reason,
            Metadata = metadata
        };
    }

    private static string[] GetCompatibilityChanges(
        VectorIndexEntry entry,
        EmbeddingGeneratorDescriptor descriptor)
    {
        var changes = new List<string>();
        if (!string.Equals(entry.EmbeddingProvider, descriptor.Provider, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(VectorIndexDiagnosticTypes.EmbeddingProviderChanged);
        }

        if (!string.Equals(entry.EmbeddingModel, descriptor.Model, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(VectorIndexDiagnosticTypes.EmbeddingModelChanged);
        }

        if (descriptor.Dimension > 0 && entry.Dimension != descriptor.Dimension)
        {
            changes.Add(VectorIndexDiagnosticTypes.DimensionChanged);
        }

        if (entry.Metadata.TryGetValue("normalize", out var normalized)
            && bool.TryParse(normalized, out var entryNormalize)
            && entryNormalize != descriptor.Normalize)
        {
            changes.Add(VectorIndexDiagnosticTypes.NormalizationMismatch);
        }

        return changes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string DuplicateKey(VectorIndexEntry entry)
    {
        return $"{entry.WorkspaceId}\u001f{entry.CollectionId}\u001f{entry.ItemId}\u001f{entry.EmbeddingProvider}\u001f{entry.EmbeddingModel}";
    }

    internal sealed record SourceItem(
        string ItemId,
        string ItemKind,
        string Layer,
        string Text,
        string WorkspaceId,
        string CollectionId,
        DateTimeOffset UpdatedAt,
        IReadOnlyDictionary<string, string> Metadata);
}

/// <summary>执行 vector reindex 计划；只有显式确认 apply 时才写入 vector index。</summary>
public sealed class VectorReindexExecutor
{
    private readonly VectorReindexPlanner _planner;
    private readonly IEmbeddingGenerator? _generator;
    private readonly IVectorIndexStore? _vectorStore;
    private readonly IVectorReindexReportStore? _reportStore;

    public VectorReindexExecutor(
        VectorReindexPlanner planner,
        IEmbeddingGenerator? generator,
        IVectorIndexStore? vectorStore,
        IVectorReindexReportStore? reportStore)
    {
        _planner = planner;
        _generator = generator;
        _vectorStore = vectorStore;
        _reportStore = reportStore;
    }

    public Task<VectorReindexPlan> CreatePlanOnlyAsync(
        VectorReindexRequest request,
        CancellationToken cancellationToken = default)
    {
        return _planner.CreatePlanAsync(request, cancellationToken);
    }

    public async Task<VectorReindexResult> ExecuteAsync(
        VectorReindexRequest request,
        string? jobId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var warnings = new List<string>();
        var plan = await _planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
        warnings.AddRange(plan.Warnings);

        var shouldApply = request.Apply && !request.DryRun;
        if (shouldApply && !request.ConfirmApply)
        {
            throw new InvalidOperationException("Vector reindex apply 需要 ConfirmApply=true。");
        }

        if (shouldApply && (_generator is null || _vectorStore is null))
        {
            throw new InvalidOperationException("Vector reindex apply 需要 IEmbeddingGenerator 与 IVectorIndexStore。");
        }

        var processed = new List<VectorReindexPlanItem>();
        var created = 0;
        var updated = 0;
        var skipped = 0;
        var failed = 0;

        if (shouldApply)
        {
            var sources = await _planner.LoadSourceItemsAsync(request, cancellationToken).ConfigureAwait(false);
            var sourceById = sources.ToDictionary(source => source.ItemId, StringComparer.OrdinalIgnoreCase);
            var targets = plan.Items
                .Where(item => item.Action is "Create" or "Update")
                .ToArray();

            foreach (var item in targets)
            {
                try
                {
                    if (!sourceById.TryGetValue(item.ItemId, out var source))
                    {
                        failed++;
                        errors.Add($"source item missing during apply: {item.ItemId}");
                        continue;
                    }

                    var result = await _generator!.GenerateAsync(new EmbeddingGeneratorRequest
                    {
                        OperationId = string.IsNullOrWhiteSpace(request.OperationId) ? Guid.NewGuid().ToString("N") : request.OperationId,
                        WorkspaceId = request.WorkspaceId,
                        CollectionId = request.CollectionId,
                        Inputs =
                        [
                            new EmbeddingGeneratorInput
                            {
                                ItemId = source.ItemId,
                                Text = source.Text,
                                ItemKind = source.ItemKind,
                                Layer = source.Layer,
                                Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase)
                            }
                        ]
                    }, cancellationToken).ConfigureAwait(false);

                    var entry = result.Entries.First();
                    await _vectorStore!.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
                    if (item.Action == "Create")
                    {
                        created++;
                    }
                    else
                    {
                        updated++;
                    }

                    processed.Add(item);
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{item.ItemId}: {ex.Message}");
                }
            }

            skipped = plan.Items.Count(item => item.Action == "Skip")
                      + plan.Items.Count(item => item.Action is "Duplicate" or "DeleteOrphan");
        }
        else
        {
            skipped = plan.Items.Count;
            processed.AddRange(plan.Items);
        }

        var completedAt = DateTimeOffset.UtcNow;
        var report = new VectorReindexResult
        {
            ReportId = Guid.NewGuid().ToString("N"),
            OperationId = string.IsNullOrWhiteSpace(request.OperationId) ? plan.PlanId : request.OperationId,
            JobId = jobId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Plan = plan,
            Summary = new VectorReindexSummary
            {
                TotalCandidates = plan.TotalCandidates,
                Created = created,
                Updated = updated,
                Skipped = skipped,
                Failed = failed,
                Duplicate = plan.DuplicateItems.Count,
                Orphan = plan.OrphanItems.Count,
                EstimatedEmbeddingCount = plan.EstimatedEmbeddingCount,
                DryRun = !shouldApply,
                Applied = shouldApply && failed == 0
            },
            ProcessedItems = processed,
            Warnings = warnings,
            Errors = errors,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };

        if (_reportStore is not null)
        {
            await _reportStore.SaveAsync(report, cancellationToken).ConfigureAwait(false);
        }

        return report;
    }
}

/// <summary>处理 vector_reindex 后台作业。</summary>
public sealed class VectorIndexingJobProcessor : IContextJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly VectorReindexExecutor _executor;

    public VectorIndexingJobProcessor(VectorReindexExecutor executor)
    {
        _executor = executor;
    }

    public ContextJobKind Kind => ContextJobKind.VectorReindex;

    public async Task ProcessAsync(ContextJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var request = string.IsNullOrWhiteSpace(job.PayloadJson)
            ? new VectorReindexRequest()
            : JsonSerializer.Deserialize<VectorReindexRequest>(job.PayloadJson, JsonOptions) ?? new VectorReindexRequest();

        request = EnsureJobDefaults(job, request);
        await _executor.ExecuteAsync(request, job.JobId, cancellationToken).ConfigureAwait(false);
    }

    private static VectorReindexRequest EnsureJobDefaults(ContextJob job, VectorReindexRequest request)
    {
        return new VectorReindexRequest
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId) ? job.JobId : request.OperationId,
            WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? job.WorkspaceId : request.WorkspaceId,
            CollectionId = string.IsNullOrWhiteSpace(request.CollectionId) ? job.CollectionId : request.CollectionId,
            Layer = request.Layer,
            ItemKind = request.ItemKind,
            Layers = request.Layers,
            DryRun = request.DryRun,
            Apply = request.Apply,
            ConfirmApply = request.ConfirmApply,
            Force = request.Force,
            BatchSize = request.BatchSize,
            MaxItems = request.MaxItems,
            IncludeContextItems = request.IncludeContextItems,
            IncludeMemoryItems = request.IncludeMemoryItems,
            Metadata = request.Metadata
        };
    }
}

/// <summary>Vector reindex 报告 Markdown 渲染器。</summary>
public static class VectorReindexReportRenderer
{
    public static string ToMarkdown(VectorReindexResult result)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("# Vector Reindex Report");
        builder.AppendLine();
        builder.AppendLine($"- ReportId: `{result.ReportId}`");
        builder.AppendLine($"- OperationId: `{result.OperationId}`");
        builder.AppendLine($"- Workspace: `{result.WorkspaceId}`");
        builder.AppendLine($"- Collection: `{result.CollectionId}`");
        builder.AppendLine($"- DryRun: `{result.Summary.DryRun}`");
        builder.AppendLine($"- Applied: `{result.Summary.Applied}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- TotalCandidates: `{result.Summary.TotalCandidates}`");
        builder.AppendLine($"- Created: `{result.Summary.Created}`");
        builder.AppendLine($"- Updated: `{result.Summary.Updated}`");
        builder.AppendLine($"- Skipped: `{result.Summary.Skipped}`");
        builder.AppendLine($"- Failed: `{result.Summary.Failed}`");
        builder.AppendLine($"- Duplicate: `{result.Summary.Duplicate}`");
        builder.AppendLine($"- Orphan: `{result.Summary.Orphan}`");
        builder.AppendLine($"- EstimatedEmbeddingCount: `{result.Summary.EstimatedEmbeddingCount}`");
        builder.AppendLine();
        builder.AppendLine("## Plan");
        builder.AppendLine();
        builder.AppendLine($"- ToCreate: `{result.Plan.ToCreate}`");
        builder.AppendLine($"- ToUpdate: `{result.Plan.ToUpdate}`");
        builder.AppendLine($"- ToSkip: `{result.Plan.ToSkip}`");
        builder.AppendLine($"- ToDeleteOrphan: `{result.Plan.ToDeleteOrphan}`");
        builder.AppendLine();
        builder.AppendLine("| Action | ItemId | Kind | Layer | Reason |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var item in result.Plan.Items.Take(50))
        {
            builder.AppendLine($"| {item.Action} | {item.ItemId} | {item.ItemKind} | {item.Layer} | {item.Reason.Replace("|", "/")} |");
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Errors");
            foreach (var error in result.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }
}
