using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Infrastructure;

namespace ContextCore.Core.Services;

/// <summary>
/// V6.5 影子适配器增量诊断。对比 baseline candidates 与 shadow adapter candidates，
/// 分类 delta=0 原因，输出完整诊断 profile。不修改 selected set，不写 formal package。
/// </summary>
public sealed class ShadowAdapterDeltaDiagnosticsRunner
{
    public ShadowAdapterDeltaDiagnosticsReport RunDiagnostics(
        ScopedShadowAdapterObservationWindowReport? v64Gate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        ShadowAdapterDeltaDiagnosticsOptions? options = null)
    {
        options ??= new ShadowAdapterDeltaDiagnosticsOptions();
        var blocked = new List<string>();
        if (v64Gate is null || !v64Gate.ObservationPassed)
            blocked.Add("V64GateMissingOrNotPassed");
        var hasData = dataset is not null && dataset.Samples.Count > 0 && dataset.CorpusItems.Count > 0;
        if (!hasData) blocked.Add("MissingDataset");

        var topK = Math.Max(1, options.TopK);
        var adapt = new ScopedShadowRetrievalAdapter(new[] { "" });
        var diag = new List<string>();

        int sampleCount = 0, baselineOnlyTotal = 0, shadowOnlyTotal = 0, overlapTotal = 0;
        int baselinePoolSize = 0, shadowPoolSize = 0;
        int filteredByEligibility = 0, filteredByLifecycle = 0, filteredByBelowTopK = 0, filteredByDuplicate = 0;

        foreach (var sample in dataset!.Samples)
        {
            var qt = Tokenize(sample.QueryText);
            var corpus = dataset.CorpusItems;
            var baselineAll = corpus
                .Select(i => (Id: i.ItemId, Score: DenseScore(qt,i)+LexicalScore(qt,i)+AnchorScore(qt,i)*0.5))
                .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var baselineTop = baselineAll.Take(topK).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fullCandidateList = baselineAll.Take(options.DiagnosticTopK).Select(x => x.Id).ToList();

            var result = adapt.ExecuteAsync(new RetrievalAdapterRequest
            {
                OperationId = $"diag-{sample.SampleId}",
                QueryText = sample.QueryText,
                BaselineCandidateIds = baselineTop.ToList(),
                WorkspaceId = "", CollectionId = "",
            }, CancellationToken.None).GetAwaiter().GetResult();

            var shadowAdded = new HashSet<string>(result.AddedCandidateIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var shadowRemoved = new HashSet<string>(result.RemovedCandidateIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var shadowFull = new HashSet<string>(fullCandidateList, StringComparer.OrdinalIgnoreCase);

            var baselineOnly = baselineTop.Where(id => !shadowFull.Contains(id)).ToArray();
            var shadowOnly = shadowFull.Where(id => !baselineTop.Contains(id)).ToArray();
            var overlap = baselineTop.Where(id => shadowFull.Contains(id)).ToArray();

            baselineOnlyTotal += baselineOnly.Length; shadowOnlyTotal += shadowOnly.Length; overlapTotal += overlap.Length;
            baselinePoolSize += baselineTop.Count; shadowPoolSize += shadowFull.Count;

            // 检查过滤原因
            foreach (var id in fullCandidateList)
            {
                if (!baselineTop.Contains(id)) { filteredByBelowTopK++; continue; }
                var item = corpus.FirstOrDefault(c => c.ItemId == id);
                if (item is null) continue;
                if (!string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(item.TargetSection, VectorQueryTargetSections.HistoricalContext, StringComparison.OrdinalIgnoreCase))
                    filteredByEligibility++;
                if (string.Equals(item.Lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Lifecycle, "Historical", StringComparison.OrdinalIgnoreCase))
                    filteredByLifecycle++;
            }
            sampleCount++;
        }

        var overlapRate = baselinePoolSize > 0 ? (double)overlapTotal / baselinePoolSize : 0;
        diag.Add($"samples={sampleCount} baselinePool={baselinePoolSize} shadowPool={shadowPoolSize}");
        diag.Add($"overlap={overlapTotal}/{baselinePoolSize} ({overlapRate:P2})");
        diag.Add($"baselineOnly={baselineOnlyTotal} shadowOnly={shadowOnlyTotal}");
        diag.Add($"filters: eligibility={filteredByEligibility} lifecycle={filteredByLifecycle} belowTopK={filteredByBelowTopK} duplicate={filteredByDuplicate}");

        var reasons = new List<string>();
        if (shadowOnlyTotal == 0) reasons.Add("BaselineAlreadyContainsAll");
        if (overlapRate >= 0.99) reasons.Add("CandidateSourceNoUniqueContribution");
        if (filteredByBelowTopK > 0) reasons.Add("ShadowCandidateBelowTopK");
        if (filteredByEligibility > 0 || filteredByLifecycle > 0) reasons.Add("ShadowCandidateFiltered");
        if (string.IsNullOrEmpty(dataset?.Samples.FirstOrDefault()?.Metadata?.TryGetValue("workspaceId", out _) == true ? "" : null))
            reasons.Add("ScopeSyntheticDatasetLimitation");
        if (reasons.Count == 0 && shadowOnlyTotal == 0) reasons.Add("AdapterInputInsufficient");

        diag.Add($"deltaZeroCauses: {string.Join(", ", reasons)}");
        var blk = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        return new ShadowAdapterDeltaDiagnosticsReport
        {
            OperationId = $"shadow-adapter-delta-diag-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DiagnosticsPassed = blk.Length == 0,
            Recommendations = blk.Length == 0 ? "ReadyForShadowAdapterDeltaTriage" : "Blocked",
            SampleCount = sampleCount, BaselinePoolSize = baselinePoolSize, ShadowPoolSize = shadowPoolSize,
            OverlapCount = overlapTotal, OverlapRate = overlapRate,
            BaselineOnlyCount = baselineOnlyTotal, ShadowOnlyCount = shadowOnlyTotal,
            FilteredByEligibilityCount = filteredByEligibility, FilteredByLifecycleCount = filteredByLifecycle,
            FilteredByBelowTopKCount = filteredByBelowTopK, FilteredByDuplicateCount = filteredByDuplicate,
            DeltaZeroCauses = reasons, Diagnostics = diag, BlockedReasons = blk,
        };
    }

    public static string BuildMarkdown(string title, ShadowAdapterDeltaDiagnosticsReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`"); b.AppendLine();
        b.AppendLine($"- DiagnosticsPassed: `{r.DiagnosticsPassed}`");
        b.AppendLine($"- SampleCount: `{r.SampleCount}`");
        b.AppendLine($"- BaselinePool: `{r.BaselinePoolSize}` ShadowPool: `{r.ShadowPoolSize}`");
        b.AppendLine($"- Overlap: `{r.OverlapCount}/{r.BaselinePoolSize}` ({r.OverlapRate:P2})");
        b.AppendLine($"- BaselineOnly: `{r.BaselineOnlyCount}` ShadowOnly: `{r.ShadowOnlyCount}`");
        b.AppendLine($"- Filters: eligibility={r.FilteredByEligibilityCount} lifecycle={r.FilteredByLifecycleCount} belowTopK={r.FilteredByBelowTopKCount} dup={r.FilteredByDuplicateCount}");
        b.AppendLine($"- DeltaZeroCauses: `{string.Join(", ", r.DeltaZeroCauses)}`");
        AppendList(b, "Diagnostics", r.Diagnostics);
        AppendList(b, "Blocked", r.BlockedReasons);
        b.AppendLine(); b.AppendLine("V6.5 delta diagnostics only. No formal retrieval, package write, selected set change, packing/runtime mutation.");
        return b.ToString();
    }

    private static double DenseScore(IReadOnlySet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var t = Tokenize($"{it.Content} {it.TargetSection} {it.ItemKind} {it.SourceKind} {string.Join(' ', it.Tags ?? Array.Empty<string>())}");
        return qt.Count == 0 || t.Count == 0 ? 0 : qt.Count(t.Contains) / Math.Sqrt(qt.Count * t.Count);
    }
    private static double LexicalScore(IReadOnlySet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var t = Tokenize(it.Content ?? ""); if (qt.Count == 0 || t.Count == 0) return 0;
        var o = qt.Count(t.Contains); var u = qt.Count + t.Count - o; return u == 0 ? 0 : (double)o / u;
    }
    private static double AnchorScore(IReadOnlySet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var a = Tokenize($"{string.Join(' ', it.Tags ?? Array.Empty<string>())} {string.Join(' ', it.Anchors ?? Array.Empty<string>())} {it.TargetSection}");
        return qt.Count == 0 || a.Count == 0 ? 0 : qt.Count(a.Contains) / (double)a.Count;
    }
    private static HashSet<string> Tokenize(string v)
    {
        var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var b = new StringBuilder(v.Length);
        foreach (var ch in v) { if (char.IsLetterOrDigit(ch) || ch == '-') b.Append(char.ToLowerInvariant(ch)); else { if (b.Length > 0) { r.Add(b.ToString()); b.Clear(); } } }
        if (b.Length > 0) r.Add(b.ToString()); return r;
    }
    private static void AppendList(StringBuilder b, string t, IReadOnlyList<string> items)
    {
        b.AppendLine(); b.AppendLine($"## {t}");
        if (items.Count == 0) b.AppendLine("- (empty)");
        else foreach (var i in items) b.AppendLine($"- `{i}`");
    }
}

public sealed class ShadowAdapterDeltaDiagnosticsReport
{
    public string OperationId { get; init; } = ""; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool DiagnosticsPassed { get; init; }
    public string Recommendations { get; init; } = "Blocked";
    public int SampleCount { get; init; }
    public int BaselinePoolSize { get; init; } public int ShadowPoolSize { get; init; }
    public int OverlapCount { get; init; } public double OverlapRate { get; init; }
    public int BaselineOnlyCount { get; init; } public int ShadowOnlyCount { get; init; }
    public int FilteredByEligibilityCount { get; init; } public int FilteredByLifecycleCount { get; init; }
    public int FilteredByBelowTopKCount { get; init; } public int FilteredByDuplicateCount { get; init; }
    public IReadOnlyList<string> DeltaZeroCauses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class ShadowAdapterDeltaDiagnosticsOptions
{
    public int TopK { get; init; } = 5;
    public int DiagnosticTopK { get; init; } = 10;
}

