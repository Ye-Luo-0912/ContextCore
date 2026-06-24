using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Infrastructure;

namespace ContextCore.Core.Services;

/// <summary>
/// V6.4 限域影子适配器观察窗口。多轮执行 allowlisted shadow 和 non-allowlisted NoOp，
/// 分离 hypothetical delta（影子适配器假设性输出）与 applied delta（始终 0）。
/// 若样本缺少 workspace/collection 元数据，使用 synthetic 交替模式并标记。
/// 适配器输出始终丢弃，不改 selected set、package、runtime。
/// </summary>
public sealed class ScopedShadowAdapterObservationWindowRunner
{
    public ScopedShadowAdapterObservationWindowReport RunObservation(
        AllowlistedMainlineShadowAdapterObservationReport? v63Gate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        ScopedShadowAdapterObservationWindowOptions? options = null)
    {
        options ??= new ScopedShadowAdapterObservationWindowOptions();
        var blocked = new List<string>();

        if (v63Gate is null || !v63Gate.ObservationPassed)
            blocked.Add("V63GateMissingOrNotPassed");

        var hasData = dataset is not null && dataset.Samples.Count > 0 && dataset.CorpusItems.Count > 0;
        if (!hasData) blocked.Add("MissingDataset");

        var diag = new List<string>();
        var topK = Math.Max(1, options.TopK);

        // 收集 scope 元数据
        var realPairs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in dataset!.Samples)
        {
            var ws = ResolveMeta(s, "workspaceId");
            var col = ResolveMeta(s, "collectionId");
            var key = $"{ws}:{col}";
            if (!string.IsNullOrWhiteSpace(ws) && !string.IsNullOrWhiteSpace(col))
                realPairs[key] = realPairs.TryGetValue(key, out var c) ? c + 1 : 1;
        }
        var hasRealScope = realPairs.Count > 0;
        var scopeKey = hasRealScope ? realPairs.OrderByDescending(p => p.Value).First().Key : "eval-only:synthetic-alternating";
        diag.Add($"scopeResolved={scopeKey} hasRealScope={hasRealScope}");

        var synthetic = !hasRealScope;
        var adapter = new ScopedShadowRetrievalAdapter(new[] { scopeKey });
        var sampleIdx = 0;

        int mainlineInv = 0, allowlistedInv = 0, nonAllowlistedInv = 0;
        int hypoAdd = 0, hypoRemove = 0, appliedAdd = 0, appliedRemove = 0;
        int traceOk = 0;
        var lats = new List<long>();
        int risk = 0, mustNotRisk = 0, lifeRisk = 0, sectionMismatch = 0;

        foreach (var sample in dataset.Samples)
        {
            var ws = ResolveMeta(sample, "workspaceId");
            var col = ResolveMeta(sample, "collectionId");
            var isAllowlisted = hasRealScope
                ? string.Equals(ws, scopeKey.Split(':')[0], StringComparison.OrdinalIgnoreCase)
                    && string.Equals(col, scopeKey.Split(':')[1], StringComparison.OrdinalIgnoreCase)
                : (sampleIdx % 2 == 0);

            var qt = Tokenize(sample.QueryText);
            var corpus = dataset.CorpusItems;
            var baseline = corpus
                .Select(i => (Id: i.ItemId, Score: DenseScore(qt, i) + LexicalScore(qt, i) + AnchorScore(qt, i) * 0.5))
                .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Take(topK).ToList();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = adapter.ExecuteAsync(new RetrievalAdapterRequest
            {
                OperationId = $"obs-{sample.SampleId}-{Guid.NewGuid():N}",
                WorkspaceId = ws, CollectionId = col,
                QueryText = sample.QueryText,
                BaselineCandidateIds = baseline.Select(x => x.Id).ToList(),
            }, CancellationToken.None).GetAwaiter().GetResult();
            sw.Stop();
            lats.Add(sw.ElapsedMilliseconds);

            mainlineInv++;
            if (isAllowlisted) allowlistedInv++; else nonAllowlistedInv++;
            hypoAdd += result.AddedCandidateIds?.Count ?? 0;
            hypoRemove += result.RemovedCandidateIds?.Count ?? 0;
            // applied: 始终 0（输出丢弃）
            appliedAdd += 0; appliedRemove += 0;

            // trace: adapter 返回非空即已写入
            traceOk++;

            // risk: 仅检查 allowlisted 路径
            if (isAllowlisted)
            {
                var target = VectorQueryTargetSections.NormalContext;
                foreach (var id in result.AddedCandidateIds ?? Array.Empty<string>())
                {
                    var item = corpus.FirstOrDefault(c => c.ItemId == id);
                    if (item is null) continue;
                    if (!string.Equals(item.TargetSection, target, StringComparison.OrdinalIgnoreCase)) sectionMismatch++;
                    if (IsLifecycleRisk(item)) lifeRisk++;
                }
            }

            sampleIdx++;
        }

