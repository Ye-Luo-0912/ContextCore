using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 负责将逻辑路径（WorkspaceId/CollectionId）解析为实际文件系统路径。
/// 所有文件路径均以 <see cref="FileStorageOptions.RootPath"/> 为根目录。
/// </summary>
public sealed class FilePathResolver
{
	private readonly string _root;
	private readonly ContextCoreDataLayout _layout;

	/// <summary>
	/// 使用指定的存储选项初始化 <see cref="FilePathResolver"/>。
	/// </summary>
	/// <param name="options">文件存储配置选项。</param>
	public FilePathResolver(FileStorageOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		// 使用 ResolvedRootPath：统一展开环境变量并转为绝对路径
		_root = options.ResolvedRootPath;
		_layout = new ContextCoreDataLayout(options);
	}

	/// <summary>获取存储根目录的绝对路径。</summary>
	public string RootPath => _root;

	// ── Collections ──────────────────────────────────────────────────────────

	/// <summary>获取指定工作空间下所有集合的根目录。</summary>
	public string GetCollectionsDirectory(string workspaceId)
		=> Path.Combine(_root, "workspaces", workspaceId, "collections");

	/// <summary>获取指定集合的目录。</summary>
	public string GetCollectionDirectory(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionsDirectory(workspaceId), collectionId);

