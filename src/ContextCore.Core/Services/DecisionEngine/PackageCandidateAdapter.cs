using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// Package 候选适配器（PackageTraceCandidate → ContextCandidateEnvelope）
//
// 目标：
// 让现有 BasicContextPackageBuilder 路径产出的 PackageTraceCandidate 集合
// 可以转换为统一的 ContextCandidateEnvelope 集合，从而接入 的
// IContextDecisionEngine 编排路径。
//
// 设计原则：
// 1. 适配器是单向转换（PackageTraceCandidate → Envelope），不修改原候选。
// 2. 适配器是幂等的：相同输入产生相同输出。
// 3. 适配器不调用 Engine 或 Storage；纯内存转换。
// 4. 适配器不破坏现有 BasicContextPackageBuilder.BuildDetailedAsync 主链。
// 5. Kind 字符串（如 "working_memory" / "hard_constraint"）映射到
// ContextCandidateSource 枚举，统一两路径的候选来源表达。
// 6. 修复：映射函数不再读取 DateTimeOffset.UtcNow。
// 时间戳由 CandidateAdaptationContext.ObservedAt 统一传入；
// ToDecisionRequest 填充 WorkspaceId / CollectionId / QueryText，
// 避免 PolicyRegistry 按空 workspace 解析默认 Bundle。
//
// 字段映射：
// PackageTraceCandidate.Id → envelope.CandidateId
// PackageTraceCandidate.Kind (string) → envelope.Source (enum)
// PackageTraceCandidate.Type → envelope.Type
// PackageTraceCandidate.Score → envelope.Utility.DeterministicScore + FinalScore
// PackageTraceCandidate.EstimatedTokens → envelope.EstimatedTokens
// PackageTraceCandidate.SourceRefs → envelope.ProvenanceRefs (封装为 EvidenceRef)
// PackageTraceCandidate.Metadata → 不直接复制，按字段映射到 Safety/Features
// PackageTraceCandidate.ScoreBreakdown → envelope.Features.ScoreBreakdown (转字典)
//
// 反向投影（envelope → ContextPackageDecision）由 PackageResultProjector 负责。
// ===========================================================================

