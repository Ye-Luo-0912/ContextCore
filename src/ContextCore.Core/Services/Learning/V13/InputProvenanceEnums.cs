namespace ContextCore.Core.Services.Learning.V13;

// Byte-backed enums for stable classification
public enum InputSourceKind : byte
{
    Unknown = 0,
    User = 1,
    Llm = 2,
    Tool = 3,
    Web = 4,
    File = 5,
    Runtime = 6,
    System = 7
}

public enum InputActorKind : byte
{
    Unknown = 0,
    User = 1,
    Assistant = 2,
    Tool = 3,
    Web = 4,
    Runtime = 5,
    System = 6
}

public enum DataAuthorityKind : byte
{
    Unknown = 0,
    Authoritative = 1,
    Shadow = 2,
    Diagnostic = 3,
    Synthetic = 4
}

public enum LabelStatusKind : byte
{
    Unknown = 0,
    Unlabeled = 1,
    WeakLabel = 2,
    HumanApproved = 3,
    Rejected = 4
}

public enum LearningDataKind : byte
{
    Unknown = 0,
    RuntimeTrace = 1,
    RankingPair = 2,
    HardNegative = 3,
    RouterExample = 4,
    ShadowEvalRow = 5,
    GateDecision = 6,
    PromotionDecision = 7,
    DocumentChunk = 8
}

[Flags]
public enum DataUsageFlags : ushort
{
    None = 0,
    Gate = 1,
    Training = 2,
    Eval = 4,
    Audit = 8,
    Runtime = 16
}
