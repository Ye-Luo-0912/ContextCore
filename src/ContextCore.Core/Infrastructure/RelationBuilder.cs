using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 根据压缩响应或上下文包构建 <see cref="ContextRelation"/> 列表的工具类。
/// </summary>
public sealed class RelationBuilder
{
    /// <summary>从压缩响应中构建关系列表，包括"派生自"与"摘要"关系。</summary>
    public IReadOnlyList<ContextRelation> BuildForCompressionResponse(CompressionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var relations = new List<ContextRelation>();
        var now = DateTimeOffset.UtcNow;

        foreach (var generatedItem in response.GeneratedItems)
        {
            if (string.IsNullOrWhiteSpace(generatedItem.WorkspaceId)
                || string.IsNullOrWhiteSpace(generatedItem.CollectionId)
                || string.IsNullOrWhiteSpace(generatedItem.Id))
            {
                continue;
            }

            foreach (var sourceId in ResolveDerivedFrom(generatedItem))
            {
                relations.Add(CreateCompressionRelation(
                    generatedItem,
                    sourceId,
                    ContextRelationTypes.DerivedFrom,
                    response,
                    now));

                if (string.Equals(generatedItem.Type, "summary", StringComparison.OrdinalIgnoreCase))
                {
                    relations.Add(CreateCompressionRelation(
                        generatedItem,
                        sourceId,
                        ContextRelationTypes.Summarizes,
                        response,
                        now));
                }
            }

            if (!string.IsNullOrWhiteSpace(response.OperationId))
            {
                relations.Add(CreateGeneratedByRelation(generatedItem, response, now));
            }
        }

        return [.. relations
            .GroupBy(relation => RelationKey(relation), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    /// <summary>从上下文条目的 <see cref="ContextItem.Refs"/> 构建通用 <c>related_to</c> 关系。</summary>
    public IReadOnlyList<ContextRelation> BuildForContextItem(ContextItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.WorkspaceId)
            || string.IsNullOrWhiteSpace(item.CollectionId)
            || string.IsNullOrWhiteSpace(item.Id))
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var relations = item.Refs
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !string.Equals(value, item.Id, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(targetId => new ContextRelation
            {
                Id = CreateStableId(
                    item.WorkspaceId,
                    item.CollectionId,
                    ContextRelationTypes.RelatedTo,
                    item.Id,
                    targetId),
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                SourceId = item.Id,
                TargetId = targetId,
                RelationType = ContextRelationTypes.RelatedTo,
                Weight = Math.Max(0.1, item.Importance),
                Confidence = 0.8,
                SourceRefs = ResolveContextItemRelationSourceRefs(item, targetId),
                Metadata = new Dictionary<string, string>
                {
                    ["sourceItemType"] = item.Type
                },
                CreatedAt = now
            });

        return [.. relations];
    }

    /// <summary>从上下文包中构建各 Section 与来源条目之间的"包含"关系列表。</summary>
    public IReadOnlyList<ContextRelation> BuildForPackage(ContextPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (string.IsNullOrWhiteSpace(package.WorkspaceId)
            || string.IsNullOrWhiteSpace(package.CollectionId)
            || string.IsNullOrWhiteSpace(package.PackageId))
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var relations = new List<ContextRelation>();

        foreach (var section in package.Sections)
        {
            var sourceIds = section.ItemRefs.Count > 0
                ? section.ItemRefs
                : section.SourceRefs;

            foreach (var sourceId in sourceIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                relations.Add(new ContextRelation
                {
                    Id = CreateStableId(
                        package.WorkspaceId,
                        package.CollectionId,
                        ContextRelationTypes.IncludedInPackage,
                        sourceId,
                        package.PackageId),
                    WorkspaceId = package.WorkspaceId,
                    CollectionId = package.CollectionId,
                    SourceId = sourceId,
                    TargetId = package.PackageId,
                    RelationType = ContextRelationTypes.IncludedInPackage,
                    Weight = Math.Max(0.1, section.Priority),
                    Confidence = 1.0,
                    SourceRefs = ResolvePackageRelationSourceRefs(section, sourceId),
                    Metadata = new Dictionary<string, string>
                    {
                        ["packageId"] = package.PackageId,
                        ["sectionName"] = section.Name,
                        ["sectionPriority"] = section.Priority.ToString(),
                        ["sectionEstimatedTokens"] = section.EstimatedTokens.ToString()
                    },
                    CreatedAt = now
                });
            }
        }

        return [.. relations
            .GroupBy(relation => RelationKey(relation), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private static ContextRelation CreateCompressionRelation(
        ContextItem generatedItem,
        string sourceId,
        string relationType,
        CompressionResponse response,
        DateTimeOffset now)
    {
        var metadata = new Dictionary<string, string>(generatedItem.Metadata)
        {
            ["operationId"] = response.OperationId,
            ["generatedItemType"] = generatedItem.Type
        };

        return new ContextRelation
        {
            Id = CreateStableId(
                generatedItem.WorkspaceId,
                generatedItem.CollectionId,
                relationType,
                generatedItem.Id,
                sourceId),
            WorkspaceId = generatedItem.WorkspaceId,
            CollectionId = generatedItem.CollectionId,
            SourceId = generatedItem.Id,
            TargetId = sourceId,
            RelationType = relationType,
            Weight = relationType == ContextRelationTypes.Summarizes ? 0.95 : 1.0,
            Confidence = 1.0,
            SourceRefs = ResolveCompressionRelationSourceRefs(generatedItem, sourceId),
            Metadata = metadata,
            CreatedAt = now
        };
    }

    private static ContextRelation CreateGeneratedByRelation(
        ContextItem generatedItem,
        CompressionResponse response,
        DateTimeOffset now)
    {
        var metadata = new Dictionary<string, string>(generatedItem.Metadata)
        {
            ["operationId"] = response.OperationId,
            ["targetKind"] = "operation",
            ["generatedItemType"] = generatedItem.Type
        };

        return new ContextRelation
        {
            Id = CreateStableId(
                generatedItem.WorkspaceId,
                generatedItem.CollectionId,
                ContextRelationTypes.GeneratedBy,
                generatedItem.Id,
                response.OperationId),
            WorkspaceId = generatedItem.WorkspaceId,
            CollectionId = generatedItem.CollectionId,
            SourceId = generatedItem.Id,
            TargetId = response.OperationId,
            RelationType = ContextRelationTypes.GeneratedBy,
            Weight = 1.0,
            Confidence = 1.0,
            SourceRefs = ResolveGeneratedBySourceRefs(generatedItem, response.OperationId),
            Metadata = metadata,
            CreatedAt = now
        };
    }

    private static IReadOnlyList<string> ResolveDerivedFrom(ContextItem generatedItem)
    {
        if (generatedItem.Metadata.TryGetValue("derivedFrom", out var derivedFrom)
            && !string.IsNullOrWhiteSpace(derivedFrom))
        {
            return [.. derivedFrom
                .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        return [.. generatedItem.SourceRefs
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> ResolveCompressionRelationSourceRefs(ContextItem generatedItem, string sourceId)
    {
        return [.. generatedItem.SourceRefs
            .Append(generatedItem.Id)
            .Append(sourceId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> ResolveGeneratedBySourceRefs(
        ContextItem generatedItem,
        string operationId)
    {
        return [.. generatedItem.SourceRefs
            .Append(generatedItem.Id)
            .Append(operationId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> ResolveContextItemRelationSourceRefs(
        ContextItem item,
        string targetId)
    {
        return [.. item.SourceRefs
            .Append(item.Id)
            .Append(targetId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> ResolvePackageRelationSourceRefs(
        ContextPackageSection section,
        string sourceId)
    {
        return [.. section.SourceRefs
            .Append(sourceId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string RelationKey(ContextRelation relation)
    {
        return string.Join(
            '\u001f',
            relation.WorkspaceId,
            relation.CollectionId,
            relation.RelationType,
            relation.SourceId,
            relation.TargetId);
    }

    private static string CreateStableId(
        string workspaceId,
        string collectionId,
        string relationType,
        string sourceId,
        string targetId)
    {
        var key = string.Join('\u001f', workspaceId, collectionId, relationType, sourceId, targetId);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"rel-{Convert.ToHexString(bytes)[..24].ToLowerInvariant()}";
    }
}
