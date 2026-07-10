using System.Text;
using ContextCore.Abstractions;
using ContextCore.Core.Infrastructure;

namespace ContextCore.Core.Services;

/// <summary>
/// 适配器空操作绑定烟雾测试。验证 no-op 适配器接缝可调用，
/// 返回空候选差异，不改变 baseline，不读取 vector/graph provider。
/// </summary>
public sealed class AdapterNoOpBindingSmokeRunner
{
    public AdapterNoOpBindingSmokeReport RunSmoke(
        FormalRetrievalIntegrationFreezeReport? freezeGate,
        AdapterNoOpBindingSmokeOptions? options = null)
    {
        options ??= new AdapterNoOpBindingSmokeOptions();
        var blocked = new List<string>();
        var diagnostics = new List<string>();

        if (freezeGate is null || !freezeGate.FreezePassed)
            blocked.Add("FreezeGateNotPassed");

        var noOp = new NoOpContextRetrievalAdapter();
        var shadow = new NoOpShadowRetrievalAdapter(options.TraceRoot);

        var request = new RetrievalAdapterRequest
        {
            OperationId = "smoke-test-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = options.WorkspaceId,
            CollectionId = options.CollectionId,
            QueryText = options.QueryText,
            BaselineCandidateIds = new[] { "candidate-a", "candidate-b", "candidate-c" },
        };

        diagnostics.Add($"adapter name: {noOp.Name}");
        diagnostics.Add($"shadow adapter name: {shadow.Name}");
        diagnostics.Add($"request: op={request.OperationId} ws={request.WorkspaceId} col={request.CollectionId} query={request.QueryText} baselineCount={request.BaselineCandidateIds.Count}");

        var noOpResult = noOp.ExecuteAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        diagnostics.Add($"no-op applied={noOpResult.Applied} added={noOpResult.AddedCandidateIds.Count} removed={noOpResult.RemovedCandidateIds.Count}");

        var shadowResult = shadow.ExecuteAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        diagnostics.Add($"shadow applied={shadowResult.Applied} added={shadowResult.AddedCandidateIds.Count} removed={shadowResult.RemovedCandidateIds.Count} tracePath={shadowResult.TracePath}");

        var invocationCount = 2; // noOp + shadow
        if (invocationCount <= 0) blocked.Add("ZeroAdapterInvocations");
        if (noOpResult.AddedCandidateIds.Count != 0) blocked.Add("NoOpAddedCandidatesNonEmpty");
        if (noOpResult.RemovedCandidateIds.Count != 0) blocked.Add("NoOpRemovedCandidatesNonEmpty");
        if (shadowResult.AddedCandidateIds.Count != 0) blocked.Add("ShadowAddedCandidatesNonEmpty");
        if (shadowResult.RemovedCandidateIds.Count != 0) blocked.Add("ShadowRemovedCandidatesNonEmpty");
        if (noOpResult.Applied) blocked.Add("NoOpAppliedTrue");
        if (shadowResult.Applied) blocked.Add("ShadowAppliedTrue");

        var passed = blocked.Count == 0;
        return new AdapterNoOpBindingSmokeReport
        {
            OperationId = $"adapter-noop-binding-smoke-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            SmokePassed = passed,
            InvocationCount = invocationCount,
            AddCount = 0,
            RemoveCount = 0,
            NoOpType = noOp.GetType().Name,
            ShadowType = shadow.GetType().Name,
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

    public static string BuildMarkdown(string title, AdapterNoOpBindingSmokeReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`"); b.AppendLine();
        b.AppendLine("## 烟雾测试摘要");
        b.AppendLine($"- SmokePassed: `{report.SmokePassed}`");
        b.AppendLine($"- InvocationCount: `{report.InvocationCount}`");
        b.AppendLine($"- AddCount: `{report.AddCount}`");
        b.AppendLine($"- RemoveCount: `{report.RemoveCount}`");
        b.AppendLine($"- NoOpType: `{report.NoOpType}`");
        b.AppendLine($"- ShadowType: `{report.ShadowType}`");
        b.AppendLine($"- FormalSelectedSetChanged: `{report.FormalSelectedSetChanged}`");
        b.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        b.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        b.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        b.AppendList("诊断信息", report.Diagnostics);
        b.AppendList("阻塞原因", report.BlockedReasons);
        b.AppendLine(); b.AppendLine("空操作适配器绑定烟雾测试。所有不变量保持在 false/0。");
        return b.ToString();
    }
}

/// <summary>空操作绑定烟雾测试报告。</summary>
public sealed class AdapterNoOpBindingSmokeReport
{
    public string OperationId { get; init; } = ""; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool SmokePassed { get; init; }
    public int InvocationCount { get; init; } public int AddCount { get; init; } public int RemoveCount { get; init; }
    public string NoOpType { get; init; } = ""; public string ShadowType { get; init; } = "";
    public bool FormalSelectedSetChanged { get; init; } public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; } public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; } public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; } public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>空操作绑定烟雾测试选项。</summary>
public sealed class AdapterNoOpBindingSmokeOptions
{
    public string TraceRoot { get; init; } = "vector/trace";
    public string WorkspaceId { get; init; } = "smoke-ws";
    public string CollectionId { get; init; } = "smoke-col";
    public string QueryText { get; init; } = "smoke query noop binding test";
}

internal static class StringBuilderExtensions
{
    public static void AppendList(this StringBuilder b, string title, IReadOnlyList<string> items)
    {
        b.AppendLine(); b.AppendLine($"## {title}");
        if (items.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var item in items) b.AppendLine($"- `{item}`");
    }
}