/// <summary>
/// Package 候选适配器。将 <see cref="PackageTraceCandidate"/>
/// 集合转换为 <see cref="ContextCandidateEnvelope"/> 集合，作为现有 Package
/// 路径与统一 Engine 之间的桥梁。
/// </summary>
public static class PackageCandidateAdapter
{
    /// <summary>
    /// 将单个 <see cref="PackageTraceCandidate"/> 转换为
    /// <see cref="ContextCandidateEnvelope"/>，使用传入的 context 提供时间戳与作用域。
    /// </summary>
    /// <param name="candidate">原始候选。</param>
    /// <param name="context">适配上下文（提供 ObservedAt / WorkspaceId / CollectionId 等）。</param>
    public static ContextCandidateEnvelope ToEnvelope(
        PackageTraceCandidate candidate,
        CandidateAdaptationContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        var source = ResolveCandidateSource(candidate.Kind);
        var isMandatory = IsMandatoryKind(candidate.Kind);
        var constraintLevel = ResolveConstraintLevel(candidate.Kind);
        var lifecycleState = ResolveLifecycleState(candidate.Metadata);

        // 填充 CanonicalKey / Origins / ExpertContributions / PolicyReference
        var expertKind = MapSourceToExpertKind(source);
        var canonicalKey = CanonicalCandidateKey.Create(
            workspaceId: context.WorkspaceId,
            collectionId: context.CollectionId,
            entityKind: ResolveEntityKind(candidate, source),
            entityId: candidate.Id,
            entityVersion: ResolveEntityVersion(candidate));
        var origins = new List<ExpertOrigin>
        {
            new(expertKind, Contribution: 1.0, ObservedAt: context.ObservedAt)
        };
        var expertContributions = new Dictionary<ExpertKind, double> { [expertKind] = 1.0 };

        return new ContextCandidateEnvelope
        {
            CandidateId = candidate.Id,
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
                // IsMandatory 仅对 hard_constraint / constraints 为 true（Hard 级别）；
                // soft_constraint（Soft）与 merged_constraint（Mixed）均不免预算。
                // IsHardConstraint 仅当 ConstraintLevel == Hard 时为 true。
                IsMandatory = isMandatory,
                ConstraintLevel = constraintLevel,
                IsHardConstraint = constraintLevel == ConstraintLevel.Hard,
                LifecycleState = lifecycleState,
                IsSuperseded = lifecycleState.Equals("superseded", StringComparison.OrdinalIgnoreCase),
                IsDeprecatedUsedByActiveChain = IsDeprecatedUsedByActiveChain(candidate.Metadata),
                PassesSafetyGate = true
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = candidate.Score,
                FinalScore = candidate.Score,
                ModelConfidence = 0.0, // Package 路径默认 deterministic-only
                ReasonCode = ResolveReasonCode(candidate.Kind)
            },
            Features = new CandidateFeatureVector
            {
                ScoreBreakdown = ConvertScoreBreakdown(candidate.ScoreBreakdown),
                ChannelSources = ResolveChannelSources(candidate.Kind)
            },
            // 使用 context.ObservedAt 而非 DateTimeOffset.UtcNow
            ProvenanceRefs = candidate.SourceRefs
                .Where(r => !string.IsNullOrEmpty(r))
                .Select(r => new EvidenceRef
                {
                    RefId = r,
                    RefType = "package-source-ref",
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
    /// 将 <see cref="PackageTraceCandidate"/> 集合批量转换为
    /// <see cref="ContextCandidateEnvelope"/> 集合，使用传入的 context。
    /// </summary>
    public static IReadOnlyList<ContextCandidateEnvelope> ToEnvelopes(
        IEnumerable<PackageTraceCandidate> candidates,
        CandidateAdaptationContext context)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);
        return candidates.Select(c => ToEnvelope(c, context)).ToList();
    }

    /// <summary>
    /// 将 <see cref="ContextPackageBuildResult"/> 整体转换为
    /// <see cref="ContextDecisionRequest"/>，并填充 workspace/collection/query 作用域。
    /// </summary>
    /// <param name="result">Package 主链产出的结果。</param>
    /// <param name="tokenBudget">token 预算上限。</param>
    /// <param name="enableModel">是否启用模型评分。</param>
    /// <param name="context">适配上下文（提供 WorkspaceId/CollectionId/QueryText/ObservedAt）。</param>
    public static ContextDecisionRequest ToDecisionRequest(
        ContextPackageBuildResult result,
        int tokenBudget,
        bool enableModel,
        CandidateAdaptationContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        var selectedEnvelopes = result.SelectedItems
            .Select(d => ToEnvelopeFromDecision(d, context))
            .ToList();
        var droppedEnvelopes = result.DroppedItems
            .Select(item => ToEnvelopeFromDroppedItem(item, context))
            .ToList();

        var allEnvelopes = new List<ContextCandidateEnvelope>(selectedEnvelopes.Count + droppedEnvelopes.Count);
        allEnvelopes.AddRange(selectedEnvelopes);
        allEnvelopes.AddRange(droppedEnvelopes);

        // 填充 WorkspaceId / CollectionId / QueryText / RequestId
        return new ContextDecisionRequest
        {
            RequestId = string.IsNullOrEmpty(context.RequestId) ? result.BuildId : context.RequestId,
            DecisionSource = ContextDecisionSource.Package,
            WorkspaceId = context.WorkspaceId,
            CollectionId = context.CollectionId,
            QueryText = context.QueryText,
            Candidates = allEnvelopes,
            TokenBudget = tokenBudget,
            EnableModel = enableModel
        };
    }

    // ----------------------------------------------------------------------
    // 私有辅助方法
    // ----------------------------------------------------------------------

    private static ContextCandidateSource ResolveCandidateSource(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return ContextCandidateSource.Unknown;

        // 与 PackageTraceRecorder.MapSourceType / ResultProjector 排序逻辑保持一致。
        // ContextCandidateSource 不区分 Raw / CurrentTask，按 channel 语义归并：
        // - raw/legacy 在 PackageTraceRecorder 中映射到 Keyword channel（lexical 路径）
        // → ContextCandidateSource.Lexical
        // - current_task 在枚举注释中明确归入 Recency（"Recency / Task-State"）
        // → ContextCandidateSource.Recency
        return kind.ToLowerInvariant() switch
        {
            "raw" or "legacy" => ContextCandidateSource.Lexical,
            "current_task" => ContextCandidateSource.Recency,
            "hard_constraint" or "soft_constraint" or "merged_constraint" =>
                ContextCandidateSource.Constraint,
            "working_memory" => ContextCandidateSource.WorkingMemory,
            "stable_memory" => ContextCandidateSource.StableMemory,
            "historical_context" => ContextCandidateSource.StableMemory, // 历史归档视为稳定记忆
            "global_context" => ContextCandidateSource.GlobalContext,
            "recent_context" => ContextCandidateSource.Recency,
            "related_context" => ContextCandidateSource.RelatedContext,
            "constraints" => ContextCandidateSource.Constraint,
            _ => ContextCandidateSource.Unknown
        };
    }

    private static bool IsMandatoryKind(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return false;
        var lower = kind.ToLowerInvariant();
        // 仅 hard_constraint / constraints 视为 mandatory（Hard 级别）。
        // merged_constraint（Mixed）与 soft_constraint（Soft）不再免预算。
        return lower == "hard_constraint" || lower == "constraints";
    }

    /// <summary>
    /// 根据 kind 字符串解析约束强制级别。
    /// hard_constraint / constraints → Hard
    /// soft_constraint → Soft
    /// merged_constraint → Mixed（不可直接免预算）
    /// 其他 → null（非 Constraint 来源）
    /// </summary>
    private static ConstraintLevel? ResolveConstraintLevel(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return null;
        return kind.ToLowerInvariant() switch
        {
            "hard_constraint" or "constraints" => ConstraintLevel.Hard,
            "soft_constraint" => ConstraintLevel.Soft,
            "merged_constraint" => ConstraintLevel.Mixed,
            _ => null
        };
    }

    private static string ResolveLifecycleState(Dictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("lifecycleStatus", out var state) && !string.IsNullOrEmpty(state))
        {
            return state;
        }
        return "active";
    }

