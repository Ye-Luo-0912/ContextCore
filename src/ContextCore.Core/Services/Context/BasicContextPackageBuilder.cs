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
    private int _packageTraceWriteFailures;
    private int _decisionTraceWriteFailures;

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
        _sectionAssembler = new SectionAssembler(EstimatePackageTokens, TruncatePackageTokens);
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
        var policy = request.Policy ?? PackagePolicyResolver.CreateDefaultProductionPolicy(request);

        // 入口一次解析所有构建选项，集中 PackagePolicyResolver 调用
        var tokenContext = CreateTokenEstimationContext(request);
        var options = ResolvedPackageOptions.Resolve(request, policy, tokenContext);

        // 热点读路径缓存：按请求指纹缓存 PackageTemplate（不可变），命中时投影为新的 ContextPackageBuildResult。
        // PackageId/BuildId/CreatedAt/响应 metadata 在每次投影时重新生成，缓存前后请求身份完全隔离。
        // trace 写入仅在缓存 miss 时触发（factory 内部），缓存命中无需重复记录。
        if (_cacheAccessor is not null)
        {
            var cacheKey = StateCacheKey.From($"pkg:{options.WorkspaceId}:{options.CollectionId}:{PackageRequestFingerprintBuilder.Build(request, policy)}");
            var scopes = PackageRequestFingerprintBuilder.BuildDependencyScopes(options.WorkspaceId, options.CollectionId ?? string.Empty);
            var template = await _cacheAccessor.GetOrAddAsync<PackageTemplate>(
                cacheKey, scopes,
                ct => BuildAndTraceTemplateAsync(options, ct),
                cancellationToken).ConfigureAwait(false);
            CoreMetrics.PackageBuildDuration.Record(sw.Elapsed.TotalMilliseconds);
            // 缓存命中/未命中均通过投影生成独立结果对象（新 PackageId/BuildId/CreatedAt/metadata）
            return _resultProjector.ProjectResult(template, options, _packageTraceWriteFailures, _decisionTraceWriteFailures);
        }

        // 无缓存路径：构建模板 → 投影 → 写入 trace
        PackageTemplate tmpl = await BuildTemplateAsync(options, cancellationToken).ConfigureAwait(false);
        var result = _resultProjector.ProjectResult(tmpl, options);
        await WriteTracesAsync(result, cancellationToken).ConfigureAwait(false);
        CoreMetrics.PackageBuildDuration.Record(sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <summary>执行构建并写入 trace（package trace + decision trace，fail-open）。返回 PackageTemplate。</summary>
    private async Task<PackageTemplate> BuildAndTraceTemplateAsync(
        ResolvedPackageOptions options,
        CancellationToken cancellationToken)
    {
        var template = await BuildTemplateAsync(options, cancellationToken).ConfigureAwait(false);
        // trace 需要完整的 ContextPackageBuildResult，投影一次用于 trace 写入
        var traceResult = _resultProjector.ProjectResult(template, options, _packageTraceWriteFailures, _decisionTraceWriteFailures);
        await WriteTracesAsync(traceResult, cancellationToken).ConfigureAwait(false);
        return template;
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
            catch (Exception)
            {
                // P5-0.4: package trace 写入失败不得影响正式 package 构建，但需记录降级指标。
                Interlocked.Increment(ref _packageTraceWriteFailures);
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
            catch (Exception)
            {
                // decision trace 写入失败不得影响正式 package 输出，但需记录降级指标。
                Interlocked.Increment(ref _decisionTraceWriteFailures);
            }
        }
    }

    public static int EstimateTokens(string? content)
    {
        return LegacyCharacterTokenizer.EstimateTokenCount(content);
    }

    private TokenEstimationContext CreateTokenEstimationContext(ContextPackageRequest request)
    {
        var modelName = PackagePolicyResolver.ResolveTokenizerModel(request);
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

    /// <summary>
    /// 一次 tokenize 截断到 token 预算内，委托到 tokenizer 的 TruncateForTokenBudget。
    /// 消除 SectionAssembler.TrimToTokenBudget 中的二分重算。
    /// </summary>
    private string TruncatePackageTokens(string content, int tokenBudget, TokenEstimationContext tokenContext)
    {
        if (tokenBudget <= 0 || string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }
        return _tokenizerResolver.TruncateForTokenBudget(content, tokenBudget, tokenContext.ModelName).TruncatedContent;
    }

    /// <summary>
    /// 统一构建流水线入口：委托到四阶段（PackageInputLoader -> CandidateSelector/SectionAssembler -> ResultProjector）。
    /// 返回不可变 PackageTemplate，由调用方投影为 ContextPackageBuildResult。
    /// </summary>
    private async Task<PackageTemplate> BuildTemplateAsync(
        ResolvedPackageOptions options,
        CancellationToken cancellationToken)
    {
        // 四阶段流水线：加载 -> 选择(装配) -> 模板投影。各阶段保持原 BuildWithPolicyAsync 的变异顺序。
        var inputs = await _inputLoader.LoadAsync(options, cancellationToken).ConfigureAwait(false);
        var selection = await _candidateSelector.SelectCandidatesAsync(
            inputs, options, cancellationToken).ConfigureAwait(false);
        return _resultProjector.ProjectTemplate(selection, options);
    }
}
