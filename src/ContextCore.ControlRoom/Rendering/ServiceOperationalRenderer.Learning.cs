using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

public static partial class ServiceOperationalRenderer
{
    public static string RenderLearning(ServiceLearningSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Context Learning");
        builder.AppendLine($"时间     : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务     : {snapshot.BaseUrl}");
        builder.AppendLine($"Feedback : {snapshot.FeedbackSignals.Count}");
        builder.AppendLine($"Records  : {snapshot.Records.Count}");
        builder.AppendLine($"Cases    : {snapshot.Cases.Count}");
        builder.AppendLine($"Signals  : positive={snapshot.PositiveCount} negative={snapshot.NegativeCount} stale={snapshot.StaleCount}");
        if (snapshot.Summary is not null)
        {
            builder.AppendLine($"Summary  : records={snapshot.Summary.RecordCount} cases={snapshot.Summary.CaseCount}");
            builder.AppendLine($"Statuses : draft={snapshot.Summary.DraftCaseCount} candidate={snapshot.Summary.CandidateCaseCount} activeRegression={snapshot.Summary.ActiveRegressionCaseCount} archived={snapshot.Summary.ArchivedCaseCount} rejected={snapshot.Summary.RejectedCaseCount}");
        }

        if (snapshot.LastGeneration is not null)
        {
            builder.AppendLine($"Generation: scanned={snapshot.LastGeneration.RecordsScanned} created={snapshot.LastGeneration.Created} existing={snapshot.LastGeneration.Existing}");
        }

        if (snapshot.LastStatusUpdate is not null)
        {
            builder.AppendLine($"LastUpdate: {snapshot.LastStatusUpdate.CaseId} -> {snapshot.LastStatusUpdate.Status} op={snapshot.LastStatusUpdate.OperationId}");
        }

        builder.AppendLine();
        builder.AppendLine("Failure Types");
        var failureTypes = snapshot.Summary?.FailureTypeCounts ?? snapshot.FailureTypeSummary;
        if (failureTypes.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in failureTypes.OrderBy(pair => pair.Key.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Case Kinds");
        if (snapshot.Summary is null || snapshot.Summary.CaseKindCounts.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in snapshot.Summary.CaseKindCounts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Active Regression Cases");
        if (snapshot.RegressionCases.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var learningCase in snapshot.RegressionCases.Take(10))
            {
                builder.AppendLine($"- {learningCase.CaseId} [{learningCase.CaseKind}] {learningCase.Title}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Promotion Feedback Signals");
        if (snapshot.FeedbackSignals.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var feedback in snapshot.FeedbackSignals.Take(20))
            {
                builder.AppendLine($"- {feedback.FeedbackId} [{feedback.Action}] candidate={feedback.CandidateId}");
                builder.AppendLine($"  reviewer : {feedback.Reviewer}");
                builder.AppendLine($"  target   : suggested={feedback.SuggestedTargetLayer} actual={feedback.ActualTargetLayer ?? "-"} created={feedback.CreatedTargetItemId ?? "-"}");
                builder.AppendLine($"  reason   : {feedback.Reason}");
                builder.AppendLine($"  evidence : {string.Join(", ", feedback.EvidenceRefs)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent Feedback");
        if (snapshot.Records.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var record in snapshot.Records.Take(20))
            {
                builder.AppendLine($"- {record.RecordId} [{record.Signal}/{record.FailureType}] {record.EventKind}");
                builder.AppendLine($"  source   : {record.SourceKind}/{record.SourceId}");
                builder.AppendLine($"  candidate: {record.CandidateId ?? "-"} review={record.ReviewId ?? "-"}");
                builder.AppendLine($"  reason   : {record.Reason}");
                builder.AppendLine($"  evidence : {string.Join(", ", record.EvidenceRefs)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Learning Cases");
        if (snapshot.Cases.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var learningCase in snapshot.Cases.Take(20))
            {
                builder.AppendLine($"- {learningCase.CaseId} [{learningCase.CaseKind}/{learningCase.Signal}/{learningCase.FailureType}/{learningCase.Status}]");
                builder.AppendLine($"  title    : {learningCase.Title}");
                builder.AppendLine($"  source   : {learningCase.SourceKind}/{learningCase.SourceId} record={learningCase.SourceRecordId}");
                builder.AppendLine($"  evidence : {string.Join(", ", learningCase.EvidenceRefs)}");
            }
        }

        return builder.ToString();
    }
}
