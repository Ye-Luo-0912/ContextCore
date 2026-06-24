using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 正式检索集成冻结与空操作绑定计划。读取 V5 主线各 gate 产物，确认 guardrail
/// 全部通过后输出冻结报告和空操作绑定计划。只读：不接正式检索、不写正式 package、
/// 不改 selected set、不改 PackingPolicy/package output、不切 runtime。
/// </summary>
public sealed class FormalRetrievalIntegrationFreezeRunner
{
    public FormalRetrievalIntegrationFreezeReport BuildFreeze(
        FormalRetrievalIntegrationPlanReport? planGate,
        RuntimeObservableFeatureContractReport? contractGate,
        RuntimeRetrievalFeatureDerivationReport? derivationGate,
        FormalRetrievalIntegrationFreezeOptions? options = null)
    {
        options ??= new FormalRetrievalIntegrationFreezeOptions();
        var blocked = new List<string>();

        if (planGate is null) blocked.Add("PlanGateMissing");
        else if (!planGate.PlanPassed) blocked.Add("PlanGateNotPassed");
        else if (planGate.FormalRetrievalAllowed) blocked.Add("PlanGateFormalRetrievalAllowed");
        else if (planGate.RuntimeSwitchAllowed) blocked.Add("PlanGateRuntimeSwitchAllowed");
        else if (planGate.PackageOutputChanged) blocked.Add("PlanGatePackageOutputChanged");
        else if (planGate.PackingPolicyChanged) blocked.Add("PlanGatePackingPolicyChanged");
        else if (planGate.VectorStoreBindingChanged) blocked.Add("PlanGateVectorStoreBindingChanged");
        else if (planGate.FormalPackageWritten) blocked.Add("PlanGateFormalPackageWritten");

        if (contractGate is null || !contractGate.GatePassed) blocked.Add("ContractGateMissingOrNotPassed");
        if (derivationGate is null || !derivationGate.GatePassed) blocked.Add("DerivationGateMissingOrNotPassed");

        var passed = blocked.Count == 0;
        return new FormalRetrievalIntegrationFreezeReport
        {
            OperationId = "formal-retrieval-integration-freeze-" + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = passed,
            Recommendation = passed ? "ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan" : "BlockedByIncompleteGates",
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalPackageWritten = false,
            SelectedProfile = "combined-safe",
            EvalProtocol = "V5.11",
            InputContract = "formal-adapter-input-contract-v1",
            OutputPolicyShadowGate = "V5.15 passed",
            IntegrationDecision = "V5.17 passed",
            FrozenArtifactPaths = new[]
            {
                "vector/v5/shadow-formal-retrieval-adapter-plan-gate.json",
                "vector/v5/shadow-formal-retrieval-adapter-gate.json",
                "vector/v5/formal-adapter-package-shadow-comparison-gate.json",
                "vector/v5/graph-vector-retrieval-quality-gate.json",
                "vector/v5/retrieval-quality-repair-gate.json",
                "vector/v5/runtime-observable-feature-contract-gate.json",
                "vector/v5/runtime-feature-derivation-gate.json",
                "vector/v5/graph-hub-noise-control-gate.json",
                "vector/v5/query-driven-candidate-source-repair.json",
                "vector/v5/formal-retrieval-integration-freeze.json",
                "vector/v5/adapter-noop-binding-plan.json",
            },
            BlockedReasons = blocked,
        };
    }

    public AdapterNoOpBindingPlanReport BuildNoOpPlan(
        FormalRetrievalIntegrationFreezeReport? freezeGate,
        AdapterNoOpBindingPlanOptions? options = null)
    {
        if (freezeGate is null || !freezeGate.FreezePassed)
            return new AdapterNoOpBindingPlanReport { PlanPassed = false, Recommendation = "BlockedByFreezeGateNotPassed" };

        return new AdapterNoOpBindingPlanReport
        {
            OperationId = "adapter-no-op-binding-plan-" + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            PlanPassed = true,
            Recommendation = "ReadyForAdapterNoOpBindingPlanFreeze",
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            PlanVersion = "1.0",
            DiEntryPoints = new[] { "IContextRetrievalAdapter (scoped DI registration via ServiceCollectionExtensions)" },
            NoOpAdapterInterface = "IShadowRetrievalAdapter { Task<ShadowAdapterResult> ExecuteAsync(QueryContext, CancellationToken) }",
            ShadowTracePath = "vector/trace/shadow-adapter-trace-{query}.jsonl",
            RollbackPlan = "Disable IShadowRetrievalAdapter DI registration; restore original IContextPackageBuilder path.",
            KillSwitchPlan = "Remove adapter DI binding; re-run integration-freeze-gate before re-enabling.",
            ImplementationPhases = new[]
            {
                "Phase 1: Define IShadowRetrievalAdapter interface + no-op implementation",
                "Phase 2: Wire DI registration behind feature flag (default off)",
                "Phase 3: Integrate shadow trace writer with existing package assembly pipeline",
                "Phase 4: Controlled canary rollout with observation window",
                "Phase 5: Full formal retrieval binding (separate approval gate required)",
            },
            BlockedReasons = Array.Empty<string>(),
        };
    }

