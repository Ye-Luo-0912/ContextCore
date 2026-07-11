using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Learning.V14_0;

namespace ContextCore.Core;

internal sealed class PackageTraceRecorder
{
    private readonly IRuntimeCandidateTraceSink _traceSink;
    private readonly Func<string?> _getOperationId;
    private readonly Func<string?> _getRequestId;

    public PackageTraceRecorder(
        IRuntimeCandidateTraceSink traceSink,
        Func<string?> getOperationId,
        Func<string?> getRequestId)
    {
        _traceSink = traceSink;
        _getOperationId = getOperationId;
        _getRequestId = getRequestId;
    }

    internal void AddSectionDecisionsWithDedup(
        ICollection<ContextPackageDecision> selectedItems,
        ICollection<DroppedContextItem> droppedItems,
        IReadOnlyList<PackageTraceCandidate> candidates,
        string sectionName,
        BasicContextPackageBuilder.SectionBuildResult sectionResult,
        HashSet<string> globalSelectedIds,
        Dictionary<string, ContextPackageDecision> primaryDecisions,
        string sectionContent = "")
    {
        if (candidates.Count == 0)
        {
            return;
        }

        if (sectionResult.Added)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (globalSelectedIds.Contains(candidate.Id))
                {
                    if (primaryDecisions.TryGetValue(candidate.Id, out var primaryDecision))
                    {
                        var refsList = new List<string>();
                        if (primaryDecision.Metadata.TryGetValue("alsoReferencedBy", out var existingRefs))
                        {
                            refsList.AddRange(existingRefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        }
                        if (!refsList.Contains(sectionName, StringComparer.OrdinalIgnoreCase))
                        {
                            refsList.Add(sectionName);
                            primaryDecision.Metadata["alsoReferencedBy"] = string.Join(",", refsList);
                        }
                    }

                    WriteTraceRow(candidate, sectionName, false, "referenced by duplicate section", selectedByScoring: true);
                    selectedItems.Add(CreateDecision(
                        candidate,
                        sectionName,
                        "referenced by duplicate section",
                        0));
                    continue;
                }

                var isKept = (i == 0);
                if (i > 0 && !string.IsNullOrEmpty(sectionContent) && !string.IsNullOrEmpty(candidate.Content))
                {
                    var testLength = Math.Min(candidate.Content.Length, 15);
                    var testStr = candidate.Content[..testLength];
                    isKept = sectionContent.Contains(testStr, StringComparison.OrdinalIgnoreCase);
                }

                if (isKept)
                {
                    var decision = CreateDecision(
                        candidate,
                        sectionName,
                        sectionResult.Reason,
                        candidate.EstimatedTokens);
                    WriteTraceRow(candidate, sectionName, true, sectionResult.Reason);
                    selectedItems.Add(decision);
                    globalSelectedIds.Add(candidate.Id);
                    primaryDecisions[candidate.Id] = decision;
                }
                else
                {
                    droppedItems.Add(CreateDropped(candidate, "token budget exhausted"));
                    WriteTraceRow(candidate, sectionName, false, "token budget exhausted", selectedByScoring: true);
                }
            }
        }
        else
        {
            foreach (var candidate in candidates)
            {
                droppedItems.Add(CreateDropped(candidate, sectionResult.Reason));
                WriteTraceRow(candidate, sectionName, false, sectionResult.Reason, selectedByScoring: false);
            }
        }
    }

    internal void WriteTraceRow(PackageTraceCandidate c, string section, bool included, string reason,
        bool selectedByScoring = true)
    {
        if (!_traceSink.Enabled) return;
        try
        {
            var kind = c.Kind;
            var (srcType, auth, stratType, chan) = MapTraceFields(kind, section, c);
            _traceSink.Write(new RuntimeCandidateTraceRow
            {
                OperationId = _getOperationId() ?? "unknown",
                RequestId = _getRequestId() ?? "unknown",
                CandidateId = c.Id,
                SourceId = c.Id,
                SourceType = srcType,
                Authority = auth,
                StrategyType = stratType,
                RetrievalChannel = chan,
                TraceSource = (byte)3, // PackageTrace
                DeterministicScore = c.Score,
                StrategyScore = c.Score,
                FinalScore = c.Score,
                SelectedByScoring = selectedByScoring,
                IncludedInPackage = included,
                DroppedReason = included ? "" : reason,
                TokenCost = c.EstimatedTokens,
                Section = section
            });
        }
        catch { /* trace write failure must not affect main flow */ }
    }

    private static (byte sourceType, byte authority, byte strategyType, byte retrievalChannel) MapTraceFields(
        string kind, string section, PackageTraceCandidate c)
    {
        var kindLower = kind?.ToLowerInvariant() ?? section?.ToLowerInvariant() ?? "";
        var sectionLower = section?.ToLowerInvariant() ?? "";

        byte sourceType = kindLower switch
        {
            "raw" or "legacy" => 1,
            "current_task" => 6,
            "hard_constraint" or "soft_constraint" or "merged_constraint" => 3,
            "working_memory" or "stable_memory" or "historical_context" => 2,
            "global_context" => 4,
            "recent_context" => 5,
            "related_context" => 7,
            _ => 1
        };

        byte authority = kindLower switch
        {
            "raw" or "legacy" or "recent_context" => 2,
            "current_task" => 5,
            "hard_constraint" or "soft_constraint" or "merged_constraint" or "constraints" => 1,
            "working_memory" => 5,
            "stable_memory" => 1,
            "global_context" => 1,
            "related_context" => 4,
            "historical_context" => 3,
            _ => 1
        };

        byte strategyType = kindLower switch
        {
            "current_task" => 4,
            "hard_constraint" or "soft_constraint" or "merged_constraint" or "constraints" => 3,
            "working_memory" or "recent_context" => 1,
            "stable_memory" => 2,
            "global_context" => 2,
            "related_context" => 5,
            "raw" or "legacy" => 1,
            _ => 1
        };

        byte retrievalChannel = sectionLower switch
        {
            "raw" or "legacy" => sectionLower == "legacy" ? (byte)4 : (byte)4,
            "current_task" => (byte)5,
            "hard_constraints" or "soft_constraints" or "constraints" => kindLower.Contains("constraint") ? (byte)6 : (byte)2,
            "working_memory" or "stable_memory" or "global_context" or "historical_context" => (byte)2,
            "recent_context" => (byte)4,
            "related_context" => (byte)3,
            _ => (byte)2
        };
        return (sourceType, authority, strategyType, retrievalChannel);
    }

    internal static void AddSectionDecisions(
        ICollection<ContextPackageDecision> selectedItems,
        ICollection<DroppedContextItem> droppedItems,
        IReadOnlyList<PackageTraceCandidate> candidates,
        string sectionName,
        BasicContextPackageBuilder.SectionBuildResult sectionResult,
        string sectionContent = "")
    {
        if (candidates.Count == 0)
        {
            return;
        }

        if (sectionResult.Added)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var isKept = (i == 0);
                if (i > 0 && !string.IsNullOrEmpty(sectionContent) && !string.IsNullOrEmpty(candidate.Content))
                {
                    var testLength = Math.Min(candidate.Content.Length, 15);
                    var testStr = candidate.Content[..testLength];
                    isKept = sectionContent.Contains(testStr, StringComparison.OrdinalIgnoreCase);
                }

                if (isKept)
                {
                    selectedItems.Add(CreateDecision(
                        candidate,
                        sectionName,
                        sectionResult.Reason,
                        candidate.EstimatedTokens));
                }
                else
                {
                    droppedItems.Add(CreateDropped(candidate, "token budget exhausted"));
                }
            }
        }
        else
        {
            foreach (var candidate in candidates)
            {
                droppedItems.Add(CreateDropped(candidate, sectionResult.Reason));
            }
        }
    }

    internal static ContextPackageDecision CreateDecision(
        PackageTraceCandidate candidate,
        string sectionName,
        string reason,
        int estimatedTokens)
    {
        return new ContextPackageDecision
        {
            ItemId = candidate.Id,
            Kind = candidate.Kind,
            Type = candidate.Type,
            SectionName = sectionName,
            Reason = reason,
            Score = candidate.Score,
            EstimatedTokens = estimatedTokens,
            SourceRefs = candidate.SourceRefs,
            Metadata = new Dictionary<string, string>(candidate.Metadata),
            ScoreBreakdown = candidate.ScoreBreakdown
        };
    }

    internal static DroppedContextItem CreateDropped(
        PackageTraceCandidate candidate,
        string reason)
    {
        return new DroppedContextItem
        {
            ItemId = candidate.Id,
            Kind = candidate.Kind,
            Type = candidate.Type,
            Reason = reason,
            Score = candidate.Score,
            EstimatedTokens = candidate.EstimatedTokens,
            SourceRefs = candidate.SourceRefs,
            Metadata = new Dictionary<string, string>(candidate.Metadata)
        };
    }
}
