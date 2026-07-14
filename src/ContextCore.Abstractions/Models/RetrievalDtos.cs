namespace ContextCore.Abstractions.Models
{
    /// <summary>混合检索候选项来源类型。</summary>
    public enum ContextRetrievalCandidateKind
    {
        ContextItem,
        MemoryItem
    }

    /// <summary>混合检索请求。</summary>
    public sealed class ContextRetrievalRequest
    {
        public string OperationId { get; init; } = string.Empty;

        public string WorkspaceId { get; init; } = string.Empty;

        public string CollectionId { get; init; } = string.Empty;

        public string? QueryText { get; init; }

        public string? RewrittenQueryText { get; init; }

        public IReadOnlyList<string> RequiredTags { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> RequiredTypes { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> Refs { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> RequiredIds { get; init; } = Array.Empty<string>();

        public IReadOnlyList<float> QueryVector { get; init; } = Array.Empty<float>();

        public string? ModelName { get; init; }

        /// <summary>BGE 等检索模型的 query instruction；为空时不添加前缀。</summary>
        public string QueryInstruction { get; init; } = "为这个句子生成表示以用于检索相关文章：";

        public int TopK { get; init; } = 10;

        public int CandidateTake { get; init; } = 50;

        public int VectorTopK { get; init; } = 20;

        public double? MinVectorScore { get; init; }

        /// <summary>关系扩展允许经过的关系类型；为空表示不限制关系类型。</summary>
        public IReadOnlyList<string> AllowedRelationTypes { get; init; } = Array.Empty<string>();

        /// <summary>关系扩展最大跳数。默认 1 跳，运行时会做上限保护，避免图遍历失控。</summary>
        public int RelationExpansionDepth { get; init; } = 1;

        public int TokenBudget { get; init; } = 4000;

        public bool IncludeKeywordRecall { get; init; } = true;

        public bool IncludeVectorRecall { get; init; } = true;

        public bool IncludeRelationExpansion { get; init; } = true;

        public bool IncludeWorkingMemory { get; init; } = true;

        public bool IncludeStableMemory { get; init; } = true;

        public bool IncludeContent { get; init; } = true;

        public Dictionary<string, string> Metadata { get; init; } = new();

        /// <summary>
        /// 可选的短期锚定召回计划。存在时，HybridContextRetriever 将按计划调整召回优先级和过滤策略。
        /// 为 null 时保持原有行为不变（eval 兼容）。
        /// </summary>
        public RetrievalPlan? Plan { get; init; }
    }

    /// <summary>混合检索结果。</summary>
    public sealed class ContextRetrievalResult
    {
        public string OperationId { get; init; } = string.Empty;

        public bool Succeeded { get; init; } = true;

        public string? ErrorMessage { get; init; }

        public IReadOnlyList<ContextRetrievalCandidate> SelectedItems { get; init; } = Array.Empty<ContextRetrievalCandidate>();

        public IReadOnlyList<ContextRetrievalDecision> DroppedItems { get; init; } = Array.Empty<ContextRetrievalDecision>();

        public int EstimatedTokens { get; init; }

        public ContextOperationUsage Usage { get; init; } = new();

        public ContextRetrievalTrace Trace { get; init; } = new();

        public Dictionary<string, string> Metadata { get; init; } = new();

        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>检索候选项，统一承载原始上下文和记忆条目。</summary>
    public sealed class ContextRetrievalCandidate
    {
        public string CandidateId { get; init; } = string.Empty;

        public string SourceId { get; init; } = string.Empty;

        public ContextRetrievalCandidateKind Kind { get; init; }

        public string Type { get; init; } = string.Empty;

        public string? Title { get; init; }

        public string Content { get; init; } = string.Empty;

        public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

        public double Score { get; init; }

        public int EstimatedTokens { get; init; }

        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

        public Dictionary<string, string> Metadata { get; init; } = new();
    }

    /// <summary>检索候选项的选中或丢弃决策。</summary>
    public sealed class ContextRetrievalDecision
    {
        public string CandidateId { get; init; } = string.Empty;

        public string SourceId { get; init; } = string.Empty;

        public ContextRetrievalCandidateKind Kind { get; init; }

        public string Type { get; init; } = string.Empty;

        public string Reason { get; init; } = string.Empty;

        public double Score { get; init; }

        public int EstimatedTokens { get; init; }

        public Dictionary<string, string> Metadata { get; init; } = new();
    }

    /// <summary>检索流程中的一个阶段摘要。</summary>
    public sealed class ContextRetrievalStageTrace
    {
        public string Name { get; init; } = string.Empty;

        public int CandidateCount { get; init; }

        public Dictionary<string, string> Metadata { get; init; } = new();
    }

    /// <summary>完整检索 trace，记录候选、阶段和最终选择。</summary>
    public sealed class ContextRetrievalTrace
    {
        public string RetrievalId { get; init; } = string.Empty;

        public string WorkspaceId { get; init; } = string.Empty;

        public string CollectionId { get; init; } = string.Empty;

        public string? QueryText { get; init; }

        public string? RewrittenQueryText { get; init; }

        public IReadOnlyList<ContextRetrievalStageTrace> Stages { get; init; } = Array.Empty<ContextRetrievalStageTrace>();

        public IReadOnlyList<ContextRetrievalCandidate> Candidates { get; init; } = Array.Empty<ContextRetrievalCandidate>();

        public IReadOnlyList<ContextRetrievalDecision> SelectedItems { get; init; } = Array.Empty<ContextRetrievalDecision>();

        public IReadOnlyList<ContextRetrievalDecision> DroppedItems { get; init; } = Array.Empty<ContextRetrievalDecision>();

        public Dictionary<string, string> Metadata { get; init; } = new();

        public DateTimeOffset CreatedAt { get; init; }
    }
}
