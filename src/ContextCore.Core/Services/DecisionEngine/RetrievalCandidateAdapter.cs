using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// Retrieval 候选适配器（ContextRetrievalCandidate → ContextCandidateEnvelope）
//
// 目标：
// 让现有 HybridContextRetriever 路径产出的 ContextRetrievalCandidate 集合
// 可以转换为统一的 ContextCandidateEnvelope 集合，从而接入 的
// IContextDecisionEngine 编排路径。
//
// 设计原则：
// 1. 适配器是单向转换（ContextRetrievalCandidate → Envelope），不修改原候选。
// 2. 适配器是幂等的：相同输入产生相同输出。
// 3. 适配器不调用 Engine 或 Storage；纯内存转换。
// 4. 适配器保留所有原字段信息（CandidateId/SourceId/Kind/Score/EstimatedTokens/
// Reasons/SourceRefs/Metadata），并通过 envelope 的 Features/Safety/Utility
// 三个正交维度结构化表达。
// 5. 适配器不破坏现有 HybridContextRetriever.RetrieveAsync 主链；它只是
// 提供一个可选的 envelope 转换入口。
// 6. 修复：映射函数不再读取 DateTimeOffset.UtcNow。
// 时间戳由 CandidateAdaptationContext.ObservedAt 统一传入；
// ToDecisionRequest 填充 WorkspaceId / CollectionId / QueryText，
// 避免 PolicyRegistry 按空 workspace 解析默认 Bundle。
//
// 字段映射：
// ContextRetrievalCandidate.CandidateId → envelope.CandidateId（首选）
// 备选：SourceId（CandidateId 为空时）
// ContextRetrievalCandidate.Kind → envelope.Source（枚举映射）
// ContextRetrievalCandidate.Type → envelope.Type（直传）
// ContextRetrievalCandidate.Score → envelope.Utility.FinalScore + DeterministicScore
// ContextRetrievalCandidate.EstimatedTokens → envelope.EstimatedTokens
// ContextRetrievalCandidate.Reasons → envelope.Utility.ReasonCode（拼接）
// ContextRetrievalCandidate.SourceRefs → envelope.ProvenanceRefs（封装为 EvidenceRef）
// ContextRetrievalCandidate.Metadata["mandatory"] → envelope.Safety.IsMandatory
// ContextRetrievalCandidate.Metadata["lifecycleStatus"] → envelope.Safety.LifecycleState
//
// 反向投影（envelope → ContextRetrievalCandidate）由 RetrievalResultProjector 负责。
// ===========================================================================

