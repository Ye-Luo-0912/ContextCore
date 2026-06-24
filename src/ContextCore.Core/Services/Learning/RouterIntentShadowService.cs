using System.Globalization;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Planning;

namespace ContextCore.Core.Services;

public sealed class RouterIntentShadowRecordRequest
{
    public string RequestId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string EntryPoint { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string RuntimeIntent { get; init; } = string.Empty;

    public double RuntimeConfidence { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Router shadow 旁路采集服务；只写 trace，不改变正式 router / planning / retrieval / package 输出。</summary>
public sealed class RouterIntentShadowService
{
    public const string PolicyVersion = "router-intent-shadow-r2/v1";
    private const double LowConfidenceThreshold = 0.35;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RouterShadowOptions _options;
    private readonly IRouterIntentShadowTraceStore? _store;
    private readonly PlanningIntentDetector _detector;
    private readonly object _classifierGate = new();
    private RouterIntentClassifier? _classifier;
    private bool _classifierInitialized;

    public RouterIntentShadowService(
        RouterShadowOptions options,
        IRouterIntentShadowTraceStore? store,
        PlanningIntentDetector detector)
    {
        _options = options;
        _store = store;
        _detector = detector;
    }

    public async Task<RouterIntentShadowTrace?> RecordAsync(
        RouterIntentShadowRecordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.Enabled || !_options.TraceCollectionEnabled || _store is null)
        {
            return null;
        }

        var runtimeIntent = ResolveRuntimeIntent(request);
        var prediction = GetClassifier().Predict(BuildExample(request));
        var shadowIntent = NormalizeIntent(prediction.Intent);
        var agreement = string.Equals(runtimeIntent, shadowIntent, StringComparison.OrdinalIgnoreCase);
        if (agreement && !_options.RecordAgreements)
        {
            return null;
        }

        if (!agreement && !_options.RecordDisagreements)
        {
            return null;
        }

        var lowConfidence = prediction.Confidence < LowConfidenceThreshold;
        var trace = new RouterIntentShadowTrace
        {
            RequestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("N") : request.RequestId.Trim(),
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SessionId = request.SessionId,
            EntryPoint = request.EntryPoint,
            Mode = string.IsNullOrWhiteSpace(request.Mode) ? "Unknown" : request.Mode.Trim(),
            QueryText = request.QueryText,
            RuntimeIntent = runtimeIntent,
            ShadowIntent = shadowIntent,
            ShadowConfidence = Math.Clamp(prediction.Confidence, 0, 1),
            Agreement = agreement,
            DisagreementType = ResolveDisagreementType(agreement, lowConfidence, prediction.Abstained),
            TopPredictions = ResolveTopPredictions(prediction),
            LowConfidence = lowConfidence,
            Abstained = prediction.Abstained,
            WouldChangePlanningProfile = !string.Equals(
                ResolvePlanningProfile(runtimeIntent),
                ResolvePlanningProfile(shadowIntent),
                StringComparison.OrdinalIgnoreCase),
            WouldChangeVectorProfile = !string.Equals(
                ResolveVectorProfile(runtimeIntent),
                ResolveVectorProfile(shadowIntent),
                StringComparison.OrdinalIgnoreCase),
            FormalOutputChanged = false,
            CreatedAt = DateTimeOffset.UtcNow,
            PolicyVersion = PolicyVersion,
            Metadata = BuildMetadata(request, prediction)
        };

        await _store.SaveAsync(trace, cancellationToken).ConfigureAwait(false);
        return trace;
    }

    private string ResolveRuntimeIntent(RouterIntentShadowRecordRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.RuntimeIntent))
        {
            return NormalizeIntent(request.RuntimeIntent);
        }

        var detection = _detector.Detect(new ContextPlanningSnapshot
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SessionId = request.SessionId
        }, request.QueryText, request.Mode);
        return NormalizeIntent(detection.Intent);
    }

    private RouterIntentClassifier GetClassifier()
    {
        if (_classifierInitialized && _classifier is not null)
        {
            return _classifier;
        }

        lock (_classifierGate)
        {
            if (_classifierInitialized && _classifier is not null)
            {
                return _classifier;
            }

            _classifier = CreateClassifier();
            _classifier.Fit(ReadTrainingExamples());
            _classifierInitialized = true;
            return _classifier;
        }
    }

    private RouterIntentClassifier CreateClassifier()
    {
        return string.Equals(
            _options.ShadowClassifier,
            RouterIntentClassifierBaselineNames.ExistingRuleBasedRouterBaseline,
            StringComparison.OrdinalIgnoreCase)
            ? new ExistingRuleBasedRouterBaseline()
            : new TokenCentroidRouterBaseline();
    }

    private static IReadOnlyList<ContextPolicyFeatureExample> ReadTrainingExamples()
    {
        var path = Path.Combine(
            LearningDatasetQualityReportBuilder.DefaultFeatureDirectory,
            LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName);
        if (!File.Exists(path))
        {
            return Array.Empty<ContextPolicyFeatureExample>();
        }

        var examples = new List<ContextPolicyFeatureExample>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var example = JsonSerializer.Deserialize<ContextPolicyFeatureExample>(line, JsonOptions);
                if (example is not null)
                {
                    examples.Add(example);
                }
            }
            catch (JsonException)
            {
            }
        }

        return examples;
    }

    private static ContextPolicyFeatureExample BuildExample(RouterIntentShadowRecordRequest request)
    {
        return new ContextPolicyFeatureExample
        {
            ExampleId = request.RequestId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId ?? string.Empty,
            SourceType = "runtime-router-shadow",
            SourceId = request.RequestId,
            TaskKind = "RouterIntent",
            Mode = request.Mode,
            InputSummary = request.QueryText,
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["queryText"] = request.QueryText,
                ["entryPoint"] = request.EntryPoint
            }
        };
    }

    private static Dictionary<string, string> BuildMetadata(
        RouterIntentShadowRecordRequest request,
        RouterIntentClassifierPrediction prediction)
    {
        var metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["shadowIntent"] = string.IsNullOrWhiteSpace(prediction.Intent) ? string.Empty : prediction.Intent,
            ["shadowConfidence"] = prediction.Confidence.ToString("0.####", CultureInfo.InvariantCulture),
            ["runtimeConfidence"] = request.RuntimeConfidence.ToString("0.####", CultureInfo.InvariantCulture),
            ["formalOutputChanged"] = "false"
        };
        if (prediction.Reasons.Count > 0)
        {
            metadata["shadowReasons"] = string.Join(";", prediction.Reasons);
        }

        return metadata;
    }

    private static IReadOnlyList<RouterIntentShadowTopPrediction> ResolveTopPredictions(
        RouterIntentClassifierPrediction prediction)
    {
        if (prediction.TopPredictions.Count > 0)
        {
            return prediction.TopPredictions;
        }

        return
        [
            new RouterIntentShadowTopPrediction
            {
                Intent = NormalizeIntent(prediction.Intent),
                Confidence = Math.Clamp(prediction.Confidence, 0, 1),
                Reason = prediction.Reasons.FirstOrDefault() ?? string.Empty
            }
        ];
    }

    private static string ResolveDisagreementType(bool agreement, bool lowConfidence, bool abstained)
    {
        if (agreement)
        {
            return RouterIntentShadowDisagreementTypes.Agreement;
        }

        if (abstained)
        {
            return RouterIntentShadowDisagreementTypes.ShadowAbstained;
        }

        return lowConfidence
            ? RouterIntentShadowDisagreementTypes.LowConfidenceDisagreement
            : RouterIntentShadowDisagreementTypes.IntentMismatch;
    }

    private static string ResolvePlanningProfile(string intent)
    {
        return NormalizeIntent(intent) switch
        {
            PlanningIntentDetector.AuditDeprecated => "audit",
            PlanningIntentDetector.ConflictCheck => "conflict",
            PlanningIntentDetector.CodingTask => "coding",
            PlanningIntentDetector.NovelGeneration => "novel",
            PlanningIntentDetector.AutomationRecovery => "automation",
            PlanningIntentDetector.LongTermPreference => "preference",
            PlanningIntentDetector.CurrentTask => "current",
            _ => "fuzzy"
        };
    }

    private static string ResolveVectorProfile(string intent)
    {
        return NormalizeIntent(intent) switch
        {
            PlanningIntentDetector.AuditDeprecated => "audit-v1",
            PlanningIntentDetector.ConflictCheck => "diagnostics-v1",
            PlanningIntentDetector.CurrentTask => "current-task-v1",
            _ => "normal-v1"
        };
    }

    private static string NormalizeIntent(string? intent)
    {
        return string.IsNullOrWhiteSpace(intent)
            ? PlanningIntentDetector.FuzzyQuestion
            : intent.Trim();
    }
}
