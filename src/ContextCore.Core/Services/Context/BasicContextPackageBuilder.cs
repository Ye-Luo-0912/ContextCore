using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Learning.V14_0;

namespace ContextCore.Core;

/// <summary>
/// 默认上下文包构建器，按请求或策略从原始上下文、记忆、约束、全局项和关系中选择内容。
/// </summary>
public sealed class BasicContextPackageBuilder : IContextPackageBuilder
{
    private readonly IConstraintStore? _constraintStore;
    private readonly IGlobalContextStore? _globalContextStore;
    private readonly IMemoryStore? _memoryStore;
    private readonly IContextPackageBuildTraceStore? _traceStore;
    private readonly IDecisionTraceStore? _decisionTraceStore;
    private readonly IContextTokenizerResolver _tokenizerResolver;
    private readonly IWorkingMemoryService? _workingMemoryService;
    private readonly IContextStore _store;
    private readonly IRuntimeCandidateTraceSink _runtimeCandidateTraceSink;
    private readonly GraphExpansionCoordinator _graphExpansionCoordinator;
    private readonly PackageTraceRecorder _traceRecorder;
    private readonly ContextStateCacheAccessor? _cacheAccessor;
    private readonly AsyncLocal<string?> _currentOperationId = new();
    private readonly AsyncLocal<string?> _currentRequestId = new();
    // 构建流水线四阶段（从原 BuildWithPolicyAsync 单体方法提取，保持字节级确定性输出不变）：
    //   PackageInputLoader -> CandidateSelector(使用 SectionAssembler) -> ResultProjector
    private readonly PackageInputLoader _inputLoader;
    private readonly SectionAssembler _sectionAssembler;
    private readonly CandidateSelector _candidateSelector;
    private readonly ResultProjector _resultProjector;
    private int _decisionTraceWriteFailures;
    private DateTimeOffset _decisionTraceLastFailureAt;
    private string? _decisionTraceLastFailureCategory;

    /// <summary>decision trace 写入失败次数（fail-open，不影响正式 package 输出）。</summary>
    public int DecisionTraceWriteFailures => _decisionTraceWriteFailures;

    /// <summary>decision trace 最近一次写入失败时间；无失败则为 null。</summary>
    public DateTimeOffset? DecisionTraceLastFailureAt =>
        _decisionTraceWriteFailures > 0 ? _decisionTraceLastFailureAt : null;

    /// <summary>decision trace 最近一次写入失败的异常类别（Type.Name）；无失败则为 null。</summary>
    public string? DecisionTraceLastFailureCategory => _decisionTraceLastFailureCategory;

    /// <summary>decision trace sink 类型名（用于诊断报告）；未配置则为 null。</summary>
    public string? DecisionTraceSinkType => _decisionTraceStore?.GetType().FullName;

    /// <summary>observability 是否处于降级状态（任一 trace 路径存在写入失败）。</summary>
    public bool IsObservabilityDegraded =>
        _decisionTraceWriteFailures > 0 || _traceRecorder.TraceWriteFailures > 0;

    public BasicContextPackageBuilder(IContextStore store)
        : this(store, null, null, null, null, null, null)
    {
    }

    public BasicContextPackageBuilder(
        IContextStore store,
        IConstraintStore? constraintStore,
        IGlobalContextStore? globalContextStore,
        IMemoryStore? memoryStore,
        IRelationStore? relationStore,
        IContextPackageBuildTraceStore? traceStore = null,
        IContextTokenizerResolver? tokenizerResolver = null,
        IWorkingMemoryService? workingMemoryService = null,
        IDecisionTraceStore? decisionTraceStore = null,
        IRuntimeCandidateTraceSink? runtimeCandidateTraceSink = null,
        RelationTraversalEngine? traversalEngine = null,
        ContextStateCacheAccessor? cacheAccessor = null)
    {
        _store = store;
        _constraintStore = constraintStore;
        _globalContextStore = globalContextStore;
        _memoryStore = memoryStore;
        _traceStore = traceStore;
        _tokenizerResolver = tokenizerResolver ?? new DefaultContextTokenizerResolver();
        _workingMemoryService = workingMemoryService;
        _graphExpansionCoordinator = new GraphExpansionCoordinator(
            store,
            relationStore,
            traversalEngine);
        _decisionTraceStore = decisionTraceStore;
        _runtimeCandidateTraceSink = runtimeCandidateTraceSink ?? new NullRuntimeCandidateTraceSink();
        _traceRecorder = new PackageTraceRecorder(
            _runtimeCandidateTraceSink,
            () => _currentOperationId.Value,
            () => _currentRequestId.Value);
        _cacheAccessor = cacheAccessor;
        // 初始化四阶段流水线：SectionAssembler/CandidateSelector 共享同一 EstimatePackageTokens 委托，
        // 保证 token 估算与原内联实现完全一致。
        _sectionAssembler = new SectionAssembler(EstimatePackageTokens);
        _inputLoader = new PackageInputLoader(
            _store,
            _constraintStore,
            _globalContextStore,
            _memoryStore,
            _workingMemoryService);
        _candidateSelector = new CandidateSelector(
            _sectionAssembler,
            _traceRecorder,
            _graphExpansionCoordinator,
            EstimatePackageTokens);
        _resultProjector = new ResultProjector(_traceRecorder);
    }

