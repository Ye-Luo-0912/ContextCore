using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>根据运行时候选元数据构建 ranker 特征包络；不读取 eval label、sampleId 或 itemId 内容。</summary>
public sealed class CandidateFeatureEnvelopeBuilder
{
    public CandidateFeatureEnvelope Build(ContextEvalItemDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var section = diagnostic.SectionName ?? string.Empty;
        var kind = diagnostic.Kind ?? string.Empty;
        var type = diagnostic.Type ?? string.Empty;
        var metadataSurface = string.Join(' ', section, kind, type);
        var layer = ResolveLayer(metadataSurface);
        var itemKind = string.IsNullOrWhiteSpace(kind)
            ? (string.IsNullOrWhiteSpace(type) ? "unknown" : type)
            : kind;
        var lifecycle = ResolveLifecycle(metadataSurface);
        var isDeprecated = IsLifecycle(lifecycle, "Deprecated");
        var isHistorical = IsLifecycle(lifecycle, "Historical");
        var isSuperseded = IsLifecycle(lifecycle, "Superseded");
        var provenance = diagnostic.SourceRefs
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var replacementState = ResolveReplacementState(lifecycle, metadataSurface, provenance);
        var hasActiveReplacement = string.Equals(replacementState, "HasActiveReplacement", StringComparison.OrdinalIgnoreCase);
        var reviewStatus = ResolveReviewStatus(lifecycle, layer, provenance);
        var diagnostics = BuildDiagnostics(
                lifecycle,
                reviewStatus,
                replacementState,
                provenance,
                section)
            .ToArray();

        return new CandidateFeatureEnvelope
        {
            CandidateId = diagnostic.ItemId,
            ItemId = diagnostic.ItemId,
            Layer = layer,
            ItemKind = itemKind,
            Lifecycle = lifecycle,
            ReviewStatus = reviewStatus,
            SourceRef = provenance.FirstOrDefault() ?? string.Empty,
            Provenance = provenance,
            ReplacementState = replacementState,
            IsDeprecated = isDeprecated,
            IsHistorical = isHistorical,
            IsSuperseded = isSuperseded,
            HasActiveReplacement = hasActiveReplacement,
            ConstraintRelevance = ContainsSystemToken(metadataSurface, "constraint"),
            RelationEvidence = provenance.Any(static item =>
                item.Contains("relation", StringComparison.OrdinalIgnoreCase)
                || item.Contains("review", StringComparison.OrdinalIgnoreCase)
                || item.Contains("evidence", StringComparison.OrdinalIgnoreCase)),
            Diagnostics = diagnostics,
            FeatureCompleteness = ComputeFeatureCompleteness(
                layer,
                itemKind,
                lifecycle,
                reviewStatus,
                replacementState,
                provenance)
        };
    }

    public IReadOnlyList<CandidateFeatureEnvelope> BuildMany(IEnumerable<ContextEvalItemDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics.Select(Build).ToArray();
    }

    private static string ResolveLayer(string metadataSurface)
    {
        if (ContainsSystemToken(metadataSurface, "historical") || ContainsSystemToken(metadataSurface, "audit"))
        {
            return "historical_context";
        }

        if (ContainsSystemToken(metadataSurface, "conflict"))
        {
            return "conflict_evidence";
        }

        if (ContainsSystemToken(metadataSurface, "constraint"))
        {
            return "constraints";
        }

        if (ContainsSystemToken(metadataSurface, "stable"))
        {
            return "stable_context";
        }

        if (ContainsSystemToken(metadataSurface, "working"))
        {
            return "working_context";
        }

        if (ContainsSystemToken(metadataSurface, "candidate"))
        {
            return "candidate_context";
        }

        if (ContainsSystemToken(metadataSurface, "diagnostic"))
        {
            return "diagnostics";
        }

        return "unknown";
    }

