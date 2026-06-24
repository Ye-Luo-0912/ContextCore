using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem;

/// <summary>集中生成 trace artifact descriptor，避免各 store 自行拼路径。</summary>
public sealed class TraceArtifactDescriptorFactory
{
    public ArtifactDescriptor Create(
        string workspaceId,
        string collectionId,
        ArtifactKind traceKind,
        string? operationId = null,
        string? dateShard = null,
        string? capabilityId = null,
        string? reportId = null,
        string extension = ".jsonl")
    {
        if (!IsTraceKind(traceKind))
        {
            throw new ArgumentException($"ArtifactKind {traceKind} 不是 trace artifact。", nameof(traceKind));
        }

        return new ArtifactDescriptor
        {
            Kind = traceKind,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CapabilityId = string.IsNullOrWhiteSpace(capabilityId) ? ResolveCapability(traceKind) : capabilityId,
            OperationId = operationId,
            ReportId = string.IsNullOrWhiteSpace(reportId) ? ResolveReportId(traceKind) : reportId,
            DateShard = string.IsNullOrWhiteSpace(dateShard)
                ? DateTimeOffset.UtcNow.ToString("yyyyMMdd")
                : dateShard,
            Extension = extension
        };
    }

    public ArtifactDescriptor CreateToolCall(
        string workspaceId,
        string collectionId,
        string operationId,
        string phase,
        string? dateShard = null)
        => Create(
            workspaceId,
            collectionId,
            ArtifactKind.TraceToolCall,
            operationId,
            dateShard,
            "tool-calls",
            phase,
            ".json");

    private static string ResolveCapability(ArtifactKind kind)
        => kind switch
        {
            ArtifactKind.TraceRetrieval => "retrieval",
            ArtifactKind.TracePlanning => "planning",
            ArtifactKind.TraceToolCall => "tool-calls",
            ArtifactKind.TraceRouterShadow => "router-shadow",
            ArtifactKind.TraceRankerShadow => "ranker-shadow",
            ArtifactKind.TraceVectorShadow => "vector-shadow",
            ArtifactKind.TraceGraphShadow => "graph-shadow",
            ArtifactKind.TraceRelationDualWrite => "relation-dual-write",
            ArtifactKind.TraceRelationShadowRead => "relation-shadow-read",
            ArtifactKind.TraceRelationProviderSwitch => "relation-provider-switch",
            ArtifactKind.TraceLearningFeedbackDualWrite => "learning-feedback-dual-write",
            ArtifactKind.TraceLearningFeedbackShadowRead => "learning-feedback-shadow-read",
            ArtifactKind.TraceLearningFeedbackProviderSwitch => "learning-feedback-provider-switch",
            ArtifactKind.TracePackageBuild => "package-build",
            ArtifactKind.TraceModelCall => "model-calls",
            ArtifactKind.TraceError => "errors",
            _ => "traces"
        };

    private static string ResolveReportId(ArtifactKind kind)
        => kind switch
        {
            ArtifactKind.TraceRetrieval => "retrieval-traces",
            ArtifactKind.TracePlanning => "planning-traces",
            ArtifactKind.TraceToolCall => "tool-call-trace",
            ArtifactKind.TraceRouterShadow => "router-shadow-traces",
            ArtifactKind.TraceRankerShadow => "ranker-shadow-traces",
            ArtifactKind.TraceVectorShadow => "vector-shadow-traces",
            ArtifactKind.TraceGraphShadow => "graph-shadow-traces",
            ArtifactKind.TraceRelationDualWrite => "relation-dual-write-traces",
            ArtifactKind.TraceRelationShadowRead => "relation-shadow-read-traces",
            ArtifactKind.TraceRelationProviderSwitch => "relation-provider-switch-traces",
            ArtifactKind.TraceLearningFeedbackDualWrite => "learning-feedback-dual-write-traces",
            ArtifactKind.TraceLearningFeedbackShadowRead => "learning-feedback-shadow-read-traces",
            ArtifactKind.TraceLearningFeedbackProviderSwitch => "learning-feedback-provider-switch-traces",
            ArtifactKind.TracePackageBuild => "package-build-traces",
            ArtifactKind.TraceModelCall => "model-call-traces",
            ArtifactKind.TraceError => "error-traces",
            _ => "traces"
        };

    private static bool IsTraceKind(ArtifactKind kind)
        => kind is ArtifactKind.TraceRetrieval
            or ArtifactKind.TracePlanning
            or ArtifactKind.TraceToolCall
            or ArtifactKind.TraceRouterShadow
            or ArtifactKind.TraceRankerShadow
            or ArtifactKind.TraceVectorShadow
            or ArtifactKind.TraceGraphShadow
            or ArtifactKind.TraceRelationDualWrite
            or ArtifactKind.TraceRelationShadowRead
            or ArtifactKind.TraceRelationProviderSwitch
            or ArtifactKind.TraceLearningFeedbackDualWrite
            or ArtifactKind.TraceLearningFeedbackShadowRead
            or ArtifactKind.TraceLearningFeedbackProviderSwitch
            or ArtifactKind.TracePackageBuild
            or ArtifactKind.TraceModelCall
            or ArtifactKind.TraceError;
}
