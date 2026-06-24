using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>对一个工作区集合内的条目、引用和关系执行一致性校验。</summary>
public sealed class CollectionValidationService
{
    private readonly IContextStore _contextStore;
    private readonly IRelationStore _relationStore;

    public CollectionValidationService(IContextStore contextStore, IRelationStore relationStore)
    {
        _contextStore = contextStore;
        _relationStore = relationStore;
    }

    public async Task<CollectionValidationReport> ValidateAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        }

        if (string.IsNullOrWhiteSpace(collectionId))
        {
            throw new ArgumentException("CollectionId is required.", nameof(collectionId));
        }

        var items = await _contextStore.QueryAsync(new ContextQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = int.MaxValue,
            IncludeContent = false,
            IncludeDerived = true
        }, cancellationToken).ConfigureAwait(false);
        var relations = await _relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var issues = new List<ContextValidationIssue>();
        var itemIds = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddDuplicateIdIssues(items, issues);
        var referenceGraph = BuildReferenceGraph(items, itemIds, issues);
        AddRelationIssues(relations, itemIds, issues);
        AddCycleIssues(referenceGraph, issues);

        return new CollectionValidationReport
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ItemCount = items.Count,
            RelationCount = relations.Count,
            Succeeded = issues.All(issue => issue.Severity != ContextValidationSeverity.Error),
            Issues = issues.ToArray()
        };
    }

    private static void AddDuplicateIdIssues(
        IReadOnlyList<ContextItem> items,
        ICollection<ContextValidationIssue> issues)
    {
        foreach (var group in items
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            issues.Add(Error(
                "DuplicateId",
                $"Context item id '{group.Key}' appears {group.Count()} times in the collection.",
                $"items[{group.Key}]"));
        }
    }

    private static Dictionary<string, HashSet<string>> BuildReferenceGraph(
        IReadOnlyList<ContextItem> items,
        ISet<string> itemIds,
        ICollection<ContextValidationIssue> issues)
    {
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item.Id)))
        {
            var refs = ExtractLocalReferences(item).ToArray();
            var edges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            graph[item.Id] = edges;

            foreach (var reference in refs)
            {
                if (string.IsNullOrWhiteSpace(reference))
                {
                    continue;
                }

                if (!itemIds.Contains(reference))
                {
                    issues.Add(Error(
                        "OrphanRef",
                        $"Context item '{item.Id}' references missing item '{reference}'.",
                        $"items[{item.Id}].refs"));
                    continue;
                }

                edges.Add(reference);
            }
        }

        return graph;
    }

    private static void AddRelationIssues(
        IReadOnlyList<ContextRelation> relations,
        ISet<string> itemIds,
        ICollection<ContextValidationIssue> issues)
    {
        foreach (var relation in relations)
        {
            if (RequiresContextItemSource(relation.RelationType)
                && !itemIds.Contains(relation.SourceId))
            {
                issues.Add(Error(
                    "MissingRelationSource",
                    $"Relation '{relation.Id}' source '{relation.SourceId}' does not exist.",
                    $"relations[{relation.Id}].sourceId"));
            }

            if (RequiresContextItemTarget(relation.RelationType)
                && !itemIds.Contains(relation.TargetId))
            {
                issues.Add(Error(
                    "MissingRelationTarget",
                    $"Relation '{relation.Id}' target '{relation.TargetId}' does not exist.",
                    $"relations[{relation.Id}].targetId"));
            }
        }
    }

    private static void AddCycleIssues(
        IReadOnlyDictionary<string, HashSet<string>> graph,
        ICollection<ContextValidationIssue> issues)
    {
        var state = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var reportedCycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Keys)
        {
            Visit(node, graph, state, stack, reportedCycles, issues);
        }
    }

    private static bool RequiresContextItemSource(string relationType)
    {
        return !string.Equals(relationType, ContextRelationTypes.IncludedInPackage, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresContextItemTarget(string relationType)
    {
        return !string.Equals(relationType, ContextRelationTypes.GeneratedBy, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(relationType, ContextRelationTypes.IncludedInPackage, StringComparison.OrdinalIgnoreCase);
    }

    private static void Visit(
        string node,
        IReadOnlyDictionary<string, HashSet<string>> graph,
        IDictionary<string, VisitState> state,
        Stack<string> stack,
        ISet<string> reportedCycles,
        ICollection<ContextValidationIssue> issues)
    {
        if (state.ContainsKey(node))
        {
            return;
        }

        state[node] = VisitState.Visiting;
        stack.Push(node);

        if (graph.TryGetValue(node, out var targets))
        {
            foreach (var target in targets)
            {
                if (!state.TryGetValue(target, out var targetState))
                {
                    Visit(target, graph, state, stack, reportedCycles, issues);
                    continue;
                }

                if (targetState == VisitState.Visiting)
                {
                    var cycle = FormatCycle(stack, target);
                    if (reportedCycles.Add(cycle))
                    {
                        issues.Add(Error(
                            "CircularReference",
                            $"Circular reference detected: {cycle}.",
                            $"items[{target}].refs"));
                    }
                }
            }
        }

        stack.Pop();
        state[node] = VisitState.Visited;
    }

    private static string FormatCycle(Stack<string> stack, string repeatedNode)
    {
        var path = stack.Reverse().ToList();
        var index = path.FindIndex(node => string.Equals(node, repeatedNode, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            path = path.Skip(index).ToList();
        }

        path.Add(repeatedNode);
        return string.Join(" -> ", path);
    }

    private static IEnumerable<string> ExtractLocalReferences(ContextItem item)
    {
        foreach (var reference in item.Refs.Where(IsLocalReference))
        {
            yield return reference;
        }

        foreach (var sourceRef in item.SourceRefs.Where(IsLocalReference))
        {
            yield return sourceRef;
        }

        if (item.Metadata.TryGetValue("derivedFrom", out var derivedFrom))
        {
            foreach (var reference in SplitReferenceList(derivedFrom).Where(IsLocalReference))
            {
                yield return reference;
            }
        }
    }

    private static IEnumerable<string> SplitReferenceList(string value)
    {
        return value.Split([',', ';', '|', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsLocalReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !Uri.TryCreate(value, UriKind.Absolute, out _)
            && !value.Contains(':', StringComparison.Ordinal);
    }

    private static ContextValidationIssue Error(string code, string message, string path)
    {
        return new ContextValidationIssue
        {
            Code = code,
            Message = message,
            Path = path,
            Severity = ContextValidationSeverity.Error
        };
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }
}

/// <summary>集合级校验的汇总报告。</summary>
public sealed class CollectionValidationReport
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int ItemCount { get; init; }

    public int RelationCount { get; init; }

    public bool Succeeded { get; init; }

    public IReadOnlyList<ContextValidationIssue> Issues { get; init; } = Array.Empty<ContextValidationIssue>();
}