    private static string ResolveLifecycle(string metadataSurface)
    {
        if (ContainsSystemToken(metadataSurface, "rejected"))
        {
            return "Rejected";
        }

        if (ContainsSystemToken(metadataSurface, "deprecated"))
        {
            return "Deprecated";
        }

        if (ContainsSystemToken(metadataSurface, "superseded"))
        {
            return "Superseded";
        }

        if (ContainsSystemToken(metadataSurface, "historical"))
        {
            return "Historical";
        }

        if (ContainsSystemToken(metadataSurface, "stable")
            || ContainsSystemToken(metadataSurface, "working")
            || ContainsSystemToken(metadataSurface, "constraint")
            || ContainsSystemToken(metadataSurface, "conflict"))
        {
            return "Active";
        }

        if (ContainsSystemToken(metadataSurface, "candidate"))
        {
            return "Candidate";
        }

        return "Unknown";
    }

    private static string ResolveReplacementState(
        string lifecycle,
        string metadataSurface,
        IReadOnlyList<string> provenance)
    {
        if (IsLifecycle(lifecycle, "Superseded"))
        {
            return provenance.Any(static item =>
                item.Contains("replacement", StringComparison.OrdinalIgnoreCase)
                || item.Contains("replaces", StringComparison.OrdinalIgnoreCase)
                || item.Contains("superseded_by", StringComparison.OrdinalIgnoreCase))
                ? "HasActiveReplacement"
                : "MissingReplacementMetadata";
        }

        if (ContainsSystemToken(metadataSurface, "replacement")
            || ContainsSystemToken(metadataSurface, "replaces")
            || ContainsSystemToken(metadataSurface, "superseded_by"))
        {
            return "HasActiveReplacement";
        }

        return "NotRequired";
    }

    private static string ResolveReviewStatus(
        string lifecycle,
        string layer,
        IReadOnlyList<string> provenance)
    {
        if (IsLifecycle(lifecycle, "Unknown"))
        {
            return string.Empty;
        }

        if (provenance.Any(static item =>
                item.Contains("review", StringComparison.OrdinalIgnoreCase)
                || item.Contains("candidate", StringComparison.OrdinalIgnoreCase)
                || item.Contains("gap", StringComparison.OrdinalIgnoreCase)))
        {
            return "Reviewed";
        }

        if (string.Equals(layer, "stable_context", StringComparison.OrdinalIgnoreCase)
            || string.Equals(layer, "working_context", StringComparison.OrdinalIgnoreCase)
            || string.Equals(layer, "constraints", StringComparison.OrdinalIgnoreCase)
            || string.Equals(layer, "conflict_evidence", StringComparison.OrdinalIgnoreCase))
        {
            return "RuntimeEligible";
        }

        return string.Empty;
    }

    private static IEnumerable<string> BuildDiagnostics(
        string lifecycle,
        string reviewStatus,
        string replacementState,
        IReadOnlyList<string> provenance,
        string section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            yield return CandidateRerankerBlockedReasons.IncompleteFeatureEnvelope;
        }

        if (IsLifecycle(lifecycle, "Unknown"))
        {
            yield return CandidateRerankerBlockedReasons.MissingLifecycleMetadata;
        }

        if (string.IsNullOrWhiteSpace(reviewStatus))
        {
            yield return CandidateRerankerBlockedReasons.MissingReviewStatus;
        }

        if (provenance.Count == 0)
        {
            yield return CandidateRerankerBlockedReasons.MissingProvenance;
        }

        if (string.Equals(replacementState, "MissingReplacementMetadata", StringComparison.OrdinalIgnoreCase))
        {
            yield return CandidateRerankerBlockedReasons.MissingReplacementMetadata;
        }
    }

    private static double ComputeFeatureCompleteness(
        string layer,
        string itemKind,
        string lifecycle,
        string reviewStatus,
        string replacementState,
        IReadOnlyList<string> provenance)
    {
        var total = 6d;
        var present = 0d;
        if (!string.IsNullOrWhiteSpace(layer) && !string.Equals(layer, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            present++;
        }

        if (!string.IsNullOrWhiteSpace(itemKind) && !string.Equals(itemKind, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            present++;
        }

        if (!string.IsNullOrWhiteSpace(lifecycle) && !IsLifecycle(lifecycle, "Unknown"))
        {
            present++;
        }

        if (!string.IsNullOrWhiteSpace(reviewStatus))
        {
            present++;
        }

        if (!string.IsNullOrWhiteSpace(replacementState))
        {
            present++;
        }

        if (provenance.Count > 0)
        {
            present++;
        }

        return present / total;
    }

    private static bool IsLifecycle(string value, string expected)
    {
        return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSystemToken(string value, string token)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}
