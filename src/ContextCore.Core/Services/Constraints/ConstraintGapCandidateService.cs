using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>从 eval/report 只读生成约束语料缺口候选项，不写入 ConstraintStore。</summary>
public sealed class ConstraintGapCandidateService
{
    private const string PlanningConstraintReportSource = "planning-optin-constraint-safety-report";
    private const string ExtendedFailureTriageSource = "extended-failure-triage-report";
    private const string PolicyVersion = "constraint-gap-review-policy/v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IConstraintGapCandidateStore _gapStore;
    private readonly IConstraintStore _constraintStore;

    public ConstraintGapCandidateService(
        IConstraintGapCandidateStore gapStore,
        IConstraintStore constraintStore)
    {
        _gapStore = gapStore;
        _constraintStore = constraintStore;
    }

    public async Task<ConstraintGapGenerationResult> GenerateAsync(
        ConstraintGapGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        var warnings = new List<string>();
        var sources = await LoadSourcesAsync(request, warnings, cancellationToken).ConfigureAwait(false);
        var scopedConstraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Level = ConstraintLevel.Hard,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var created = 0;
        var existing = 0;
        var skippedMatched = 0;
        var gaps = new List<ConstraintGapCandidate>();
        var limit = request.Limit > 0 ? request.Limit : 200;

        foreach (var source in sources.Take(limit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = FindMatchingConstraints(scopedConstraints, source.ExpectedConstraintText);
            if (matches.Count > 0)
            {
                skippedMatched++;
                continue;
            }

            var candidate = BuildGapCandidate(request, source);
            var existingGap = await FindExistingGapAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (existingGap is not null)
            {
                existing++;
                gaps.Add(existingGap);
            }
            else
            {
                var saved = await _gapStore.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
                gaps.Add(saved);
                created++;
            }
        }

        return new ConstraintGapGenerationResult
        {
            OperationId = Guid.NewGuid().ToString("N"),
            GeneratedAt = DateTimeOffset.UtcNow,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            ScannedSampleCount = sources
                .Select(source => $"{source.Source}\u001f{source.SourceSampleId}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            MissingConstraintCount = sources.Count,
            CreatedCount = created,
            ExistingCount = existing,
            SkippedMatchedCount = skippedMatched,
            Gaps = gaps
                .DistinctBy(gap => gap.GapId, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static gap => gap.CreatedAt)
                .ToArray(),
            Warnings = warnings
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    public Task<IReadOnlyList<ConstraintGapCandidate>> QueryAsync(
        ConstraintGapCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkspaceId);
        return _gapStore.QueryAsync(query, cancellationToken);
    }

    public Task<ConstraintGapCandidate?> GetAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        return _gapStore.GetAsync(gapId, cancellationToken);
    }

    public Task<IReadOnlyList<ConstraintGapReviewRecord>> GetReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        return _gapStore.QueryReviewsAsync(gapId, cancellationToken);
    }

    public Task<ConstraintGapReviewResult?> AcceptAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(gapId, "accept", ConstraintGapStatus.Accepted, request, cancellationToken);
    }

