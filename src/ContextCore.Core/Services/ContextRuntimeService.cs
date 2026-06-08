using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Core;

/// <summary>
/// <see cref="IContextRuntimeService"/> 的核心实现，协调摄入、记忆、晋升、打包等操作，
/// 并通过 <see cref="IContextEventSink"/> 发出结构化操作事件。
/// </summary>
public sealed class ContextRuntimeService : IContextRuntimeService
{
    private readonly ContextInputIngestionService _inputIngestionService;
    private readonly IContextEventSink _eventSink;
    private readonly IMemoryStore _memoryStore;
    private readonly IContextPackageBuilder _packageBuilder;
    private readonly IMemoryPromotionService _promotionService;
    private readonly IContextValidationService _validationService;

    public ContextRuntimeService(
        IContextStore contextStore,
        IMemoryStore memoryStore,
        IMemoryPromotionService promotionService,
        IContextPackageBuilder packageBuilder,
        ContextInputIngestionService inputIngestionService,
        IContextValidationService? validationService = null,
        IContextEventSink? eventSink = null)
    {
        _inputIngestionService = inputIngestionService;
        _memoryStore = memoryStore;
        _promotionService = promotionService;
        _packageBuilder = packageBuilder;
        _validationService = validationService ?? new ContextValidationService();
        _eventSink = eventSink ?? NullContextEventSink.Instance;
    }

    public Task<ContextItem> IngestAsync(
        ContextItem item,
        CancellationToken cancellationToken = default)
    {
        var command = ToInputCommand(item);
        return IngestLegacyItemAsync(command, cancellationToken);
    }

    public Task<ContextInputIngestionResult> IngestAsync(
        ContextInputCommand command,
        CancellationToken cancellationToken = default)
    {
        return IngestCommandCoreAsync("context.ingest.command", command, cancellationToken);
    }

    private Task<ContextInputIngestionResult> IngestCommandCoreAsync(
        string operationName,
        ContextInputCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var operationId = string.IsNullOrWhiteSpace(command.OperationId)
            ? Guid.NewGuid().ToString("N")
            : command.OperationId;
        var normalized = new ContextInputCommand
        {
            OperationId = operationId,
            WorkspaceId = command.WorkspaceId,
            CollectionId = command.CollectionId,
            Source = command.Source,
            InputKind = command.InputKind,
            ContentFormat = command.ContentFormat,
            Content = command.Content,
            SessionId = command.SessionId,
            Mode = command.Mode,
            SourceRefs = command.SourceRefs,
            Metadata = new Dictionary<string, string>(command.Metadata)
        };

        return ExecuteAsync(
            operationName,
            normalized.WorkspaceId,
            normalized.CollectionId,
            () => _inputIngestionService.IngestDetailedAsync(normalized, cancellationToken),
            cancellationToken,
            new Dictionary<string, string>
            {
                ["source"] = normalized.Source,
                ["inputKind"] = normalized.InputKind
            },
            operationIdOverride: operationId);
    }

