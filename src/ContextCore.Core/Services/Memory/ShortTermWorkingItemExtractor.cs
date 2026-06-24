using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>根据短期原始事件提炼工作项，并提供合并规则。</summary>
public interface IShortTermWorkingItemExtractor
{
    ShortTermWorkingItem? Extract(ShortTermRawEvent rawEvent, ShortTermMemoryPolicy policy);

    ShortTermWorkingItem Merge(ShortTermWorkingItem? existingItem, ShortTermWorkingItem extractedItem, ShortTermMemoryPolicy policy);
}

/// <summary>基于 inputKind / metadata 的规则型短期工作记忆提炼器。</summary>
public sealed class RuleBasedShortTermWorkingItemExtractor : IShortTermWorkingItemExtractor
{
    private const string WorkingKindKey = "workingKind";
    private const string WorkingKeyKey = "workingKey";
    private const string WorkingTitleKey = "workingTitle";
    private const string WorkingStatusKey = "workingStatus";
    private const string WorkingImportanceKey = "workingImportance";
    private const string WorkingTagsKey = "workingTags";

    public ShortTermWorkingItem? Extract(ShortTermRawEvent rawEvent, ShortTermMemoryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(rawEvent);
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.EnableWorkingItemExtraction)
        {
            return null;
        }

        var kind = ResolveWorkingKind(rawEvent, policy);
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        var title = ResolveTitle(rawEvent, policy);
        var workingKey = ResolveWorkingKey(rawEvent, kind, title, policy);
        var itemId = BuildItemId(rawEvent, kind, workingKey);
        var now = rawEvent.CreatedAt == default ? DateTimeOffset.UtcNow : rawEvent.CreatedAt;
        var importance = ResolveImportance(rawEvent, kind, policy);
        var tags = ResolveTags(rawEvent, kind, policy);
        var sourceRefs = ResolveSourceRefs(rawEvent);