	/// <summary>获取集合元数据文件路径（collection.json）。</summary>
	public string GetCollectionFilePath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "collection.json");

	// ── Items ─────────────────────────────────────────────────────────────────

	/// <summary>获取存储条目元数据的目录。</summary>
	public string GetItemsDirectory(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "items");

	/// <summary>获取条目元数据 JSONL 文件路径。</summary>
	public string GetItemsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetItemsDirectory(workspaceId, collectionId), "items.jsonl");

	/// <summary>获取存储原始内容文件的目录。</summary>
	public string GetRawDirectory(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "raw");

	/// <summary>根据内容格式获取条目原始内容文件的路径。</summary>
	public string GetRawContentPath(
		string workspaceId,
		string collectionId,
		string itemId,
		ContextContentFormat format)
	{
		var ext = format switch
		{
			ContextContentFormat.Markdown => ".md",
			ContextContentFormat.Json => ".json",
			ContextContentFormat.Yaml => ".yaml",
			ContextContentFormat.Xml => ".xml",
			ContextContentFormat.Html => ".html",
			_ => ".txt"
		};
		return Path.Combine(GetRawDirectory(workspaceId, collectionId), itemId + ext);
	}

	// ── Relations ────────────────────────────────────────────────────────────

	/// <summary>获取关系数据 JSONL 文件路径。</summary>
	public string GetRelationsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "relations.jsonl");

	/// <summary>获取 Relation review / lifecycle 人工操作审核历史 JSONL 文件路径。</summary>
	public string GetRelationReviewsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "relations", "relation-reviews.jsonl");

	// ── Constraints ──────────────────────────────────────────────────────────

	/// <summary>获取集合级约束 JSONL 文件路径。</summary>
	public string GetConstraintsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "constraints", "constraints.jsonl");

	/// <summary>获取约束缺口候选项 JSONL 文件路径。</summary>
	public string GetConstraintGapCandidatesJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "constraints", "gap-candidates.jsonl");

	/// <summary>获取约束缺口候选项审核记录 JSONL 文件路径。</summary>
	public string GetConstraintGapReviewsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "constraints", "gap-reviews.jsonl");

	/// <summary>获取 CandidateConstraint 审核记录 JSONL 文件路径。</summary>
	public string GetCandidateConstraintReviewsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "constraints", "candidate-constraint-reviews.jsonl");

	/// <summary>获取工作空间全局约束 JSONL 文件路径。</summary>
	public string GetGlobalConstraintsJsonlPath(string workspaceId)
		=> Path.Combine(_root, workspaceId, "global-constraints.jsonl");

	// ── Global Context ───────────────────────────────────────────────────────

	/// <summary>获取工作空间全局上下文 JSONL 文件路径。</summary>
	public string GetGlobalContextJsonlPath(string workspaceId)
		=> Path.Combine(_root, workspaceId, "global-context.jsonl");

	// ── Index ────────────────────────────────────────────────────────────────

	/// <summary>获取索引数据目录。</summary>
	public string GetIndexDirectory(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "index");

	/// <summary>获取索引 JSONL 文件路径。</summary>
	public string GetIndexJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetIndexDirectory(workspaceId, collectionId), "index.jsonl");

	// ── Vectors ──────────────────────────────────────────────────────────────

	/// <summary>获取向量数据目录。</summary>
	public string GetVectorsDirectory(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "vectors");

	/// <summary>获取向量数据 JSONL 文件路径。</summary>
	public string GetVectorsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetVectorsDirectory(workspaceId, collectionId), "vectors.jsonl");

	/// <summary>获取 V1 vector index JSONL 文件路径；与旧向量存储分离，避免影响正式检索路径。</summary>
	public string GetVectorIndexJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetVectorsDirectory(workspaceId, collectionId), "vector-index.jsonl");

	/// <summary>获取 V1 vector reindex 报告 JSONL 文件路径。</summary>
	public string GetVectorReindexReportsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetVectorsDirectory(workspaceId, collectionId), "reindex-reports.jsonl");

	/// <summary>获取 lifecycle metadata review candidate JSONL 文件路径；不写 sidecar metadata。</summary>
	public string GetVectorLifecycleMetadataReviewCandidatesJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetVectorsDirectory(workspaceId, collectionId), "lifecycle-metadata-review-candidates.jsonl");

	/// <summary>获取 lifecycle metadata review 历史 JSONL 文件路径；不写业务 source item。</summary>
	public string GetVectorLifecycleMetadataReviewsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetVectorsDirectory(workspaceId, collectionId), "lifecycle-metadata-reviews.jsonl");

	/// <summary>获取 lifecycle metadata sidecar JSONL 文件路径；只保存旁路 override。</summary>
	public string GetVectorLifecycleSidecarMetadataJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetVectorsDirectory(workspaceId, collectionId), "lifecycle-metadata-sidecar.jsonl");

	// ── Retrieval Traces ─────────────────────────────────────────────────────

	/// <summary>获取检索 trace JSONL 文件路径。</summary>
	public string GetRetrievalTraceJsonlPath(string workspaceId, string collectionId)
		=> GetTraceArtifactPath(ArtifactKind.TraceRetrieval, workspaceId, collectionId, "retrieval-traces");

	/// <summary>获取旧版检索 trace JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyRetrievalTraceJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "retrieval", "traces.jsonl");

	/// <summary>获取检索 trace 标准目录，用于枚举日期分片。</summary>
	public string GetRetrievalTraceDirectory(string workspaceId, string collectionId)
		=> GetTraceCategoryDirectory(workspaceId, collectionId, "retrieval");

	// ── Package Build Traces ────────────────────────────────────────────────

	/// <summary>获取上下文包构建 trace JSONL 文件路径。</summary>
	public string GetPackageBuildTraceJsonlPath(string workspaceId, string collectionId)
		=> GetTraceArtifactPath(ArtifactKind.TracePackageBuild, workspaceId, collectionId, "package-build-traces");

	/// <summary>获取旧版上下文包构建 trace JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyPackageBuildTraceJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "packages", "build-traces.jsonl");

	/// <summary>获取上下文包构建 trace 标准目录，用于枚举日期分片。</summary>
	public string GetPackageBuildTraceDirectory(string workspaceId, string collectionId)
		=> GetTraceCategoryDirectory(workspaceId, collectionId, "package-build");

	/// <summary>获取上下文包策略 JSONL 文件路径。</summary>
	public string GetPackagePoliciesJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "packages", "policies.jsonl");

	// ── Memory ───────────────────────────────────────────────────────────────

	/// <summary>获取工作记忆目录。</summary>
	public string GetWorkingDirectory(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "working");

	/// <summary>获取工作记忆（最近）JSONL 文件路径。</summary>
	public string GetRecentMemoryJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetWorkingDirectory(workspaceId, collectionId), "recent-memory.jsonl");

	/// <summary>获取旧版工作记忆 JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyRecentMemoryJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "recent.jsonl");

	/// <summary>获取候选记忆 JSONL 文件路径。</summary>
	public string GetMemoryCandidatesJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "candidates.jsonl");

	/// <summary>获取 CandidateMemory 治理 JSONL 文件路径。</summary>
	public string GetCandidateMemoryJsonlPath(string workspaceId, string collectionId)
		=> GetMemoryArtifactPath(ArtifactKind.MemoryCandidateItem, workspaceId, collectionId, "candidate-memory");

	/// <summary>获取旧版 CandidateMemory 治理 JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyCandidateMemoryJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "candidate-memory.jsonl");

	/// <summary>获取 CandidateMemory 人工 review / cleanup 审核历史 JSONL 文件路径。</summary>
	public string GetCandidateMemoryReviewsJsonlPath(string workspaceId, string collectionId)
		=> GetMemoryArtifactPath(ArtifactKind.MemoryCandidateReview, workspaceId, collectionId, "candidate-memory-reviews");

	/// <summary>获取旧版 CandidateMemory 人工 review / cleanup 审核历史 JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyCandidateMemoryReviewsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "candidate-memory-reviews.jsonl");

	/// <summary>获取稳定记忆 JSONL 文件路径。</summary>
	public string GetStableMemoryJsonlPath(string workspaceId, string collectionId)
		=> GetMemoryArtifactPath(ArtifactKind.MemoryStableItem, workspaceId, collectionId, "stable-memory");

	/// <summary>获取旧版稳定记忆 JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyStableMemoryJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "stable.jsonl");

	/// <summary>获取当前活跃上下文 JSON 文件路径。</summary>
	public string GetActiveContextJsonPath(string workspaceId, string collectionId)
		=> Path.Combine(GetWorkingDirectory(workspaceId, collectionId), "active-context.json");

	/// <summary>获取旧版活跃上下文 JSON 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyActiveContextJsonPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "active-context.json");

	/// <summary>获取当前任务 JSON 文件路径。</summary>
	public string GetCurrentTaskJsonPath(string workspaceId, string collectionId)
		=> Path.Combine(GetWorkingDirectory(workspaceId, collectionId), "current-task.json");

	/// <summary>获取记忆晋升日志 JSONL 文件路径。</summary>
	public string GetPromotionLogJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "promotion-log.jsonl");

	/// <summary>获取 Promotion Review 候选项 JSONL 文件路径。</summary>
	public string GetPromotionCandidatesJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "promotion-candidates.jsonl");

	/// <summary>获取 Stable Review 候选项 JSONL 文件路径。</summary>
	public string GetStableReviewCandidatesJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "stable-review-candidates.jsonl");

	/// <summary>获取 Stable Review 候选项审核历史 JSONL 文件路径。</summary>
	public string GetStableReviewCandidateReviewsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "stable-review-candidate-reviews.jsonl");

	/// <summary>获取 Stable Memory 生命周期人工 review 审核历史 JSONL 文件路径。</summary>
	public string GetStableLifecycleReviewsJsonlPath(string workspaceId, string collectionId)
		=> GetMemoryArtifactPath(ArtifactKind.MemoryStableLifecycleReview, workspaceId, collectionId, "stable-lifecycle-reviews");

	/// <summary>获取旧版 Stable Memory 生命周期人工 review 审核历史 JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyStableLifecycleReviewsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "stable-lifecycle-reviews.jsonl");

	/// <summary>获取旧版记忆晋升日志 JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyPromotionLogJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "promotions.jsonl");

	// ── Short-Term Memory ─────────────────────────────────────────────────────

	/// <summary>获取短期记忆目录。</summary>
	public string GetShortTermDirectory(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "memory", "short-term");

	/// <summary>获取旧版短期记忆目录，用于读取迁移前的数据。</summary>
	public string GetLegacyShortTermDirectory(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "short-term");

	/// <summary>获取短期原始事件 JSONL 文件路径。</summary>
	public string GetShortTermRawEventsJsonlPath(string workspaceId, string collectionId)
		=> GetMemoryArtifactPath(ArtifactKind.MemoryShortTermRawEvent, workspaceId, collectionId, "raw-events");

	/// <summary>获取旧版短期原始事件 JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyShortTermRawEventsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetLegacyShortTermDirectory(workspaceId, collectionId), "raw-events.jsonl");

	/// <summary>获取短期工作项 JSONL 文件路径。</summary>
	public string GetShortTermWorkingItemsJsonlPath(string workspaceId, string collectionId)
		=> GetMemoryArtifactPath(ArtifactKind.MemoryShortTermWorkingItem, workspaceId, collectionId, "working-items");

	/// <summary>获取旧版短期工作项 JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyShortTermWorkingItemsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetLegacyShortTermDirectory(workspaceId, collectionId), "working-items.jsonl");

	/// <summary>获取短期记忆归档目录。</summary>
	public string GetShortTermArchiveDirectory(string workspaceId, string collectionId)
		=> Path.Combine(GetShortTermDirectory(workspaceId, collectionId), "archive");

	/// <summary>获取旧版短期记忆归档目录，用于读取迁移前的数据。</summary>
	public string GetLegacyShortTermArchiveDirectory(string workspaceId, string collectionId)
		=> Path.Combine(GetLegacyShortTermDirectory(workspaceId, collectionId), "archive");

	/// <summary>获取短期原始事件归档 JSONL 文件路径。</summary>
	public string GetShortTermArchivedRawEventsJsonlPath(string workspaceId, string collectionId)
		=> GetMemoryArtifactPath(ArtifactKind.MemoryShortTermArchive, workspaceId, collectionId, "raw-events");

	/// <summary>获取旧版短期原始事件归档 JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyShortTermArchivedRawEventsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetLegacyShortTermArchiveDirectory(workspaceId, collectionId), "raw-events.jsonl");

	/// <summary>获取短期工作项归档 JSONL 文件路径。</summary>
	public string GetShortTermArchivedWorkingItemsJsonlPath(string workspaceId, string collectionId)
		=> GetMemoryArtifactPath(ArtifactKind.MemoryShortTermArchive, workspaceId, collectionId, "working-items");

	/// <summary>获取旧版短期工作项归档 JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyShortTermArchivedWorkingItemsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetLegacyShortTermArchiveDirectory(workspaceId, collectionId), "working-items.jsonl");

	/// <summary>获取短期压缩运行历史 JSONL 文件路径。</summary>
	public string GetShortTermCompactionRunsJsonlPath(string workspaceId, string collectionId)
		=> GetMemoryArtifactPath(ArtifactKind.MemoryShortTermCompactionRun, workspaceId, collectionId, "compaction-runs");

	/// <summary>获取旧版短期压缩运行历史 JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyShortTermCompactionRunsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetLegacyShortTermDirectory(workspaceId, collectionId), "compact-runs.jsonl");

	/// <summary>获取 temporal memory 灰度区占位 JSONL 文件路径。</summary>
	public string GetTemporalMemoryItemsJsonlPath(string workspaceId, string collectionId)
		=> GetMemoryArtifactPath(ArtifactKind.MemoryTemporalItem, workspaceId, collectionId, "temporal-items");

	/// <summary>获取 temporal memory 诊断占位 JSONL 文件路径。</summary>
	public string GetTemporalMemoryDiagnosticsJsonlPath(string workspaceId, string collectionId)
		=> GetMemoryArtifactPath(ArtifactKind.MemoryTemporalDiagnostics, workspaceId, collectionId, "temporal-diagnostics");

	/// <summary>获取短期晋升候选项 JSONL 文件路径。</summary>
	public string GetShortTermPromotionCandidatesJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetShortTermDirectory(workspaceId, collectionId), "promotion-candidates.jsonl");

	/// <summary>获取短期晋升候选项审核历史 JSONL 文件路径。</summary>
	public string GetShortTermPromotionCandidateReviewsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetShortTermDirectory(workspaceId, collectionId), "promotion-candidate-reviews.jsonl");

	// ── Learning ──────────────────────────────────────────────────────────────

	/// <summary>获取学习记录目录。</summary>
	public string GetLearningDirectory(string workspaceId, string collectionId)
		=> Path.Combine(GetCollectionDirectory(workspaceId, collectionId), "learning");

	/// <summary>获取学习记录 JSONL 文件路径。</summary>
	public string GetLearningRecordsJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetLearningDirectory(workspaceId, collectionId), "records.jsonl");

	/// <summary>获取晋升反馈信号 JSONL 文件路径。</summary>
	public string GetLearningFeedbackJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetLearningDirectory(workspaceId, collectionId), "feedback.jsonl");

	/// <summary>获取学习案例 JSONL 文件路径。</summary>
	public string GetLearningCasesJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetLearningDirectory(workspaceId, collectionId), "cases.jsonl");

	/// <summary>获取 Router intent shadow trace JSONL 文件路径。</summary>
	public string GetRouterShadowTracesJsonlPath(string workspaceId, string collectionId)
		=> GetTraceArtifactPath(ArtifactKind.TraceRouterShadow, workspaceId, collectionId, "router-shadow-traces");

	/// <summary>获取旧版 Router intent shadow trace JSONL 文件路径，用于读取迁移前的数据。</summary>
	public string GetLegacyRouterShadowTracesJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetLearningDirectory(workspaceId, collectionId), "router-shadow-traces.jsonl");

	/// <summary>获取 Router intent shadow trace 标准目录，用于枚举日期分片。</summary>
	public string GetRouterShadowTracesDirectory(string workspaceId, string collectionId)
		=> GetTraceCategoryDirectory(workspaceId, collectionId, "router-shadow");

	/// <summary>获取运行时学习反馈事件 JSONL 文件路径。</summary>
	public string GetRuntimeLearningFeedbackJsonlPath(string workspaceId, string collectionId)
		=> Path.Combine(GetLearningDirectory(workspaceId, collectionId), "runtime-feedback-events.jsonl");

	private string GetMemoryArtifactPath(ArtifactKind kind, string workspaceId, string collectionId, string reportId)
		=> _layout.ResolveArtifactPath(new ArtifactDescriptor
		{
			Kind = kind,
			WorkspaceId = workspaceId,
			CollectionId = collectionId,
			ReportId = reportId,
			Extension = ".jsonl"
		});

	private string GetTraceArtifactPath(ArtifactKind kind, string workspaceId, string collectionId, string reportId)
		=> _layout.ResolveArtifactPath(new ArtifactDescriptor
		{
			Kind = kind,
			WorkspaceId = workspaceId,
			CollectionId = collectionId,
			ReportId = reportId,
			DateShard = DateTimeOffset.UtcNow.ToString("yyyyMMdd"),
			Extension = ".jsonl"
		});

	private string GetTraceCategoryDirectory(string workspaceId, string collectionId, string category)
	{
		var path = _layout.ResolveArtifactPath(new ArtifactDescriptor
		{
			Kind = ArtifactKind.TraceRetrieval,
			WorkspaceId = workspaceId,
			CollectionId = collectionId,
			ReportId = "placeholder",
			DateShard = DateTimeOffset.UtcNow.ToString("yyyyMMdd"),
			Extension = ".jsonl"
		});
		var tracesRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
		var categoryPath = Path.GetFullPath(Path.Combine(tracesRoot, category));
		if (!categoryPath.StartsWith(tracesRoot, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("trace category path escaped traces root.");
		}

		return categoryPath;
	}
}
