using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Jobs;

/// <summary>根据 <see cref="ContextJob.Kind"/> 将后台任务分派给对应处理器。</summary>
public sealed class ContextJobDispatcher : IContextJobDispatcher
{
    private readonly IReadOnlyDictionary<ContextJobKind, IContextJobProcessor> _processors;

    public ContextJobDispatcher(IEnumerable<IContextJobProcessor> processors)
    {
        _processors = processors.ToDictionary(processor => processor.Kind);
    }

    public Task DispatchAsync(ContextJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // 每种 JobKind 只允许一个处理器生效，避免同一个后台任务被多个实现重复执行。
        if (!_processors.TryGetValue(job.Kind, out var processor))
        {
            throw new NotSupportedException($"No processor registered for job kind '{job.Kind}'.");
        }

        return processor.ProcessAsync(job, cancellationToken);
    }
}

/// <summary>执行压缩任务：读取输入、调用压缩器，并持久化摘要、索引提示和派生关系。</summary>
public sealed class CompressionJobProcessor : IContextJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IContextStore _contextStore;
    private readonly IContextIndex _index;
    private readonly IContextCompressor _compressor;
    private readonly IRelationStore _relationStore;
    private readonly RelationBuilder _relationBuilder;

    public CompressionJobProcessor(
        IContextStore contextStore,
        IContextIndex index,
        IContextCompressor compressor,
        IRelationStore relationStore,
        RelationBuilder relationBuilder)
    {
        _contextStore = contextStore;
        _index = index;
        _compressor = compressor;
        _relationStore = relationStore;
        _relationBuilder = relationBuilder;
    }

    public ContextJobKind Kind => ContextJobKind.Compression;

    public async Task ProcessAsync(ContextJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var request = ResolveRequest(job);
        if (request.Inputs.Count == 0)
        {
            // 作业载荷可只描述操作参数；未显式提供 Inputs 时从集合中拉取可压缩原始上下文。
            var inputs = await _contextStore.QueryAsync(new ContextQuery
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                ExcludedTypes = ["summary"],
                IncludeDerived = false,
                IncludeContent = true,
                Take = 100
            }, cancellationToken).ConfigureAwait(false);

            request = CopyRequest(request, inputs);
        }

        var response = await _compressor.CompressAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Status == CompressionStatus.Failed)
        {
            var message = response.Errors.Count == 0
                ? "Compression failed."
                : string.Join("; ", response.Errors.Select(error => error.Message));
            throw new InvalidOperationException(message);
        }

        foreach (var item in response.GeneratedItems)
        {
            await _contextStore.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in response.IndexHints)
        {
            await _index.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        foreach (var relation in _relationBuilder.BuildForCompressionResponse(response))
        {
            await _relationStore.SaveAsync(relation, cancellationToken).ConfigureAwait(false);
        }
    }

    private static CompressionRequest ResolveRequest(ContextJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.PayloadJson))
        {
            var request = JsonSerializer.Deserialize<CompressionRequest>(job.PayloadJson, JsonOptions);
            if (request is not null)
            {
                return EnsureJobDefaults(job, request);
            }
        }

        return new CompressionRequest
        {
            OperationId = job.JobId,
            WorkspaceId = job.WorkspaceId,
            CollectionId = job.CollectionId,
            TaskKind = CompressionTaskKind.Summarize,
            Options = new CompressionOptions
            {
                Depth = CompressionDepth.Light,
                GenerateIndexHints = true,
                PreserveSourceRefs = true,
                TargetTokenBudget = 300
            }
        };
    }

    private static CompressionRequest EnsureJobDefaults(ContextJob job, CompressionRequest request)
    {
        return new CompressionRequest
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId) ? job.JobId : request.OperationId,
            WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? job.WorkspaceId : request.WorkspaceId,
            CollectionId = string.IsNullOrWhiteSpace(request.CollectionId) ? job.CollectionId : request.CollectionId,
            TaskKind = request.TaskKind,
            SubKind = request.SubKind,
            Inputs = request.Inputs,
            Options = request.Options,
            Metadata = request.Metadata,
            ExtensionPayloadJson = request.ExtensionPayloadJson
        };
    }

    private static CompressionRequest CopyRequest(CompressionRequest request, IReadOnlyList<ContextItem> inputs)
    {
        return new CompressionRequest
        {
            OperationId = request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            TaskKind = request.TaskKind,
            SubKind = request.SubKind,
            Inputs = inputs,
            Options = request.Options,
            Metadata = request.Metadata,
            ExtensionPayloadJson = request.ExtensionPayloadJson
        };
    }
}

/// <summary>用于已登记但尚未实现的任务类型，让队列失败原因保持明确。</summary>
public sealed class UnsupportedJobProcessor : IContextJobProcessor
{
    public UnsupportedJobProcessor(ContextJobKind kind)
    {
        Kind = kind;
    }

    public ContextJobKind Kind { get; }

    public Task ProcessAsync(ContextJob job, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException($"Job kind '{Kind}' is not supported yet.");
    }
}