        return new ShortTermWorkingItem
        {
            ItemId = itemId,
            WorkspaceId = rawEvent.WorkspaceId,
            CollectionId = rawEvent.CollectionId,
            SessionId = rawEvent.SessionId,
            Kind = kind,
            Title = title,
            Summary = string.IsNullOrWhiteSpace(rawEvent.Content) ? title : rawEvent.Content,
            Status = ResolveStatus(rawEvent, kind, policy),
            Lifecycle = ResolveLifecycle(kind),
            Importance = importance,
            Tags = tags,
            Refs = [rawEvent.EventId],
            SourceRefs = sourceRefs,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = policy.DefaultWorkingItemTtl > TimeSpan.Zero ? now.Add(policy.DefaultWorkingItemTtl) : null,
            Metadata = BuildMetadata(rawEvent, workingKey)
        };
    }

    public ShortTermWorkingItem Merge(ShortTermWorkingItem? existingItem, ShortTermWorkingItem extractedItem, ShortTermMemoryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(extractedItem);
        ArgumentNullException.ThrowIfNull(policy);

        if (existingItem is null)
        {
            return extractedItem;
        }

        return new ShortTermWorkingItem
        {
            ItemId = existingItem.ItemId,
            WorkspaceId = existingItem.WorkspaceId,
            CollectionId = existingItem.CollectionId,
            SessionId = existingItem.SessionId,
            Kind = extractedItem.Kind,
            Title = string.IsNullOrWhiteSpace(extractedItem.Title) ? existingItem.Title : extractedItem.Title,
            Summary = string.IsNullOrWhiteSpace(extractedItem.Summary) ? existingItem.Summary : extractedItem.Summary,
            Status = string.IsNullOrWhiteSpace(extractedItem.Status) ? existingItem.Status : extractedItem.Status,
            Lifecycle = string.IsNullOrWhiteSpace(extractedItem.Lifecycle) ? existingItem.Lifecycle : extractedItem.Lifecycle,
            Importance = Math.Max(existingItem.Importance, extractedItem.Importance),
            Tags = existingItem.Tags.Concat(extractedItem.Tags).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Refs = existingItem.Refs.Concat(extractedItem.Refs).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            SourceRefs = existingItem.SourceRefs.Concat(extractedItem.SourceRefs).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            CreatedAt = existingItem.CreatedAt,
            UpdatedAt = extractedItem.UpdatedAt,
            ExpiresAt = extractedItem.ExpiresAt ?? existingItem.ExpiresAt,
            Metadata = existingItem.Metadata.Concat(extractedItem.Metadata)
                .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string ResolveWorkingKind(ShortTermRawEvent rawEvent, ShortTermMemoryPolicy policy)
    {
        if (policy.EnableExplicitWorkingMetadata
            && rawEvent.Metadata.TryGetValue(WorkingKindKey, out var explicitKind)
            && !string.IsNullOrWhiteSpace(explicitKind))
        {
            return explicitKind.Trim();
        }

        var inputKind = rawEvent.Metadata.TryGetValue("inputKind", out var value) ? value : rawEvent.Tags.FirstOrDefault();
        return inputKind?.Trim().ToLowerInvariant() switch
        {
            "task_update" => "ActiveTask",
            "decision" => "RecentDecision",
            "open_question" => "OpenQuestion",
            "known_issue" => "KnownIssue",
            "warning" => "RecentWarning",
            _ => string.Empty
        };
    }

    private static string ResolveTitle(ShortTermRawEvent rawEvent, ShortTermMemoryPolicy policy)
    {
        if (policy.EnableExplicitWorkingMetadata
            && rawEvent.Metadata.TryGetValue(WorkingTitleKey, out var explicitTitle)
            && !string.IsNullOrWhiteSpace(explicitTitle))
        {
            return explicitTitle.Trim();
        }

        var source = rawEvent.Content.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return rawEvent.EventKind;
        }

        return source.Length <= 80 ? source : source[..80];
    }

    private static string ResolveWorkingKey(ShortTermRawEvent rawEvent, string kind, string title, ShortTermMemoryPolicy policy)
    {
        if (policy.EnableExplicitWorkingMetadata
            && policy.MergeByWorkingKey
            && rawEvent.Metadata.TryGetValue(WorkingKeyKey, out var explicitKey)
            && !string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey.Trim();
        }

        return $"{kind}:{NormalizeTitle(title)}";
    }

    private static string BuildItemId(ShortTermRawEvent rawEvent, string kind, string workingKey)
    {
        var input = string.Join('\u001f', rawEvent.WorkspaceId, rawEvent.CollectionId, rawEvent.SessionId ?? string.Empty, kind, workingKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"stw-{Convert.ToHexString(hash)[..20].ToLowerInvariant()}";
    }

    private static double ResolveImportance(ShortTermRawEvent rawEvent, string kind, ShortTermMemoryPolicy policy)
    {
        if (policy.EnableExplicitWorkingMetadata
            && rawEvent.Metadata.TryGetValue(WorkingImportanceKey, out var explicitImportance)
            && double.TryParse(explicitImportance, out var parsed))
        {
            return parsed;
        }

        return kind switch
        {
            "ActiveTask" => 0.95,
            "RecentDecision" => 0.85,
            "OpenQuestion" => 0.75,
            "KnownIssue" => 0.80,
            "RecentWarning" => 0.78,
            _ => 0.5
        };
    }

    private static IReadOnlyList<string> ResolveTags(ShortTermRawEvent rawEvent, string kind, ShortTermMemoryPolicy policy)
    {
        var tags = new HashSet<string>(rawEvent.Tags, StringComparer.OrdinalIgnoreCase)
        {
            kind
        };

        if (policy.EnableExplicitWorkingMetadata
            && rawEvent.Metadata.TryGetValue(WorkingTagsKey, out var explicitTags)
            && !string.IsNullOrWhiteSpace(explicitTags))
        {
            foreach (var tag in explicitTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                tags.Add(tag);
            }
        }

        return tags.ToArray();
    }

    private static IReadOnlyList<string> ResolveSourceRefs(ShortTermRawEvent rawEvent)
    {
        if (rawEvent.Metadata.TryGetValue("sourceRefs", out var sourceRefs) && !string.IsNullOrWhiteSpace(sourceRefs))
        {
            return sourceRefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return Array.Empty<string>();
    }

    private static string ResolveStatus(ShortTermRawEvent rawEvent, string kind, ShortTermMemoryPolicy policy)
    {
        if (policy.EnableExplicitWorkingMetadata
            && rawEvent.Metadata.TryGetValue(WorkingStatusKey, out var explicitStatus)
            && !string.IsNullOrWhiteSpace(explicitStatus))
        {
            return explicitStatus.Trim();
        }

        return kind switch
        {
            "ActiveTask" => "active",
            "RecentDecision" => "recorded",
            "OpenQuestion" => "open",
            "KnownIssue" => "open",
            "RecentWarning" => "warning",
            _ => "recorded"
        };
    }

    private static string ResolveLifecycle(string kind)
    {
        return kind switch
        {
            "ActiveTask" => "Active",
            "RecentDecision" => "Recent",
            "OpenQuestion" => "Open",
            "KnownIssue" => "Tracked",
            "RecentWarning" => "Recent",
            _ => "Recent"
        };
    }

    private static Dictionary<string, string> BuildMetadata(ShortTermRawEvent rawEvent, string workingKey)
    {
        var metadata = new Dictionary<string, string>(rawEvent.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["workingKey"] = workingKey,
            ["sourceEventId"] = rawEvent.EventId,
            ["sourceEventKind"] = rawEvent.EventKind
        };
        return metadata;
    }

    private static string NormalizeTitle(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            .ToArray();
        var normalized = new string(chars);
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
