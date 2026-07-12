using ContextCore.Abstractions.Models;
using ContextCore.Evaluation.Models;

namespace ContextCore.Evaluation.Learning;

/// <summary>
/// 静态能力注册表。列出所有受审计的能力及其期望状态、gate artifact 路径。
/// ProjectStateAuditRunner 通过此注册表驱动 Capability Readiness Matrix。
/// </summary>
internal static class CapabilityRegistry
{
    /// <summary>
    /// 能力描述符。每条记录对应矩阵中的一个能力条目。
    /// </summary>
    /// <param name="CapabilityId">唯一能力标识，用于跨报告引用。</param>
    /// <param name="Area">能力所属领域 (Graph/Vector/Input/Output/Learning/Architecture/Decision/Storage/Service/Foundation/Router/Reranker)。</param>
    /// <param name="CapabilityKind">能力类别 (Architecture/Runtime/Storage/Evaluation/Decision/Graph/Output/Foundation)。</param>
    /// <param name="Phase">引入此能力的阶段编号 (V5/P3/GRAPH-01 等)。</param>
    /// <param name="SourceReportPath">gate artifact 相对路径；null 表示结构性能力（无 gate 报告，按 ExpectedStatus 直接采信）。</param>
    /// <param name="ExpectedStatus">报告通过后期望的状态 (Frozen/Ready/PreviewOnly/PlanOnly)。</param>
    internal sealed record CapabilityDescriptor(
        string CapabilityId,
        string Area,
        string CapabilityKind,
        string Phase,
        string? SourceReportPath,
        string ExpectedStatus);

    /// <summary>
    /// 全部受审计能力列表。包含 P3/P4 后的新结构能力，移除已删除的 Vector V4 条目。
    /// </summary>
    internal static IReadOnlyList<CapabilityDescriptor> Capabilities { get; } = new[]
    {
        // — Foundation (frozen) —
        new CapabilityDescriptor("Foundation", "Foundation", "Foundation", "V17", "foundation/foundation-release-candidate-gate.json", ProjectStateAuditStatuses.Frozen),
        new CapabilityDescriptor("ServiceFoundation", "Service", "Runtime", "V17", "service/service-foundation-freeze-gate.json", ProjectStateAuditStatuses.Frozen),
        new CapabilityDescriptor("StorageFoundation", "Storage", "Storage", "V17", "foundation/foundation-freeze-report.json", ProjectStateAuditStatuses.Frozen),

        // — Postgres provider series —
        new CapabilityDescriptor("RelationGovernancePostgres", "Graph", "Storage", "P3", "storage/postgres/postgres-relation-governance-readiness-gate.json", ProjectStateAuditStatuses.Ready),
        new CapabilityDescriptor("LearningFeedbackPostgres", "Learning", "Storage", "P3", "storage/postgres/postgres-learning-feedback-freeze-gate.json", ProjectStateAuditStatuses.Ready),
        new CapabilityDescriptor("JobQueuePostgres", "Storage", "Storage", "P3", "storage/postgres/postgres-job-queue-freeze-gate.json", ProjectStateAuditStatuses.Ready),
        new CapabilityDescriptor("VectorPostgresProvider", "Vector", "Storage", "P3", "storage/postgres/postgres-vector-freeze-gate.json", ProjectStateAuditStatuses.PreviewOnly),
        new CapabilityDescriptor("ProviderRegistrationContract", "Storage", "Storage", "P3", null, ProjectStateAuditStatuses.Ready),

        // — Vector / formal retrieval —
        new CapabilityDescriptor("FormalRetrievalIntegrationPlan", "Vector", "Evaluation", "V5", "vector/v5/formal-retrieval-integration-plan-gate.json", ProjectStateAuditStatuses.PlanOnly),

        // — Router / Reranker —
        new CapabilityDescriptor("RouterGuardedOptIn", "Router", "Runtime", "V17", "learning/router/router-guarded-optin-readiness-gate.json", ProjectStateAuditStatuses.PreviewOnly),
        new CapabilityDescriptor("CandidateReranker", "Reranker", "Evaluation", "V17", "eval/vector-retrieval-shadow-readiness-gate.json", ProjectStateAuditStatuses.PreviewOnly),

        // — Learning / Input —
        new CapabilityDescriptor("RuntimeChangeGate", "Learning", "Runtime", "V17", "learning/readiness/learning-runtime-change-readiness-gate.json", ProjectStateAuditStatuses.Ready),
        new CapabilityDescriptor("InputDatasetV2", "Input", "Runtime", "V5", "vector/dataset-v2/generated/materialization-gate.json", ProjectStateAuditStatuses.Ready),

        // — Architecture consolidation (P3.1-P4) —
        new CapabilityDescriptor("ArchitectureConsolidation", "Architecture", "Architecture", "P3.1-P4", "eval/architecture-cleanup-plan.json", ProjectStateAuditStatuses.Ready),
        new CapabilityDescriptor("EvaluationExtraction", "Architecture", "Architecture", "P3", null, ProjectStateAuditStatuses.Frozen),
        new CapabilityDescriptor("HistoricalRunnerDeletion", "Architecture", "Architecture", "P4-A", null, ProjectStateAuditStatuses.Frozen),
        new CapabilityDescriptor("PackageBuilderDecomposition", "Architecture", "Architecture", "P4-C", null, ProjectStateAuditStatuses.Ready),

        // — Decision foundation (V17) —
        new CapabilityDescriptor("DecisionFoundation", "Decision", "Decision", "V17", null, ProjectStateAuditStatuses.Frozen),

        // — Graph projector / traversal engine —
        new CapabilityDescriptor("GraphProjector", "Graph", "Graph", "GRAPH-01", null, ProjectStateAuditStatuses.Ready),
        new CapabilityDescriptor("RelationTraversalEngine", "Graph", "Graph", "GRAPH-08", null, ProjectStateAuditStatuses.Ready),
    };
}