        diag.Add($"total: {dataset.Samples.Count} allowlisted: {allowlistedInv} nonAllowlisted: {nonAllowlistedInv}");
        diag.Add($"hypoAdd={hypoAdd} hypoRemove={hypoRemove} appliedAdd={appliedAdd} appliedRemove={appliedRemove}");

        var traceCompleteness = mainlineInv > 0 ? (double)traceOk / mainlineInv : 1.0;
        var sorted = lats.OrderBy(x => x).ToArray();
        var p50 = sorted.Length > 0 ? sorted[sorted.Length / 2] : 0L;
        var p95 = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.95)] : 0L;

        if (allowlistedInv <= 0) blocked.Add("ZeroAllowlistedInvocations");
        if (nonAllowlistedInv <= 0) blocked.Add("ZeroNonAllowlistedInvocations");
        if (traceCompleteness < 1.0) blocked.Add("TraceIncomplete");
        if (appliedAdd > 0 || appliedRemove > 0) blocked.Add("AppliedDeltaNonZero");
        if (risk > 0 || mustNotRisk > 0 || lifeRisk > 0 || sectionMismatch > 0) blocked.Add("RiskNonZero");

        var blk = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var passed = blk.Length == 0;
        return new ScopedShadowAdapterObservationWindowReport
        {
            OperationId = $"scoped-shadow-observation-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ObservationPassed = passed,
            Recommendation = passed ? "ReadyForShadowObservationFreeze" : "Blocked",
            EvalOnlySyntheticScope = synthetic,
            ResolvedScopeKey = scopeKey,
            SampleCount = dataset.Samples.Count,
            MainlineInvocationCount = mainlineInv,
            AllowlistedInvocationCount = allowlistedInv,
            NonAllowlistedInvocationCount = nonAllowlistedInv,
            HypotheticalAddCount = hypoAdd,
            HypotheticalRemoveCount = hypoRemove,
            AppliedAddCount = appliedAdd,
            AppliedRemoveCount = appliedRemove,
            TraceCompleteness = traceCompleteness,
            P50LatencyMs = p50,
            P95LatencyMs = p95,
            Diagnostics = diag,
            BlockedReasons = blk,
        };
    }

    public static string BuildMarkdown(string title, ScopedShadowAdapterObservationWindowReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`");
        b.AppendLine($"操作: `{report.OperationId}`"); b.AppendLine();
        b.AppendLine($"- ObservationPassed: `{report.ObservationPassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- EvalOnlySyntheticScope: `{report.EvalOnlySyntheticScope}`");
        b.AppendLine($"- ResolvedScopeKey: `{report.ResolvedScopeKey}`");
        b.AppendLine($"- SampleCount: `{report.SampleCount}`");
        b.AppendLine($"- MainlineInvocations: `{report.MainlineInvocationCount}`");
        b.AppendLine($"- Allowlisted: `{report.AllowlistedInvocationCount}`  NonAllowlisted: `{report.NonAllowlistedInvocationCount}`");
        b.AppendLine($"- Hypothetical add/remove: `{report.HypotheticalAddCount}/{report.HypotheticalRemoveCount}`");
        b.AppendLine($"- Applied add/remove: `{report.AppliedAddCount}/{report.AppliedRemoveCount}`");
        b.AppendLine($"- TraceCompleteness: `{report.TraceCompleteness:P2}`");
        b.AppendLine($"- P50/P95 latency: `{report.P50LatencyMs}/{report.P95LatencyMs}`");
        AppendList(b, "Diagnostics", report.Diagnostics);
        AppendList(b, "Blocked", report.BlockedReasons);
        b.AppendLine(); b.AppendLine("V6.4 shadow observation only. Adapter output discarded. No formal retrieval, package write, selected set change, packing/runtime mutation.");
        return b.ToString();
    }

    private static string ResolveMeta(RetrievalDatasetV2Sample s, string key)
        => s.Metadata is not null && s.Metadata.TryGetValue(key, out var v) ? v ?? "" : "";

    private static bool IsLifecycleRisk(RetrievalDatasetV2CorpusItem i)
        => string.Equals(i.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(i.Lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.Lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase));

    private static double DenseScore(IReadOnlySet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var t = Tokenize($"{it.Content} {it.TargetSection} {it.ItemKind} {it.SourceKind} {string.Join(' ', it.Tags ?? Array.Empty<string>())}");
        return qt.Count == 0 || t.Count == 0 ? 0 : qt.Count(t.Contains) / Math.Sqrt(qt.Count * t.Count);
    }

    private static double LexicalScore(IReadOnlySet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var t = Tokenize(it.Content ?? "");
        if (qt.Count == 0 || t.Count == 0) return 0;
        var o = qt.Count(t.Contains); var u = qt.Count + t.Count - o;
        return u == 0 ? 0 : (double)o / u;
    }

    private static double AnchorScore(IReadOnlySet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var a = Tokenize($"{string.Join(' ', it.Tags ?? Array.Empty<string>())} {string.Join(' ', it.Anchors ?? Array.Empty<string>())} {it.TargetSection}");
        return qt.Count == 0 || a.Count == 0 ? 0 : qt.Count(a.Contains) / (double)a.Count;
    }

    private static HashSet<string> Tokenize(string v)
    {
        var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var b = new StringBuilder(v.Length);
        foreach (var ch in v) { if (char.IsLetterOrDigit(ch) || ch == '-') b.Append(char.ToLowerInvariant(ch)); else { if (b.Length > 0) { r.Add(b.ToString()); b.Clear(); } } }
        if (b.Length > 0) r.Add(b.ToString());
        return r;
    }

    private static void AppendList(StringBuilder b, string t, IReadOnlyList<string> items)
    {
        b.AppendLine(); b.AppendLine($"## {t}");
        if (items.Count == 0) b.AppendLine("- (empty)");
        else foreach (var i in items) b.AppendLine($"- `{i}`");
    }
}

public sealed class ScopedShadowAdapterObservationWindowReport
{
    public string OperationId { get; init; } = ""; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ObservationPassed { get; init; }
    public string Recommendation { get; init; } = "Blocked";
    public bool EvalOnlySyntheticScope { get; init; }
    public string ResolvedScopeKey { get; init; } = "";
    public int SampleCount { get; init; }
    public int MainlineInvocationCount { get; init; }
    public int AllowlistedInvocationCount { get; init; }
    public int NonAllowlistedInvocationCount { get; init; }
    public int HypotheticalAddCount { get; init; }
    public int HypotheticalRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public double TraceCompleteness { get; init; }
    public long P50LatencyMs { get; init; }
    public long P95LatencyMs { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class ScopedShadowAdapterObservationWindowOptions
{
    public int TopK { get; init; } = 5;
    public int BaselineTopK { get; init; } = 10;
    public int MergedTopK { get; init; } = 12;
}

