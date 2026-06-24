using System.Globalization;
using System.Text.RegularExpressions;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>Scores compression output using structural signals that are available without another model call.</summary>
public sealed class CompressionQualityEvaluator
{
    private const string MetadataPrefix = "quality.";
    private static readonly Regex TermPattern = new(@"[\p{L}\p{Nd}]{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "about", "should",
        "would", "could", "when", "then", "than", "have", "has", "are", "was", "were",
        "you", "your", "context", "content", "source", "item"
    };

    public CompressionQualityReport Evaluate(
        CompressionRequest request,
        CompressionResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var generated = response.GeneratedItems.FirstOrDefault();
        var inputTokens = response.Usage.InputTokens > 0
            ? response.Usage.InputTokens
            : request.Inputs.Sum(item => BasicContextPackageBuilder.EstimateTokens(item.Content));
        var outputTokens = response.Usage.OutputTokens > 0
            ? response.Usage.OutputTokens
            : response.GeneratedItems.Sum(item => BasicContextPackageBuilder.EstimateTokens(item.Content));
        var compressionRatio = inputTokens > 0
            ? Clamp01((double)outputTokens / inputTokens)
            : 0;

        var sourceCoverage = ResolveSourceCoverage(request, generated);
        var keyTermCoverage = ResolveKeyTermCoverage(request, response, generated);
        var tagCoverage = ResolveTagCoverage(request, generated);
        var contentSignal = ResolveContentSignal(request, generated, outputTokens);
        var budgetScore = ResolveBudgetScore(request, outputTokens);
        var compressionFit = ResolveCompressionFit(request, compressionRatio, inputTokens, outputTokens);
        var sourceTraceability = request.Options.PreserveSourceRefs || request.Inputs.Count > 1
            ? sourceCoverage
            : generated is null ? 0 : Math.Max(sourceCoverage, 0.8);
        var hasGeneratedItem = generated is null ? 0.0 : 1.0;
        var completeness = response.Status == CompressionStatus.Failed
            ? 0
            : Clamp01(
                (sourceTraceability * 0.45)
                + (contentSignal * 0.3)
                + (keyTermCoverage * 0.1)
                + (tagCoverage * 0.1)
                + (hasGeneratedItem * 0.05));

        var missingRequiredSources = request.Options.PreserveSourceRefs
            && request.Inputs.Count > 0
            && sourceCoverage < 1;
        var outputLongerThanInput = inputTokens > 0 && outputTokens > inputTokens;

        var consistency = Clamp01(1.0
            - (response.Errors.Count * 0.25)
            - (response.Warnings.Count * 0.08)
            - (missingRequiredSources ? 0.2 : 0)
            - (outputLongerThanInput ? 0.15 : 0)
            - (contentSignal <= 0 && request.Inputs.Count > 0 ? 0.25 : 0)
            - (generated is null ? 0.3 : 0)
            - (response.Status == CompressionStatus.Failed ? 0.5 : 0));

        var tagPresence = generated?.Tags.Count > 0 ? 1.0 : 0.0;
        var sourceRefsScore = request.Options.PreserveSourceRefs ? sourceCoverage : 1.0;
        var indexHintScore = request.Options.GenerateIndexHints
            ? response.IndexHints.Count > 0 ? 1.0 : 0.0
            : 1.0;
        var usability = Clamp01(
            (contentSignal * 0.25)
            + (tagPresence * 0.15)
            + (sourceRefsScore * 0.15)
            + (indexHintScore * 0.15)
            + (budgetScore * 0.15)
            + (compressionFit * 0.15)
            - (response.Errors.Count * 0.15));

        var explicitRequiresReview = response.Status == CompressionStatus.RequiresReview
            || generated?.Metadata.TryGetValue("requiresReview", out var value) == true
                && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        var overCompressed = inputTokens >= 40 && outputTokens > 0 && compressionRatio < 0.05;
        var invalidStructuredOutput = response.Errors.Any(error =>
            error.Code.Contains("json", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("json", StringComparison.OrdinalIgnoreCase));
        var risk = Clamp01(
            (response.Status == CompressionStatus.Failed ? 0.8 : 0)
            + (explicitRequiresReview ? 0.35 : 0)
            + ((1 - completeness) * 0.25)
            + ((1 - consistency) * 0.25)
            + ((1 - usability) * 0.2)
            + (missingRequiredSources ? 0.18 : 0)
            + (outputLongerThanInput ? 0.12 : 0)
            + (compressionRatio > 0.9 ? 0.1 : 0)
            + (overCompressed ? 0.12 : 0)
            + (budgetScore < 1 ? 0.1 : 0)
            + (invalidStructuredOutput ? 0.25 : 0)
            + (response.Errors.Count > 0 ? 0.25 : 0)
            + (response.Warnings.Count > 0 ? 0.08 : 0));

        var requiresReview = explicitRequiresReview
            || response.Status == CompressionStatus.Failed
            || response.Errors.Count > 0
            || completeness < 0.35
            || consistency < 0.5
            || risk >= 0.65;

        return new CompressionQualityReport
        {
            OperationId = response.OperationId,
            GeneratedItemId = generated?.Id ?? string.Empty,
            CompletenessScore = Round(completeness),
            ConsistencyScore = Round(consistency),
            UsabilityScore = Round(usability),
            CompressionRatio = Round(compressionRatio),
            RiskScore = Round(risk),
            RequiresReview = requiresReview,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Status = response.Status,
            Signals = BuildSignals(
                response,
                request,
                sourceCoverage,
                keyTermCoverage,
                tagCoverage,
                compressionRatio,
                budgetScore,
                contentSignal,
                requiresReview,
                missingRequiredSources,
                outputLongerThanInput,
                overCompressed),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static ContextItem WithQualityMetadata(
        ContextItem item,
        CompressionQualityReport report)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(report);

        var metadata = new Dictionary<string, string>(item.Metadata)
        {
            [$"{MetadataPrefix}operationId"] = report.OperationId,
            [$"{MetadataPrefix}generatedItemId"] = report.GeneratedItemId,
            [$"{MetadataPrefix}completenessScore"] = Format(report.CompletenessScore),
            [$"{MetadataPrefix}consistencyScore"] = Format(report.ConsistencyScore),
            [$"{MetadataPrefix}usabilityScore"] = Format(report.UsabilityScore),
            [$"{MetadataPrefix}compressionRatio"] = Format(report.CompressionRatio),
            [$"{MetadataPrefix}riskScore"] = Format(report.RiskScore),
            [$"{MetadataPrefix}requiresReview"] = report.RequiresReview ? "true" : "false",
            [$"{MetadataPrefix}inputTokens"] = report.InputTokens.ToString(CultureInfo.InvariantCulture),
            [$"{MetadataPrefix}outputTokens"] = report.OutputTokens.ToString(CultureInfo.InvariantCulture),
            [$"{MetadataPrefix}status"] = report.Status.ToString(),
            [$"{MetadataPrefix}signals"] = string.Join(",", report.Signals),
            [$"{MetadataPrefix}createdAt"] = report.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
        };

        return new ContextItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Title = item.Title,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags,
            Refs = item.Refs,
            SourceRefs = item.SourceRefs,
            Metadata = metadata,
            Importance = item.Importance,
            Version = item.Version,
            Checksum = item.Checksum,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    public static bool TryReadFromMetadata(
        ContextItem item,
        out CompressionQualityReport report)
    {
        ArgumentNullException.ThrowIfNull(item);

        report = new CompressionQualityReport();
        if (!item.Metadata.TryGetValue($"{MetadataPrefix}completenessScore", out var completenessText)
            || !TryReadDouble(completenessText, out var completeness))
        {
            return false;
        }

        var consistency = ReadDouble(item.Metadata, "consistencyScore");
        var usability = ReadDouble(item.Metadata, "usabilityScore");
        var ratio = ReadDouble(item.Metadata, "compressionRatio");
        var risk = ReadDouble(item.Metadata, "riskScore");
        var requiresReview = item.Metadata.TryGetValue($"{MetadataPrefix}requiresReview", out var reviewText)
            && bool.TryParse(reviewText, out var parsedReview)
            && parsedReview;
        var status = item.Metadata.TryGetValue($"{MetadataPrefix}status", out var statusText)
            && Enum.TryParse<CompressionStatus>(statusText, ignoreCase: true, out var parsedStatus)
                ? parsedStatus
                : CompressionStatus.Succeeded;
        var createdAt = item.Metadata.TryGetValue($"{MetadataPrefix}createdAt", out var createdAtText)
            && DateTimeOffset.TryParse(
                createdAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedCreatedAt)
                ? parsedCreatedAt
                : item.UpdatedAt;

        report = new CompressionQualityReport
        {
            OperationId = item.Metadata.GetValueOrDefault($"{MetadataPrefix}operationId", item.Metadata.GetValueOrDefault("operationId", string.Empty)),
            GeneratedItemId = item.Metadata.GetValueOrDefault($"{MetadataPrefix}generatedItemId", item.Id),
            CompletenessScore = completeness,
            ConsistencyScore = consistency,
            UsabilityScore = usability,
            CompressionRatio = ratio,
            RiskScore = risk,
            RequiresReview = requiresReview,
            InputTokens = ReadInt(item.Metadata, "inputTokens"),
            OutputTokens = ReadInt(item.Metadata, "outputTokens"),
            Status = status,
            Signals = item.Metadata.TryGetValue($"{MetadataPrefix}signals", out var signals)
                ? signals.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>(),
            CreatedAt = createdAt
        };
        return true;
    }

    private static double ResolveSourceCoverage(
        CompressionRequest request,
        ContextItem? generated)
    {
        if (request.Inputs.Count == 0)
        {
            return generated is null ? 0 : 1;
        }

        if (generated is null)
        {
            return 0;
        }

        var covered = generated.SourceRefs
            .Concat(generated.Refs)
            .Concat(ReadDerivedFrom(generated))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inputIds = request.Inputs
            .Select(item => item.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return inputIds.Length == 0
            ? 1
            : (double)inputIds.Count(covered.Contains) / inputIds.Length;
    }

    private static IEnumerable<string> ReadDerivedFrom(ContextItem item)
    {
        return item.Metadata.TryGetValue("derivedFrom", out var derivedFrom)
            ? derivedFrom.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
    }

    private static double ResolveKeyTermCoverage(
        CompressionRequest request,
        CompressionResponse response,
        ContextItem? generated)
    {
        var inputTerms = ExtractKeyTerms(request.Inputs.SelectMany(item => new[]
        {
            item.Id,
            item.Title ?? string.Empty,
            item.Type,
            item.Content,
            string.Join(' ', item.Tags),
            string.Join(' ', item.SourceRefs)
        }));
        if (inputTerms.Count == 0)
        {
            return generated is null && request.Inputs.Count > 0 ? 0 : 1;
        }

        if (generated is null)
        {
            return 0;
        }

        var outputTerms = ExtractKeyTerms(new[]
        {
            generated.Id,
            generated.Title ?? string.Empty,
            generated.Type,
            generated.Content,
            string.Join(' ', generated.Tags),
            string.Join(' ', generated.SourceRefs),
            string.Join(' ', response.IndexHints.Select(hint => hint.Key))
        });

        return (double)inputTerms.Count(outputTerms.Contains) / inputTerms.Count;
    }

    private static double ResolveTagCoverage(
        CompressionRequest request,
        ContextItem? generated)
    {
        var inputTags = request.Inputs
            .SelectMany(item => item.Tags)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(NormalizeRouteTerm)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (inputTags.Length == 0)
        {
            return generated is null && request.Inputs.Count > 0 ? 0 : 1;
        }

        if (generated is null)
        {
            return 0;
        }

        var outputTags = generated.Tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(NormalizeRouteTerm)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (double)inputTags.Count(outputTags.Contains) / inputTags.Length;
    }

    private static double ResolveContentSignal(
        CompressionRequest request,
        ContextItem? generated,
        int outputTokens)
    {
        if (generated is null || string.IsNullOrWhiteSpace(generated.Content))
        {
            return request.Inputs.Count == 0 ? 1 : 0;
        }

        var minimumUsefulTokens = request.TaskKind switch
        {
            CompressionTaskKind.Extract => 3,
            CompressionTaskKind.RebuildIndex => 2,
            _ => 6
        };
        if (outputTokens <= 0)
        {
            outputTokens = BasicContextPackageBuilder.EstimateTokens(generated.Content);
        }

        return outputTokens >= minimumUsefulTokens
            ? 1
            : Clamp01((double)outputTokens / minimumUsefulTokens);
    }

    private static double ResolveBudgetScore(
        CompressionRequest request,
        int outputTokens)
    {
        if (request.Options.TargetTokenBudget is not { } budget || budget <= 0)
        {
            return 1;
        }

        if (outputTokens <= budget)
        {
            return 1;
        }

        return Clamp01(1 - ((double)(outputTokens - budget) / budget));
    }

    private static double ResolveCompressionFit(
        CompressionRequest request,
        double compressionRatio,
        int inputTokens,
        int outputTokens)
    {
        if (inputTokens <= 0 || outputTokens <= 0)
        {
            return request.Inputs.Count == 0 ? 1 : 0;
        }

        var maxExpectedRatio = request.Options.Depth switch
        {
            CompressionDepth.Light => 0.85,
            CompressionDepth.Deep => 0.5,
            CompressionDepth.Audit => 0.8,
            _ => 0.7
        };

        if (compressionRatio <= maxExpectedRatio && compressionRatio >= 0.05)
        {
            return 1;
        }

        if (compressionRatio < 0.05)
        {
            return inputTokens < 40 ? 0.8 : 0.55;
        }

        return Clamp01(1 - ((compressionRatio - maxExpectedRatio) / Math.Max(0.1, 1 - maxExpectedRatio)));
    }

    private static IReadOnlyCollection<string> ExtractKeyTerms(IEnumerable<string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (Match match in TermPattern.Matches(value))
            {
                var term = NormalizeRouteTerm(match.Value);
                if (term.Length < 3 || StopWords.Contains(term))
                {
                    continue;
                }

                counts[term] = counts.GetValueOrDefault(term) + 1;
            }
        }

        return counts
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .Select(entry => entry.Key)
            .ToArray();
    }

    private static string NormalizeRouteTerm(string value)
    {
        return value
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static IReadOnlyList<string> BuildSignals(
        CompressionResponse response,
        CompressionRequest request,
        double sourceCoverage,
        double keyTermCoverage,
        double tagCoverage,
        double compressionRatio,
        double budgetScore,
        double contentSignal,
        bool requiresReview)
    {
        return BuildSignals(
            response,
            request,
            sourceCoverage,
            keyTermCoverage,
            tagCoverage,
            compressionRatio,
            budgetScore,
            contentSignal,
            requiresReview,
            missingRequiredSources: false,
            outputLongerThanInput: false,
            overCompressed: false);
    }

    private static IReadOnlyList<string> BuildSignals(
        CompressionResponse response,
        CompressionRequest request,
        double sourceCoverage,
        double keyTermCoverage,
        double tagCoverage,
        double compressionRatio,
        double budgetScore,
        double contentSignal,
        bool requiresReview,
        bool missingRequiredSources,
        bool outputLongerThanInput,
        bool overCompressed)
    {
        var signals = new List<string>
        {
            $"status:{response.Status}"
        };

        signals.Add($"source-coverage:{Round(sourceCoverage).ToString("0.###", CultureInfo.InvariantCulture)}");
        signals.Add($"key-term-coverage:{Round(keyTermCoverage).ToString("0.###", CultureInfo.InvariantCulture)}");
        signals.Add($"tag-coverage:{Round(tagCoverage).ToString("0.###", CultureInfo.InvariantCulture)}");

        if (sourceCoverage < 1 && request.Inputs.Count > 0)
        {
            signals.Add("partial-source-coverage");
        }

        if (missingRequiredSources)
        {
            signals.Add("missing-source-refs");
        }

        if (contentSignal <= 0)
        {
            signals.Add("empty-generated-content");
        }

        if (budgetScore < 1)
        {
            signals.Add("over-token-budget");
        }

        if (outputLongerThanInput)
        {
            signals.Add("output-longer-than-input");
        }

        if (compressionRatio > 0.9)
        {
            signals.Add("low-compression");
        }

        if (overCompressed)
        {
            signals.Add("over-compressed");
        }

        if (response.Warnings.Count > 0)
        {
            signals.Add("warnings");
            if (response.Warnings.Any(w => string.Equals(w.Code, "FallbackUsedWarning", StringComparison.OrdinalIgnoreCase)))
            {
                signals.Add("fallback-used");
            }
        }
        if (response.Trace?.FallbackUsed == true)
        {
            if (!signals.Contains("fallback-used"))
            {
                signals.Add("fallback-used");
            }
        }

        if (response.Errors.Count > 0)
        {
            signals.Add("errors");
        }

        if (requiresReview)
        {
            signals.Add("requires-review");
        }

        return signals;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue($"{MetadataPrefix}{key}", out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }

    private static double ReadDouble(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue($"{MetadataPrefix}{key}", out var value)
            && TryReadDouble(value, out var parsed)
                ? parsed
                : 0;
    }

    private static bool TryReadDouble(string value, out double parsed)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private static double Clamp01(double value)
    {
        return Math.Clamp(value, 0, 1);
    }

    private static double Round(double value)
    {
        return Math.Round(value, 3, MidpointRounding.AwayFromZero);
    }

    private static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