/// <summary>
/// Retrieval 候选适配器。将 <see cref="ContextRetrievalCandidate"/>
/// 集合转换为 <see cref="ContextCandidateEnvelope"/> 集合，作为现有 Retrieval
/// 路径与统一 Engine 之间的桥梁。
/// </summary>
public static class RetrievalCandidateAdapter
{
    /// <summary>
    /// 将单个 <see cref="ContextRetrievalCandidate"/> 转换为
    /// <see cref="ContextCandidateEnvelope"/>，使用传入的 context 提供时间戳与作用域。
    /// </summary>
    /// <param name="candidate">原始候选。</param>
    /// <param name="context">适配上下文（提供 ObservedAt / WorkspaceId / CollectionId 等）。</param>
    public static ContextCandidateEnvelope ToEnvelope(
        ContextRetrievalCandidate candidate,
        CandidateAdaptationContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        var candidateId = string.IsNullOrWhiteSpace(candidate.CandidateId)
            ? candidate.SourceId
            : candidate.CandidateId;

        var source = ResolveCandidateSource(candidate);
        var isMandatory = IsMandatoryCandidate(candidate);
        var constraintLevel = ResolveConstraintLevel(candidate);
        var lifecycleState = ResolveLifecycleState(candidate);

        // 填充 CanonicalKey / Origins / ExpertContributions / PolicyReference
        var expertKind = MapSourceToExpertKind(source);
        var canonicalKey = CanonicalCandidateKey.Create(
            workspaceId: context.WorkspaceId,
            collectionId: context.CollectionId,
            entityKind: ResolveEntityKind(candidate, source),
            entityId: candidateId,
            entityVersion: ResolveEntityVersion(candidate));
        var origins = new List<ExpertOrigin>
        {
            new(expertKind, Contribution: 1.0, ObservedAt: context.ObservedAt)
        };
        var expertContributions = new Dictionary<ExpertKind, double> { [expertKind] = 1.0 };

        return new ContextCandidateEnvelope
        {
            CandidateId = candidateId,
            Source = source,
            Type = candidate.Type,
            TokenCost = new CandidateTokenCost
            {
                ContentTokens = candidate.EstimatedTokens,
                TokenizerId = "length-div-4",
                IsEstimated = true
            },
            WorkspaceId = context.WorkspaceId,
            CollectionId = context.CollectionId,
            CanonicalKey = canonicalKey,
            Origins = origins,
            ExpertContributions = expertContributions,
            PolicyReference = context.PolicyReference,
            Safety = new CandidateSafetyState
            {
                // 不再把 Constraint 来源一律视为 hard。
                // IsMandatory：metadata mandatory 标记 || ConstraintLevel is Hard or System。
                // IsHardConstraint：仅当 ConstraintLevel == Hard（与 PackageCandidateAdapter 对齐）。
                // soft_constraint（Soft）与 merged_constraint（Mixed）均不免预算。
                IsMandatory = isMandatory
                              || constraintLevel is ConstraintLevel.Hard
                                  or ConstraintLevel.System,
                ConstraintLevel = constraintLevel,
                IsHardConstraint = constraintLevel == ConstraintLevel.Hard,
                LifecycleState = lifecycleState,
                IsSuperseded = lifecycleState.Equals("superseded", StringComparison.OrdinalIgnoreCase),
                PassesSafetyGate = true // 默认通过；具体拦截由 Engine 阶段决定
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = candidate.Score,
                FinalScore = candidate.Score,
                ModelConfidence = 0.0, // Retrieval 路径默认 deterministic-only
                ReasonCode = ResolveReasonCode(candidate)
            },
            Features = new CandidateFeatureVector
            {
                ChannelSources = ResolveChannelSources(candidate)
            },
            // 使用 context.ObservedAt 而非 DateTimeOffset.UtcNow
            ProvenanceRefs = candidate.SourceRefs
                .Where(r => !string.IsNullOrEmpty(r))
                .Select(r => new EvidenceRef
                {
                    RefId = r,
                    RefType = "retrieval-source-ref",
                    WorkspaceId = string.IsNullOrEmpty(context.WorkspaceId) ? null : context.WorkspaceId,
                    CollectionId = string.IsNullOrEmpty(context.CollectionId) ? null : context.CollectionId,
                    GeneratedAt = context.ObservedAt,
                    ContentFingerprint = context.PolicySnapshot is null
                        ? null
                        : $"{context.PolicySnapshot.BundleId}@{context.PolicySnapshot.Version}"
                })
                .ToList()
        };
    }

