namespace ContextCore.Core.Services.Learning.V13;

public sealed class InputEnvelopeContract
{
    public string InputId { get; init; } = "";
    public InputSourceKind SourceKind { get; init; }
    public InputActorKind ActorKind { get; init; }
    public DataAuthorityKind AuthorityKind { get; init; }
    public LabelStatusKind LabelStatus { get; init; }
    public LearningDataKind DataKind { get; init; }
    public DataUsageFlags UsageFlags { get; init; }
    public string SourcePath { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public long ByteSize { get; init; }
    public DateTimeOffset IngestedAt { get; init; }
    public string Provenance { get; init; } = "";
}

public sealed class DocumentLineageContract
{
    public string DocumentId { get; init; } = "";
    public string DocumentVersionId { get; init; } = "";
    public string RootInputId { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public InputSourceKind SourceKind { get; init; }
    public DataAuthorityKind AuthorityKind { get; init; }
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
    public string ChunkHash { get; init; } = "";
    public int CharStart { get; init; }
    public int CharEnd { get; init; }
    public int LineStart { get; init; }
    public int LineEnd { get; init; }
    public string TransformVersion { get; init; } = "";
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
    public int SyntheticRecordCount { get; init; }
    public int DiagnosticRecordCount { get; init; }
    public int AuthoritativeRecordCount { get; init; }
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
    public int SyntheticGateLeakage { get; init; }
    public int DiagnosticTrainingLeakage { get; init; }
    public bool InputProvenanceContractReady { get; init; }
    public bool ChunkLineageContractReady { get; init; }
    public bool DatasetInventoryReady { get; init; }
    public bool StableEnumsReady { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<LearningDatasetInventoryItem> Inventory { get; init; } = Array.Empty<LearningDatasetInventoryItem>();
    public IReadOnlyList<DocumentLineageContract> DocumentLineages { get; init; } = Array.Empty<DocumentLineageContract>();
}
