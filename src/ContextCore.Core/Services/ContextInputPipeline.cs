using ContextCore.Abstractions;

namespace ContextCore.Core.Services;

/// <summary>标准化输入命令，补齐默认 sourceRef，并收敛会话/模式元数据。</summary>
public sealed class ContextInputNormalizer
{
    public ContextInputCommand Normalize(ContextInputCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var metadata = new Dictionary<string, string>(command.Metadata, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(command.SessionId))
        {
            metadata["sessionId"] = command.SessionId;
        }

        if (!string.IsNullOrWhiteSpace(command.Mode))
        {
            metadata["mode"] = command.Mode;
        }

        var normalizedSourceRefs = command.SourceRefs
            .Where(sourceRef => !string.IsNullOrWhiteSpace(sourceRef))
            .Select(static sourceRef => sourceRef.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedSourceRefs.Length == 0 && !string.IsNullOrWhiteSpace(command.Source))
        {
            normalizedSourceRefs = [$"source:{command.Source.Trim()}"];
        }

        return new ContextInputCommand
        {
            OperationId = command.OperationId.Trim(),
            WorkspaceId = command.WorkspaceId.Trim(),
            CollectionId = command.CollectionId.Trim(),
            Source = command.Source.Trim(),
            InputKind = command.InputKind.Trim(),
            ContentFormat = command.ContentFormat,
            Content = command.Content,
            SessionId = string.IsNullOrWhiteSpace(command.SessionId) ? null : command.SessionId.Trim(),
            Mode = string.IsNullOrWhiteSpace(command.Mode) ? null : command.Mode.Trim(),
            SourceRefs = normalizedSourceRefs,
            Metadata = metadata
        };
    }
}

/// <summary>校验输入层命令是否合法，复用运行时的统一验证结果格式。</summary>
public sealed class ContextInputValidator
{
    public ContextValidationResult Validate(ContextInputCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var issues = new List<ContextValidationIssue>();
        Require(issues, command.WorkspaceId, "WorkspaceId", "WorkspaceId is required.");
        Require(issues, command.CollectionId, "CollectionId", "CollectionId is required.");
        Require(issues, command.Source, "Source", "Source is required.");
        Require(issues, command.InputKind, "InputKind", "InputKind is required.");

        if (command.ContentFormat != ContextContentFormat.BinaryRef && string.IsNullOrWhiteSpace(command.Content))
        {
            issues.Add(new ContextValidationIssue
            {
                Code = "ContentRequired",
                Message = "Content is required unless ContentFormat is BinaryRef.",
                Path = "Content",
                Severity = ContextValidationSeverity.Error
            });
        }

        return new ContextValidationResult
        {
            Succeeded = issues.All(issue => issue.Severity != ContextValidationSeverity.Error),
            Issues = issues.ToArray()
        };
    }

    private static void Require(
        ICollection<ContextValidationIssue> issues,
        string value,
        string path,
        string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new ContextValidationIssue
            {
                Code = "Required",
                Message = message,
                Path = path,
                Severity = ContextValidationSeverity.Error
            });
        }
    }
}

/// <summary>计算输入内容的稳定哈希，用于幂等写入和重复检测。</summary>
public sealed class ContextInputHasher
{
    public string ComputeContentHash(ContextInputCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return BasicContextIngestionService.ComputeChecksum(command.Content);
    }
}

/// <summary>为输入条目分配单调递增的顺序号，粒度为 workspace + collection。</summary>
public sealed class ContextInputSequencer
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _sequences = new(StringComparer.OrdinalIgnoreCase);

    public long NextSequenceId(string workspaceId, string collectionId)
    {
        var key = $"{workspaceId}\u001f{collectionId}";
        return _sequences.AddOrUpdate(key, 1, static (_, current) => current + 1);
    }
}

/// <summary>
/// 输入层摄取服务：标准化、校验、哈希、顺序分配、幂等检测和最终持久化。
/// 该服务不替代旧接口，而是作为兼容层下的新标准入口。
/// </summary>
public sealed class ContextInputIngestionService
{
    private const string LegacyIdKey = "legacy.id";
    private const string LegacyTypeKey = "legacy.type";
    private const string LegacyTitleKey = "legacy.title";
    private const string LegacyTagsKey = "legacy.tags";
    private const string LegacyRefsKey = "legacy.refs";
    private const string LegacyImportanceKey = "legacy.importance";
    private const string LegacyVersionKey = "legacy.version";
    private const string LegacyChecksumKey = "legacy.checksum";
    private const string LegacyCreatedAtKey = "legacy.createdAt";
    private const string LegacyUpdatedAtKey = "legacy.updatedAt";

