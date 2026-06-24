using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Vector Query Preview 服务；只读查询独立 vector index，不改变正式 retrieval/package。</summary>
public sealed class VectorQueryPreviewService
{
    private readonly IVectorIndexStore? _store;
    private readonly IEmbeddingGenerator? _generator;
    private readonly VectorIndexService _diagnosticsService;
    private readonly VectorQueryProfileRegistry _profileRegistry;
    private readonly VectorCandidateEligibilityPolicy _eligibilityPolicy;

    public VectorQueryPreviewService(
        IVectorIndexStore? store,
        IEmbeddingGenerator? generator,
        VectorIndexService diagnosticsService,
        VectorQueryProfileRegistry? profileRegistry = null,
        VectorCandidateEligibilityPolicy? eligibilityPolicy = null)
    {
        _store = store;
        _generator = generator;
        _diagnosticsService = diagnosticsService;
        _profileRegistry = profileRegistry ?? new VectorQueryProfileRegistry();
        _eligibilityPolicy = eligibilityPolicy ?? new VectorCandidateEligibilityPolicy();
    }

    public async Task<VectorQueryPreviewResult> PreviewAsync(
        VectorQueryPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.QueryText);

        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? $"vector-query-preview-{Guid.NewGuid():N}"
            : request.OperationId;
        var topK = Math.Clamp(request.TopK > 0 ? request.TopK : 10, 1, 1000);
        var profile = _profileRegistry.Resolve(request.ProfileId);
        var warnings = new List<string>();
        var diagnostics = await _diagnosticsService
            .GetDiagnosticsAsync(request.WorkspaceId, request.CollectionId, cancellationToken)
            .ConfigureAwait(false);

        if (_store is null)
        {
            warnings.Add("当前 provider 未注册 V1 vector index store，query preview 无候选。");
            return NewEmptyResult(request, operationId, topK, diagnostics, warnings, storeAvailable: false, generatorAvailable: _generator is not null);
        }

        if (_generator is null)
        {
            warnings.Add("当前 provider 未注册 embedding generator，query preview 无法生成 query embedding。");
            return NewEmptyResult(request, operationId, topK, diagnostics, warnings, storeAvailable: true, generatorAvailable: false);
        }

        if (diagnostics.IndexedCount == 0)
        {
            warnings.Add("vector index 当前为空；请先执行 reindex apply 后再预览 query。");
            return NewEmptyResult(request, operationId, topK, diagnostics, warnings, storeAvailable: true, generatorAvailable: true);
        }

        var embedding = await _generator.GenerateAsync(new EmbeddingGeneratorRequest
        {
            OperationId = operationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Inputs =
            [
                new EmbeddingGeneratorInput
                {
                    ItemId = "__query__",
                    Text = request.QueryText,
                    ItemKind = "query",
                    Layer = "query",
                    Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
                }
            ]
        }, cancellationToken).ConfigureAwait(false);
        var queryVector = embedding.Entries.FirstOrDefault()?.Vector ?? Array.Empty<float>();
        if (queryVector.Count == 0)
        {
            warnings.Add("embedding generator 返回空 query vector。");
            return NewEmptyResult(request, operationId, topK, diagnostics, warnings, storeAvailable: true, generatorAvailable: true);
        }

