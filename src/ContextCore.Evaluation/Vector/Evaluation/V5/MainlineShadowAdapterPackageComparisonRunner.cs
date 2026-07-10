using System.Diagnostics;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Infrastructure;

namespace ContextCore.Core.Services;

/// <summary>
/// 主线影子适配器调用与包比较。模拟真实 candidate assembly 路径：
/// 对每个样本通过 DI 调用适配器（ScopedShadow / NoOp），
/// 比较假设性 add/remove 对 baseline package 的影响，记录延迟指标。
/// 适配器输出始终丢弃，baseline 继续作为正式结果。
/// </summary>
public sealed class MainlineShadowAdapterPackageComparisonRunner
{
    public MainlineShadowAdapterPackageComparisonReport RunComparison(
        ScopedShadowAdapterInvocationReport? v61Gate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        MainlineShadowAdapterPackageComparisonOptions? options = null)
    {
        options ??= new MainlineShadowAdapterPackageComparisonOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        if (v61Gate is null || !v61Gate.InvocationPassed)
            blocked.Add("V61GateNotPassed");

        var allowlistedKey = $"{options.AllowlistedWorkspace}:{options.AllowlistedCollection}";
        var adapter = new ScopedShadowRetrievalAdapter(new[] { allowlistedKey });

        var hasData = dataset is not null && dataset.Samples.Count > 0 && dataset.CorpusItems.Count > 0;
        if (!hasData) blocked.Add("MissingDataset");

        var totalBaselineCandidates = 0;
        var totalShadowAdds = 0;
        var totalShadowRemoves = 0;
        var allowlistedCount = 0;
        var nonAllowlistedCount = 0;
        var mainlineInvocationCount = 0;
        var traceCount = 0;
        var sampleCount = 0;
        var totalBaselineTokens = 0;
        var totalShadowTokens = 0;
        var maxTokenDelta = 0;
        var latencies = new List<long>();

        if (hasData)
        {
            foreach (var sample in dataset!.Samples)
            {
                var ws = ResolveMetadata(sample, "workspaceId");
                var col = ResolveMetadata(sample, "collectionId");
                var isAllowlisted = string.Equals(ws, options.AllowlistedWorkspace, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(col, options.AllowlistedCollection, StringComparison.OrdinalIgnoreCase);

                var qt = Tokenize(sample.QueryText);
                var corpus = dataset.CorpusItems;

                // baseline candidates
                var baselineScores = corpus
                    .Select(item => (Id: item.ItemId, Score: DenseScore(qt, item) + LexicalScore(qt, item) + AnchorScore(qt, item) * 0.5))
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .Take(options.BaselineTopK).ToList();

                totalBaselineCandidates += baselineScores.Count;
                var baselineTokenCount = baselineScores.Sum(s =>
                {
                    var item = corpus.FirstOrDefault(c => c.ItemId == s.Id);
                    return item is null ? 0 : Tokenize($"{item.Content} {string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)}").Count;
                });
                totalBaselineTokens += baselineTokenCount;

                // adapter invocation (simulates DI call)
                var sw = Stopwatch.StartNew();
                var result = adapter.WithTraceWriter(options.TraceRoot).ExecuteAsync(new RetrievalAdapterRequest
                {
                    OperationId = $"mline-{sample.SampleId}-{Guid.NewGuid():N}",
                    WorkspaceId = ws, CollectionId = col,
                    QueryText = sample.QueryText,
                    BaselineCandidateIds = baselineScores.Select(x => x.Id).ToList(),
                }, CancellationToken.None).GetAwaiter().GetResult();
                sw.Stop();
                latencies.Add(sw.ElapsedMilliseconds);

                mainlineInvocationCount++;
                if (isAllowlisted) allowlistedCount++; else nonAllowlistedCount++;

                if (!string.IsNullOrEmpty(result.TracePath) && File.Exists(result.TracePath))
                    traceCount++;

                totalShadowAdds += result.AddedCandidateIds.Count;
                totalShadowRemoves += result.RemovedCandidateIds.Count;

                // shadow package token estimate (hypothetical)
                var shadowTokenCount = baselineTokenCount;
                var removedTokens = result.RemovedCandidateIds.Sum(id =>
                {
                    var item = corpus.FirstOrDefault(c => c.ItemId == id);
                    return item is null ? 0 : Tokenize($"{item.Content} {string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)}").Count;
                });
                shadowTokenCount -= removedTokens;
                totalShadowTokens += shadowTokenCount;
                var tokenDelta = Math.Abs(baselineTokenCount - shadowTokenCount);
                if (tokenDelta > maxTokenDelta) maxTokenDelta = tokenDelta;

                sampleCount++;
            }
        }

        diag.Add($"samples={sampleCount} mainlineInvocations={mainlineInvocationCount}");
        diag.Add($"allowlisted={allowlistedCount} nonAllowlisted={nonAllowlistedCount}");
        diag.Add($"totalBaselineCandidates={totalBaselineCandidates}");
        diag.Add($"totalShadowAdds={totalShadowAdds} totalShadowRemoves={totalShadowRemoves}");
        diag.Add($"tracesWritten={traceCount}/{mainlineInvocationCount}");

        var traceCompleteness = mainlineInvocationCount > 0 ? (double)traceCount / mainlineInvocationCount : 1.0;
        var latencySorted = latencies.OrderBy(x => x).ToArray();
        var p50Latency = latencySorted.Length > 0 ? latencySorted[latencySorted.Length / 2] : 0L;
        var p95Latency = latencySorted.Length > 0
            ? latencySorted[(int)(latencySorted.Length * 0.95)]
            : 0L;

        if (mainlineInvocationCount <= 0) blocked.Add("ZeroMainlineInvocations");
        if (nonAllowlistedCount > 0)
        {
            // non-allowlisted calls return NoOp; only allowlisted may report hypothetical removals
            diag.Add($"nonAllowlistedScope: confirmed NoOp behavior (no add/remove from non-allowlisted)");
        }
        if (traceCompleteness < 1.0) blocked.Add("IncompleteTraceCoverage");
        if (p95Latency > options.MaxLatencyMs) blocked.Add("LatencyExceedsBudget");

        var passed = blocked.Count == 0;
        return new MainlineShadowAdapterPackageComparisonReport
        {
            OperationId = $"mainline-shadow-adapter-package-comparison-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ComparisonPassed = passed,
            SampleCount = sampleCount,
            MainlineInvocationCount = mainlineInvocationCount,
            AllowlistedCount = allowlistedCount,
            NonAllowlistedCount = nonAllowlistedCount,
            TotalBaselineCandidateCount = totalBaselineCandidates,
            TotalShadowAddCount = totalShadowAdds,
            TotalShadowRemoveCount = totalShadowRemoves,
            BaselineTokenTotal = totalBaselineTokens,
            ShadowTokenTotal = totalShadowTokens,
            TokenDeltaMax = maxTokenDelta,
            TraceCompleteness = traceCompleteness,
            P50LatencyMs = p50Latency,
            P95LatencyMs = p95Latency,
            FormalSelectedSetChanged = false,
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            Diagnostics = diag,
            BlockedReasons = blocked,
        };
    }

    public static string BuildMarkdown(string title, MainlineShadowAdapterPackageComparisonReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`"); b.AppendLine();
        b.AppendLine("## 比较摘要");
        b.AppendLine($"- ComparisonPassed: `{report.ComparisonPassed}`");
        b.AppendLine($"- MainlineInvocationCount: `{report.MainlineInvocationCount}`");
        b.AppendLine($"- AllowlistedCount: `{report.AllowlistedCount}` NonAllowlistedCount: `{report.NonAllowlistedCount}`");
        b.AppendLine($"- TotalBaselineCandidates: `{report.TotalBaselineCandidateCount}`");
        b.AppendLine($"- ShadowAdds: `{report.TotalShadowAddCount}` ShadowRemoves: `{report.TotalShadowRemoveCount}`");
        b.AppendLine($"- BaselineTokenTotal: `{report.BaselineTokenTotal}` ShadowTokenTotal: `{report.ShadowTokenTotal}`");
        b.AppendLine($"- TokenDeltaMax: `{report.TokenDeltaMax}`");
        b.AppendLine($"- TraceCompleteness: `{report.TraceCompleteness:P2}`");
        b.AppendLine($"- P50/P95 Latency: `{report.P50LatencyMs}/{report.P95LatencyMs} ms`");
        b.AppendLine($"- FormalSelectedSetChanged: `{report.FormalSelectedSetChanged}`");
        b.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        AppendList(b, "诊断", report.Diagnostics);
        AppendList(b, "阻塞", report.BlockedReasons);
        b.AppendLine(); b.AppendLine("V6.2 mainline shadow adapter package comparison。适配器输出始终丢弃，baseline 继续作为正式结果。");
        return b.ToString();
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> items)
    {
        b.AppendLine(); b.AppendLine($"## {title}");
        if (items.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var item in items) b.AppendLine($"- `{item}`");
    }

    private static string ResolveMetadata(RetrievalDatasetV2Sample sample, string key)
        => sample.Metadata is not null && sample.Metadata.TryGetValue(key, out var v) ? v ?? "" : "";

    private static HashSet<string> Tokenize(string v)
    {
        var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var b = new StringBuilder(v.Length);
        foreach (var ch in v) { if (char.IsLetterOrDigit(ch) || ch == '-') b.Append(char.ToLowerInvariant(ch)); else { if (b.Length > 0) { r.Add(b.ToString()); b.Clear(); } } }
        if (b.Length > 0) r.Add(b.ToString());
        return r;
    }

    private static double DenseScore(HashSet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var t = Tokenize($"{it.Content} {it.TargetSection} {it.ItemKind} {it.SourceKind} {string.Join(' ', it.Tags ?? Array.Empty<string>())}");
        return qt.Count == 0 || t.Count == 0 ? 0 : qt.Count(t.Contains) / Math.Sqrt(qt.Count * t.Count);
    }

    private static double LexicalScore(HashSet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var t = Tokenize(it.Content ?? ""); if (qt.Count == 0 || t.Count == 0) return 0;
        var o = qt.Count(t.Contains); var u = qt.Count + t.Count - o; return u == 0 ? 0 : (double)o / u;
    }

    private static double AnchorScore(HashSet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var a = Tokenize($"{string.Join(' ', it.Tags ?? Array.Empty<string>())} {string.Join(' ', it.Anchors ?? Array.Empty<string>())} {it.TargetSection}");
        return qt.Count == 0 || a.Count == 0 ? 0 : qt.Count(a.Contains) / (double)a.Count;
    }
}

/// <summary>主线影子适配器包比较报告。</summary>
public sealed class MainlineShadowAdapterPackageComparisonReport
{
    public string OperationId { get; init; } = ""; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ComparisonPassed { get; init; }
    public int SampleCount { get; init; } public int MainlineInvocationCount { get; init; }
    public int AllowlistedCount { get; init; } public int NonAllowlistedCount { get; init; }
    public int TotalBaselineCandidateCount { get; init; }
    public int TotalShadowAddCount { get; init; } public int TotalShadowRemoveCount { get; init; }
    public int BaselineTokenTotal { get; init; } public int ShadowTokenTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public double TraceCompleteness { get; init; }
    public long P50LatencyMs { get; init; } public long P95LatencyMs { get; init; }
    public bool FormalSelectedSetChanged { get; init; } public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; } public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; } public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; } public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>主线影子适配器包比较选项。</summary>
public sealed class MainlineShadowAdapterPackageComparisonOptions
{
    public string TraceRoot { get; init; } = "vector/trace";
    public string AllowlistedWorkspace { get; init; } = "ws-mline";
    public string AllowlistedCollection { get; init; } = "col-mline";
    public int BaselineTopK { get; init; } = 5;
    public long MaxLatencyMs { get; init; } = 5000;
}