    private readonly BasicContextIngestionService _ingestionService;
    private readonly IContextStore _contextStore;
    private readonly ContextInputHasher _hasher;
    private readonly ContextInputNormalizer _normalizer;
    private readonly ContextInputSequencer _sequencer;
    private readonly IShortTermMemoryStore? _shortTermMemoryStore;
    private readonly ShortTermMemoryPolicy _shortTermPolicy;
    private readonly ContextInputValidator _validator;
    private readonly IShortTermWorkingItemExtractor? _workingItemExtractor;

    public ContextInputIngestionService(
        IContextStore contextStore,
        ContextInputNormalizer normalizer,
        ContextInputValidator validator,
        ContextInputHasher hasher,
        ContextInputSequencer sequencer,
        IShortTermMemoryStore? shortTermMemoryStore = null,
        IShortTermWorkingItemExtractor? workingItemExtractor = null,
        ShortTermMemoryPolicy? shortTermPolicy = null)
    {
        _contextStore = contextStore;
        _normalizer = normalizer;
        _validator = validator;
        _hasher = hasher;
        _sequencer = sequencer;
        _shortTermMemoryStore = shortTermMemoryStore;
        _workingItemExtractor = workingItemExtractor;
        _shortTermPolicy = shortTermPolicy ?? new ShortTermMemoryPolicy();
        _ingestionService = new BasicContextIngestionService(contextStore);
    }

    public async Task<ContextItem> IngestAsync(
        ContextInputCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await IngestDetailedAsync(command, cancellationToken).ConfigureAwait(false);
        return result.Item;
    }

    public async Task<ContextInputIngestionResult> IngestDetailedAsync(
        ContextInputCommand command,
        CancellationToken cancellationToken = default)
    {
        var normalized = _normalizer.Normalize(command);
        ThrowIfInvalid(_validator.Validate(normalized));

        var contentHash = _hasher.ComputeContentHash(normalized);
        var duplicate = await FindDuplicateAsync(normalized, contentHash, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            var duplicateSequenceId = _sequencer.NextSequenceId(normalized.WorkspaceId, normalized.CollectionId);
            await RecordShortTermRawEventAsync(
                normalized,
                contentHash,
                duplicateSequenceId,
                created: false,
                duplicateItemId: duplicate.Id,
                cancellationToken).ConfigureAwait(false);

            return new ContextInputIngestionResult
            {
                Item = duplicate,
                Created = false,
                Deduped = true,
                ContentHash = contentHash,
                SequenceId = ParseLong(duplicate.Metadata.GetValueOrDefault("sequenceId")) ?? 0,
                OperationId = duplicate.Metadata.GetValueOrDefault("operationId", normalized.OperationId)
            };
        }

        var sequenceId = _sequencer.NextSequenceId(normalized.WorkspaceId, normalized.CollectionId);
        var now = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string>(normalized.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = normalized.Source,
            ["inputKind"] = normalized.InputKind,
            ["contentHash"] = contentHash,
            ["sequenceId"] = sequenceId.ToString(),
            ["operationId"] = normalized.OperationId
        };

        var item = new ContextItem
        {
            Id = GetLegacyValue(metadata, LegacyIdKey) ?? Guid.NewGuid().ToString("N"),
            WorkspaceId = normalized.WorkspaceId,
            CollectionId = normalized.CollectionId,
            Type = GetLegacyValue(metadata, LegacyTypeKey) ?? normalized.InputKind,
            Title = GetLegacyValue(metadata, LegacyTitleKey),
            Content = normalized.Content,
            ContentFormat = normalized.ContentFormat,
            Tags = ParseCsv(GetLegacyValue(metadata, LegacyTagsKey)),
            Refs = ParseCsv(GetLegacyValue(metadata, LegacyRefsKey)),
            SourceRefs = normalized.SourceRefs.ToArray(),
            Metadata = metadata,
            Importance = ParseDouble(GetLegacyValue(metadata, LegacyImportanceKey)) ?? 0.5,
            Version = ParseLong(GetLegacyValue(metadata, LegacyVersionKey)) ?? 1,
            Checksum = GetLegacyValue(metadata, LegacyChecksumKey) ?? contentHash,
            CreatedAt = ParseDateTimeOffset(GetLegacyValue(metadata, LegacyCreatedAtKey)) ?? now,
            UpdatedAt = ParseDateTimeOffset(GetLegacyValue(metadata, LegacyUpdatedAtKey)) ?? now
        };

        var saved = await _ingestionService.IngestAsync(item, cancellationToken).ConfigureAwait(false);
        var rawEvent = await RecordShortTermRawEventAsync(
            normalized,
            contentHash,
            sequenceId,
            created: true,
            duplicateItemId: null,
            cancellationToken).ConfigureAwait(false);
        await ExtractWorkingItemAsync(rawEvent, cancellationToken).ConfigureAwait(false);

        return new ContextInputIngestionResult
        {
            Item = saved,
            Created = true,
            Deduped = false,
            ContentHash = contentHash,
            SequenceId = sequenceId,
            OperationId = normalized.OperationId
        };
    }