    public async Task<ContextPackage> BuildAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await BuildDetailedAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Package;
    }

    public async Task<ContextPackageBuildResult> BuildDetailedAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // TRACE-01: 设置请求级 trace 上下文（AsyncLocal），替代全局静态状态。
        var prevOpId = _currentOperationId.Value;
        var prevReqId = _currentRequestId.Value;
        _currentOperationId.Value = request.OperationId ?? Guid.NewGuid().ToString("N");
        _currentRequestId.Value = request.RequestId ?? Guid.NewGuid().ToString("N");
        try
        {
            return await BuildDetailedCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _currentOperationId.Value = prevOpId;
            _currentRequestId.Value = prevReqId;
        }
    }

    private async Task<ContextPackageBuildResult> BuildDetailedCoreAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 统一打包流水线：当调用方未显式提供 Policy 时，使用默认生产 Policy 委托到唯一流水线。
        // 原 Legacy 路径（按 item ID 拆 section、Kind="raw"）已合并到 Policy 路径的 recent_context section。
        var policy = request.Policy ?? CreateDefaultProductionPolicy(request);

        // 热点读路径缓存：按请求指纹缓存构建结果，任一依赖 store 写入即失效。
        // 命中时跳过 BuildWithPolicyAsync 与 trace 写入（trace 为 fail-open 可观测性，缓存命中无需重复记录）。
        // 工厂内执行 build + trace，仅在 miss 时触发；single-flight 合并并发 miss。
        if (_cacheAccessor is not null)
        {
            var workspaceId = PackagePolicyResolver.NormalizeRequiredValue(request.WorkspaceId);
            var collectionId = PackagePolicyResolver.NormalizeRequiredValue(policy.CollectionId, request.CollectionId);
            var cacheKey = StateCacheKey.From($"pkg:{workspaceId}:{collectionId}:{BuildRequestFingerprint(request, policy)}");
            var scopes = BuildPackageDependencyScopes(workspaceId, collectionId);
            var cached = await _cacheAccessor.GetOrAddAsync(
                cacheKey, scopes,
                ct => BuildAndTraceAsync(request, policy, ct),
                cancellationToken).ConfigureAwait(false);
            CoreMetrics.PackageBuildDuration.Record(sw.Elapsed.TotalMilliseconds);
            // 缓存命中时返回深拷贝，避免调用方修改缓存对象（PackageId/BuildId/CreatedAt 复用、集合可变性）
            return CloneBuildResult(cached);
        }

        // 无缓存路径：原有逻辑
        ContextPackageBuildResult result = await BuildWithPolicyAsync(request, policy, cancellationToken)
            .ConfigureAwait(false);
        await WriteTracesAsync(result, cancellationToken).ConfigureAwait(false);
        CoreMetrics.PackageBuildDuration.Record(sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <summary>执行构建并写入 trace（package trace + decision trace，fail-open）。</summary>
    private async Task<ContextPackageBuildResult> BuildAndTraceAsync(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        CancellationToken cancellationToken)
    {
        var result = await BuildWithPolicyAsync(request, policy, cancellationToken).ConfigureAwait(false);
        await WriteTracesAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>写入 package trace 与 decision trace。两者均为 fail-open：写入失败不影响正式 package 输出。</summary>
    private async Task WriteTracesAsync(ContextPackageBuildResult result, CancellationToken cancellationToken)
    {
        if (_traceStore is not null)
        {
            try
            {
                await _traceStore.SaveAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // P5-0.4: package trace 写入失败不得影响正式 package 构建。
            }
        }

        // V17.0: 投影只读 decision trace，不改变 result。
        if (_decisionTraceStore is not null)
        {
            try
            {
                var decisionRecord = ContextDecisionProjector.ProjectPackage(result);
                await _decisionTraceStore.SaveAsync(decisionRecord, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // decision trace 写入失败不得影响正式 package 输出，但需记录降级指标。
                Interlocked.Increment(ref _decisionTraceWriteFailures);
                _decisionTraceLastFailureAt = DateTimeOffset.UtcNow;
                _decisionTraceLastFailureCategory = ex.GetType().Name;
            }
        }
    }

    /// <summary>
    /// 深拷贝缓存命中的构建结果，确保每次返回独立对象实例。
    /// 避免调用方修改缓存对象导致后续命中返回被污染的数据。
    /// </summary>
    private static ContextPackageBuildResult CloneBuildResult(ContextPackageBuildResult source)
    {
        return new ContextPackageBuildResult
        {
            BuildId = source.BuildId,
            Package = new ContextPackage
            {
                PackageId = source.Package.PackageId,
                WorkspaceId = source.Package.WorkspaceId,
                CollectionId = source.Package.CollectionId,
                EstimatedTokens = source.Package.EstimatedTokens,
                CreatedAt = source.Package.CreatedAt,
                Sections = source.Package.Sections.ToArray(),
                Metadata = new Dictionary<string, string>(source.Package.Metadata)
            },
            SelectedItems = source.SelectedItems.ToArray(),
            ItemReferences = source.ItemReferences.ToArray(),
            DroppedItems = source.DroppedItems.ToArray(),
            Uncertainties = source.Uncertainties.ToArray(),
            Budget = source.Budget,
            Output = source.Output,
            TokenBudget = source.TokenBudget,
            EstimatedTokens = source.EstimatedTokens,
            Metadata = new Dictionary<string, string>(source.Metadata),
            Plan = source.Plan,
            CreatedAt = source.CreatedAt
        };
    }

    /// <summary>
    /// 构建请求指纹：仅包含影响构建输出的字段，排除 OperationId/RequestId（per-call GUID）。
    /// 相同指纹的请求产生相同 package（在依赖 scope 未变更的前提下）。
    /// 使用长度前缀编码防止分隔符碰撞（输入值中包含 | 或 : 不会导致不同输入产生相同指纹）。
    /// </summary>
    internal static string BuildRequestFingerprint(ContextPackageRequest request, ContextPackagePolicy policy)
    {
        var sb = new StringBuilder();
        AppendField(sb, request.WorkspaceId);
        AppendField(sb, request.CollectionId);
        AppendField(sb, request.QueryText);
        AppendSorted(sb, request.RequiredTags);
        AppendSorted(sb, request.RequiredTypes);
        AppendField(sb, request.TokenBudget.ToString());
        AppendField(sb, ((int)request.Mode).ToString());
        AppendField(sb, request.IncludeRecent.ToString());
        AppendField(sb, request.IsAuditMode?.ToString() ?? "null");
        AppendField(sb, ResolveTokenizerModel(request));
        // mustHit IDs 影响候选排序与选取
        AppendSorted(sb, PackagePolicyResolver.ResolvePackageMustHitIds(request));
        // currentTask 元数据影响 current_task section 内容
        AppendField(sb, RequestTaskResolver.HasRequestCurrentTaskMetadata(request).ToString());
        if (RequestTaskResolver.HasRequestCurrentTaskMetadata(request))
        {
            AppendField(sb, RequestTaskResolver.ReadRequestMetadata(request, "currentTaskId", "taskId", "current_task.id"));
            AppendField(sb, RequestTaskResolver.ReadRequestMetadata(request, "currentTaskTitle", "taskTitle", "current_task.title"));
            AppendField(sb, RequestTaskResolver.ReadRequestMetadata(request, "currentTaskDescription", "taskDescription", "current_task.description"));
            AppendField(sb, RequestTaskResolver.ReadRequestMetadata(request, "currentTaskStatus", "taskStatus", "current_task.status"));
        }
        // policy 指纹
        AppendField(sb, policy.Id);
        AppendField(sb, ((int)policy.Mode).ToString());
        AppendField(sb, policy.TokenBudget.ToString());
        AppendField(sb, policy.IncludeGlobalContext.ToString());
        AppendField(sb, policy.IncludeHardConstraints.ToString());
        AppendField(sb, policy.IncludeSoftConstraints.ToString());
        AppendField(sb, policy.IncludeWorkingMemory.ToString());
        AppendField(sb, policy.IncludeStableMemory.ToString());
        AppendField(sb, policy.IncludeRecentRawContext.ToString());
        AppendField(sb, policy.MaxRecentItems.ToString());
        AppendField(sb, policy.EnableStrictRelevanceFilter.ToString());
        AppendField(sb, policy.IsAuditMode?.ToString() ?? "null");
        // SectionOrder 必须保持声明顺序（影响最终 section 排列），不能排序
        AppendOrdered(sb, policy.SectionOrder);
        AppendSortedKeyValuePairs(sb, policy.SectionPriorities);
        AppendSortedKeyValuePairs(sb, policy.SectionTokenBudgets);
        AppendSortedStringDictionary(sb, policy.Metadata);
        // request.Metadata 会被完整复制到响应，必须纳入指纹以区分不同 metadata 的请求
        AppendSortedStringDictionary(sb, request.Metadata);
        return sb.ToString();
    }

    /// <summary>长度前缀编码：len:value| 格式，防止值中包含分隔符导致碰撞。</summary>
    private static void AppendField(StringBuilder sb, string? value)
    {
        var v = value ?? string.Empty;
        sb.Append(v.Length).Append(':').Append(v).Append('|');
    }

    private static void AppendSorted(StringBuilder sb, IEnumerable<string>? values)
    {
        if (values is null)
        {
            sb.Append("-|");
            return;
        }
        // 避免在空集合上分配数组
        if (values is ICollection<string> { Count: 0 })
        {
            sb.Append("0:|");
            return;
        }
        var sorted = values.OrderBy(v => v, StringComparer.Ordinal).ToArray();
        sb.Append(sorted.Length).Append(':');
        foreach (var v in sorted)
        {
            sb.Append(v.Length).Append(':').Append(v).Append(',');
        }
        sb.Append('|');
    }

    /// <summary>保持声明顺序写入（用于 SectionOrder 等顺序敏感字段）。</summary>
    private static void AppendOrdered(StringBuilder sb, IEnumerable<string>? values)
    {
        if (values is null)
        {
            sb.Append("-|");
            return;
        }
        if (values is ICollection<string> { Count: 0 })
        {
            sb.Append("0:|");
            return;
        }
        var arr = values.ToArray();
        sb.Append(arr.Length).Append(':');
        foreach (var v in arr)
        {
            sb.Append(v.Length).Append(':').Append(v).Append(',');
        }
        sb.Append('|');
    }

    /// <summary>对 string 字典排序后写入指纹（key=value 格式）。</summary>
    private static void AppendSortedStringDictionary(StringBuilder sb, IReadOnlyDictionary<string, string>? dict)
    {
        if (dict is null || dict.Count == 0)
        {
            sb.Append("-|");
            return;
        }
        var keys = dict.Keys.ToArray();
        Array.Sort(keys, StringComparer.Ordinal);
        sb.Append(keys.Length).Append(':');
        foreach (var key in keys)
        {
            var entry = key + "=" + dict[key];
            sb.Append(entry.Length).Append(':').Append(entry).Append(',');
        }
        sb.Append('|');
    }

    /// <summary>
    /// 对键值对集合排序后写入指纹，避免 LINQ Select 分配中间字符串数组和 ToArray。
    /// 直接在 StringBuilder 上拼接 "key:value" 格式。
    /// </summary>
    private static void AppendSortedKeyValuePairs(StringBuilder sb, IReadOnlyDictionary<string, int>? pairs)
    {
        if (pairs is null || pairs.Count == 0)
        {
            sb.Append("-|");
            return;
        }
        // 复用 pairs.Keys 排序，避免分配 KeyValuePair 数组
        var keys = pairs.Keys.ToArray();
        Array.Sort(keys, StringComparer.Ordinal);
        sb.Append(keys.Length).Append(':');
        foreach (var key in keys)
        {
            var entry = key + ":" + pairs[key].ToString();
            sb.Append(entry.Length).Append(':').Append(entry).Append(',');
        }
        sb.Append('|');
    }

    /// <summary>
    /// 构建包依赖的 scope 集合。任一 store 在相关 workspace+collection 上写入即失效缓存。
    /// 包含 WorkingMemoryService 以覆盖 SetCurrentTaskAsync 等操作导致的 current_task section 变更。
    /// GlobalContextStore 同时订阅 collection-level 和 workspace-level scope，
    /// 因为全局数据写入时 CollectionId 可能为空（workspace 级），decorator 会用 string.Empty 作为 CollectionId。
    /// </summary>
    private static DependencyScopeSet BuildPackageDependencyScopes(string workspaceId, string collectionId)
    {
        return new DependencyScopeSet(
            new CacheInvalidationKey("ContextStore", workspaceId, collectionId, null),
            new CacheInvalidationKey("MemoryStore", workspaceId, collectionId, null),
            new CacheInvalidationKey("ConstraintStore", workspaceId, collectionId, null),
            // collection 级全局数据
            new CacheInvalidationKey("GlobalContextStore", workspaceId, collectionId, null),
            // workspace 级全局数据（CollectionId=null 的全局条目写入时 decorator 用 string.Empty）
            new CacheInvalidationKey("GlobalContextStore", workspaceId, string.Empty, null),
            new CacheInvalidationKey("RelationStore", workspaceId, collectionId, null),
            new CacheInvalidationKey("WorkingMemoryService", workspaceId, collectionId, null));
    }

    public static int EstimateTokens(string? content)
    {
        return LegacyCharacterTokenizer.EstimateTokenCount(content);
    }

    /// <summary>
    /// 创建默认生产 Policy，用于未显式提供 Policy 的请求。
    /// 仅启用最近原始上下文（与原 Legacy 路径行为一致），约束/记忆/全局上下文需调用方显式提供 Policy 才会纳入。
    /// TokenBudget 由请求或模式预算解析。
    /// </summary>
    private static ContextPackagePolicy CreateDefaultProductionPolicy(ContextPackageRequest request)
    {
        return new ContextPackagePolicy
        {
            Id = "default-production",
            WorkspaceId = request.WorkspaceId,
            CollectionId = string.IsNullOrWhiteSpace(request.CollectionId) ? null : request.CollectionId,
            Name = "DefaultProduction",
            Description = "默认生产策略：未显式提供 Policy 时使用，仅启用最近原始上下文（与原 Legacy 路径一致）。",
            Mode = request.Mode,
            IncludeGlobalContext = false,
            IncludeHardConstraints = false,
            IncludeSoftConstraints = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            IncludeRecentRawContext = true,
            MaxRecentItems = 20,
            IsAuditMode = request.IsAuditMode
        };
    }

    private TokenEstimationContext CreateTokenEstimationContext(ContextPackageRequest request)
    {
        var modelName = ResolveTokenizerModel(request);
        var estimate = _tokenizerResolver.Estimate(string.Empty, modelName);
        return new TokenEstimationContext(
            estimate.ModelName,
            estimate.Source,
            estimate.IsFallback);
    }

    private int EstimatePackageTokens(string? content, TokenEstimationContext tokenContext)
    {
        return _tokenizerResolver.Estimate(content, tokenContext.ModelName).TokenCount;
    }

    private static string? ResolveTokenizerModel(ContextPackageRequest request)
    {
        foreach (var key in new[] { "tokenizerModel", "modelName", "model", "llm.model", "route.model" })
        {
            if (request.Metadata.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// 统一构建流水线入口：委托到四阶段（PackageInputLoader -> CandidateSelector/SectionAssembler -> ResultProjector）。
    /// 原单体逻辑已按数据流边界拆分到独立 stage 类，保持字节级确定性输出不变。
    /// 缓存路径（BuildDetailedCoreAsync）与 trace 写入（WriteTracesAsync）仍保留在本类。
    /// </summary>
    private async Task<ContextPackageBuildResult> BuildWithPolicyAsync(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        CancellationToken cancellationToken)
    {
        var tokenContext = CreateTokenEstimationContext(request);

        // 四阶段流水线：加载 -> 选择(装配) -> 投影。各阶段保持原 BuildWithPolicyAsync 的变异顺序。
        var inputs = await _inputLoader.LoadAsync(request, policy, cancellationToken).ConfigureAwait(false);
        var selection = await _candidateSelector.SelectCandidatesAsync(
            inputs, request, policy, tokenContext, cancellationToken).ConfigureAwait(false);
        return _resultProjector.ProjectResult(selection, request, policy, tokenContext);
    }
}

internal sealed record TokenEstimationContext(string? ModelName, string Source, bool IsFallback);

internal sealed class MergedContextConstraint
{
    public MergedContextConstraint(
        ContextConstraint constraint,
        string priorityLabel,
        int priorityRank,
        int index)
    {
        Constraint = constraint;
        PriorityLabel = priorityLabel;
        PriorityRank = priorityRank;
        Index = index;
    }

    public ContextConstraint Constraint { get; }

    public string PriorityLabel { get; }

    public int PriorityRank { get; }

    public int Index { get; }
}

internal sealed class ContextEvidenceEntry
{
    public ContextEvidenceEntry(
        string itemId,
        string sectionName,
        string kind,
        string type,
        IReadOnlyList<string> sourceRefs,
        string reason)
    {
        ItemId = itemId;
        SectionName = sectionName;
        Kind = kind;
        Type = type;
        SourceRefs = sourceRefs;
        Reason = reason;
    }

    public string ItemId { get; }

    public string SectionName { get; }

    public string Kind { get; }

    public string Type { get; }

    public IReadOnlyList<string> SourceRefs { get; }

    public string Reason { get; }
}
