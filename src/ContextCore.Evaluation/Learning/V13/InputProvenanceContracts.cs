namespace ContextCore.Core.Services.Learning.V13;

public sealed class InputEnvelopeContract
{
    public string InputId { get; init; } = "";
    public InputSourceKind SourceKind { get; init; }
    public InputActorKind ActorKind { get; init; }
    public string SourceRef { get; init; } = "";
    public string OperationId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string CollectionId { get; init; } = "";
    public DataAuthorityKind Authority { get; init; }
    public LabelStatusKind LabelStatus { get; init; }
    public DataUsageFlags UsageFlags { get; init; }
    public string ContentHash { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public Dictionary<string,string> Metadata { get; init; } = new();
    // Authority rules enforced at gate:
    // - LLM source → Derived or Diagnostic, never Authoritative by default
    // - Synthetic → no Gate usage
    // - Diagnostic/Derived → no Training unless self-evaluated or evidence confirmed
}

public sealed class DocumentLineageContract
{
    public string DocumentId { get; init; } = "";
    public string DocumentVersionId { get; init; } = "";
    public string RootInputId { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public InputSourceKind SourceKind { get; init; }
    public DataAuthorityKind Authority { get; init; }
    public long ByteSize { get; init; }
    public int LineCount { get; init; }
    public int ChunkCount { get; init; }
    public string TransformVersion { get; init; } = "";
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed class ChunkLineageContract
{
    public string ChunkId { get; init; } = "";
    public string DocumentId { get; init; } = "";
    public string DocumentVersionId { get; init; } = "";
    public string RootInputId { get; init; } = "";
    public string ParentChunkId { get; init; } = "";
    public string ParentNodeId { get; init; } = "";
    public string ChunkHash { get; init; } = "";
    public int CharStart { get; init; }
    public int CharEnd { get; init; }
    public int PageStart { get; init; }
    public int PageEnd { get; init; }
    public int LineStart { get; init; }
    public int LineEnd { get; init; }
    public string TransformVersion { get; init; } = "";
    public int ChunkIndex { get; init; }
}

public sealed class LearningDatasetInventoryItem
{
    public string DatasetPath { get; init; } = "";
    public LearningDataKind DataKind { get; init; }
    public InputSourceKind SourceKind { get; init; }
    public InputActorKind ActorKind { get; init; }
    public DataAuthorityKind AuthorityKind { get; init; }
    public LabelStatusKind LabelStatus { get; init; }
    public DataUsageFlags UsageFlags { get; init; }
    public int RecordCount { get; init; }
    public long TotalBytes { get; init; }
    public string SourceHash { get; init; } = "";
    public bool HasSourceKind => SourceKind != InputSourceKind.Unknown;
    public bool HasAuthority => AuthorityKind != DataAuthorityKind.Unknown;
    public bool HasUsageFlags => UsageFlags != DataUsageFlags.None;
    public bool CanUseForTraining => HasAuthority && HasUsageFlags && UsageFlags.HasFlag(DataUsageFlags.Training)
        && AuthorityKind != DataAuthorityKind.Synthetic && AuthorityKind != DataAuthorityKind.Diagnostic;
    public bool CanUseForEval => HasUsageFlags && UsageFlags.HasFlag(DataUsageFlags.Eval);
    public bool CanUseForGate => HasUsageFlags && UsageFlags.HasFlag(DataUsageFlags.Gate)
        && AuthorityKind != DataAuthorityKind.Synthetic;
    public int SyntheticRecordCount { get; init; }
    public int DiagnosticRecordCount { get; init; }
    public int AuthoritativeRecordCount { get; init; }
    public int DerivedRecordCount { get; init; }
    public int LlmSourcedCount { get; init; }
    public IReadOnlyList<string> RiskFlags { get; init; } = Array.Empty<string>();
}

public sealed class LearningDataQualityGateReport
{
    public string GeneratedAt { get; init; } = "";
    public bool GatePassed { get; init; }
    public int TotalDatasets { get; init; }
    public int TotalRecords { get; init; }
    public bool EveryDatasetHasSourceKind { get; init; }
    public bool EveryDatasetHasAuthority { get; init; }
    public bool EveryDatasetHasUsageFlags { get; init; }
    public bool EveryChunkCanTraceToDocument { get; init; }
    public bool ChunkLineageContractHasRootInput { get; init; }
    public int SyntheticGateLeakage { get; init; }
    public int DiagnosticTrainingLeakage { get; init; }
    public int LlmAuthoritativeLeakage { get; init; }
    public bool InputProvenanceContractReady { get; init; }
    public bool ChunkLineageContractReady { get; init; }
    public bool StableEnumContractReady { get; init; }
    public bool DatasetInventoryReady { get; init; }
    public bool RuntimePromotionApplied => false;
    public bool PackageOutputChanged => false;
    public bool VectorBindingChanged => false;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<LearningDatasetInventoryItem> Inventory { get; init; } = Array.Empty<LearningDatasetInventoryItem>();
    public IReadOnlyList<DocumentLineageContract> DocumentLineages { get; init; } = Array.Empty<DocumentLineageContract>();
}