    private static bool IsDeprecatedUsedByActiveChain(Dictionary<string, string> metadata)
    {
        return metadata.TryGetValue("lifecycleStatus", out var state)
            && state.Equals("deprecated", StringComparison.OrdinalIgnoreCase)
            && metadata.TryGetValue("usedByActiveChain", out var usedStr)
            && bool.TryParse(usedStr, out var used)
            && used;
    }

    private static string ResolveReasonCode(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return "deterministic-only";
        var lower = kind.ToLowerInvariant();
        if (lower == "hard_constraint" || lower == "constraints") return "mandatory";
        return "deterministic-only";
    }

    private static IReadOnlyDictionary<string, double> ConvertScoreBreakdown(ItemScoreBreakdown? breakdown)
    {
        if (breakdown == null) return new Dictionary<string, double>(StringComparer.Ordinal);

        // ItemScoreBreakdown 含 13 个子分维度；此处提取为字典
        var dict = new Dictionary<string, double>(StringComparer.Ordinal);
        var b = breakdown;
        AddIfNonZero(dict, "base", b.BaseScore);
        AddIfNonZero(dict, "layer", b.LayerScore);
        AddIfNonZero(dict, "status", b.StatusScore);
        AddIfNonZero(dict, "semanticAnchor", b.SemanticAnchorScore);
        AddIfNonZero(dict, "rawTokenMatch", b.RawTokenMatchScore);
        AddIfNonZero(dict, "anchorMatchBonus", b.AnchorMatchBonus);
        AddIfNonZero(dict, "modeMatch", b.ModeMatchScore);
        AddIfNonZero(dict, "taskIntent", b.TaskIntentScore);
        AddIfNonZero(dict, "recency", b.RecencyScore);
        AddIfNonZero(dict, "relation", b.RelationScore);
        AddIfNonZero(dict, "lifecyclePenalty", b.LifecyclePenalty);
        AddIfNonZero(dict, "redundancyPenalty", b.RedundancyPenalty);
        AddIfNonZero(dict, "final", b.FinalScore);
        return dict;
    }

    private static void AddIfNonZero(Dictionary<string, double> dict, string key, double value)
    {
        if (value != 0) dict[key] = value;
    }

    private static IReadOnlyList<string> ResolveChannelSources(string kind)
    {
        // Package 路径的 channel 即 Kind 本身
        if (string.IsNullOrEmpty(kind)) return Array.Empty<string>();
        return new[] { kind };
    }

