using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services;

/// <summary>从学习记录生成可回放的学习案例。</summary>
public interface IContextLearningCaseGenerator
{
    ContextLearningCase? Generate(ContextLearningRecord record);
}

/// <summary>规则型 learning case 生成器，不做模型训练或自动调参。</summary>
public sealed class RuleBasedContextLearningCaseGenerator : IContextLearningCaseGenerator
{
    public ContextLearningCase? Generate(ContextLearningRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var caseKind = ResolveCaseKind(record);
        if (caseKind is null)
        {
            return null;
        }

        var metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceRecordId"] = record.RecordId,
            ["sourceLearningEventKind"] = record.EventKind,
            ["generatedBy"] = "context-learning-case-generator/rule-based",
            ["ruleVersion"] = "context-learning-case-rules/v1"
        };

        return new ContextLearningCase
        {
            CaseId = $"clc-{BuildShortHash($"{record.RecordId}\u001f{caseKind}")}",
            SourceType = ResolveSourceType(record),
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            SessionId = record.SessionId,
            SourceRecordId = record.RecordId,
            SourceKind = record.SourceKind,
            SourceId = ResolveSourceId(record),
            CaseKind = caseKind,
            Title = ResolveTitle(record),
            Summary = ResolveSummary(record),
            InputSummary = ResolveSummary(record),
            ExpectedBehavior = ResolveExpectedBehavior(record),
            Signal = record.Signal,
            FailureType = record.FailureType,
            CorrectionReason = record.Reason,
            Status = ContextLearningCaseStatus.Draft,
            EvidenceRefs = record.EvidenceRefs.ToArray(),
            PositiveRefs = record.Signal == ContextFeedbackSignal.Positive ? record.EvidenceRefs.ToArray() : Array.Empty<string>(),
            NegativeRefs = record.Signal == ContextFeedbackSignal.Negative ? record.EvidenceRefs.ToArray() : Array.Empty<string>(),
            CreatedAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt,
            Metadata = metadata
        };
    }

    private static string? ResolveCaseKind(ContextLearningRecord record)
    {
        if (string.Equals(record.EventKind, "PromotionAccepted", StringComparison.OrdinalIgnoreCase))
        {
            return "PositivePromotionSample";
        }

        if (string.Equals(record.EventKind, "PromotionRejected", StringComparison.OrdinalIgnoreCase))
        {
            return "PromotionFalsePositive";
        }

        if (string.Equals(record.EventKind, "PromotionExpired", StringComparison.OrdinalIgnoreCase))
        {
            return "StaleContextSample";
        }

        return null;
    }

    private static string ResolveTitle(ContextLearningRecord record)
    {
        if (record.Metadata.TryGetValue("candidateTitle", out var candidateTitle)
            && !string.IsNullOrWhiteSpace(candidateTitle))
        {
            return candidateTitle;
        }

        if (record.Metadata.TryGetValue("title", out var title)
            && !string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        return $"{record.EventKind}: {record.SourceId}";
    }

    private static string ResolveSummary(ContextLearningRecord record)
    {
        if (record.Metadata.TryGetValue("candidateSummary", out var candidateSummary)
            && !string.IsNullOrWhiteSpace(candidateSummary))
        {
            return candidateSummary;
        }

        if (!string.IsNullOrWhiteSpace(record.Reason))
        {
            return record.Reason;
        }

        return ResolveTitle(record);
    }

    private static string ResolveExpectedBehavior(ContextLearningRecord record)
    {
        return record.Signal switch
        {
            ContextFeedbackSignal.Positive => "Keep this promotion pattern as a positive candidate-layer example.",
            ContextFeedbackSignal.Negative => "Avoid promoting similar candidates without stronger evidence.",
            ContextFeedbackSignal.Stale => "Treat similar stale candidates as time-sensitive and avoid retaining them after expiry.",
            _ => record.EventKind
        };
    }

    private static string ResolveSourceType(ContextLearningRecord record)
    {
        return record.Metadata.TryGetValue("feedbackSourceType", out var sourceType)
            && !string.IsNullOrWhiteSpace(sourceType)
            ? sourceType
            : "ContextLearningRecord";
    }

    private static string ResolveSourceId(ContextLearningRecord record)
    {
        return record.Metadata.TryGetValue("feedbackId", out var feedbackId)
            && !string.IsNullOrWhiteSpace(feedbackId)
            ? feedbackId
            : record.SourceId;
    }

    private static string BuildShortHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }
}