    public Task<ConstraintGapReviewResult?> RejectAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(gapId, "reject", ConstraintGapStatus.Rejected, request, cancellationToken);
    }

    private async Task<ConstraintGapReviewResult?> ReviewAsync(
        string gapId,
        string action,
        string targetStatus,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        ArgumentNullException.ThrowIfNull(request);

        var gap = await _gapStore.GetAsync(gapId, cancellationToken).ConfigureAwait(false);
        if (gap is null)
        {
            return null;
        }

        if (!string.Equals(gap.Status, ConstraintGapStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"约束缺口候选项当前状态为 {gap.Status}，不能再次执行 {action}。",
                nameof(gapId));
        }

        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId.Trim();
        var reviewer = string.IsNullOrWhiteSpace(request.Reviewer) ? "manual" : request.Reviewer.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? action : request.Reason.Trim();
        var now = DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        string? createdConstraintId = null;
        var reviewId = $"cgr-{BuildShortHash($"{gap.GapId}\u001f{action}\u001f{now:O}")}";

        if (action.Equals("accept", StringComparison.OrdinalIgnoreCase))
        {
            var constraint = BuildCandidateConstraint(gap, reviewer, reason, reviewId, now);
            await _constraintStore.SaveAsync(constraint, cancellationToken).ConfigureAwait(false);
            createdConstraintId = constraint.Id;
        }

        var updatedGap = await _gapStore.UpdateStatusAsync(
            gap.GapId,
            targetStatus,
            reviewer,
            reason,
            cancellationToken).ConfigureAwait(false);
        if (updatedGap is null)
        {
            return null;
        }

        var review = new ConstraintGapReviewRecord
        {
            ReviewId = reviewId,
            GapId = gap.GapId,
            WorkspaceId = gap.WorkspaceId,
            CollectionId = gap.CollectionId,
            SessionId = gap.SessionId,
            Action = action,
            FromStatus = gap.Status,
            ToStatus = targetStatus,
            Reviewer = reviewer,
            Reason = reason,
            CreatedConstraintId = createdConstraintId,
            TargetItemKind = createdConstraintId is null ? null : "constraint",
            TargetLayer = createdConstraintId is null ? null : "CandidateConstraint",
            SourceSampleId = gap.SourceSampleId,
            SourceOperationId = gap.SourceOperationId,
            ExpectedConstraintText = gap.ExpectedConstraintText,
            EvidenceRefs = gap.EvidenceRefs.ToArray(),
            CreatedAt = now,
            ReviewedAt = now,
            Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["operationId"] = operationId,
                ["sourceConstraintGapId"] = gap.GapId,
                ["sourceSampleId"] = gap.SourceSampleId,
                ["sourceOperationId"] = gap.SourceOperationId,
                ["expectedConstraintText"] = gap.ExpectedConstraintText,
                ["reviewedAt"] = now.ToString("O")
            },
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };
        await _gapStore.AppendReviewAsync(review, cancellationToken).ConfigureAwait(false);

        return new ConstraintGapReviewResult
        {
            OperationId = operationId,
            GapId = gap.GapId,
            Action = action,
            Status = updatedGap.Status,
            ReviewId = review.ReviewId,
            Reviewer = reviewer,
            Reason = reason,
            ReviewedAt = now,
            CreatedConstraintId = createdConstraintId,
            TargetItemId = createdConstraintId,
            TargetItemKind = review.TargetItemKind,
            TargetLayer = review.TargetLayer,
            Gap = updatedGap,
            Review = review,
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };
    }

    private async Task<ConstraintGapCandidate?> FindExistingGapAsync(
        ConstraintGapCandidate candidate,
        CancellationToken cancellationToken)
    {
        var existing = await _gapStore.QueryAsync(new ConstraintGapCandidateQuery
        {
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SourceSampleId = candidate.SourceSampleId,
            Limit = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        return existing.FirstOrDefault(item =>
            string.Equals(NormalizeText(item.ExpectedConstraintText), NormalizeText(candidate.ExpectedConstraintText), StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyList<GapSourceItem>> LoadSourcesAsync(
        ConstraintGapGenerationRequest request,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var sources = new List<GapSourceItem>();
        if (request.IncludePlanningConstraintReport)
        {
            var path = ResolveReportPath(
                request.PlanningConstraintReportPath,
                "eval",
                "planning-optin-constraint-safety-report-extended.json");
            if (File.Exists(path))
            {
                var report = await ReadJsonAsync<PlanningOptInConstraintSafetyReport>(path, cancellationToken).ConfigureAwait(false);
                if (report is not null)
                {
                    sources.AddRange(report.Samples
                        .Where(sample => sample.MissingConstraints.Count > 0)
                        .SelectMany(sample => sample.MissingConstraints.Select(expected => FromPlanningSample(report, sample, expected))));
                }
            }
            else
            {
                warnings.Add($"report_missing:{path}");
            }
        }

        if (request.IncludeExtendedFailureTriageReport)
        {
            var path = ResolveReportPath(
                request.ExtendedFailureTriageReportPath,
                "eval",
                "extended-failure-triage-report.json");
            if (File.Exists(path))
            {
                var report = await ReadJsonAsync<ExtendedFailureTriageReport>(path, cancellationToken).ConfigureAwait(false);
                if (report is not null)
                {
                    sources.AddRange(report.Samples
                        .Where(sample => !sample.ConstraintStatus.Satisfied && sample.ConstraintStatus.Missing.Count > 0)
                        .SelectMany(sample => sample.ConstraintStatus.Missing.Select(expected => FromExtendedSample(report, sample, expected))));
                }
            }
            else
            {
                warnings.Add($"report_missing:{path}");
            }
        }

        return sources
            .Where(source => !string.IsNullOrWhiteSpace(source.ExpectedConstraintText))
            .DistinctBy(
                source => $"{source.Source}\u001f{source.SourceSampleId}\u001f{NormalizeText(source.ExpectedConstraintText)}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveReportPath(string? configuredPath, params string[] defaultSegments)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return Path.GetFullPath(Path.Combine(defaultSegments));
    }

    private static GapSourceItem FromPlanningSample(
        PlanningOptInConstraintSafetyReport report,
        PlanningOptInConstraintSafetySample sample,
        string expected)
    {
        return new GapSourceItem(
            PlanningConstraintReportSource,
            sample.SampleId,
            report.ReportId,
            expected,
            sample.FallbackUsed || string.Equals(sample.ConstraintRepairStatus, "ConstraintRepairFailed", StringComparison.OrdinalIgnoreCase)
                ? ConstraintGapSeverity.High
                : ConstraintGapSeverity.Medium,
            "Expected hard constraint was not covered by proposal constraint repair and no matching hard constraint exists in ConstraintStore.",
            [$"eval:{PlanningConstraintReportSource}:{sample.SampleId}"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mode"] = sample.Mode,
                ["intent"] = sample.Intent,
                ["optInMatched"] = sample.OptInMatched.ToString(),
                ["applied"] = sample.Applied.ToString(),
                ["fallbackUsed"] = sample.FallbackUsed.ToString(),
                ["constraintSource"] = sample.ConstraintSource,
                ["lostAtStage"] = sample.LostAtStage,
                ["constraintRepairStatus"] = sample.ConstraintRepairStatus,
                ["suggestedFix"] = sample.SuggestedFix,
                ["sourceReport"] = PlanningConstraintReportSource
            });
    }

    private static GapSourceItem FromExtendedSample(
        ExtendedFailureTriageReport report,
        ExtendedFailureTriageSample sample,
        string expected)
    {
        return new GapSourceItem(
            ExtendedFailureTriageSource,
            sample.SampleId,
            report.OperationId,
            expected,
            ConstraintGapSeverity.High,
            "Formal extended eval expected a hard constraint that is missing from constraint/package sections and no matching hard constraint exists in ConstraintStore.",
            [$"eval:{ExtendedFailureTriageSource}:{sample.SampleId}"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mode"] = sample.Mode,
                ["failedReason"] = sample.FailedReason,
                ["failureCategories"] = string.Join(',', sample.FailureCategories),
                ["suspectedRootCause"] = sample.SuspectedRootCause,
                ["suggestedFixType"] = sample.SuggestedFixType,
                ["sourceReport"] = ExtendedFailureTriageSource
            });
    }

    private static IReadOnlyList<ContextConstraint> FindMatchingConstraints(
        IReadOnlyList<ContextConstraint> constraints,
        string expectedConstraintText)
    {
        var expected = NormalizeText(expectedConstraintText);
        if (string.IsNullOrWhiteSpace(expected))
        {
            return Array.Empty<ContextConstraint>();
        }

        return constraints
            .Where(constraint =>
            {
                var content = NormalizeText(constraint.Content);
                return !string.IsNullOrWhiteSpace(content)
                    && (content.Contains(expected, StringComparison.OrdinalIgnoreCase)
                        || expected.Contains(content, StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();
    }

    private static ConstraintGapCandidate BuildGapCandidate(
        ConstraintGapGenerationRequest request,
        GapSourceItem source)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["generatedBy"] = "constraint-gap-candidate-service/rule-based",
            ["policyVersion"] = PolicyVersion,
            ["createdFrom"] = "constraint_gap_review",
            ["constraintStoreWrite"] = "false"
        };

        return new ConstraintGapCandidate
        {
            GapId = BuildGapId(request.WorkspaceId, request.CollectionId, source.ExpectedConstraintText, source.SourceSampleId),
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SessionId = request.SessionId,
            Source = source.Source,
            SourceSampleId = source.SourceSampleId,
            SourceOperationId = source.SourceOperationId,
            ExpectedConstraintText = source.ExpectedConstraintText,
            MatchedConstraintIds = Array.Empty<string>(),
            SuggestedConstraintTitle = BuildSuggestedTitle(source.ExpectedConstraintText),
            SuggestedConstraintScope = "Collection",
            SuggestedConstraintType = "Hard",
            Severity = source.Severity,
            Reason = source.Reason,
            EvidenceRefs = source.EvidenceRefs,
            Status = ConstraintGapStatus.Pending,
            CreatedAt = now,
            Metadata = metadata
        };
    }

    private static string BuildSuggestedTitle(string expectedConstraintText)
    {
        var text = expectedConstraintText.Trim();
        return text.Length <= 48
            ? text
            : string.Concat(text.AsSpan(0, 48), "...");
    }

    private static ContextConstraint BuildCandidateConstraint(
        ConstraintGapCandidate gap,
        string reviewer,
        string reason,
        string reviewId,
        DateTimeOffset now)
    {
        var metadata = new Dictionary<string, string>(gap.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceConstraintGapId"] = gap.GapId,
            ["sourceConstraintGapReviewId"] = reviewId,
            ["sourceSampleId"] = gap.SourceSampleId,
            ["sourceOperationId"] = gap.SourceOperationId,
            ["expectedConstraintText"] = gap.ExpectedConstraintText,
            ["reviewer"] = reviewer,
            ["reviewReason"] = reason,
            ["evidenceRefs"] = string.Join(",", gap.EvidenceRefs),
            ["createdFrom"] = "constraint_gap_accept",
            ["status"] = ContextMemoryStatus.Candidate.ToString(),
            ["suggestedConstraintType"] = gap.SuggestedConstraintType,
            ["suggestedConstraintScope"] = gap.SuggestedConstraintScope,
            ["source"] = gap.Source
        };

        return new ContextConstraint
        {
            Id = $"constraint:gap:{gap.GapId}",
            WorkspaceId = gap.WorkspaceId,
            CollectionId = gap.CollectionId,
            Scope = ParseScope(gap.SuggestedConstraintScope),
            Level = ConstraintLevel.User,
            Content = gap.ExpectedConstraintText,
            AppliesToRefs = Array.Empty<string>(),
            SourceRefs = gap.EvidenceRefs
                .Append(gap.GapId)
                .Append(gap.SourceSampleId)
                .Append(gap.SourceOperationId)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Status = ContextMemoryStatus.Candidate,
            Confidence = 0.75,
            Metadata = metadata,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ContextScope ParseScope(string? value)
    {
        return Enum.TryParse<ContextScope>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ContextScope.Collection;
    }

    private static string BuildGapId(
        string workspaceId,
        string collectionId,
        string expectedConstraintText,
        string sourceSampleId)
    {
        var key = string.Join('\u001f', workspaceId, collectionId, NormalizeText(expectedConstraintText), sourceSampleId);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"constraint-gap-{Convert.ToHexString(hash)[..20].ToLowerInvariant()}";
    }

    private static string BuildShortHash(string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Trim().Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private sealed record GapSourceItem(
        string Source,
        string SourceSampleId,
        string SourceOperationId,
        string ExpectedConstraintText,
        string Severity,
        string Reason,
        IReadOnlyList<string> EvidenceRefs,
        Dictionary<string, string> Metadata);
}