    /// <summary>
    /// 将 selected 的 <see cref="ContextPackageDecision"/> 转换为
    /// <see cref="ContextCandidateEnvelope"/>，用于 parity 对比中 Legacy selected 集合。
    /// </summary>
    public static ContextCandidateEnvelope ToEnvelopeFromDecision(
        ContextPackageDecision decision,
        CandidateAdaptationContext context)
    {
        var source = ResolveCandidateSource(decision.Kind);
        var constraintLevel = ResolveConstraintLevel(decision.Kind);
        var expertKind = MapSourceToExpertKind(source);
        var canonicalKey = CanonicalCandidateKey.Create(
            workspaceId: context.WorkspaceId,
            collectionId: context.CollectionId,
            entityKind: ResolveEntityKindFromDecision(decision, source),
            entityId: decision.ItemId,
            entityVersion: ResolveEntityVersionFromDecision(decision));
        return new ContextCandidateEnvelope
        {
            CandidateId = decision.ItemId,
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
                // 与 ToEnvelope 保持一致的 ConstraintLevel 推导
                IsMandatory = IsMandatoryKind(decision.Kind),
                ConstraintLevel = constraintLevel,
                IsHardConstraint = constraintLevel == ConstraintLevel.Hard,
                PassesSafetyGate = true
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = decision.Score,
                FinalScore = decision.Score,
                ModelConfidence = 0.0,
                ReasonCode = string.IsNullOrEmpty(decision.Reason) ? "deterministic-only" : decision.Reason
            },
            Features = new CandidateFeatureVector
            {
                ChannelSources = ResolveChannelSources(decision.Kind)
            },
            // 使用 context.ObservedAt 而非 DateTimeOffset.UtcNow
            ProvenanceRefs = decision.SourceRefs
                .Where(r => !string.IsNullOrEmpty(r))
                .Select(r => new EvidenceRef
                {
                    RefId = r,
                    RefType = "package-source-ref",
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
    /// 将 dropped 的 <see cref="DroppedContextItem"/> 转换为
    /// <see cref="ContextCandidateEnvelope"/>，用于 parity 对比中 Legacy dropped 集合。
    /// </summary>
    public static ContextCandidateEnvelope ToEnvelopeFromDroppedItem(
        DroppedContextItem item,
        CandidateAdaptationContext context)
    {
        var source = ResolveCandidateSource(item.Kind);
        var constraintLevel = ResolveConstraintLevel(item.Kind);
        var blockReason = CandidateDecisionReasonCodeMapper.MapFromReason(item.Reason);
        var expertKind = MapSourceToExpertKind(source);
        var canonicalKey = CanonicalCandidateKey.Create(
            workspaceId: context.WorkspaceId,
            collectionId: context.CollectionId,
            entityKind: string.IsNullOrEmpty(item.Type) ? source.ToString().ToLowerInvariant() : item.Type,
            entityId: item.ItemId,
            entityVersion: ComputeStableContentHash(item.ItemId, item.Type, item.Kind, item.EstimatedTokens));
        return new ContextCandidateEnvelope
        {
            CandidateId = item.ItemId,
            Source = source,
            Type = item.Type,
            TokenCost = new CandidateTokenCost
            {
                ContentTokens = item.EstimatedTokens,
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
                ConstraintLevel = constraintLevel,
                IsHardConstraint = constraintLevel == ConstraintLevel.Hard,
                PassesSafetyGate = false,
                BlockReasonCode = blockReason,
                BlockReasonDetail = item.Reason
            },
            Utility = new CandidateUtilityScore
            {
                ModelConfidence = 0.0,
                ReasonCode = "dropped-by-package"
            },
            Features = new CandidateFeatureVector
            {
                ChannelSources = ResolveChannelSources(item.Kind)
            },
            // dropped envelope 填充 provenance（与 selected 路径一致）
            ProvenanceRefs = new List<EvidenceRef>
            {
                new()
                {
                    RefId = item.ItemId,
                    RefType = "package-dropped-ref",
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
    /// 退回 Kind 字符串，最后退回 Source 枚举名。
    /// </summary>
    private static string ResolveEntityKind(PackageTraceCandidate candidate, ContextCandidateSource source)
    {
        if (!string.IsNullOrEmpty(candidate.Type)) return candidate.Type;
        if (!string.IsNullOrEmpty(candidate.Kind)) return candidate.Kind;
        return source.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// 解析 EntityKind（从 ContextPackageDecision）。
    /// </summary>
    private static string ResolveEntityKindFromDecision(ContextPackageDecision decision, ContextCandidateSource source)
    {
        if (!string.IsNullOrEmpty(decision.Type)) return decision.Type;
        if (!string.IsNullOrEmpty(decision.Kind)) return decision.Kind;
        return source.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// 解析 EntityVersion。优先使用 metadata["version"] / metadata["entityVersion"]，
    /// 否则使用 candidate.Content 的 stable content hash。
    /// </summary>
    private static string ResolveEntityVersion(PackageTraceCandidate candidate)
    {
        if (candidate.Metadata.TryGetValue("version", out var ver) && !string.IsNullOrEmpty(ver))
            return ver;
        if (candidate.Metadata.TryGetValue("entityVersion", out var ev) && !string.IsNullOrEmpty(ev))
            return ev;
        return ComputeStableContentHash(
            candidate.Id, candidate.Type, candidate.Content,
            candidate.Score, candidate.EstimatedTokens);
    }

    /// <summary>
    /// 解析 EntityVersion（从 ContextPackageDecision）。
    /// Decision 不含 Content，使用可识别字段计算 stable hash。
    /// </summary>
    private static string ResolveEntityVersionFromDecision(ContextPackageDecision decision)
    {
        return ComputeStableContentHash(
            decision.ItemId, decision.Type, decision.Kind,
            decision.Score, decision.EstimatedTokens);
    }

    /// <summary>
    /// 计算 stable content hash（SHA256，截取前 16 字符）。
    /// 复用 RetrievalCandidateAdapter 的实现以保持一致。
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
