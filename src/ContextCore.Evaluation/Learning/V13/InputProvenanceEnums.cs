namespace ContextCore.Core.Services.Learning.V13;

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
    Synthetic = 4,
    Derived = 5
}

public enum LabelStatusKind : byte
{
    Unknown = 0,
    Unlabeled = 1,
    WeakSignal = 2,
    SelfEvaluated = 3,
    UserPreferred = 4,
    EvidenceConfirmed = 5,
    Rejected = 6
}

public enum LearningDataKind : byte
{
    Unknown = 0,
    InputEnvelope = 1,
    Document = 2,
    DocumentChunk = 3,
    RuntimeTrace = 4,
    RankingPair = 5,
    HardNegative = 6,
    RouterExample = 7,
    ShadowEvalRow = 8,
    GateDecision = 9,
    PromotionDecision = 10,
    FeedbackSignal = 11
}

[Flags]
public enum DataUsageFlags : ushort
{
    None = 0,
    Gate = 1,
    Training = 2,
    Eval = 4,
    Audit = 8,
    Runtime = 16,
    Retrieval = 32,
    Packaging = 64
}
