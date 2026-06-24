using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;


namespace ContextCore.ControlRoom.Commands;

public static partial class EvalCommand
{
    private static async Task ExecuteLearningDatasetQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var featuresDirectory = CommandHelpers.GetOption(args, "--features-dir")
            ?? CommandHelpers.GetOption(args, "--in-dir")
            ?? Path.Combine(current, "learning", "features");
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine(current, "learning", "features", "dataset-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "learning", "features", "dataset-quality-report.md");

        var builder = new LearningDatasetQualityReportBuilder();
        var report = await builder.BuildAsync(featuresDirectory, cancellationToken).ConfigureAwait(false);
        await builder.WriteAsync(report, outputPath, markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Learning dataset quality report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Learning dataset quality markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] Counts: policy={report.PolicyFeedbackFeatureCount}, rankingPairs={report.RankingPairCount}, routerIntent={report.RouterIntentExampleCount}");
        Console.WriteLine($"[Eval] Risks: {(report.DataRisks.Count == 0 ? "-" : string.Join(", ", report.DataRisks))}");
        Console.WriteLine($"[Eval] Next: {report.RecommendedNextAction}");
    }


    private static async Task ExecuteLearningBaselineAsync(
        string subcommand,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var featuresDirectory = CommandHelpers.GetOption(args, "--features-dir")
            ?? CommandHelpers.GetOption(args, "--in-dir")
            ?? Path.Combine(current, "learning", "features");
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "learning", "baselines");
        var task = CommandHelpers.GetOption(args, "--task")
            ?? (string.Equals(subcommand, "learning-baseline-router", StringComparison.OrdinalIgnoreCase)
                ? "router"
                : string.Equals(subcommand, "learning-baseline-ranker", StringComparison.OrdinalIgnoreCase)
                    ? "ranker"
                    : "all");

        Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
        var runner = new LearningOfflineBaselineRunner();

