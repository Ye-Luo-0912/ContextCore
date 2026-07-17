namespace ContextCore.Core;

/// <summary>
/// 单个候选的格式化文本段，携带候选 ID、对应文本以及该候选的 SourceRefs/ItemRefs，
/// 供 Packer 按 segment 粒度截断并精确归属。
/// P0-6.2: 携带 SourceRefs/ItemRefs，使 Section refs 只从 accepted + partially accepted segments 聚合，
/// 避免被拒绝候选的 refs 仍写入 section。
/// </summary>
internal sealed record CandidateSegment(
    string CandidateId,
    string FormattedText,
    IReadOnlyList<string> SourceRefs,
    IReadOnlyList<string> ItemRefs)
{
    /// <summary>兼容旧调用：无 SourceRefs/ItemRefs 时构造空列表。</summary>
    internal CandidateSegment(string CandidateId, string FormattedText)
        : this(CandidateId, FormattedText, Array.Empty<string>(), Array.Empty<string>())
    {
    }
}

/// <summary>
/// Section 装配的结构化结果，记录精确的候选接受/拒绝状态。
/// 解除 <see cref="PackageTraceRecorder"/> 对 <see cref="BasicContextPackageBuilder"/> 嵌套类型的依赖，
/// 并替代基于字符串前缀的猜测判断（参见 AddSection 调用方）。
/// </summary>
internal sealed class SectionPackingResult
{
    private SectionPackingResult(
        bool added,
        string reason,
        int actualTokens,
        bool truncated,
        IReadOnlyList<string> acceptedCandidateIds,
        IReadOnlyList<string> rejectedCandidateIds,
        string? partiallyAcceptedCandidateId,
        int partiallyAcceptedIncludedTokens)
    {
        Added = added;
        Reason = reason;
        ActualTokens = actualTokens;
        Truncated = truncated;
        AcceptedCandidateIds = acceptedCandidateIds;
        RejectedCandidateIds = rejectedCandidateIds;
        PartiallyAcceptedCandidateId = partiallyAcceptedCandidateId;
        PartiallyAcceptedIncludedTokens = partiallyAcceptedIncludedTokens;
    }

    /// <summary>Section 是否被加入 package。</summary>
    public bool Added { get; }

    /// <summary>Section 被加入或拒绝的原因。</summary>
    public string Reason { get; }

    /// <summary>Section 实际消耗的 token 数；未加入则为 0。</summary>
    public int ActualTokens { get; }

    /// <summary>Section 内容是否因 token 预算被裁剪。</summary>
    public bool Truncated { get; }

    /// <summary>
    /// 被实际完整保留进 section 输出的候选 ID 列表（精确，非字符串猜测）。
    /// 未加入 section 时为空列表。
    /// </summary>
    public IReadOnlyList<string> AcceptedCandidateIds { get; }

    /// <summary>
    /// 因 token 预算截断而仅部分保留的候选 ID；无部分保留则为 null。
    /// </summary>
    public string? PartiallyAcceptedCandidateId { get; }

    /// <summary>
    /// 输入但未被保留进 section 输出的候选 ID 列表。
    /// 未加入 section 时为空列表。
    /// </summary>
    public IReadOnlyList<string> RejectedCandidateIds { get; }

    /// <summary>
    /// P0-6.3: PartiallyAcceptedCandidateId 实际保留进 section 输出的 token 数（截断后）。
    /// 无 partially accepted 候选时为 0。供 PackageTraceRecorder 写入 trace row 的 IncludedTokens 字段，
    /// 使下游诊断能观察到部分截断候选的精确保留量（而不仅是"被截断"布尔标志）。
    /// </summary>
    public int PartiallyAcceptedIncludedTokens { get; }

    /// <summary>创建已加入的 SectionPackingResult。</summary>
    public static SectionPackingResult Selected(
        string reason,
        int actualTokens,
        bool truncated,
        IReadOnlyList<string> acceptedCandidateIds,
        IReadOnlyList<string> rejectedCandidateIds,
        string? partiallyAcceptedCandidateId = null,
        int partiallyAcceptedIncludedTokens = 0)
    {
        return new SectionPackingResult(
            added: true,
            reason,
            actualTokens,
            truncated,
            acceptedCandidateIds,
            rejectedCandidateIds,
            partiallyAcceptedCandidateId,
            partiallyAcceptedIncludedTokens);
    }

    /// <summary>创建未加入的 SectionPackingResult（候选全部拒绝）。</summary>
    public static SectionPackingResult Dropped(string reason)
    {
        return new SectionPackingResult(
            added: false,
            reason,
            actualTokens: 0,
            truncated: false,
            acceptedCandidateIds: Array.Empty<string>(),
            rejectedCandidateIds: Array.Empty<string>(),
            partiallyAcceptedCandidateId: null,
            partiallyAcceptedIncludedTokens: 0);
    }
}