    private async Task<ShortTermRawEvent> RecordShortTermRawEventAsync(
        ContextInputCommand command,
        string contentHash,
        long sequenceId,
        bool created,
        string? duplicateItemId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string>(command.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["contentHash"] = contentHash,
            ["duplicate"] = (!created).ToString().ToLowerInvariant(),
            ["inputKind"] = command.InputKind,
            ["sourceRefs"] = string.Join(",", command.SourceRefs)
        };

        if (!string.IsNullOrWhiteSpace(duplicateItemId))
        {
            metadata["duplicateItemId"] = duplicateItemId;
        }

        var rawEvent = new ShortTermRawEvent
        {
            EventId = $"{(string.IsNullOrWhiteSpace(command.OperationId) ? Guid.NewGuid().ToString("N") : command.OperationId)}:{sequenceId}",
            OperationId = command.OperationId,
            WorkspaceId = command.WorkspaceId,
            CollectionId = command.CollectionId,
            SessionId = command.SessionId,
            Source = command.Source,
            EventKind = created ? "ingest_succeeded" : "ingest_duplicate",
            // duplicate 事件默认只记录轻量事件头，不重复保存正文；此行为已在文档中说明。
            Content = created ? command.Content : string.Empty,
            ContentFormat = command.ContentFormat,
            CreatedAt = now,
            SequenceId = sequenceId,
            Tags = BuildShortTermTags(command),
            Metadata = metadata
        };

        if (_shortTermMemoryStore is not null)
        {
            await _shortTermMemoryStore.AppendRawEventAsync(rawEvent, cancellationToken).ConfigureAwait(false);
        }

        return rawEvent;
    }

    private async Task ExtractWorkingItemAsync(
        ShortTermRawEvent rawEvent,
        CancellationToken cancellationToken)
    {
        if (_shortTermMemoryStore is null || _workingItemExtractor is null || !_shortTermPolicy.EnableWorkingItemExtraction)
        {
            return;
        }

        var extracted = _workingItemExtractor.Extract(rawEvent, _shortTermPolicy);
        if (extracted is null)
        {
            return;
        }

        var existing = await _shortTermMemoryStore.GetWorkingItemAsync(
            extracted.WorkspaceId,
            extracted.CollectionId,
            extracted.ItemId,
            cancellationToken).ConfigureAwait(false);
        var merged = _workingItemExtractor.Merge(existing, extracted, _shortTermPolicy);
        await _shortTermMemoryStore.SaveWorkingItemAsync(merged, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> BuildShortTermTags(ContextInputCommand command)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            command.InputKind
        };

        if (!string.IsNullOrWhiteSpace(command.Mode))
        {
            tags.Add($"mode:{command.Mode}");
        }

        if (!string.IsNullOrWhiteSpace(command.SessionId))
        {
            tags.Add("session-bound");
        }

        return tags.ToArray();
    }

    private async Task<ContextItem?> FindDuplicateAsync(
        ContextInputCommand command,
        string contentHash,
        CancellationToken cancellationToken)
    {
        foreach (var sourceRef in command.SourceRefs)
        {
            var existingItems = await _contextStore.QueryAsync(new ContextQuery
            {
                WorkspaceId = command.WorkspaceId,
                CollectionId = command.CollectionId,
                Refs = [sourceRef],
                Take = 100,
                IncludeContent = true
            }, cancellationToken).ConfigureAwait(false);

            var duplicate = existingItems.FirstOrDefault(item =>
                string.Equals(item.Checksum, contentHash, StringComparison.OrdinalIgnoreCase)
                && item.SourceRefs.Any(existingRef => string.Equals(existingRef, sourceRef, StringComparison.OrdinalIgnoreCase)));
            if (duplicate is not null)
            {
                return duplicate;
            }
        }

        return null;
    }

    private static string? GetLegacyValue(Dictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static IReadOnlyList<string> ParseCsv(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static double? ParseDouble(string? value)
    {
        return double.TryParse(value, out var parsed) ? parsed : null;
    }

    private static long? ParseLong(string? value)
    {
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
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

        throw new ContextInputValidationException(message, result.Issues);
    }
}
