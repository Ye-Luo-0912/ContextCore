using System.Text.Json;

namespace ContextCore.ControlRoom.Models;

/// <summary>
/// 轻量 snapshot 记录，替代强类型 EvalGateReportDtos 反序列化。
/// 仅包含 ControlRoomService.Storage 映射所需字段，由 JsonDocument.Parse 容错填充。
/// </summary>
public sealed record FormalRetrievalIntegrationDecisionSnapshot
{
    public bool DecisionPassed { get; init; }
    public bool GatePassed { get; init; }
    public bool ReadyForFormalRetrievalIntegrationFreeze { get; init; }
    public bool ReadyForAdapterNoOpBindingPlan { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string IntegrationDecision { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public int RiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed record RetrievalEvalProtocolGateSnapshot
{
    public bool GatePassed { get; init; }
    public bool TieBreakDeterministic { get; init; }
    public bool SourceNonDiscriminativeDetected { get; init; }
    public bool TemplateHomogeneityDetected { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string ProtocolVersion { get; init; } = string.Empty;
    public int VectorTopK { get; init; }
    public int MergedTopK { get; init; }
    public int FinalTopK { get; init; }
    public int HashOrderSensitivityCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed record CandidateSourceDiscriminabilityAuditSnapshot
{
    public int NonDiscriminativeSourceCount { get; init; }
    public double TemplateHomogeneityScore { get; init; }
    public double BaselineRecall { get; init; }
    public double MergedRecall { get; init; }
}

public sealed record InputMetadataEnrichmentPreviewSnapshot
{
    public bool PreviewPassed { get; init; }
    public bool GatePassed { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public int MetadataCoverageDelta { get; init; }
    public int IndependentNonDenseSourceCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public double BeforeRecall { get; init; }
    public double AfterRecall { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed record EnrichedCandidateSourceRepairRecheckSnapshot
{
    public bool RecheckPassed { get; init; }
    public bool GatePassed { get; init; }
    public bool QualityImproved { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public int MustHitBelowTopKDelta { get; init; }
    public int RiskAfterPolicy { get; init; }
    public double TrainDerivedRecallDelta { get; init; }
    public double HoldoutDerivedRecallDelta { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> QualityBlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed record SourceAwareRankingRepairSnapshot
{
    public bool ReportPassed { get; init; }
    public bool GatePassed { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string SelectedProfileId { get; init; } = string.Empty;
    public int DenseWinnerLostCount { get; init; }
    public int UniqueSourceRecoveryCount { get; init; }
    public int SourceNoiseCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public double TrainDevRecallDelta { get; init; }
    public double TestRecallDelta { get; init; }
    public double HoldoutRecallDelta { get; init; }
    public double BlindHoldoutRecallDelta { get; init; }
    public double FallbackRate { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed record OutputTokenPriorityShadowSnapshot
{
    public bool ShadowPassed { get; init; }
    public bool GatePassed { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public int TokenDeltaP95 { get; init; }
    public int TokenBudgetExceededCount { get; init; }
    public int PriorityInversionCount { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed record FormalAdapterInputContractSnapshot
{
    public bool ContractPassed { get; init; }
    public bool GatePassed { get; init; }
    public bool DatasetEvalFieldsBlocked { get; init; }
    public bool GoldLabelsBlocked { get; init; }
    public bool SampleMetadataBlocked { get; init; }
    public bool ShadowArtifactFieldsBlocked { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string ContractVersion { get; init; } = string.Empty;
    public int RuntimeInputFieldCount { get; init; }
    public int DeniedFieldCount { get; init; }
    public int ContractForbiddenPropertyCount { get; init; }
    public int FormalSourceForbiddenReadCount { get; init; }
    public int EvalOnlyForbiddenReadCount { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed record FormalRetrievalIntegrationFreezeSnapshot
{
    public bool FreezePassed { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string SelectedProfile { get; init; } = string.Empty;
    public int FrozenArtifactCount { get; init; }
}

/// <summary>
/// JsonDocument 容错读取辅助方法，供 ControlRoomService.Storage 的 TryLoad*Summary 方法使用。
/// 字段缺失或类型不匹配时返回默认值，不抛异常。
/// </summary>
internal static class VectorShadowQualitySnapshotReader
{
    public static bool GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True && v.GetBoolean();

    public static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    public static int GetInt32(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static double GetDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    public static IReadOnlyList<string> GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    list.Add(s);
                }
            }
        }

        return list;
    }

    public static string GetNestedString(JsonElement root, string parent, string name)
        => root.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    public static int GetNestedInt32(JsonElement root, string parent, string name)
        => root.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static int GetArrayLength(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
}