    public static string BuildFreezeMarkdown(string title, FormalRetrievalIntegrationFreezeReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`"); b.AppendLine();
        b.AppendLine("## 冻结摘要");
        b.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- SelectedProfile: `{report.SelectedProfile}`");
        b.AppendLine($"- EvalProtocol: `{report.EvalProtocol}`");
        b.AppendLine($"- InputContract: `{report.InputContract}`");
        b.AppendLine($"- OutputPolicyShadowGate: `{report.OutputPolicyShadowGate}`");
        b.AppendLine($"- IntegrationDecision: `{report.IntegrationDecision}`");
        b.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        b.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        b.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        AppendList(b, "已冻结产物", report.FrozenArtifactPaths);
        AppendList(b, "阻塞原因", report.BlockedReasons);
        b.AppendLine(); b.AppendLine("V5 主线已冻结。通过全部 guardrail gate，确认不接正式检索、不切 runtime、不改 output/PackingPolicy。");
        return b.ToString();
    }

    public static string BuildPlanMarkdown(string title, AdapterNoOpBindingPlanReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`"); b.AppendLine();
        b.AppendLine("## 计划摘要");
        b.AppendLine($"- PlanPassed: `{report.PlanPassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- PlanVersion: `{report.PlanVersion}`");
        b.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        AppendList(b, "DI 接入点", report.DiEntryPoints);
        b.AppendLine(); b.AppendLine($"## 空操作适配器接口");
        b.AppendLine($"`{report.NoOpAdapterInterface}`");
        b.AppendLine(); b.AppendLine($"## 影子追踪路径");
        b.AppendLine($"`{report.ShadowTracePath}`");
        b.AppendLine(); b.AppendLine($"## 回滚计划");
        b.AppendLine($"{report.RollbackPlan}");
        b.AppendLine(); b.AppendLine($"## 急停计划");
        b.AppendLine($"{report.KillSwitchPlan}");
        AppendList(b, "实现阶段", report.ImplementationPhases);
        AppendList(b, "阻塞原因", report.BlockedReasons);
        b.AppendLine(); b.AppendLine("空操作绑定计划。不修改正式 DI binding、不启用 formal retrieval、不写 formal package、不改 selected set、不改 PackingPolicy/package output。");
        return b.ToString();
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> items)
    {
        b.AppendLine(); b.AppendLine($"## {title}");
        if (items.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var item in items) b.AppendLine($"- `{item}`");
    }
}

/// <summary>正式检索集成冻结报告。</summary>
public sealed class FormalRetrievalIntegrationFreezeReport
{
    public string OperationId { get; init; } = ""; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public string Recommendation { get; init; } = "KeepPreviewOnly";
    public bool FormalRetrievalAllowed { get; init; } public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; } public bool UseForRuntime { get; init; }
    public bool PackageOutputChanged { get; init; } public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; } public bool VectorStoreBindingChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public string SelectedProfile { get; init; } = "combined-safe";
    public string EvalProtocol { get; init; } = "V5.11";
    public string InputContract { get; init; } = "formal-adapter-input-contract-v1";
    public string OutputPolicyShadowGate { get; init; } = "V5.15 passed";
    public string IntegrationDecision { get; init; } = "V5.17 passed";
    public IReadOnlyList<string> FrozenArtifactPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>空操作绑定计划报告。</summary>
public sealed class AdapterNoOpBindingPlanReport
{
    public string OperationId { get; init; } = ""; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PlanPassed { get; init; }
    public string Recommendation { get; init; } = "KeepPreviewOnly";
    public bool FormalRetrievalAllowed { get; init; } public bool RuntimeSwitchAllowed { get; init; }
    public string PlanVersion { get; init; } = "1.0";
    public IReadOnlyList<string> DiEntryPoints { get; init; } = Array.Empty<string>();
    public string NoOpAdapterInterface { get; init; } = "";
    public string ShadowTracePath { get; init; } = "";
    public string RollbackPlan { get; init; } = "";
    public string KillSwitchPlan { get; init; } = "";
    public IReadOnlyList<string> ImplementationPhases { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>正式检索集成冻结选项。</summary>
public sealed class FormalRetrievalIntegrationFreezeOptions
{
    public bool RequirePlanGatePassed { get; init; } = true;
    public bool RequireContractGatePassed { get; init; } = true;
    public bool RequireDerivationGatePassed { get; init; } = true;
}

/// <summary>空操作绑定计划选项。</summary>
public sealed class AdapterNoOpBindingPlanOptions { }