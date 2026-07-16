using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

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