        if (string.Equals(task, "router", StringComparison.OrdinalIgnoreCase)
            || string.Equals(task, "all", StringComparison.OrdinalIgnoreCase))
        {
            var inputPath = CommandHelpers.GetOption(args, "--router-input")
                ?? Path.Combine(featuresDirectory, LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName);
            var jsonPath = Path.Combine(outputDirectory, "router-intent-baseline-report.json");
            var markdownPath = Path.Combine(outputDirectory, "router-intent-baseline-report.md");
            var report = await runner.RunRouterAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"[Eval] Router baseline report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Router baseline markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Router status={report.Status}; samples={report.SampleCount}; best={report.BestBaseline}");
            foreach (var baseline in report.Baselines)
            {
                Console.WriteLine($"[Eval] Router {baseline.BaselineName}: accuracy={baseline.Accuracy:P2}, macroF1={baseline.MacroF1:0.####}");
            }
        }

        if (string.Equals(task, "ranker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(task, "all", StringComparison.OrdinalIgnoreCase))
        {
            var inputPath = CommandHelpers.GetOption(args, "--ranker-input")
                ?? Path.Combine(featuresDirectory, LearningDatasetQualityReportBuilder.RankingPairsFileName);
            var jsonPath = Path.Combine(outputDirectory, "ranker-baseline-report.json");
            var markdownPath = Path.Combine(outputDirectory, "ranker-baseline-report.md");
            var report = await runner.RunRankerAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"[Eval] Ranker baseline report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Ranker baseline markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Ranker status={report.Status}; pairs={report.PairCount}; best={report.BestBaseline}");
            foreach (var baseline in report.Baselines)
            {
                Console.WriteLine($"[Eval] Ranker {baseline.BaselineName}: pairwiseAccuracy={baseline.PairwiseAccuracy:P2}, fpr={baseline.FalsePositiveRate:P2}, fnr={baseline.FalseNegativeRate:P2}");
            }
        }

        if (!string.Equals(task, "router", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(task, "ranker", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(task, "all", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Error: unsupported learning baseline task '{task}'. Expected router, ranker, or all.");
        }
    }


    private static async Task ExecuteLearningReadinessFreezeReportAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, LearningReadinessFreezeRunner.DefaultOutputDirectory);
        var report = await new LearningReadinessFreezeRunner()
            .RunFreezeReportAsync(outputDirectory, cancellationToken)
            .ConfigureAwait(false);
        var resolvedOutput = Path.GetFullPath(outputDirectory);
        await MirrorExistingArtifactsAsync(
            cancellationToken,
            Path.Combine(resolvedOutput, LearningReadinessFreezeRunner.FreezeReportFileName),
            Path.Combine(resolvedOutput, LearningReadinessFreezeRunner.FreezeMarkdownFileName))
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Learning readiness freeze report: {Path.Combine(resolvedOutput, LearningReadinessFreezeRunner.FreezeReportFileName)}");
        Console.WriteLine($"[Eval] Learning readiness freeze markdown: {Path.Combine(resolvedOutput, LearningReadinessFreezeRunner.FreezeMarkdownFileName)}");
        Console.WriteLine($"[Eval] ready={report.ReadyCount}; blocked={report.BlockedCount}; recommendation={report.OverallRecommendation}");
        foreach (var capability in report.Capabilities.OrderBy(item => item.CapabilityId, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Eval] {capability.CapabilityId}: status={capability.Status}; gate={capability.GatePassed}; rec={capability.Recommendation}");
        }
    }


    private static async Task ExecuteLearningRuntimeChangeReadinessGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, LearningReadinessFreezeRunner.DefaultOutputDirectory);
        var report = await new LearningReadinessFreezeRunner()
            .RunRuntimeChangeGateAsync(outputDirectory, cancellationToken)
            .ConfigureAwait(false);
        var resolvedOutput = Path.GetFullPath(outputDirectory);
        await MirrorExistingArtifactsAsync(
            cancellationToken,
            Path.Combine(resolvedOutput, LearningReadinessFreezeRunner.RuntimeGateFileName),
            Path.Combine(resolvedOutput, LearningReadinessFreezeRunner.RuntimeGateMarkdownFileName))
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Learning runtime change readiness gate: {Path.Combine(resolvedOutput, LearningReadinessFreezeRunner.RuntimeGateFileName)}");
        Console.WriteLine($"[Eval] Learning runtime change readiness gate markdown: {Path.Combine(resolvedOutput, LearningReadinessFreezeRunner.RuntimeGateMarkdownFileName)}");
        Console.WriteLine($"[Eval] passed={report.Passed}; failed={report.FailedConditions.Count}; recommendation={report.Recommendation}");

        if (!report.Passed)
        {
            throw new InvalidOperationException(
                $"Learning runtime change readiness gate failed: {string.Join("; ", report.FailedConditions)}");
        }
    }


    private static async Task ExecuteLearningFeedbackSummaryAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var query = BuildLearningFeedbackQuery(service, args, defaultLimit: 20);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-summary.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-summary.md");

        LearningFeedbackSummaryReport report;
        if (service.State.IsServiceMode)
        {
            report = await service.State.ServiceClient!
                .GetLearningFeedbackSummaryAsync(query, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            report = await new LearningFeedbackService(service.State.LearningFeedbackStore)
                .BuildSummaryAsync(query, cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(LearningFeedbackService.BuildMarkdownReport(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Learning feedback summary: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Learning feedback summary markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] feedback={report.FeedbackCount}; capabilities={report.FeedbackByCapability.Count}; kinds={report.FeedbackByKind.Count}");
    }


    private static async Task ExecuteLearningFeedbackReviewSummaryAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-review-summary.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-review-summary.md");
        var feedbackQuery = BuildLearningFeedbackQuery(service, args, defaultLimit: int.MaxValue);
        var reviewQuery = BuildLearningFeedbackReviewQuery(args);

        LearningFeedbackReviewSummaryReport report;
        if (service.State.IsServiceMode)
        {
            report = await service.State.ServiceClient!
                .GetLearningFeedbackReviewSummaryAsync(reviewQuery, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            report = await new LearningFeedbackReviewService(
                    service.State.LearningFeedbackStore,
                    service.State.LearningFeedbackReviewStore)
                .BuildSummaryAsync(feedbackQuery, reviewQuery, cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(LearningFeedbackReviewService.BuildMarkdownReport(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning feedback review summary: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Learning feedback review markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] pending={report.PendingReviewCount}; approved={report.ApprovedCount}; rejected={report.RejectedCount}; needsRedaction={report.NeedsRedactionCount}; needsEvidence={report.NeedsMoreEvidenceCount}");
    }


    private static async Task ExecuteLearningFeedbackFeatureCandidatesAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (service.State.IsServiceMode)
        {
            throw new InvalidOperationException("learning-feedback-feature-candidates runs in direct mode so dataset candidate exports stay repo-local.");
        }

        var query = BuildLearningFeedbackQuery(service, args, defaultLimit: int.MaxValue);
        var jsonlPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-feature-candidates.jsonl");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-feature-candidates.md");
        var reportPath = CommandHelpers.GetOption(args, "--report-out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-feature-candidates-report.json");

        var report = await new LearningFeedbackFeatureCandidateBuilder(
                service.State.LearningFeedbackStore,
                service.State.LearningFeedbackReviewStore)
            .BuildAsync(query, updateNeedsMoreEvidence: false, cancellationToken)
            .ConfigureAwait(false);
        var jsonl = LearningFeedbackFeatureCandidateBuilder.ExportJsonLines(report);

        await WriteTextAsync(jsonl, jsonlPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), reportPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(LearningFeedbackFeatureCandidateBuilder.BuildMarkdownReport(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning feedback feature candidates: {Path.GetFullPath(jsonlPath)}");
        Console.WriteLine($"[Eval] Learning feedback feature candidate report: {Path.GetFullPath(reportPath)}");
        Console.WriteLine($"[Eval] Learning feedback feature candidate markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] candidates={report.GeneratedCandidateCount}; pending={report.PendingReviewCount}; needsEvidence={report.NeedsMoreEvidenceCount}; needsRedaction={report.NeedsRedactionCount}; rejected={report.RejectedCount}");
    }


    private static async Task ExecuteLearningFeedbackQualityAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (service.State.IsServiceMode)
        {
            throw new InvalidOperationException("learning-feedback-quality runs in direct mode so the report uses repo-local feedback stores and feature candidates.");
        }

        var query = BuildLearningFeedbackQuery(service, args, defaultLimit: int.MaxValue);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-quality-report.md");
        var feedback = await service.State.LearningFeedbackStore
            .QueryAsync(query, cancellationToken)
            .ConfigureAwait(false);
        var reviews = await service.State.LearningFeedbackReviewStore
            .QueryAsync(new LearningFeedbackReviewQuery { Limit = int.MaxValue }, cancellationToken)
            .ConfigureAwait(false);
        var featureCandidates = await new LearningFeedbackFeatureCandidateBuilder(
                service.State.LearningFeedbackStore,
                service.State.LearningFeedbackReviewStore)
            .BuildAsync(query, updateNeedsMoreEvidence: false, cancellationToken)
            .ConfigureAwait(false);
        var featureDataset = await new LearningFeatureDatasetService(CreatePolicyFeedbackDatasetServiceForEval(service))
            .BuildAsync(
                query.WorkspaceId ?? service.State.WorkspaceId,
                query.CollectionId ?? service.State.CollectionId,
                limit: 1,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var report = new LearningFeedbackQualityReportBuilder()
            .Build(feedback, reviews, featureCandidates, featureDataset);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(LearningFeedbackQualityReportBuilder.BuildMarkdownReport(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning feedback quality report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Learning feedback quality markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] recommendation={report.Recommendation}; reviewCoverage={report.ReviewCoverageRate:P2}; redactionCoverage={report.RedactionCoverageRate:P2}; candidates={report.FeatureCandidateCount}");
    }


    private static async Task ExecuteLearningFeedbackReviewSmokeAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (service.State.IsServiceMode)
        {
            throw new InvalidOperationException("learning-feedback-review-smoke runs in direct mode so smoke candidates stay repo-local and excluded from training.");
        }

        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-smoke-report.md");
        var candidateJsonlPath = Path.Combine("learning", "feedback", "learning-feedback-feature-candidates.jsonl");
        var candidateMarkdownPath = Path.Combine("learning", "feedback", "learning-feedback-feature-candidates.md");
        var candidateReportPath = Path.Combine("learning", "feedback", "learning-feedback-feature-candidates-report.json");
        var qualityPath = Path.Combine("learning", "feedback", "learning-feedback-quality-report.json");
        var qualityMarkdownPath = Path.Combine("learning", "feedback", "learning-feedback-quality-report.md");
        var query = new LearningFeedbackEventQuery
        {
            WorkspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "feedback-smoke",
            CollectionId = CommandHelpers.GetOption(args, "--collection") ?? "smoke",
            Limit = int.MaxValue
        };
        var workspaceId = query.WorkspaceId ?? service.State.WorkspaceId;
        var collectionId = query.CollectionId ?? service.State.CollectionId;
        var prefix = $"feedback-review-smoke-{workspaceId}-{collectionId}";
        var feedbackService = new LearningFeedbackService(service.State.LearningFeedbackStore);
        var reviewService = new LearningFeedbackReviewService(
            service.State.LearningFeedbackStore,
            service.State.LearningFeedbackReviewStore);
        var before = await feedbackService.BuildSummaryAsync(query, cancellationToken)
            .ConfigureAwait(false);

        var redactionFeedback = await feedbackService.SubmitAsync(CreateSmokeFeedbackRequest(
                workspaceId,
                collectionId,
                $"{prefix}-needs-redaction",
                ShadowCapabilityIds.VectorRetrieval,
                LearningFeedbackTargetType.VectorCandidate,
                "smoke-vector-needs-redaction",
                LearningFeedbackKinds.MissingContext),
            cancellationToken)
            .ConfigureAwait(false);
        var rejectedFeedback = await feedbackService.SubmitAsync(CreateSmokeFeedbackRequest(
                workspaceId,
                collectionId,
                $"{prefix}-reject",
                ShadowCapabilityIds.CandidateReranker,
                LearningFeedbackTargetType.RankerCandidate,
                "smoke-ranker-reject",
                LearningFeedbackKinds.RankingWrong),
            cancellationToken)
            .ConfigureAwait(false);
        var approvedFeedback = await feedbackService.SubmitAsync(CreateSmokeFeedbackRequest(
                workspaceId,
                collectionId,
                $"{prefix}-approve",
                ShadowCapabilityIds.VectorRetrieval,
                LearningFeedbackTargetType.VectorCandidate,
                "smoke-vector-approve",
                LearningFeedbackKinds.MissingContext),
            cancellationToken)
            .ConfigureAwait(false);
        var duplicateApproved = await feedbackService.SubmitAsync(CreateSmokeFeedbackRequest(
                workspaceId,
                collectionId,
                $"{prefix}-approve",
                ShadowCapabilityIds.VectorRetrieval,
                LearningFeedbackTargetType.VectorCandidate,
                "smoke-vector-approve",
                LearningFeedbackKinds.MissingContext),
            cancellationToken)
            .ConfigureAwait(false);

        var needsRedaction = await reviewService.NeedsRedactionAsync(
                redactionFeedback.FeedbackId,
                CreateSmokeReviewRequest(FeedbackReviewStatus.NeedsRedaction),
                cancellationToken)
            .ConfigureAwait(false);
        var rejected = await reviewService.RejectAsync(
                rejectedFeedback.FeedbackId,
                CreateSmokeReviewRequest(FeedbackReviewStatus.Rejected),
                cancellationToken)
            .ConfigureAwait(false);
        var approved = await reviewService.ApproveAsync(
                approvedFeedback.FeedbackId,
                CreateSmokeReviewRequest(FeedbackReviewStatus.ApprovedForDataset),
                cancellationToken)
            .ConfigureAwait(false);

        var after = await feedbackService.BuildSummaryAsync(query, cancellationToken)
            .ConfigureAwait(false);
        var export = await feedbackService.ExportJsonLinesAsync(query, cancellationToken)
            .ConfigureAwait(false);
        var candidateReport = await new LearningFeedbackFeatureCandidateBuilder(
                service.State.LearningFeedbackStore,
                service.State.LearningFeedbackReviewStore)
            .BuildAsync(query, updateNeedsMoreEvidence: false, cancellationToken)
            .ConfigureAwait(false);
        var candidateJsonl = LearningFeedbackFeatureCandidateBuilder.ExportJsonLines(candidateReport);
        await WriteTextAsync(candidateJsonl, candidateJsonlPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(candidateReport, JsonOptions), candidateReportPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(LearningFeedbackFeatureCandidateBuilder.BuildMarkdownReport(candidateReport), candidateMarkdownPath, cancellationToken)
            .ConfigureAwait(false);

        var feedback = await service.State.LearningFeedbackStore.QueryAsync(query, cancellationToken)
            .ConfigureAwait(false);
        var reviews = await service.State.LearningFeedbackReviewStore
            .QueryAsync(new LearningFeedbackReviewQuery { Limit = int.MaxValue }, cancellationToken)
            .ConfigureAwait(false);
        var qualityReport = new LearningFeedbackQualityReportBuilder()
            .Build(feedback, reviews, candidateReport);
        await WriteTextAsync(JsonSerializer.Serialize(qualityReport, JsonOptions), qualityPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(LearningFeedbackQualityReportBuilder.BuildMarkdownReport(qualityReport), qualityMarkdownPath, cancellationToken)
            .ConfigureAwait(false);

        var smokeCandidates = candidateReport.Candidates
            .Where(item => item.Metadata.TryGetValue("sourceFeedbackId", out var feedbackId)
                && feedbackId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var trainableSmokeCandidates = smokeCandidates.Count(static item =>
            !item.Metadata.TryGetValue("excludedFromTraining", out var value)
            || !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        var failed = new List<string>();
        AddIfFalse(failed, approvedFeedback.Event.FeedbackId == $"{prefix}-approve", "SubmitWorks");
        AddIfFalse(failed, duplicateApproved.DuplicateReplaced, "DuplicateFeedbackIdUpsertWorks");
        AddIfFalse(failed, approvedFeedback.Event.MetadataOnly, "MetadataOnlyWorks");
        AddIfFalse(failed, string.Equals(approvedFeedback.Event.RedactionMode, "metadata-only", StringComparison.OrdinalIgnoreCase), "RedactionModePreserved");
        AddIfFalse(failed, string.Equals(approvedFeedback.Event.TrainingUse, "disabled_until_review", StringComparison.OrdinalIgnoreCase), "TrainingUseDisabledUntilReview");
        AddIfFalse(failed, needsRedaction.ReviewStatus == FeedbackReviewStatus.NeedsRedaction, "NeedsRedactionReviewWorks");
        AddIfFalse(failed, rejected.ReviewStatus == FeedbackReviewStatus.Rejected, "RejectReviewWorks");
        AddIfFalse(failed, approved.ReviewStatus == FeedbackReviewStatus.ApprovedForDataset, "ApproveMetadataSafeFeedbackWorks");
        AddIfFalse(failed, after.FeedbackCount >= before.FeedbackCount, "SummaryCountUpdated");
        AddIfFalse(failed, export.Contains($"{prefix}-approve", StringComparison.OrdinalIgnoreCase), "ExportJsonlContainsFeedback");
        AddIfFalse(failed, smokeCandidates.Length > 0, "FeatureCandidateBuilt");
        AddIfFalse(failed, candidateJsonl.Contains($"{prefix}-approve", StringComparison.OrdinalIgnoreCase), "FeatureCandidateExported");
        AddIfFalse(failed, qualityReport.FeedbackCount > 0, "QualityReportRefreshed");
        AddIfFalse(failed, trainableSmokeCandidates == 0, "SmokeRecordExcludedFromTraining");

        var report = new LearningFeedbackSmokeReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            FeedbackId = $"{prefix}-approve",
            SubmitWorks = approvedFeedback.Event.FeedbackId == $"{prefix}-approve",
            DuplicateFeedbackIdUpsertWorks = duplicateApproved.DuplicateReplaced,
            MetadataOnlyWorks = approvedFeedback.Event.MetadataOnly,
            RedactionModePreserved = string.Equals(approvedFeedback.Event.RedactionMode, "metadata-only", StringComparison.OrdinalIgnoreCase),
            TrainingUseDisabledUntilReview = string.Equals(approvedFeedback.Event.TrainingUse, "disabled_until_review", StringComparison.OrdinalIgnoreCase),
            NeedsRedactionReviewWorks = needsRedaction.ReviewStatus == FeedbackReviewStatus.NeedsRedaction,
            RejectReviewWorks = rejected.ReviewStatus == FeedbackReviewStatus.Rejected,
            ApproveMetadataSafeFeedbackWorks = approved.ReviewStatus == FeedbackReviewStatus.ApprovedForDataset,
            SummaryCountUpdated = after.FeedbackCount >= before.FeedbackCount,
            ExportJsonlContainsFeedback = export.Contains($"{prefix}-approve", StringComparison.OrdinalIgnoreCase),
            FeatureCandidateBuilt = smokeCandidates.Length > 0,
            FeatureCandidateExported = candidateJsonl.Contains($"{prefix}-approve", StringComparison.OrdinalIgnoreCase),
            QualityReportRefreshed = qualityReport.FeedbackCount > 0,
            SmokeRecordExcludedFromTraining = trainableSmokeCandidates == 0,
            SummaryCountBefore = before.FeedbackCount,
            SummaryCountAfter = after.FeedbackCount,
            FeatureCandidateCount = smokeCandidates.Length,
            TrainableCandidateCount = trainableSmokeCandidates,
            Recommendation = failed.Count == 0 ? "FeedbackReviewSmokePassed" : "FeedbackReviewSmokeNeedsFix",
            FailedChecks = failed
        };

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildLearningFeedbackSmokeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning feedback review smoke report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Learning feedback review smoke markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] recommendation={report.Recommendation}; failed={report.FailedChecks.Count}; smokeCandidates={report.FeatureCandidateCount}; trainableSmoke={report.TrainableCandidateCount}");
    }


    private static async Task ExecuteLearningApprovedFeedbackDatasetGateAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (service.State.IsServiceMode)
        {
            throw new InvalidOperationException("learning-approved-feedback-dataset-gate runs in direct mode so the gate reads repo-local candidate exports.");
        }

        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("learning", "feedback", "learning-approved-feedback-dataset-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("learning", "feedback", "learning-approved-feedback-dataset-gate.md");
        var query = BuildLearningFeedbackQuery(service, args, defaultLimit: int.MaxValue);
        var qualityReport = await BuildLearningFeedbackQualityReportAsync(service, query, cancellationToken)
            .ConfigureAwait(false);
        var candidateReport = await new LearningFeedbackFeatureCandidateBuilder(
                service.State.LearningFeedbackStore,
                service.State.LearningFeedbackReviewStore)
            .BuildAsync(query, updateNeedsMoreEvidence: false, cancellationToken)
            .ConfigureAwait(false);
        var gate = new LearningApprovedFeedbackDatasetGateBuilder()
            .Build(qualityReport, candidateReport);

        await WriteTextAsync(JsonSerializer.Serialize(gate, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(LearningApprovedFeedbackDatasetGateBuilder.BuildMarkdownReport(gate), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning approved feedback dataset gate: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Learning approved feedback dataset gate markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] passed={gate.Passed}; trainable={gate.TrainableCandidateCount}; smokeExcluded={gate.SmokeExcludedCount}; failures={string.Join(",", gate.FailureReasons)}");
    }


    private static async Task ExecuteLearningFeedbackSmokeAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-smoke-report.md");
        var query = BuildLearningFeedbackQuery(service, args, defaultLimit: 20);
        var smokeId = $"feedback-smoke-{query.WorkspaceId}-{query.CollectionId}";
        var request = new LearningFeedbackSubmitRequest
        {
            FeedbackId = smokeId,
            WorkspaceId = query.WorkspaceId ?? service.State.WorkspaceId,
            CollectionId = query.CollectionId ?? service.State.CollectionId,
            Source = "eval learning-feedback-smoke",
            SourceOperationId = "learning-feedback-smoke",
            CapabilityId = ShadowCapabilityIds.VectorRetrieval,
            TargetType = LearningFeedbackTargetType.VectorCandidate,
            TargetId = "smoke-vector-candidate",
            FeedbackKind = LearningFeedbackKinds.MissingContext,
            FeedbackValue = -1,
            Reason = "smoke feedback reason",
            RedactionMode = "metadata-only",
            MetadataOnly = true,
            TrainingUse = "disabled_until_review",
            Confidence = 1.0,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["smoke"] = "true"
            }
        };

        var feedbackService = new LearningFeedbackService(service.State.LearningFeedbackStore);
        var before = service.State.IsServiceMode
            ? await service.State.ServiceClient!.GetLearningFeedbackSummaryAsync(query, cancellationToken).ConfigureAwait(false)
            : await feedbackService.BuildSummaryAsync(query, cancellationToken).ConfigureAwait(false);

        var first = await service.SubmitLearningFeedbackAsync(request, cancellationToken).ConfigureAwait(false);
        var second = await service.SubmitLearningFeedbackAsync(request, cancellationToken).ConfigureAwait(false);

        var after = service.State.IsServiceMode
            ? await service.State.ServiceClient!.GetLearningFeedbackSummaryAsync(query, cancellationToken).ConfigureAwait(false)
            : await feedbackService.BuildSummaryAsync(query, cancellationToken).ConfigureAwait(false);
        var export = service.State.IsServiceMode
            ? await service.State.ServiceClient!.ExportLearningFeedbackAsync(query, cancellationToken).ConfigureAwait(false)
            : await feedbackService.ExportJsonLinesAsync(query, cancellationToken).ConfigureAwait(false);

        var failed = new List<string>();
        AddIfFalse(failed, first.Event.FeedbackId == smokeId, "SubmitWorks");
        AddIfFalse(failed, second.DuplicateReplaced, "DuplicateFeedbackIdUpsertWorks");
        AddIfFalse(failed, second.Event.MetadataOnly, "MetadataOnlyWorks");
        AddIfFalse(failed, string.Equals(second.Event.RedactionMode, "metadata-only", StringComparison.OrdinalIgnoreCase), "RedactionModePreserved");
        AddIfFalse(failed, string.Equals(second.Event.TrainingUse, "disabled_until_review", StringComparison.OrdinalIgnoreCase), "TrainingUseDisabledUntilReview");
        AddIfFalse(failed, after.FeedbackCount >= before.FeedbackCount, "SummaryCountUpdated");
        AddIfFalse(failed, export.Contains(smokeId, StringComparison.OrdinalIgnoreCase), "ExportJsonlContainsFeedback");

        var report = new LearningFeedbackSmokeReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            FeedbackId = smokeId,
            SubmitWorks = first.Event.FeedbackId == smokeId,
            DuplicateFeedbackIdUpsertWorks = second.DuplicateReplaced,
            MetadataOnlyWorks = second.Event.MetadataOnly,
            RedactionModePreserved = string.Equals(second.Event.RedactionMode, "metadata-only", StringComparison.OrdinalIgnoreCase),
            TrainingUseDisabledUntilReview = string.Equals(second.Event.TrainingUse, "disabled_until_review", StringComparison.OrdinalIgnoreCase),
            SummaryCountUpdated = after.FeedbackCount >= before.FeedbackCount,
            ExportJsonlContainsFeedback = export.Contains(smokeId, StringComparison.OrdinalIgnoreCase),
            SummaryCountBefore = before.FeedbackCount,
            SummaryCountAfter = after.FeedbackCount,
            Recommendation = failed.Count == 0 ? "FeedbackCaptureReady" : "FeedbackCaptureNeedsFix",
            FailedChecks = failed
        };

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildLearningFeedbackSmokeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Learning feedback smoke report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Learning feedback smoke markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] recommendation={report.Recommendation}; failed={report.FailedChecks.Count}; before={report.SummaryCountBefore}; after={report.SummaryCountAfter}");
    }

    private static LearningFeedbackReviewQuery BuildLearningFeedbackReviewQuery(IReadOnlyList<string> args)
    {
        FeedbackReviewStatus? status = null;
        var statusText = CommandHelpers.GetOption(args, "--review-status");
        if (!string.IsNullOrWhiteSpace(statusText)
            && Enum.TryParse<FeedbackReviewStatus>(statusText, ignoreCase: true, out var parsedStatus))
        {
            status = parsedStatus;
        }

        return new LearningFeedbackReviewQuery
        {
            FeedbackId = CommandHelpers.GetOption(args, "--feedback-id"),
            ReviewStatus = status,
            Reviewer = CommandHelpers.GetOption(args, "--reviewer"),
            Limit = int.TryParse(CommandHelpers.GetOption(args, "--limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit > 0
                ? limit
                : 100,
            Offset = int.TryParse(CommandHelpers.GetOption(args, "--offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) && offset >= 0
                ? offset
                : 0
        };
    }

    private static async Task<LearningFeedbackQualityReport> BuildLearningFeedbackQualityReportAsync(
        ControlRoomService service,
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken)
    {
        var feedback = await service.State.LearningFeedbackStore
            .QueryAsync(query, cancellationToken)
            .ConfigureAwait(false);
        var reviews = await service.State.LearningFeedbackReviewStore
            .QueryAsync(new LearningFeedbackReviewQuery { Limit = int.MaxValue }, cancellationToken)
            .ConfigureAwait(false);
        var featureCandidates = await new LearningFeedbackFeatureCandidateBuilder(
                service.State.LearningFeedbackStore,
                service.State.LearningFeedbackReviewStore)
            .BuildAsync(query, updateNeedsMoreEvidence: false, cancellationToken)
            .ConfigureAwait(false);
        var featureDataset = await new LearningFeatureDatasetService(CreatePolicyFeedbackDatasetServiceForEval(service))
            .BuildAsync(
                query.WorkspaceId ?? service.State.WorkspaceId,
                query.CollectionId ?? service.State.CollectionId,
                limit: 1,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new LearningFeedbackQualityReportBuilder()
            .Build(feedback, reviews, featureCandidates, featureDataset);
    }

    private static LearningFeedbackSubmitRequest CreateSmokeFeedbackRequest(
        string workspaceId,
        string collectionId,
        string feedbackId,
        string capabilityId,
        LearningFeedbackTargetType targetType,
        string targetId,
        string feedbackKind)
    {
        return new LearningFeedbackSubmitRequest
        {
            FeedbackId = feedbackId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Source = "eval learning-feedback-review-smoke",
            SourceOperationId = "learning-feedback-review-smoke",
            CapabilityId = capabilityId,
            TargetType = targetType,
            TargetId = targetId,
            FeedbackKind = feedbackKind,
            FeedbackValue = -1,
            Reason = "smoke feedback reason",
            RedactionMode = "metadata-only",
            MetadataOnly = true,
            TrainingUse = "disabled_until_review",
            Confidence = 1.0,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["smoke"] = "true",
                ["excludedFromTraining"] = "true",
                ["trainingUse"] = "smoke_test_only"
            }
        };
    }

    private static LearningFeedbackReviewRequest CreateSmokeReviewRequest(FeedbackReviewStatus status)
    {
        return new LearningFeedbackReviewRequest
        {
            Reviewer = "eval-learning-feedback-review-smoke",
            ReviewReason = $"smoke review {status}",
            RedactionChecked = true,
            TrainingUse = status == FeedbackReviewStatus.ApprovedForDataset
                ? "smoke_test_only"
                : "disabled_until_review",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["smoke"] = "true",
                ["excludedFromTraining"] = "true"
            }
        };
    }

    private static LearningFeedbackEventQuery BuildLearningFeedbackQuery(
        ControlRoomService service,
        IReadOnlyList<string> args,
        int defaultLimit)
    {
        var limitArg = CommandHelpers.GetOption(args, "--limit");
        var offsetArg = CommandHelpers.GetOption(args, "--offset");
        var limit = defaultLimit;
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(limitArg)
            && int.TryParse(limitArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLimit)
            && parsedLimit > 0)
        {
            limit = parsedLimit;
        }

        if (!string.IsNullOrWhiteSpace(offsetArg)
            && int.TryParse(offsetArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOffset)
            && parsedOffset >= 0)
        {
            offset = parsedOffset;
        }

        return new LearningFeedbackEventQuery
        {
            WorkspaceId = CommandHelpers.GetOption(args, "--workspace") ?? service.State.WorkspaceId,
            CollectionId = CommandHelpers.GetOption(args, "--collection") ?? service.State.CollectionId,
            Source = CommandHelpers.GetOption(args, "--source"),
            SourceOperationId = CommandHelpers.GetOption(args, "--source-operation-id"),
            CapabilityId = CommandHelpers.GetOption(args, "--capability")
                ?? CommandHelpers.GetOption(args, "--capability-id"),
            TargetId = CommandHelpers.GetOption(args, "--target")
                ?? CommandHelpers.GetOption(args, "--target-id"),
            TargetType = CommandHelpers.GetOption(args, "--target-type"),
            FeedbackKind = CommandHelpers.GetOption(args, "--kind")
                ?? CommandHelpers.GetOption(args, "--feedback-kind"),
            Limit = limit,
            Offset = offset
        };
    }

    private static LearningFeedbackSubmitRequest BuildLearningFeedbackSubmitRequest(
        ControlRoomService service,
        IReadOnlyList<string> args,
        bool requireExplicitTarget)
    {
        var capabilityId = CommandHelpers.GetOption(args, "--capability")
            ?? CommandHelpers.GetOption(args, "--capability-id");
        var targetTypeText = CommandHelpers.GetOption(args, "--target-type");
        var targetId = CommandHelpers.GetOption(args, "--target-id")
            ?? CommandHelpers.GetOption(args, "--target");
        var feedbackKind = CommandHelpers.GetOption(args, "--kind")
            ?? CommandHelpers.GetOption(args, "--feedback-kind");

        if (requireExplicitTarget
            && (string.IsNullOrWhiteSpace(capabilityId)
                || string.IsNullOrWhiteSpace(targetTypeText)
                || string.IsNullOrWhiteSpace(targetId)
                || string.IsNullOrWhiteSpace(feedbackKind)))
        {
            throw new InvalidOperationException(
                "submit-learning-feedback requires --capability, --target-type, --target-id and --kind.");
        }

        if (!Enum.TryParse<LearningFeedbackTargetType>(targetTypeText, ignoreCase: true, out var targetType))
        {
            throw new InvalidOperationException($"Invalid --target-type value: {targetTypeText}");
        }

        var metadataOnly = ParseBoolOption(CommandHelpers.GetOption(args, "--metadata-only"), defaultValue: true);
        var redactionMode = CommandHelpers.GetOption(args, "--redaction-mode")
            ?? (metadataOnly ? "metadata-only" : string.Empty);
        return new LearningFeedbackSubmitRequest
        {
            FeedbackId = CommandHelpers.GetOption(args, "--feedback-id") ?? string.Empty,
            WorkspaceId = CommandHelpers.GetOption(args, "--workspace") ?? service.State.WorkspaceId,
            CollectionId = CommandHelpers.GetOption(args, "--collection") ?? service.State.CollectionId,
            Source = CommandHelpers.GetOption(args, "--source") ?? "eval submit-learning-feedback",
            SourceOperationId = CommandHelpers.GetOption(args, "--source-operation-id") ?? string.Empty,
            CapabilityId = capabilityId ?? string.Empty,
            TargetType = targetType,
            TargetId = targetId ?? string.Empty,
            FeedbackKind = feedbackKind ?? string.Empty,
            FeedbackValue = ParseDoubleOption(CommandHelpers.GetOption(args, "--value"), -1),
            Reason = CommandHelpers.GetOption(args, "--reason") ?? string.Empty,
            UserCorrection = CommandHelpers.GetOption(args, "--correction") ?? string.Empty,
            RedactionMode = redactionMode,
            MetadataOnly = metadataOnly,
            TrainingUse = "disabled_until_review",
            Confidence = ParseDoubleOption(CommandHelpers.GetOption(args, "--confidence"), 1.0)
        };
    }

    private static string BuildLearningFeedbackSmokeMarkdown(LearningFeedbackSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Learning Feedback Smoke Report");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- FeedbackId: `{report.FeedbackId}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- SummaryCountBefore: `{report.SummaryCountBefore}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- SummaryCountAfter: `{report.SummaryCountAfter}`");
        builder.AppendLine();
        builder.AppendLine("| Check | Passed |");
        builder.AppendLine("|---|---:|");
        builder.AppendLine($"| SubmitWorks | {report.SubmitWorks} |");
        builder.AppendLine($"| DuplicateFeedbackIdUpsertWorks | {report.DuplicateFeedbackIdUpsertWorks} |");
        builder.AppendLine($"| MetadataOnlyWorks | {report.MetadataOnlyWorks} |");
        builder.AppendLine($"| RedactionModePreserved | {report.RedactionModePreserved} |");
        builder.AppendLine($"| TrainingUseDisabledUntilReview | {report.TrainingUseDisabledUntilReview} |");
        builder.AppendLine($"| NeedsRedactionReviewWorks | {report.NeedsRedactionReviewWorks} |");
        builder.AppendLine($"| RejectReviewWorks | {report.RejectReviewWorks} |");
        builder.AppendLine($"| ApproveMetadataSafeFeedbackWorks | {report.ApproveMetadataSafeFeedbackWorks} |");
        builder.AppendLine($"| SummaryCountUpdated | {report.SummaryCountUpdated} |");
        builder.AppendLine($"| ExportJsonlContainsFeedback | {report.ExportJsonlContainsFeedback} |");
        builder.AppendLine($"| FeatureCandidateBuilt | {report.FeatureCandidateBuilt} |");
        builder.AppendLine($"| FeatureCandidateExported | {report.FeatureCandidateExported} |");
        builder.AppendLine($"| QualityReportRefreshed | {report.QualityReportRefreshed} |");
        builder.AppendLine($"| SmokeRecordExcludedFromTraining | {report.SmokeRecordExcludedFromTraining} |");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- FeatureCandidateCount: `{report.FeatureCandidateCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- TrainableCandidateCount: `{report.TrainableCandidateCount}`");
        if (report.FailedChecks.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Failed Checks");
            foreach (var check in report.FailedChecks)
            {
                builder.AppendLine($"- {check}");
            }
        }

        return builder.ToString();
    }

    private static void AddIfFalse(List<string> failed, bool passed, string check)
    {
        if (!passed)
        {
            failed.Add(check);
        }
    }

    private static bool ParseBoolOption(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static double ParseDoubleOption(string? value, double defaultValue)
    {
        return !string.IsNullOrWhiteSpace(value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }


    private static async Task ExecuteLearningRankerAnalysisAsync(
        string subcommand,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var featuresDirectory = CommandHelpers.GetOption(args, "--features-dir")
            ?? CommandHelpers.GetOption(args, "--in-dir")
            ?? Path.Combine(current, "learning", "features");
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "learning", "baselines");
        var inputPath = CommandHelpers.GetOption(args, "--ranker-input")
            ?? Path.Combine(featuresDirectory, LearningDatasetQualityReportBuilder.RankingPairsFileName);

        Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
        var runner = new LearningOfflineBaselineRunner();

        if (string.Equals(subcommand, "learning-ranker-ablation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            var jsonPath = Path.Combine(outputDirectory, "ranker-ablation-report.json");
            var markdownPath = Path.Combine(outputDirectory, "ranker-ablation-report.md");
            var report = await runner.RunRankerAblationAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Ranker ablation report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Ranker ablation markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Ranker ablation status={report.Status}; pairs={report.PairCount}; baseline={report.Baseline.PairwiseAccuracy:P2}");
            foreach (var ablation in report.Ablations.OrderBy(item => item.DisabledFeature, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[Eval] Ablation {ablation.DisabledFeature}: pairwiseAccuracy={ablation.PairwiseAccuracy:P2}, delta={ablation.AccuracyDelta:P2}");
            }
        }

        if (string.Equals(subcommand, "learning-ranker-weight-sweep", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            var jsonPath = Path.Combine(outputDirectory, "ranker-weight-sweep-report.json");
            var markdownPath = Path.Combine(outputDirectory, "ranker-weight-sweep-report.md");
            var report = await runner.RunRankerWeightSweepAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Ranker weight sweep report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Ranker weight sweep markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Ranker weight sweep status={report.Status}; pairs={report.PairCount}; baseline={report.Baseline.PairwiseAccuracy:P2}; best={report.BestResult.ConfigurationId} {report.BestResult.PairwiseAccuracy:P2}");
        }

        if (string.Equals(subcommand, "learning-ranker-residual-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            var jsonPath = Path.Combine(outputDirectory, "ranker-residual-audit-report.json");
            var markdownPath = Path.Combine(outputDirectory, "ranker-residual-audit-report.md");
            var report = await runner.RunRankerResidualAuditAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Ranker residual audit report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Ranker residual audit markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Ranker residual audit status={report.Status}; pairs={report.PairCount}; failures={report.Failures.Count}; clusters={(report.FailureClusters.Count == 0 ? "-" : string.Join(", ", report.FailureClusters.Select(item => $"{item.Cluster}:{item.Count}")))}");
        }

        if (string.Equals(subcommand, "learning-hard-negatives", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            var residualAuditPath = CommandHelpers.GetOption(args, "--residual-audit")
                ?? Path.Combine(outputDirectory, "ranker-residual-audit-report.json");
            var jsonLinesPath = Path.Combine(featuresDirectory, "hard-negatives.jsonl");
            var jsonPath = Path.Combine(outputDirectory, "hard-negative-report.json");
            var markdownPath = Path.Combine(outputDirectory, "hard-negative-report.md");
            var report = await runner.RunHardNegativeGenerationAsync(
                    residualAuditPath,
                    jsonLinesPath,
                    jsonPath,
                    markdownPath,
                    cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"[Eval] Hard negative dataset: {Path.GetFullPath(jsonLinesPath)}");
            Console.WriteLine($"[Eval] Hard negative report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Hard negative markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Hard negative status={report.Status}; failures={report.SourceFailureCount}; examples={report.ExampleCount}; types={(report.TypeCounts.Count == 0 ? "-" : string.Join(", ", report.TypeCounts.Select(item => $"{item.Key}:{item.Value}")))}");
        }

        if (string.Equals(subcommand, "learning-lifecycle-aware-ranker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            var jsonPath = Path.Combine(outputDirectory, "lifecycle-aware-ranker-report.json");
            var markdownPath = Path.Combine(outputDirectory, "lifecycle-aware-ranker-report.md");
            var report = await runner.RunLifecycleAwareRankerAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"[Eval] Lifecycle-aware ranker report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Lifecycle-aware ranker markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Lifecycle-aware status={report.Status}; pairs={report.PairCount}; best={report.BestBaseline}; targetPassed={report.TargetPassed}");
            foreach (var baseline in report.Baselines)
            {
                Console.WriteLine($"[Eval] Lifecycle {baseline.BaselineName}: pairwiseAccuracy={baseline.PairwiseAccuracy:P2}, residual={baseline.ResidualFailures}, deprecatedNoise={baseline.DeprecatedNoiseFailures}, fpr={baseline.FalsePositiveRate:P2}, fnr={baseline.FalseNegativeRate:P2}");
            }
        }
    }

    private static PolicyFeedbackDatasetService? CreatePolicyFeedbackDatasetServiceForEval(ControlRoomService service)
    {
        if (service.State.IsServiceMode || string.IsNullOrWhiteSpace(service.State.RootPath))
        {
            return null;
        }

        if (!string.Equals(service.State.StorageKind, "filesystem", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var options = new FileStorageOptions { RootPath = service.State.RootPath };
        var paths = new FilePathResolver(options);
        var serializer = new FileFormatSerializer();
        return new PolicyFeedbackDatasetService(
            new FileShortTermPromotionCandidateStore(paths, serializer),
            new FileStableReviewCandidateStore(paths, serializer),
            new FileConstraintGapCandidateStore(paths, serializer),
            new FileCandidateConstraintReviewStore(paths, serializer),
            new FileConstraintStore(paths, serializer));
    }

private static async Task ExecuteExportLearningFeedbackAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var query = BuildLearningFeedbackQuery(service, args, defaultLimit: 1000);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("learning", "feedback", "learning-feedback-events.jsonl");

        string jsonl;
        if (service.State.IsServiceMode)
        {
            jsonl = await service.State.ServiceClient!
                .ExportLearningFeedbackAsync(query, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            jsonl = await new LearningFeedbackService(service.State.LearningFeedbackStore)
                .ExportJsonLinesAsync(query, cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteTextAsync(jsonl, outputPath, cancellationToken)
            .ConfigureAwait(false);
        var lineCount = string.IsNullOrWhiteSpace(jsonl)
            ? 0
            : jsonl.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length;
        Console.WriteLine($"[Eval] Learning feedback export: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] records={lineCount}");
    }


    private static async Task ExecuteSubmitLearningFeedbackAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var request = BuildLearningFeedbackSubmitRequest(service, args, requireExplicitTarget: true);
        var result = await service.SubmitLearningFeedbackAsync(request, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning feedback submitted: {result.FeedbackId}");
        Console.WriteLine($"[Eval] created={result.Created}; duplicateReplaced={result.DuplicateReplaced}; metadataOnly={result.Event.MetadataOnly}; trainingUse={result.Event.TrainingUse}");
    }
}
