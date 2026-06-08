namespace ContextCore.Abstractions;

/// <summary>Attention shadow scorer 使用的结构化候选特征。</summary>
public sealed class ContextAttentionFeatureVector
{
    public string CandidateId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public ContextRetrievalCandidateKind CandidateKind { get; init; }

    public string Type { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public double Importance { get; init; }

    public double RecencyAgeHours { get; init; }

    public int ChannelHitCount { get; init; }

    public IReadOnlyList<string> ChannelSources { get; init; } = Array.Empty<string>();

    public int RelationPathCount { get; init; }

    public IReadOnlyList<string> RelationPaths { get; init; } = Array.Empty<string>();

    public string Scope { get; init; } = string.Empty;

    public int MatchedTokenCount { get; init; }

    public int MatchedAnchorCount { get; init; }

    public int PositiveLearningFeedbackCount { get; init; }

    public int NegativeLearningFeedbackCount { get; init; }

    public int StaleLearningFeedbackCount { get; init; }

    public double LearningFeedbackNetScore { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Attention shadow scorer 输出的分数拆解。</summary>
public sealed class ContextAttentionScore
{
    public string CandidateId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public ContextRetrievalCandidateKind CandidateKind { get; init; }

    public int CurrentRank { get; init; }

    public int AttentionRank { get; init; }

    public double FinalAttentionScore { get; init; }

    public double QueryMatchScore { get; init; }

    public double ShortTermMatchScore { get; init; }

    public double RelationScore { get; init; }

    public double RecencyScore { get; init; }

    public double ImportanceScore { get; init; }

    public double ChannelScore { get; init; }

    public double LearningFeedbackScore { get; init; }

    public double LifecyclePenalty { get; init; }

    public double ScopePenalty { get; init; }

    public double NoiseRiskScore { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public string ProfileId { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;

    public ContextAttentionFeatureVector FeatureVector { get; init; } = new();
}

/// <summary>Attention shadow mode 下的当前排序与 attention 排序差异。</summary>
public sealed class AttentionShadowRank
{
    public string CandidateId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public int CurrentRank { get; init; }

    public int AttentionRank { get; init; }

    /// <summary>CurrentRank - AttentionRank；正数表示 attention 排序会将候选上推。</summary>
    public int RankDelta { get; init; }

    public double CurrentScore { get; init; }

    public double AttentionScore { get; init; }

    public string Lifecycle { get; init; } = string.Empty;

    public IReadOnlyList<string> ChannelSources { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RelationPaths { get; init; } = Array.Empty<string>();

    public string ScoreBreakdown { get; init; } = string.Empty;

    public bool SelectedByCurrentPolicy { get; init; }

    public bool WouldBeSelectedByAttention { get; init; }

    public bool IsMustHit { get; init; }

    public bool IsMustNotHit { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

/// <summary>Attention shadow mode 的候选排序差异报告；只用于 trace/eval，不参与检索选择。</summary>
public sealed class AttentionShadowReport
{
    public string OperationId { get; init; } = string.Empty;

    public int CandidateCount { get; init; }

    public int SelectedCount { get; init; }

    public bool WouldChangeSelectedSet { get; init; }

    public IReadOnlyList<AttentionShadowRank> Ranks { get; init; } = Array.Empty<AttentionShadowRank>();

    public IReadOnlyList<AttentionShadowRank> AddedByAttention { get; init; } = Array.Empty<AttentionShadowRank>();

    public IReadOnlyList<AttentionShadowRank> DroppedByAttention { get; init; } = Array.Empty<AttentionShadowRank>();

    public IReadOnlyList<AttentionShadowRank> TopPromotedCandidates { get; init; } = Array.Empty<AttentionShadowRank>();

    public IReadOnlyList<AttentionShadowRank> TopDemotedCandidates { get; init; } = Array.Empty<AttentionShadowRank>();

    public int MustNotHitPromotedCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>单个 attention profile 的 shadow 对比结果。</summary>
public sealed class AttentionProfileExperimentResult
{
    public string ProfileId { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;

    public IReadOnlyList<ContextAttentionScore> AttentionScores { get; init; } = Array.Empty<ContextAttentionScore>();

    public AttentionShadowReport ShadowReport { get; init; } = new();
}

/// <summary>多个 attention profile 的 shadow 对比结果。</summary>
public sealed class AttentionProfileExperimentReport
{
    public string OperationId { get; init; } = string.Empty;

    public IReadOnlyList<AttentionProfileExperimentResult> Profiles { get; init; } = Array.Empty<AttentionProfileExperimentResult>();
}

/// <summary>Attention rerank 的显式配置。默认关闭。</summary>
public sealed class RetrievalAttentionRerankOptions
{
    public const string OffMode = "Off";

    public const string ShadowMode = "Shadow";

    public const string ApplyGuardedMode = "ApplyGuarded";

    public const string LegacySelectedSetPreservingMode = "SelectedSetPreserving";

    public string Mode { get; init; } = OffMode;

    public string Profile { get; init; } = "old-score-anchored-v1-strong";

    public bool PreserveSelectedSet { get; init; } = true;

    public bool AllowSelectedSetMutation { get; init; }

    public bool EmitShadowTrace { get; init; } = true;

    /// <summary>Legacy Phase 4/5 flag. Prefer <see cref="Mode"/> for new code.</summary>
    public bool Enabled { get; init; }

    /// <summary>Legacy Phase 4/5 profile property. Prefer <see cref="Profile"/> for new code.</summary>
    public string ProfileId { get; init; } = string.Empty;

    public string EffectiveMode => ResolveEffectiveMode();

    public string EffectiveProfile => string.IsNullOrWhiteSpace(ProfileId)
        ? Profile
        : ProfileId;

    public bool ShouldApplyGuarded => string.Equals(EffectiveMode, ApplyGuardedMode, StringComparison.OrdinalIgnoreCase);

    public bool ShouldAnalyze => ShouldApplyGuarded
        || string.Equals(EffectiveMode, ShadowMode, StringComparison.OrdinalIgnoreCase);

    private string ResolveEffectiveMode()
    {
        if (string.IsNullOrWhiteSpace(Mode))
        {
            return Enabled ? ApplyGuardedMode : OffMode;
        }

        if (string.Equals(Mode, LegacySelectedSetPreservingMode, StringComparison.OrdinalIgnoreCase))
        {
            return Enabled ? ApplyGuardedMode : ShadowMode;
        }

        return Mode.Trim();
    }
}

/// <summary>Guarded attention rerank 前后的 selected order 条目快照。</summary>
public sealed class AttentionRerankOrderItem
{
    public string CandidateId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public ContextRetrievalCandidateKind CandidateKind { get; init; }

    public string Type { get; init; } = string.Empty;

    public int Rank { get; init; }

    public double OldScore { get; init; }

    public double AttentionScore { get; init; }

    public double FinalScore { get; init; }

    public string Lifecycle { get; init; } = string.Empty;

    public bool IsMustHit { get; init; }

    public bool IsMustNotHit { get; init; }

    public bool IsConstraint { get; init; }

    public bool IsHardConstraint { get; init; }

    public bool IsLifecycleRisk { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public Dictionary<string, double> AttentionScoreBreakdown { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Guarded attention rerank 后的单候选排序变化。</summary>
public sealed class AttentionRerankItemChange
{
    public string CandidateId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public int CurrentRank { get; init; }

    public int RerankedRank { get; init; }

    public int OldRank => CurrentRank;

    public int NewRank => RerankedRank;

    /// <summary>CurrentRank - RerankedRank；正数表示 rerank 将候选上推。</summary>
    public int RankDelta { get; init; }

    public double OldScore { get; init; }

    public double AttentionScore { get; init; }

    public double FinalScore { get; init; }

    public string MoveReason { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public bool IsMustHit { get; init; }

    public bool IsMustNotHit { get; init; }

    public bool IsConstraint { get; init; }

    public bool IsHardConstraint { get; init; }

    public bool IsLifecycleRisk { get; init; }

    public Dictionary<string, double> AttentionScoreBreakdown { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

/// <summary>Guarded attention rerank 后的 section 变化。检索层默认不会改变 section。</summary>
public sealed class AttentionRerankSectionChange
{
    public string CandidateId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string FromSection { get; init; } = string.Empty;

    public string ToSection { get; init; } = string.Empty;
}

/// <summary>
/// Guarded attention rerank 的比较报告。用于 trace/eval，不参与 scoring 或 packing。
/// </summary>
public sealed class AttentionRerankComparisonReport
{
    public string OperationId { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public string Mode { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public string AttentionRerankMode { get; init; } = string.Empty;

    public string AttentionProfile { get; init; } = string.Empty;

    public bool Applied { get; init; }

    public bool AttentionApplied { get; init; }

    public bool Skipped { get; init; }

    public bool Blocked { get; init; }

    public string SkippedReason { get; init; } = string.Empty;

    public string BlockedReason { get; init; } = string.Empty;

    public bool SelectedSetPreserved { get; init; } = true;

    public int OrderChangedCount { get; init; }

    public IReadOnlyList<string> OldOrder { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NewOrder { get; init; } = Array.Empty<string>();

    public string GuardViolation { get; init; } = string.Empty;

    public IReadOnlyList<AttentionRerankItemChange> AddedItems { get; init; } = Array.Empty<AttentionRerankItemChange>();

    public IReadOnlyList<AttentionRerankItemChange> DroppedItems { get; init; } = Array.Empty<AttentionRerankItemChange>();

    public IReadOnlyList<AttentionRerankOrderItem> OldSelectedOrder { get; init; } = Array.Empty<AttentionRerankOrderItem>();

    public IReadOnlyList<AttentionRerankOrderItem> NewSelectedOrder { get; init; } = Array.Empty<AttentionRerankOrderItem>();

    public IReadOnlyList<AttentionRerankItemChange> OrderChanges { get; init; } = Array.Empty<AttentionRerankItemChange>();

    public IReadOnlyList<AttentionRerankItemChange> MovedUpItems { get; init; } = Array.Empty<AttentionRerankItemChange>();

    public IReadOnlyList<AttentionRerankItemChange> MovedDownItems { get; init; } = Array.Empty<AttentionRerankItemChange>();

    public IReadOnlyList<AttentionRerankSectionChange> SectionChanges { get; init; } = Array.Empty<AttentionRerankSectionChange>();

    public IReadOnlyList<AttentionRerankItemChange> MustHitRankDeltas { get; init; } = Array.Empty<AttentionRerankItemChange>();

    public IReadOnlyList<AttentionRerankItemChange> MustNotHitRankDeltas { get; init; } = Array.Empty<AttentionRerankItemChange>();

    public int SelectedSetChangeCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Attention shadow scorer 的权重配置。</summary>
public sealed class ContextAttentionProfile
{
    public string ProfileId { get; init; } = "default-shadow-v1";

    public string PolicyVersion { get; init; } = "context-attention-shadow-policy/v1";

    public Dictionary<string, double> Weights { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, double> Penalties { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, double> Controls { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static ContextAttentionProfile CreateDefaultShadowV1()
    {
        return new ContextAttentionProfile
        {
            ProfileId = "default-shadow-v1",
            PolicyVersion = "context-attention-shadow-policy/v1",
            Weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["queryMatch"] = 0.24,
                ["shortTermMatch"] = 0.16,
                ["relation"] = 0.12,
                ["recency"] = 0.12,
                ["importance"] = 0.14,
                ["channel"] = 0.10,
                ["learningFeedback"] = 0.12
            },
            Penalties = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["deprecatedLifecycle"] = 0.28,
                ["rejectedLifecycle"] = 0.45,
                ["globalScope"] = 0.08,
                ["noiseRisk"] = 0.18,
                ["staleFeedback"] = 0.16
            },
            Controls = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["attentionWeight"] = 1.0
            }
        };
    }

    public static ContextAttentionProfile CreateConservativeV1()
    {
        return CreateProfile(
            profileId: "conservative-v1",
            weights: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["queryMatch"] = 0.30,
                ["shortTermMatch"] = 0.13,
                ["relation"] = 0.10,
                ["recency"] = 0.12,
                ["importance"] = 0.16,
                ["channel"] = 0.11,
                ["learningFeedback"] = 0.08
            },
            penalties: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["deprecatedLifecycle"] = 0.34,
                ["rejectedLifecycle"] = 0.55,
                ["globalScope"] = 0.10,
                ["noiseRisk"] = 0.24,
                ["staleFeedback"] = 0.20
            });
    }

    public static ContextAttentionProfile CreateRelationBalancedV1()
    {
        var profile = CreateDefaultShadowV1();
        return CreateProfile(
            profileId: "relation-balanced-v1",
            weights: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["queryMatch"] = 0.22,
                ["shortTermMatch"] = 0.14,
                ["relation"] = 0.18,
                ["recency"] = 0.10,
                ["importance"] = 0.14,
                ["channel"] = 0.10,
                ["learningFeedback"] = 0.12
            },
            penalties: new Dictionary<string, double>(profile.Penalties, StringComparer.OrdinalIgnoreCase));
    }

    public static ContextAttentionProfile CreateLearningLightV1()
    {
        return CreateProfile(
            profileId: "learning-light-v1",
            weights: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["queryMatch"] = 0.27,
                ["shortTermMatch"] = 0.16,
                ["relation"] = 0.13,
                ["recency"] = 0.13,
                ["importance"] = 0.15,
                ["channel"] = 0.11,
                ["learningFeedback"] = 0.05
            },
            penalties: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["deprecatedLifecycle"] = 0.28,
                ["rejectedLifecycle"] = 0.45,
                ["globalScope"] = 0.08,
                ["noiseRisk"] = 0.14,
                ["staleFeedback"] = 0.12
            });
    }

    public static ContextAttentionProfile CreateLifecycleStrictV1()
    {
        return CreateProfile(
            profileId: "lifecycle-strict-v1",
            weights: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["queryMatch"] = 0.25,
                ["shortTermMatch"] = 0.15,
                ["relation"] = 0.12,
                ["recency"] = 0.11,
                ["importance"] = 0.14,
                ["channel"] = 0.10,
                ["learningFeedback"] = 0.13
            },
            penalties: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["deprecatedLifecycle"] = 0.45,
                ["rejectedLifecycle"] = 0.70,
                ["globalScope"] = 0.08,
                ["noiseRisk"] = 0.22,
                ["staleFeedback"] = 0.24
            });
    }

    public static ContextAttentionProfile CreateOldScoreAnchoredV1()
    {
        var profile = CreateConservativeV1();
        return CreateProfile(
            profileId: "old-score-anchored-v1",
            weights: new Dictionary<string, double>(profile.Weights, StringComparer.OrdinalIgnoreCase),
            penalties: new Dictionary<string, double>(profile.Penalties, StringComparer.OrdinalIgnoreCase),
            controls: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["currentRankWeight"] = 0.50,
                ["currentScoreWeight"] = 0.40,
                ["attentionWeight"] = 0.10
            });
    }

    public static ContextAttentionProfile CreateOldScoreAnchoredV1Light()
    {
        return CreateOldScoreAnchoredV1Variant(
            profileId: "old-score-anchored-v1-light",
            oldScoreAnchorWeight: 0.94,
            mustHitBoost: 0.02,
            constraintBoost: 0.02,
            shortTermBoost: 0.01,
            lifecycleRiskPenalty: 0.02,
            relationEvidenceBoost: 0.01,
            recencyBoost: 0.005);
    }

    public static ContextAttentionProfile CreateOldScoreAnchoredV1Balanced()
    {
        return CreateOldScoreAnchoredV1Variant(
            profileId: "old-score-anchored-v1-balanced",
            oldScoreAnchorWeight: 0.90,
            mustHitBoost: 0.04,
            constraintBoost: 0.03,
            shortTermBoost: 0.02,
            lifecycleRiskPenalty: 0.04,
            relationEvidenceBoost: 0.02,
            recencyBoost: 0.01);
    }

    public static ContextAttentionProfile CreateOldScoreAnchoredV1Strong()
    {
        return CreateOldScoreAnchoredV1Variant(
            profileId: "old-score-anchored-v1-strong",
            oldScoreAnchorWeight: 0.86,
            mustHitBoost: 0.06,
            constraintBoost: 0.04,
            shortTermBoost: 0.03,
            lifecycleRiskPenalty: 0.06,
            relationEvidenceBoost: 0.03,
            recencyBoost: 0.015);
    }

    public static ContextAttentionProfile CreateDeltaLimitedV1()
    {
        var profile = CreateConservativeV1();
        return CreateProfile(
            profileId: "delta-limited-v1",
            weights: new Dictionary<string, double>(profile.Weights, StringComparer.OrdinalIgnoreCase),
            penalties: new Dictionary<string, double>(profile.Penalties, StringComparer.OrdinalIgnoreCase),
            controls: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["maxPromotionDelta"] = 2,
                ["maxDemotionDelta"] = 2,
                ["protectCurrentTopCount"] = 3,
                ["attentionWeight"] = 1.0
            });
    }

    public static ContextAttentionProfile CreateGuardedShadowV1()
    {
        return CreateProfile(
            profileId: "guarded-shadow-v1",
            weights: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["queryMatch"] = 0.30,
                ["shortTermMatch"] = 0.16,
                ["relation"] = 0.10,
                ["recency"] = 0.10,
                ["importance"] = 0.14,
                ["channel"] = 0.11,
                ["learningFeedback"] = 0.09
            },
            penalties: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["deprecatedLifecycle"] = 0.65,
                ["rejectedLifecycle"] = 0.90,
                ["globalScope"] = 0.12,
                ["noiseRisk"] = 0.35,
                ["staleFeedback"] = 0.32
            },
            controls: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["protectCurrentTopCount"] = 3,
                ["protectMandatory"] = 1,
                ["protectAnchors"] = 1,
                ["maxPromotionDelta"] = 2,
                ["maxDemotionDelta"] = 3,
                ["guardMustNotHitWouldSelect"] = 1,
                ["attentionWeight"] = 1.0
            });
    }

    public static IReadOnlyList<ContextAttentionProfile> CreateShadowExperimentProfiles()
    {
        return
        [
            CreateDefaultShadowV1(),
            CreateConservativeV1(),
            CreateRelationBalancedV1(),
            CreateLearningLightV1(),
            CreateLifecycleStrictV1(),
            CreateOldScoreAnchoredV1(),
            CreateOldScoreAnchoredV1Light(),
            CreateOldScoreAnchoredV1Balanced(),
            CreateOldScoreAnchoredV1Strong(),
            CreateDeltaLimitedV1(),
            CreateGuardedShadowV1()
        ];
    }

    public static IReadOnlyList<ContextAttentionProfile> CreateGuardedRerankSweepProfiles()
    {
        return
        [
            CreateOldScoreAnchoredV1(),
            CreateOldScoreAnchoredV1Light(),
            CreateOldScoreAnchoredV1Balanced(),
            CreateOldScoreAnchoredV1Strong()
        ];
    }

    public static ContextAttentionProfile CreateById(string? profileId)
    {
        return profileId?.Trim().ToLowerInvariant() switch
        {
            "conservative-v1" => CreateConservativeV1(),
            "relation-balanced-v1" => CreateRelationBalancedV1(),
            "learning-light-v1" => CreateLearningLightV1(),
            "lifecycle-strict-v1" => CreateLifecycleStrictV1(),
            "old-score-anchored-v1" => CreateOldScoreAnchoredV1(),
            "old-score-anchored-v1-light" => CreateOldScoreAnchoredV1Light(),
            "old-score-anchored-v1-balanced" => CreateOldScoreAnchoredV1Balanced(),
            "old-score-anchored-v1-strong" => CreateOldScoreAnchoredV1Strong(),
            "delta-limited-v1" => CreateDeltaLimitedV1(),
            "guarded-shadow-v1" => CreateGuardedShadowV1(),
            _ => CreateDefaultShadowV1()
        };
    }

    private static ContextAttentionProfile CreateOldScoreAnchoredV1Variant(
        string profileId,
        double oldScoreAnchorWeight,
        double mustHitBoost,
        double constraintBoost,
        double shortTermBoost,
        double lifecycleRiskPenalty,
        double relationEvidenceBoost,
        double recencyBoost)
    {
        var profile = CreateConservativeV1();
        return CreateProfile(
            profileId: profileId,
            weights: new Dictionary<string, double>(profile.Weights, StringComparer.OrdinalIgnoreCase),
            penalties: new Dictionary<string, double>(profile.Penalties, StringComparer.OrdinalIgnoreCase),
            controls: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["currentRankWeight"] = 0.50,
                ["currentScoreWeight"] = 0.40,
                ["attentionWeight"] = 0.10,
                ["oldScoreAnchorWeight"] = oldScoreAnchorWeight,
                ["mustHitBoost"] = mustHitBoost,
                ["constraintBoost"] = constraintBoost,
                ["shortTermBoost"] = shortTermBoost,
                ["lifecycleRiskPenalty"] = lifecycleRiskPenalty,
                ["relationEvidenceBoost"] = relationEvidenceBoost,
                ["recencyBoost"] = recencyBoost
            });
    }

    private static ContextAttentionProfile CreateProfile(
        string profileId,
        Dictionary<string, double> weights,
        Dictionary<string, double> penalties,
        Dictionary<string, double>? controls = null)
    {
        return new ContextAttentionProfile
        {
            ProfileId = profileId,
            PolicyVersion = $"context-attention-shadow-policy/{profileId}",
            Weights = weights,
            Penalties = penalties,
            Controls = controls ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["attentionWeight"] = 1.0
            }
        };
    }
}
