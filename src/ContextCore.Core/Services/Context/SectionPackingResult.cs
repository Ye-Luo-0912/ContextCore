namespace ContextCore.Core;

/// <summary>
/// 单个候选的格式化文本段，携带候选 ID 与对应文本，供 Packer 按 segment 粒度截断并精确归属。
/// </summary>
internal sealed record CandidateSegment(string CandidateId, string FormattedText);

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
        string? partiallyAcceptedCandidateId)
    {
        Added = added;
        Reason = reason;
        ActualTokens = actualTokens;
        Truncated = truncated;
        AcceptedCandidateIds = acceptedCandidateIds;
        RejectedCandidateIds = rejectedCandidateIds;
        PartiallyAcceptedCandidateId = partiallyAcceptedCandidateId;
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

    /// <summary>创建已加入的 SectionPackingResult。</summary>
    public static SectionPackingResult Selected(
        string reason,
        int actualTokens,
        bool truncated,
        IReadOnlyList<string> acceptedCandidateIds,
        IReadOnlyList<string> rejectedCandidateIds,
        string? partiallyAcceptedCandidateId = null)
    {
        return new SectionPackingResult(
            added: true,
            reason,
            actualTokens,
            truncated,
            acceptedCandidateIds,
            rejectedCandidateIds,
            partiallyAcceptedCandidateId);
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
            partiallyAcceptedCandidateId: null);
    }
}