        var generatorDescriptor = EmbeddingGeneratorDescriptor.From(_generator);
        var searchTake = Math.Min(topK * 10, 1000);
        var raw = await _store.SearchAsync(new VectorIndexSearchQuery
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Vector = queryVector,
            EmbeddingModel = generatorDescriptor.Model,
            EmbeddingProvider = generatorDescriptor.Provider,
            Dimension = generatorDescriptor.Dimension > 0 ? generatorDescriptor.Dimension : null,
            TopK = searchTake,
            MinScore = null,
            IncludeVector = request.IncludeVector
        }, cancellationToken).ConfigureAwait(false);

        var diagnosticsByItem = diagnostics.Diagnostics
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var diagnosticsByEntry = diagnostics.Diagnostics
            .Where(item => !string.IsNullOrWhiteSpace(item.EntryId))
            .GroupBy(item => item.EntryId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var candidates = raw
            .Where(item => MatchesFilter(item.Entry.Layer, request.Layer))
            .Where(item => MatchesFilter(item.Entry.ItemKind, request.ItemKind))
            .Take(topK)
            .Select((item, index) => ToCandidate(
                item,
                index + 1,
                profile,
                request.MinSimilarity,
                generatorDescriptor,
                diagnosticsByItem,
                diagnosticsByEntry,
                _eligibilityPolicy))
            .ToArray();

        if (candidates.Length == 0)
        {
            warnings.Add("query preview 没有返回候选；可能是索引覆盖不足或过滤条件过窄。");
        }

        return new VectorQueryPreviewResult
        {
            OperationId = operationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            QueryText = request.QueryText,
            TopK = topK,
            ProfileId = profile.ProfileId,
            Layer = request.Layer,
            ItemKind = request.ItemKind,
            MinSimilarity = request.MinSimilarity,
            Candidates = candidates,
            Diagnostics = ToPreviewDiagnostics(diagnostics, _store is not null, _generator is not null),
            Warnings = warnings,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static VectorQueryPreviewResult NewEmptyResult(
        VectorQueryPreviewRequest request,
        string operationId,
        int topK,
        VectorIndexDiagnosticsReport diagnostics,
        IReadOnlyList<string> warnings,
        bool storeAvailable,
        bool generatorAvailable)
    {
        return new VectorQueryPreviewResult
        {
            OperationId = operationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            QueryText = request.QueryText,
            TopK = topK,
            ProfileId = string.IsNullOrWhiteSpace(request.ProfileId)
                ? VectorQueryProfileIds.NormalV1
                : request.ProfileId,
            Layer = request.Layer,
            ItemKind = request.ItemKind,
            MinSimilarity = request.MinSimilarity,
            Diagnostics = ToPreviewDiagnostics(diagnostics, storeAvailable, generatorAvailable),
            Warnings = warnings,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static VectorQueryPreviewCandidate ToCandidate(
        VectorIndexSearchResult result,
        int rank,
        VectorQueryProfile profile,
        double? requestMinSimilarity,
        EmbeddingGeneratorDescriptor generatorDescriptor,
        IReadOnlyDictionary<string, VectorIndexDiagnostic[]> diagnosticsByItem,
        IReadOnlyDictionary<string, VectorIndexDiagnostic[]> diagnosticsByEntry,
        VectorCandidateEligibilityPolicy eligibilityPolicy)
    {
        var entry = result.Entry;
        var relatedDiagnostics = diagnosticsByItem.GetValueOrDefault(entry.ItemId) ?? Array.Empty<VectorIndexDiagnostic>();
        if (diagnosticsByEntry.TryGetValue(entry.EntryId, out var entryDiagnostics))
        {
            relatedDiagnostics = relatedDiagnostics
                .Concat(entryDiagnostics)
                .DistinctBy(item => item.DiagnosticId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var diagnosticTypes = relatedDiagnostics
            .Select(item => item.Type)
            .Concat(BuildCompatibilityDiagnosticTypes(entry, generatorDescriptor))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var effectiveProfile = requestMinSimilarity is null
            ? profile
            : new VectorQueryProfile
            {
                ProfileId = profile.ProfileId,
                MinSimilarity = requestMinSimilarity.Value,
                AllowedLayers = profile.AllowedLayers,
                AllowedItemKinds = profile.AllowedItemKinds,
                AllowedSourceTypes = profile.AllowedSourceTypes,
                DiagnosticsOnlyItemKinds = profile.DiagnosticsOnlyItemKinds,
                RequireKnownLifecycle = profile.RequireKnownLifecycle,
                RequireCompleteLifecycleMetadata = profile.RequireCompleteLifecycleMetadata,
                AllowDeprecatedCandidates = profile.AllowDeprecatedCandidates,
                AllowHistoricalCandidates = profile.AllowHistoricalCandidates,
                AllowRejectedCandidates = profile.AllowRejectedCandidates,
                AllowCandidateLifecycle = profile.AllowCandidateLifecycle,
                DefaultTargetSection = profile.DefaultTargetSection,
                HistoricalTargetSection = profile.HistoricalTargetSection,
                DiagnosticsTargetSection = profile.DiagnosticsTargetSection
            };
        var eligibility = eligibilityPolicy.Evaluate(effectiveProfile, entry, result.Score, diagnosticTypes);

        return new VectorQueryPreviewCandidate
        {
            CandidateId = entry.ItemId,
            EntryId = entry.EntryId,
            ItemId = entry.ItemId,
            ItemKind = entry.ItemKind,
            Layer = entry.Layer,
            Rank = rank,
            RawRank = result.Rank,
            Similarity = result.Score,
            ContentHash = entry.ContentHash,
            EmbeddingModel = entry.EmbeddingModel,
            EmbeddingProvider = entry.EmbeddingProvider,
            Dimension = entry.Dimension,
            IsDuplicate = diagnosticTypes.Contains(VectorIndexDiagnosticTypes.DuplicateVectorEntry, StringComparer.OrdinalIgnoreCase),
            IsStale = diagnosticTypes.Contains(VectorIndexDiagnosticTypes.StaleEmbedding, StringComparer.OrdinalIgnoreCase)
                      || diagnosticTypes.Contains(VectorIndexDiagnosticTypes.ContentHashMismatch, StringComparer.OrdinalIgnoreCase),
            IsOrphan = diagnosticTypes.Contains(VectorIndexDiagnosticTypes.OrphanVectorEntry, StringComparer.OrdinalIgnoreCase),
            IsLifecycleRisk = IsLifecycleRisk(entry),
            Diagnostics = diagnosticTypes,
            EligibilityStatus = eligibility.EligibilityStatus,
            BlockedReasons = eligibility.BlockedReasons,
            TargetSection = eligibility.TargetSection,
            RiskIfNormalSelected = eligibility.RiskIfNormalSelected,
            RiskAfterPolicy = eligibility.RiskAfterPolicy,
            Metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static VectorQueryPreviewDiagnostics ToPreviewDiagnostics(
        VectorIndexDiagnosticsReport report,
        bool storeAvailable,
        bool generatorAvailable)
    {
        return new VectorQueryPreviewDiagnostics
        {
            StoreAvailable = storeAvailable,
            GeneratorAvailable = generatorAvailable,
            IndexEmpty = report.IndexedCount == 0,
            IndexedCount = report.IndexedCount,
            DuplicateCount = report.DuplicateCount,
            StaleCount = report.StaleCount,
            OrphanCount = report.OrphanCount,
            DimensionMismatchCount = report.DimensionMismatchCount,
            UnsupportedModelCount = report.UnsupportedModelCount,
            ProviderUnavailableCount = report.ProviderUnavailableCount,
            Diagnostics = report.Diagnostics
        };
    }

    private static bool MatchesFilter(string actual, string? expected)
    {
        return string.IsNullOrWhiteSpace(expected)
               || string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLifecycleRisk(VectorIndexEntry entry)
    {
        var lifecycle = new VectorSourceLifecycleMetadataResolver().Resolve(entry);
        return lifecycle.IsDeprecated
               || lifecycle.IsRejected
               || lifecycle.IsSuperseded
               || lifecycle.IsHistorical;
    }

    private static IEnumerable<string> BuildCompatibilityDiagnosticTypes(
        VectorIndexEntry entry,
        EmbeddingGeneratorDescriptor descriptor)
    {
        if (!string.Equals(entry.EmbeddingProvider, descriptor.Provider, StringComparison.OrdinalIgnoreCase))
        {
            yield return VectorIndexDiagnosticTypes.ProviderMismatch;
            yield return VectorIndexDiagnosticTypes.EmbeddingProviderChanged;
        }

        if (!string.Equals(entry.EmbeddingModel, descriptor.Model, StringComparison.OrdinalIgnoreCase))
        {
            yield return VectorIndexDiagnosticTypes.EmbeddingModelMismatch;
            yield return VectorIndexDiagnosticTypes.EmbeddingModelChanged;
        }

        if (descriptor.Dimension > 0 && entry.Dimension != descriptor.Dimension)
        {
            yield return VectorIndexDiagnosticTypes.DimensionMismatch;
            yield return VectorIndexDiagnosticTypes.DimensionChanged;
        }

        if (entry.Metadata.TryGetValue("normalize", out var normalized)
            && bool.TryParse(normalized, out var entryNormalize)
            && entryNormalize != descriptor.Normalize)
        {
            yield return VectorIndexDiagnosticTypes.NormalizationMismatch;
        }
    }
}