    /// <summary>
    /// 将 <see cref="ContextRetrievalCandidate"/> 集合批量转换为
    /// <see cref="ContextCandidateEnvelope"/> 集合，使用传入的 context。
    /// </summary>
    public static IReadOnlyList<ContextCandidateEnvelope> ToEnvelopes(
        IEnumerable<ContextRetrievalCandidate> candidates,
        CandidateAdaptationContext context)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);
        return candidates.Select(c => ToEnvelope(c, context)).ToList();
    }

    /// <summary>
    /// 将 <see cref="ContextRetrievalResult"/> 整体转换为
    /// <see cref="ContextDecisionRequest"/>，并填充 workspace/collection/query 作用域。
    /// </summary>
    /// <param name="result">Retrieval 主链产出的结果。</param>
    /// <param name="tokenBudget">token 预算上限。</param>
    /// <param name="topK">TopK 上限。</param>
    /// <param name="enableModel">是否启用模型评分。</param>
    /// <param name="context">适配上下文（提供 WorkspaceId/CollectionId/QueryText/ObservedAt）。</param>
    public static ContextDecisionRequest ToDecisionRequest(
        ContextRetrievalResult result,
        int tokenBudget,
        int topK,
        bool enableModel,
        CandidateAdaptationContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        var selectedEnvelopes = ToEnvelopes(result.SelectedItems, context);
        var droppedEnvelopes = result.DroppedItems
            .Select(dec => ToEnvelopeFromDecisionPublic(dec, context))
            .ToList();

        // 合并 selected + dropped 作为 Engine 输入；Engine 会重新决策
        var allEnvelopes = new List<ContextCandidateEnvelope>(selectedEnvelopes.Count + droppedEnvelopes.Count);
        allEnvelopes.AddRange(selectedEnvelopes);
        allEnvelopes.AddRange(droppedEnvelopes);

        // 填充 WorkspaceId / CollectionId / QueryText / RequestId
        return new ContextDecisionRequest
        {
            RequestId = string.IsNullOrEmpty(context.RequestId) ? result.OperationId : context.RequestId,
            DecisionSource = ContextDecisionSource.Retrieval,
            WorkspaceId = context.WorkspaceId,
            CollectionId = context.CollectionId,
            QueryText = context.QueryText,
            Candidates = allEnvelopes,
            TokenBudget = tokenBudget,
            TopK = topK,
            EnableModel = enableModel
        };
    }

    // ----------------------------------------------------------------------
    // 私有辅助方法
    // ----------------------------------------------------------------------

    private static ContextCandidateSource ResolveCandidateSource(ContextRetrievalCandidate candidate)
    {
        // ContextRetrievalCandidate.Metadata 中可能携带 channel / source 信息
        if (candidate.Metadata.TryGetValue("source", out var sourceStr))
        {
            if (Enum.TryParse<ContextCandidateSource>(sourceStr, ignoreCase: true, out var parsed))
            {
                return parsed;
            }
        }

        // 默认按 Kind 推断
        return candidate.Kind == ContextRetrievalCandidateKind.MemoryItem
            ? ContextCandidateSource.WorkingMemory
            : ContextCandidateSource.Lexical;
    }

    private static bool IsMandatoryCandidate(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("mandatory", out var mandatoryStr)
            && bool.TryParse(mandatoryStr, out var mandatory)
            && mandatory;
    }

    /// <summary>
    /// 从 Retrieval 候选的 Metadata 解析约束强制级别。
    /// 优先读取 metadata["constraintLevel"]（强类型字段），其次从 metadata["source"] 推导。
    /// 非 Constraint 来源返回 null。
    /// </summary>
    private static ConstraintLevel? ResolveConstraintLevel(ContextRetrievalCandidate candidate)
    {
        // 1. 强类型字段：metadata["constraintLevel"]
        if (candidate.Metadata.TryGetValue("constraintLevel", out var levelStr)
            && !string.IsNullOrEmpty(levelStr)
            && Enum.TryParse<ConstraintLevel>(levelStr, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        // 2. 从 metadata["source"] 推导（如 "hard_constraint" / "soft_constraint" / "merged_constraint"）
        if (candidate.Metadata.TryGetValue("source", out var sourceStr)
            && !string.IsNullOrEmpty(sourceStr))
        {
            return sourceStr.ToLowerInvariant() switch
            {
                "hard_constraint" or "constraints" => ConstraintLevel.Hard,
                "soft_constraint" => ConstraintLevel.Soft,
                "merged_constraint" => ConstraintLevel.Mixed,
                _ => null
            };
        }

        return null;
    }

    private static string ResolveLifecycleState(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("lifecycleStatus", out var state)
            && !string.IsNullOrEmpty(state)
                ? state
                : "active";
    }

    private static string ResolveReasonCode(ContextRetrievalCandidate candidate)
    {
        if (candidate.Reasons.Count == 0) return "deterministic-only";
        return string.Join(";", candidate.Reasons);
    }

    private static IReadOnlyList<string> ResolveChannelSources(ContextRetrievalCandidate candidate)
    {
        var channels = new List<string>(2);
        if (candidate.Metadata.TryGetValue("channel", out var channel) && !string.IsNullOrEmpty(channel))
        {
            channels.Add(channel);
        }
        if (candidate.Metadata.TryGetValue("retrievalChannel", out var retrievalChannel)
            && !string.IsNullOrEmpty(retrievalChannel))
        {
            channels.Add(retrievalChannel);
        }
        return channels;
    }

    /// <summary>
    /// 将 dropped 的 <see cref="ContextRetrievalDecision"/> 转换为
    /// <see cref="ContextCandidateEnvelope"/>，用于 parity 对比中 Legacy dropped 集合。
    /// </summary>
    public static ContextCandidateEnvelope ToEnvelopeFromDecisionPublic(
        ContextRetrievalDecision decision,
        CandidateAdaptationContext context)
    {
        var candidateId = string.IsNullOrWhiteSpace(decision.CandidateId)
            ? decision.SourceId
            : decision.CandidateId;
        var source = decision.Kind == ContextRetrievalCandidateKind.MemoryItem
            ? ContextCandidateSource.WorkingMemory
            : ContextCandidateSource.Lexical;

        // 填充 CanonicalKey（required）
        var expertKind = MapSourceToExpertKind(source);
        var canonicalKey = CanonicalCandidateKey.Create(
            workspaceId: context.WorkspaceId,
            collectionId: context.CollectionId,
            entityKind: string.IsNullOrEmpty(decision.Type) ? source.ToString().ToLowerInvariant() : decision.Type,
            entityId: candidateId,
            entityVersion: ComputeStableContentHash(candidateId, decision.Type, decision.Score, decision.EstimatedTokens));

        return new ContextCandidateEnvelope
        {
            CandidateId = candidateId,
            Source = source,
            Type = decision.Type,
            TokenCost = new CandidateTokenCost
            {
                ContentTokens = decision.EstimatedTokens,
                TokenizerId = "length-div-4",
                IsEstimated = true
            },
            WorkspaceId = context.WorkspaceId,
            CollectionId = context.CollectionId,
            CanonicalKey = canonicalKey,
            Origins = new List<ExpertOrigin> { new(expertKind, 1.0, context.ObservedAt) },
            ExpertContributions = new Dictionary<ExpertKind, double> { [expertKind] = 1.0 },
            PolicyReference = context.PolicyReference,
            Safety = new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCodeMapper.MapFromReason(decision.Reason),
                BlockReasonDetail = decision.Reason
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = decision.Score,
                FinalScore = decision.Score,
                ModelConfidence = 0.0,
                ReasonCode = "dropped-by-retrieval"
            },
            // dropped envelope 填充 workspace/collection/provenance（与 selected 路径一致）
            // ContextRetrievalDecision 不含 SourceRefs，故 ProvenanceRefs 仅携带 context 指纹
            ProvenanceRefs = new List<EvidenceRef>
            {
                new()
                {
                    RefId = candidateId,
                    RefType = "retrieval-dropped-ref",
                    WorkspaceId = string.IsNullOrEmpty(context.WorkspaceId) ? null : context.WorkspaceId,
                    CollectionId = string.IsNullOrEmpty(context.CollectionId) ? null : context.CollectionId,
                    GeneratedAt = context.ObservedAt,
                    ContentFingerprint = context.PolicySnapshot is null
                        ? null
                        : $"{context.PolicySnapshot.BundleId}@{context.PolicySnapshot.Version}"
                }
            }
        };
    }

    // ----------------------------------------------------------------------
    // CanonicalKey / Expert 派生辅助方法
    // ----------------------------------------------------------------------

    /// <summary>
    /// 将 ContextCandidateSource 映射到 ExpertKind（用于 Origins/ExpertContributions）。
    /// </summary>
    private static ExpertKind MapSourceToExpertKind(ContextCandidateSource source) => source switch
    {
        ContextCandidateSource.Mandatory => ExpertKind.Mandatory,
        ContextCandidateSource.Constraint => ExpertKind.Constraint,
        ContextCandidateSource.Lexical => ExpertKind.Lexical,
        ContextCandidateSource.Semantic => ExpertKind.Semantic,
        ContextCandidateSource.WorkingMemory => ExpertKind.WorkingMemory,
        ContextCandidateSource.StableMemory => ExpertKind.StableMemory,
        ContextCandidateSource.Graph => ExpertKind.Graph,
        ContextCandidateSource.Recency => ExpertKind.Recency,
        ContextCandidateSource.GlobalContext => ExpertKind.Mandatory,
        ContextCandidateSource.RelatedContext => ExpertKind.Graph,
        _ => ExpertKind.Lexical
    };

    /// <summary>
    /// 解析 EntityKind。优先使用 candidate.Type（业务类型），
    /// 退回 candidate.Kind 枚举名，最后退回 Source 枚举名。
    /// </summary>
    private static string ResolveEntityKind(ContextRetrievalCandidate candidate, ContextCandidateSource source)
    {
        if (!string.IsNullOrEmpty(candidate.Type)) return candidate.Type;
        return source.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// 解析 EntityVersion。优先使用 metadata["version"] / metadata["entityVersion"]，
    /// 否则使用 candidate.Content 的 stable content hash。
    /// </summary>
    private static string ResolveEntityVersion(ContextRetrievalCandidate candidate)
    {
        if (candidate.Metadata.TryGetValue("version", out var ver) && !string.IsNullOrEmpty(ver))
            return ver;
        if (candidate.Metadata.TryGetValue("entityVersion", out var ev) && !string.IsNullOrEmpty(ev))
            return ev;
        // 退回 stable content hash（基于 Content + Type + Score + Tokens 的确定性哈希）
        return ComputeStableContentHash(
            candidate.CandidateId, candidate.Type, candidate.Content,
            candidate.Score, candidate.EstimatedTokens);
    }

    /// <summary>
    /// 计算 stable content hash（SHA256，截取前 16 字符）。
    /// 用于 EntityVersion 在无显式版本号时的稳定回退。
    /// </summary>
    internal static string ComputeStableContentHash(params object[] parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(part ?? "null");
            sb.Append('|');
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
