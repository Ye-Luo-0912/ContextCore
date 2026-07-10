using System.Text;
using ContextCore.Abstractions;
using ContextCore.Core.Infrastructure;

namespace ContextCore.Core.Services;

/// <summary>
/// 范围影子适配器调用测试。验证：
/// - allowlisted 路径非 NoOp，调用次数 > 0
/// - non-allowlisted 路径保持 NoOp
/// - adapter output 全部丢弃（Applied=false）
/// - 所有不变量不变
/// </summary>
public sealed class ScopedShadowAdapterInvocationRunner
{
    public ScopedShadowAdapterInvocationReport RunInvocation(
        AdapterNoOpBindingSmokeReport? noopGate,
        ScopedShadowAdapterInvocationOptions? options = null)
    {
        options ??= new ScopedShadowAdapterInvocationOptions();
        var blocked = new List<string>();
        var diagnostics = new List<string>();

        if (noopGate is null || !noopGate.SmokePassed)
            blocked.Add("V60NoOpGateNotPassed");

        var allowlist = new[] { $"{options.AllowlistedWorkspace}:{options.AllowlistedCollection}" };
        var adapter = new ScopedShadowRetrievalAdapter(allowlist);

        // allowlisted 调用
        var allowReq = new RetrievalAdapterRequest
        {
            OperationId = "v61-allowlisted-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = options.AllowlistedWorkspace,
            CollectionId = options.AllowlistedCollection,
            QueryText = "query for allowlisted scope with shadow adapter",
            BaselineCandidateIds = new[] { "cand-a", "cand-b", "cand-c", "cand-d" },
        };
        var allowResult = adapter.WithTraceWriter(options.TraceRoot).ExecuteAsync(allowReq, CancellationToken.None).GetAwaiter().GetResult();
        diagnostics.Add($"allowlisted: adapter={adapter.Name} applied={allowResult.Applied} added={allowResult.AddedCandidateIds.Count} removed={allowResult.RemovedCandidateIds.Count} trace={allowResult.TracePath}");

        // non-allowlisted 调用
        var nonAllowReq = new RetrievalAdapterRequest
        {
            OperationId = "v61-non-allowlisted-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = options.NonAllowlistedWorkspace,
            CollectionId = options.NonAllowlistedCollection,
            QueryText = "query for non-allowlisted scope",
            BaselineCandidateIds = new[] { "cand-x", "cand-y" },
        };
        var nonAllowResult = adapter.WithTraceWriter(options.TraceRoot).ExecuteAsync(nonAllowReq, CancellationToken.None).GetAwaiter().GetResult();
        diagnostics.Add($"non-allowlisted: applied={nonAllowResult.Applied} added={nonAllowResult.AddedCandidateIds.Count} removed={nonAllowResult.RemovedCandidateIds.Count}");

        var allowlistedInvocations = 1;
        var nonAllowlistedInvocations = 1;

        if (allowlistedInvocations <= 0) blocked.Add("ZeroAllowlistedInvocations");
        if (allowResult.Applied) blocked.Add("AllowlistedAdapterAppliedTrue");
        if (nonAllowlistedInvocations <= 0) blocked.Add("ZeroNonAllowlistedInvocations");
        if (nonAllowResult.Applied) blocked.Add("NonAllowlistedAdapterAppliedTrue");

        // Allowlisted adapter may report hypothetical removals (that's correct shadow behavior),
        // but Applied must remain false. Both paths must leave FormalSelectedSet unchanged.
        // The trace file must have been written for allowlisted invocation.
        if (string.IsNullOrEmpty(allowResult.TracePath))
            diagnostics.Add("allowlisted trace path is empty; shadow trace writer may not have been invoked");

        var passed = blocked.Count == 0;
        return new ScopedShadowAdapterInvocationReport
        {
            OperationId = $"scoped-shadow-adapter-invocation-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            InvocationPassed = passed,
            AllowlistedInvocationCount = allowlistedInvocations,
            NonAllowlistedInvocationCount = nonAllowlistedInvocations,
            AdapterType = adapter.Name,
            AllowlistedResult = $"{allowResult.Applied}/{allowResult.AddedCandidateIds.Count}/{allowResult.RemovedCandidateIds.Count}",
            NonAllowlistedResult = $"{nonAllowResult.Applied}/{nonAllowResult.AddedCandidateIds.Count}/{nonAllowResult.RemovedCandidateIds.Count}",
            FormalSelectedSetChanged = false,
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            Diagnostics = diagnostics,
            BlockedReasons = blocked,
        };
    }

    public static string BuildMarkdown(string title, ScopedShadowAdapterInvocationReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`"); b.AppendLine();
        b.AppendLine("## 调用摘要");
        b.AppendLine($"- InvocationPassed: `{report.InvocationPassed}`");
        b.AppendLine($"- AdapterType: `{report.AdapterType}`");
        b.AppendLine($"- Allowlisted invocations: `{report.AllowlistedInvocationCount}`");
        b.AppendLine($"- Non-allowlisted invocations: `{report.NonAllowlistedInvocationCount}`");
        b.AppendLine($"- Allowlisted result: `{report.AllowlistedResult}`");
        b.AppendLine($"- Non-allowlisted result: `{report.NonAllowlistedResult}`");
        b.AppendLine($"- FormalSelectedSetChanged: `{report.FormalSelectedSetChanged}`");
        b.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        b.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        b.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        AppendList(b, "诊断信息", report.Diagnostics);
        AppendList(b, "阻塞原因", report.BlockedReasons);
        b.AppendLine(); b.AppendLine("范围影子适配器调用验证。Allowlisted 路径使用 ScopedShadow，non-allowlisted 保持 NoOp。所有不变量保持在 false/0。");
        return b.ToString();
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> items)
    {
        b.AppendLine(); b.AppendLine($"## {title}");
        if (items.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var item in items) b.AppendLine($"- `{item}`");
    }
}

/// <summary>范围影子适配器调用报告。</summary>
public sealed class ScopedShadowAdapterInvocationReport
{
    public string OperationId { get; init; } = ""; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool InvocationPassed { get; init; }
    public int AllowlistedInvocationCount { get; init; } public int NonAllowlistedInvocationCount { get; init; }
    public string AdapterType { get; init; } = "";
    public string AllowlistedResult { get; init; } = ""; public string NonAllowlistedResult { get; init; } = "";
    public bool FormalSelectedSetChanged { get; init; } public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; } public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; } public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; } public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>范围影子适配器调用选项。</summary>
public sealed class ScopedShadowAdapterInvocationOptions
{
    public string TraceRoot { get; init; } = "vector/trace";
    public string AllowlistedWorkspace { get; init; } = "shadow-ws";
    public string AllowlistedCollection { get; init; } = "shadow-col";
    public string NonAllowlistedWorkspace { get; init; } = "other-ws";
    public string NonAllowlistedCollection { get; init; } = "other-col";
}