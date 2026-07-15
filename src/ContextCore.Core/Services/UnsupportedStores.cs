using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

// 以下每个声明由 UnsupportedStoreGenerator 生成完整实现：
// 构造函数接收 provider 名称，所有方法抛出 NotSupportedException。
// 用于 Postgres provider 暂未实现的存储契约，避免运行时静默丢弃数据。

/// <summary>短期记忆存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(IShortTermMemoryStore), "Short term memory store")]
public sealed partial class UnsupportedShortTermMemoryStore;

/// <summary>短期记忆晋升候选项存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(IShortTermPromotionCandidateStore), "Short term promotion candidate store")]
public sealed partial class UnsupportedShortTermPromotionCandidateStore;

/// <summary>CandidateMemory review 存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(ICandidateMemoryReviewStore), "Candidate memory review store")]
public sealed partial class UnsupportedCandidateMemoryReviewStore;

/// <summary>Stable review 候选项存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(IStableReviewCandidateStore), "Stable review candidate store")]
public sealed partial class UnsupportedStableReviewCandidateStore;

/// <summary>上下文学习记录存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(IContextLearningStore), "Context learning store")]
public sealed partial class UnsupportedContextLearningStore;

/// <summary>Vector reindex 报告存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(IVectorReindexReportStore), "Vector reindex report store")]
public sealed partial class UnsupportedVectorReindexReportStore;

/// <summary>Vector lifecycle metadata review candidate 存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(IVectorLifecycleMetadataReviewCandidateStore), "Vector lifecycle metadata review candidate store")]
public sealed partial class UnsupportedVectorLifecycleMetadataReviewCandidateStore;

/// <summary>Vector lifecycle metadata review 存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(IVectorLifecycleMetadataReviewStore), "Vector lifecycle metadata review store")]
public sealed partial class UnsupportedVectorLifecycleMetadataReviewStore;

/// <summary>Vector lifecycle sidecar metadata 存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(IVectorLifecycleSidecarMetadataStore), "Vector lifecycle sidecar metadata store")]
public sealed partial class UnsupportedVectorLifecycleSidecarMetadataStore;

/// <summary>Artifact 存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(IArtifactStore), "Artifact store")]
public sealed partial class UnsupportedArtifactStore;

/// <summary>Stable lifecycle review 存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(IStableLifecycleReviewStore), "Stable lifecycle review store")]
public sealed partial class UnsupportedStableLifecycleReviewStore;

/// <summary>Candidate constraint review 存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(ICandidateConstraintReviewStore), "Candidate constraint review store")]
public sealed partial class UnsupportedCandidateConstraintReviewStore;

/// <summary>约束语料缺口候选项存储的占位实现。</summary>
[GenerateUnsupportedStore(typeof(IConstraintGapCandidateStore), "Constraint gap candidate store")]
public sealed partial class UnsupportedConstraintGapCandidateStore;