    private async Task<ContextItem> IngestLegacyItemAsync(
        ContextInputCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await IngestCommandCoreAsync("context.ingest", command, cancellationToken).ConfigureAwait(false);
            return result.Item;
        }
        catch (ContextInputValidationException ex)
        {
            throw new ArgumentException(ex.Message);
        }
    }

    public Task<ContextMemoryItem> AddWorkingMemoryAsync(
        ContextMemoryItem item,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            "memory.working.add",
            item.WorkspaceId,
            item.CollectionId,
            () =>
            {
                ThrowIfInvalid(_validationService.ValidateMemoryItem(item));
                return AddWorkingMemoryCoreAsync(item, cancellationToken);
            },
            cancellationToken);
    }

    public Task<ContextPromotionRecord> PromoteMemoryAsync(
        string workspaceId,
        string collectionId,
        string sourceMemoryId,
        string strategy,
        string? reason = null,
        double confidence = 1.0,
        CancellationToken cancellationToken = default,
        string? reviewer = null)
    {
        return ExecuteAsync(
            "memory.promote",
            workspaceId,
            collectionId,
            () => _promotionService.PromoteAsync(
                workspaceId,
                collectionId,
                sourceMemoryId,
                strategy,
                reason,
                confidence,
                cancellationToken,
                reviewer),
            cancellationToken,
            new Dictionary<string, string>
            {
                ["sourceMemoryId"] = sourceMemoryId,
                ["strategy"] = strategy
            });
    }

    public Task<ContextPackage> BuildPackageAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            "package.build",
            request.WorkspaceId,
            request.CollectionId,
            () =>
            {
                ThrowIfInvalid(_validationService.ValidatePackageRequest(request));
                return _packageBuilder.BuildAsync(request, cancellationToken);
            },
            cancellationToken,
            new Dictionary<string, string>
            {
                ["tokenBudget"] = request.TokenBudget.ToString(),
                ["policyId"] = request.Policy?.Id ?? string.Empty
            });
    }

    public Task<ContextPackageBuildResult> BuildPackageDetailedAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            "package.build.detailed",
            request.WorkspaceId,
            request.CollectionId,
            () =>
            {
                ThrowIfInvalid(_validationService.ValidatePackageRequest(request));
                return _packageBuilder.BuildDetailedAsync(request, cancellationToken);
            },
            cancellationToken,
            new Dictionary<string, string>
            {
                ["tokenBudget"] = request.TokenBudget.ToString(),
                ["policyId"] = request.Policy?.Id ?? string.Empty
            });
    }

    private async Task<T> ExecuteAsync<T>(
        string operationName,
        string workspaceId,
        string? collectionId,
        Func<Task<T>> action,
        CancellationToken cancellationToken,
        Dictionary<string, string>? metadata = null,
        string? operationIdOverride = null)
    {
        var operationId = string.IsNullOrWhiteSpace(operationIdOverride)
            ? Guid.NewGuid().ToString("N")
            : operationIdOverride;
        var stopwatch = Stopwatch.StartNew();
        using var activity = ContextCoreDiagnostics.StartOperation(
            operationName,
            operationId,
            workspaceId,
            collectionId);

        // 运行时统一包裹业务操作，确保成功/失败都能落一条结构化事件供 ControlRoom 和日志查看。
        await EmitAsync(
            operationId,
            operationName,
            workspaceId,
            collectionId,
            ContextEventLevel.Trace,
            "Operation started.",
            null,
            metadata,
            cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await action().ConfigureAwait(false);
            stopwatch.Stop();

            ContextCoreDiagnostics.SetStatus(activity, succeeded: true);
            await EmitAsync(
                operationId,
                operationName,
                workspaceId,
                collectionId,
                ContextEventLevel.Information,
                "Operation succeeded.",
                stopwatch.Elapsed,
                metadata,
                cancellationToken).ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var errorMetadata = metadata is null
                ? []
                : new Dictionary<string, string>(metadata);
            errorMetadata["exception"] = ex.GetType().Name;

            ContextCoreDiagnostics.SetStatus(activity, succeeded: false, ex.Message);
            await EmitAsync(
                operationId,
                operationName,
                workspaceId,
                collectionId,
                ContextEventLevel.Error,
                ex.Message,
                stopwatch.Elapsed,
                errorMetadata,
                cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    private Task EmitAsync(
        string operationId,
        string operationName,
        string workspaceId,
        string? collectionId,
        ContextEventLevel level,
        string message,
        TimeSpan? duration,
        Dictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        return _eventSink.EmitAsync(new ContextOperationEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            OperationId = operationId,
            OperationName = operationName,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Level = level,
            Message = message,
            Duration = duration,
            Metadata = metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async Task<ContextMemoryItem> AddWorkingMemoryCoreAsync(
        ContextMemoryItem item,
        CancellationToken cancellationToken)
    {
        var workingItem = NormalizeWorkingMemoryItem(item);
        await _memoryStore.SaveAsync(workingItem, cancellationToken).ConfigureAwait(false);

        return workingItem;
    }

    private static ContextMemoryItem NormalizeWorkingMemoryItem(ContextMemoryItem item)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextMemoryItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = ContextMemoryLayer.Working,
            Status = item.Status,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = item.Version <= 0 ? 1 : item.Version,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    private static void ThrowIfInvalid(ContextValidationResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        var message = string.Join(
            "; ",
            result.Issues
                .Where(issue => issue.Severity == ContextValidationSeverity.Error)
                .Select(issue => $"{issue.Path}: {issue.Message}"));

        throw new ArgumentException(message);
    }

    private static ContextInputCommand ToInputCommand(ContextItem item)
    {
        var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["legacy.id"] = item.Id,
            ["legacy.type"] = item.Type,
            ["legacy.title"] = item.Title ?? string.Empty,
            ["legacy.tags"] = string.Join(",", item.Tags),
            ["legacy.refs"] = string.Join(",", item.Refs),
            ["legacy.importance"] = item.Importance.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["legacy.version"] = item.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["legacy.checksum"] = item.Checksum ?? string.Empty,
            ["legacy.createdAt"] = item.CreatedAt == default
                ? string.Empty
                : item.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["legacy.updatedAt"] = item.UpdatedAt == default
                ? string.Empty
                : item.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };

        return new ContextInputCommand
        {
            OperationId = metadata.GetValueOrDefault("operationId", string.Empty),
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Source = metadata.GetValueOrDefault("source", "legacy-context-item"),
            InputKind = metadata.GetValueOrDefault("inputKind", item.Type),
            ContentFormat = item.ContentFormat,
            Content = item.Content,
            SessionId = metadata.GetValueOrDefault("sessionId"),
            Mode = metadata.GetValueOrDefault("mode"),
            SourceRefs = item.SourceRefs,
            Metadata = metadata
        };
    }
}
