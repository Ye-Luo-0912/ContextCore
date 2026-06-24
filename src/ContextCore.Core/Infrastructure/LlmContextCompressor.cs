using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>基于 LLM 的压缩器，将原始上下文转为结构化摘要和要点。</summary>
public sealed class LlmContextCompressor : IContextCompressor
{
    /// <summary>压缩输出结构的当前 schema 版本。</summary>
    public const string CompressSchemaVersion = "cc-compress-schema-v1";

    /// <summary>默认输入 token 价格（美元 / 百万 token）。</summary>
    private const double DefaultInputPricePerMToken = 3.0;

    /// <summary>默认输出 token 价格（美元 / 百万 token）。</summary>
    private const double DefaultOutputPricePerMToken = 15.0;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IModelGateway _modelGateway;
    private readonly CompressionPromptBuilder _promptBuilder;
    private readonly CompressionResultValidator _validator;
    private readonly CompressionQualityEvaluator _qualityEvaluator;

    public LlmContextCompressor(IModelGateway modelGateway)
        : this(modelGateway, new CompressionPromptBuilder(), new CompressionResultValidator(), new CompressionQualityEvaluator())
    {
    }

    public LlmContextCompressor(
        IModelGateway modelGateway,
        CompressionPromptBuilder promptBuilder,
        CompressionResultValidator validator,
        CompressionQualityEvaluator? qualityEvaluator = null)
    {
        ArgumentNullException.ThrowIfNull(modelGateway);
        ArgumentNullException.ThrowIfNull(promptBuilder);
        ArgumentNullException.ThrowIfNull(validator);

        _modelGateway = modelGateway;
        _promptBuilder = promptBuilder;
        _validator = validator;
        _qualityEvaluator = qualityEvaluator ?? new CompressionQualityEvaluator();
    }

    public async Task<CompressionResponse> CompressAsync(
        CompressionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long tokensUsed = 0;
        try
        {
        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId;

        var modelRequest = _promptBuilder.Build(request, operationId);
        var modelResponse = await _modelGateway.CompleteAsync(modelRequest, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        // §6.3 高风险任务禁止 fallback：审计级压缩任务使用了回退模型时直接失败
        var fallbackUsed = modelResponse.Metadata.TryGetValue("fallbackUsed", out var fbVal)
            && string.Equals(fbVal, "true", StringComparison.OrdinalIgnoreCase);
        if (fallbackUsed && request.Options.Depth == CompressionDepth.Audit)
        {
            var blockedResponse = AttachQualityReport(request, CreateFailure(
                operationId,
                "审计级压缩任务禁止使用回退模型。",
                "fallback_blocked_audit",
                modelResponse,
                now));
            return AttachTrace(blockedResponse, modelResponse, request, sw.ElapsedMilliseconds, false, false);
        }

        if (!modelResponse.Succeeded)
        {
            var failureResponse = AttachQualityReport(request, CreateFailure(
                operationId,
                modelResponse.ErrorMessage ?? "压缩模型调用失败。",
                modelResponse.Metadata.TryGetValue("failureReason", out var failureReason)
                    ? failureReason
                    : "unavailable",
                modelResponse,
                now));
            return AttachTrace(failureResponse, modelResponse, request, sw.ElapsedMilliseconds, false, false);
        }

        var parsed = TryParseModelOutput(modelResponse.Content, out var parseErrors);
        if (parsed is null)
        {
            var failureResponse = AttachQualityReport(request, CreateFailure(
                operationId,
                "压缩模型返回了非法 JSON。",
                "invalid_json",
                modelResponse,
                now,
                parseErrors));
            return AttachTrace(failureResponse, modelResponse, request, sw.ElapsedMilliseconds, true, false);
        }

        var generatedItem = BuildGeneratedItem(request, operationId, parsed, modelResponse);
        var response = new CompressionResponse
        {
            OperationId = operationId,
            Status = ResolveStatus(parsed),
            GeneratedItems = [generatedItem],
            IndexHints = BuildIndexHints(request, generatedItem, parsed, operationId, now),
            Warnings = MergeWarnings(parsed, parseErrors),
            Errors = MergeErrors(parsed, parseErrors),
            Usage = new ContextOperationUsage
            {
                InputTokens = modelResponse.InputTokens,
                OutputTokens = modelResponse.OutputTokens,
                ModelCalls = 1
            },
            CreatedAt = now,
            CompletedAt = now
        };

        var validation = _validator.Validate(response, request);
        if (!validation.IsValid)
        {
            var failureResponse = AttachQualityReport(request, new CompressionResponse
            {
                OperationId = operationId,
                Status = CompressionStatus.Failed,
                GeneratedItems = [],
                IndexHints = [],
                Warnings = [.. response.Warnings, .. validation.Warnings],
                Errors = [.. response.Errors, .. validation.Errors],
                Usage = response.Usage,
                CreatedAt = now,
                CompletedAt = now
            });
            return AttachTrace(failureResponse, modelResponse, request, sw.ElapsedMilliseconds, false, true);
        }

        var finalWarnings = response.Warnings.Concat(validation.Warnings).ToList();
        var finalStatus = response.Status;
        if (validation.Warnings.Count > 0 && finalStatus == CompressionStatus.Succeeded)
        {
            finalStatus = CompressionStatus.PartiallySucceeded;
        }

        // §6.3 fallback 输出默认进入 needs_review，并添加警告
        if (fallbackUsed)
        {
            finalWarnings.Add(new ContextWarning
            {
                Code = "FallbackUsedWarning",
                Message = "本次压缩使用了回退模型，建议人工复核。"
            });
            if (finalStatus == CompressionStatus.Succeeded)
            {
                finalStatus = CompressionStatus.RequiresReview;
            }
        }

        tokensUsed = (long)((response.Usage?.InputTokens ?? 0) + (response.Usage?.OutputTokens ?? 0));
        var successResponse = AttachQualityReport(request, new CompressionResponse
        {
            OperationId = response.OperationId,
            Status = finalStatus,
            GeneratedItems = response.GeneratedItems,
            Patches = response.Patches,
            IndexHints = response.IndexHints,
            Warnings = finalWarnings,
            Errors = response.Errors,
            Usage = response.Usage ?? new ContextOperationUsage(),
            CreatedAt = response.CreatedAt,
            CompletedAt = response.CompletedAt
        });
        return AttachTrace(successResponse, modelResponse, request, sw.ElapsedMilliseconds, false, false);
        }
        finally
        {
            CoreMetrics.CompressionDuration.Record(sw.Elapsed.TotalMilliseconds);
            if (tokensUsed > 0) CoreMetrics.CompressionTokens.Add(tokensUsed);
        }
    }

    private CompressionResponse AttachQualityReport(
        CompressionRequest request,
        CompressionResponse response)
    {
        var quality = _qualityEvaluator.Evaluate(request, response);
        var generatedItems = response.GeneratedItems
            .Select(item => CompressionQualityEvaluator.WithQualityMetadata(item, quality))
            .ToArray();

        return new CompressionResponse
        {
            OperationId = response.OperationId,
            Status = response.Status,
            GeneratedItems = generatedItems,
            Patches = response.Patches,
            IndexHints = response.IndexHints,
            Warnings = response.Warnings,
            Errors = response.Errors,
            Usage = response.Usage,
            QualityReport = quality,
            Trace = response.Trace,
            EvidenceBinding = response.EvidenceBinding,
            CreatedAt = response.CreatedAt,
            CompletedAt = response.CompletedAt
        };
    }

    private static CompressionResponse AttachTrace(
        CompressionResponse response,
        ModelResponse modelResponse,
        CompressionRequest request,
        long latencyMs,
        bool invalidJson,
        bool schemaValidationFailed)
    {
        var qualityScore = response.QualityReport is { } qr
            ? (qr.CompletenessScore + qr.ConsistencyScore + qr.UsabilityScore) / 3.0
            : 0.0;

        var meta = modelResponse.Metadata;
        var modelName = meta.TryGetValue("modelName", out var mn) ? mn : string.Empty;
        var provider = meta.TryGetValue("provider", out var pv) ? pv : string.Empty;
        var fallbackUsed = meta.TryGetValue("fallbackUsed", out var fb)
            && string.Equals(fb, "true", StringComparison.OrdinalIgnoreCase);

        // §6.1 从 gateway metadata 提取重试次数
        var retryCount = 0;
        if (meta.TryGetValue("attempt", out var attemptStr) && int.TryParse(attemptStr, out var attemptNum))
        {
            retryCount = Math.Max(0, attemptNum - 1);
        }

        // §6.1 检测超时
        var timedOut = meta.TryGetValue("failureReason", out var fr)
            && string.Equals(fr, "timeout", StringComparison.OrdinalIgnoreCase);

        // §6.1 估算成本
        var inputTokens = response.Usage?.InputTokens ?? 0;
        var outputTokens = response.Usage?.OutputTokens ?? 0;
        var inputPrice = meta.TryGetValue("inputPricePerMToken", out var ipStr) && double.TryParse(ipStr, out var ip) ? ip : DefaultInputPricePerMToken;
        var outputPrice = meta.TryGetValue("outputPricePerMToken", out var opStr) && double.TryParse(opStr, out var op) ? op : DefaultOutputPricePerMToken;
        var estimatedCost = (inputTokens * inputPrice + outputTokens * outputPrice) / 1_000_000.0;

        // §6.2 来源证据绑定
        var sourceIds = request.Inputs
            .Where(i => !string.IsNullOrEmpty(i.Id))
            .Select(i => i.Id)
            .ToArray();
        var sourceHash = ComputeSourceHash(request);
        var sourceVersion = request.Inputs.Count > 0 ? (int?)request.Inputs.Max(i => i.Version) : null;
        var generatedBy = $"{modelName}/{CompressionPromptBuilder.PromptVersion}";
        var reviewStatus = (response.QualityReport?.RequiresReview ?? false) || fallbackUsed
            ? "pending"
            : "approved";

        var trace = new CompressionTrace
        {
            ModelName = modelName,
            Provider = provider,
            FallbackUsed = fallbackUsed,
            LatencyMs = latencyMs,
            PromptVersion = CompressionPromptBuilder.PromptVersion,
            SourceItemIds = sourceIds,
            InvalidJsonReturned = invalidJson,
            SchemaValidationFailed = schemaValidationFailed,
            RequiresReview = response.QualityReport?.RequiresReview ?? false,
            QualityScore = qualityScore,
            SchemaVersion = CompressSchemaVersion,
            RetryCount = retryCount,
            TimedOut = timedOut,
            EstimatedCost = estimatedCost,
            SourceHash = sourceHash,
            SourceVersion = sourceVersion,
            GeneratedBy = generatedBy,
            ReviewStatus = reviewStatus,
            CreatedAt = response.CreatedAt
        };

        var evidenceBinding = BuildEvidenceBinding(request, response, modelName, qualityScore, reviewStatus);

        return new CompressionResponse
        {
            OperationId = response.OperationId,
            Status = response.Status,
            GeneratedItems = response.GeneratedItems,
            Patches = response.Patches,
            IndexHints = response.IndexHints,
            Warnings = response.Warnings,
            Errors = response.Errors,
            Usage = new ContextOperationUsage
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ModelCalls = response.Usage?.ModelCalls ?? 1,
                EstimatedCost = estimatedCost
            },
            QualityReport = response.QualityReport,
            Trace = trace,
            EvidenceBinding = evidenceBinding,
            CreatedAt = response.CreatedAt,
            CompletedAt = response.CompletedAt
        };
    }

    /// <summary>构建压缩输出与来源内容的证据绑定记录（§6.2）。</summary>
    private static CompressionEvidenceBinding BuildEvidenceBinding(
        CompressionRequest request,
        CompressionResponse response,
        string modelName,
        double confidence,
        string reviewStatus)
    {
        return new CompressionEvidenceBinding
        {
            SourceChunkIds = request.Inputs
                .Where(i => !string.IsNullOrEmpty(i.Id))
                .Select(i => i.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SourceHash = ComputeSourceHash(request),
            SourceVersion = request.Inputs.Count > 0 ? (int?)request.Inputs.Max(i => i.Version) : null,
            GeneratedAt = response.CreatedAt,
            GeneratedBy = $"{modelName}/{CompressionPromptBuilder.PromptVersion}",
            Confidence = confidence,
            ReviewStatus = reviewStatus
        };
    }

    /// <summary>计算来源内容的 SHA-256 哈希，用于证据绑定完整性校验。</summary>
    private static string ComputeSourceHash(CompressionRequest request)
    {
        if (request.Inputs.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var input in request.Inputs)
        {
            sb.Append(input.Id ?? string.Empty);
            sb.Append('\x1f');
            sb.Append(input.Content ?? string.Empty);
            sb.Append('\x1e');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static CompressionResponse CreateFailure(
        string operationId,
        string message,
        string failureReason,
        ModelResponse modelResponse,
        DateTimeOffset now,
        IReadOnlyList<ContextError>? parseErrors = null)
    {
        var errors = new List<ContextError>();
        if (parseErrors is not null)
        {
            errors.AddRange(parseErrors);
        }

        errors.Add(new ContextError
        {
            Code = "CompressionModelFailure",
            Message = message,
            Detail = modelResponse.ErrorMessage
        });

        return new CompressionResponse
        {
            OperationId = operationId,
            Status = CompressionStatus.Failed,
            Errors = errors,
            Usage = new ContextOperationUsage
            {
                InputTokens = modelResponse.InputTokens,
                OutputTokens = modelResponse.OutputTokens,
                ModelCalls = 1
            },
            CreatedAt = now,
            CompletedAt = now
        };
    }

    private static CompressionModelOutput? TryParseModelOutput(
        string content,
        out IReadOnlyList<ContextError> parseErrors)
    {
        var json = ExtractJsonObject(content);
        try
        {
            var parsed = JsonSerializer.Deserialize<CompressionModelOutput>(json, JsonOptions);
            parseErrors = [];
            return parsed;
        }
        catch (JsonException ex)
        {
            parseErrors = [new ContextError
            {
                Code = "InvalidModelJson",
                Message = "压缩模型输出不是合法 JSON。",
                Detail = ex.Message
            }];
            return null;
        }
    }

    private static ContextItem BuildGeneratedItem(
        CompressionRequest request,
        string operationId,
        CompressionModelOutput output,
        ModelResponse modelResponse)
    {
        var content = BuildContent(output);
        var sourceRefs = ResolveSourceRefs(request);
        var tags = ResolveTags(request, output);
        var generatedType = ResolveGeneratedType(request, output);
        var metadata = new Dictionary<string, string>
        {
            ["isDerived"] = "true",
            ["operationId"] = operationId,
            ["taskKind"] = request.TaskKind.ToString(),
            ["derivedFrom"] = string.Join(",", request.Inputs.Select(item => item.Id).Where(id => !string.IsNullOrWhiteSpace(id)))
        };

        if (!string.IsNullOrWhiteSpace(request.SubKind))
        {
            metadata["subKind"] = request.SubKind!;
        }

        if (!string.IsNullOrWhiteSpace(output.Status))
        {
            metadata["modelStatus"] = output.Status!;
        }

        if (output.Confidence is not null)
        {
            metadata["confidence"] = output.Confidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (output.RequiresReview is not null)
        {
            metadata["requiresReview"] = output.RequiresReview.Value ? "true" : "false";
        }

        if (output.Tags.Count > 0)
        {
            metadata["modelTags"] = string.Join(",", output.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)));
        }

        if (output.KeyPoints.Count > 0)
        {
            metadata["keyPointCount"] = output.KeyPoints.Count.ToString();
        }

        metadata["provider"] = modelResponse.Metadata.GetValueOrDefault("provider", "model-gateway");
        metadata["modelName"] = modelResponse.Metadata.GetValueOrDefault("modelName", string.Empty);

        return new ContextItem
        {
            Id = $"{operationId}-{generatedType}",
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Type = generatedType,
            Title = output.Title ?? "LLM 压缩结果",
            Content = content,
            ContentFormat = ContextContentFormat.Markdown,
            Tags = tags,
            Refs = request.Inputs.Select(item => item.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            SourceRefs = sourceRefs,
            Metadata = metadata,
            Importance = request.Inputs.Count == 0 ? 0.5 : request.Inputs.Average(item => item.Importance),
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static string BuildContent(CompressionModelOutput output)
    {
        if (!string.IsNullOrWhiteSpace(output.Summary))
        {
            return output.Summary!;
        }

        if (!string.IsNullOrWhiteSpace(output.Content))
        {
            return output.Content!;
        }

        if (output.KeyPoints.Count > 0)
        {
            var builder = new StringBuilder();
            foreach (var point in output.KeyPoints.Where(point => !string.IsNullOrWhiteSpace(point)))
            {
                builder.Append("- ");
                builder.AppendLine(point.Trim());
            }

            return builder.ToString().TrimEnd();
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ResolveTags(CompressionRequest request, CompressionModelOutput output)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in request.Inputs.SelectMany(item => item.Tags))
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                tags.Add(tag);
            }
        }

        foreach (var tag in output.Tags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                tags.Add(tag);
            }
        }

        tags.Add("llm");
        tags.Add(ResolveGeneratedType(request, output));

        return tags.ToArray();
    }

    private static IReadOnlyList<string> ResolveSourceRefs(CompressionRequest request)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in request.Inputs)
        {
            if (!string.IsNullOrWhiteSpace(input.Id))
            {
                refs.Add(input.Id);
            }

            if (!request.Options.PreserveSourceRefs)
            {
                continue;
            }

            foreach (var sourceRef in input.SourceRefs)
            {
                if (!string.IsNullOrWhiteSpace(sourceRef))
                {
                    refs.Add(sourceRef);
                }
            }
        }

        return refs.ToArray();
    }

    private static string ResolveGeneratedType(CompressionRequest request, CompressionModelOutput output)
    {
        if (request.TaskKind == CompressionTaskKind.Extract
            || request.SubKind?.Contains("key", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "key_points";
        }

        if (!string.IsNullOrWhiteSpace(output.Type))
        {
            return NormalizeType(output.Type!);
        }

        return request.TaskKind switch
        {
            CompressionTaskKind.Summarize => "summary",
            CompressionTaskKind.Reduce => "compressed",
            CompressionTaskKind.Merge => "merged",
            CompressionTaskKind.Refresh => "summary",
            CompressionTaskKind.Normalize => "normalized",
            CompressionTaskKind.Validate => "audit",
            _ => "summary"
        };
    }

    private static CompressionStatus ResolveStatus(CompressionModelOutput output)
    {
        if (output.RequiresReview == true)
        {
            return CompressionStatus.RequiresReview;
        }

        if (!string.IsNullOrWhiteSpace(output.Status)
            && Enum.TryParse<CompressionStatus>(PascalCase(output.Status), ignoreCase: true, out var status))
        {
            return status;
        }

        if (output.Errors.Count > 0)
        {
            return CompressionStatus.PartiallySucceeded;
        }

        return CompressionStatus.Succeeded;
    }

    private static IReadOnlyList<ContextIndexEntry> BuildIndexHints(
        CompressionRequest request,
        ContextItem generatedItem,
        CompressionModelOutput output,
        string operationId,
        DateTimeOffset now)
    {
        if (!request.Options.GenerateIndexHints)
        {
            return [];
        }

        var entries = new List<ContextIndexEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hint in output.IndexHints)
        {
            if (string.IsNullOrWhiteSpace(hint.Key))
            {
                continue;
            }

            var key = hint.Key.Trim();
            var kind = string.IsNullOrWhiteSpace(hint.Kind) ? "keyword" : hint.Kind.Trim();
            if (!seen.Add($"{kind}:{key}"))
            {
                continue;
            }

            entries.Add(new ContextIndexEntry
            {
                Id = StableId(operationId, kind, key),
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                Key = key,
                Kind = kind,
                ContextRefs = [generatedItem.Id],
                Weight = hint.Weight ?? 1.0,
                Metadata = new Dictionary<string, string>
                {
                    ["operationId"] = operationId,
                    ["source"] = "llm"
                },
                CreatedAt = now
            });
        }

        foreach (var tag in output.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
        {
            var key = tag.Trim();
            if (!seen.Add($"tag:{key}"))
            {
                continue;
            }

            entries.Add(new ContextIndexEntry
            {
                Id = StableId(operationId, "tag", key),
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                Key = key,
                Kind = "tag",
                ContextRefs = [generatedItem.Id],
                Weight = 0.8,
                Metadata = new Dictionary<string, string>
                {
                    ["operationId"] = operationId,
                    ["source"] = "llm"
                },
                CreatedAt = now
            });
        }

        return entries;
    }

    private static IReadOnlyList<ContextWarning> MergeWarnings(CompressionModelOutput output, IReadOnlyList<ContextError> parseErrors)
    {
        var warnings = new List<ContextWarning>();
        foreach (var warning in output.Warnings)
        {
            if (string.IsNullOrWhiteSpace(warning.Code) && string.IsNullOrWhiteSpace(warning.Message))
            {
                continue;
            }

            warnings.Add(new ContextWarning
            {
                Code = warning.Code ?? "ModelWarning",
                Message = warning.Message ?? string.Empty
            });
        }

        foreach (var error in parseErrors)
        {
            warnings.Add(new ContextWarning
            {
                Code = error.Code,
                Message = error.Message
            });
        }

        return warnings;
    }

    private static IReadOnlyList<ContextError> MergeErrors(CompressionModelOutput output, IReadOnlyList<ContextError> parseErrors)
    {
        var errors = new List<ContextError>();
        errors.AddRange(parseErrors);

        foreach (var error in output.Errors)
        {
            if (string.IsNullOrWhiteSpace(error.Code) && string.IsNullOrWhiteSpace(error.Message))
            {
                continue;
            }

            errors.Add(new ContextError
            {
                Code = error.Code ?? "ModelError",
                Message = error.Message ?? string.Empty,
                Detail = error.Detail
            });
        }

        return errors;
    }

    private static string ExtractJsonObject(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        content = content.Trim();
        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineBreak = content.IndexOf('\n');
            if (firstLineBreak >= 0)
            {
                content = content[(firstLineBreak + 1)..];
            }

            var fenceIndex = content.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceIndex >= 0)
            {
                content = content[..fenceIndex];
            }
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return content[start..(end + 1)];
        }

        return content;
    }

    private static string NormalizeType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "summary";
        }

        var normalized = value.Trim().Replace(' ', '_');
        return normalized.ToLowerInvariant();
    }

    private static string PascalCase(string value)
    {
        var normalized = value.Trim().Replace(' ', '_').Replace('-', '_');
        if (normalized.Contains('_'))
        {
            var parts = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                if (part.Length == 0)
                {
                    continue;
                }

                builder.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                {
                    builder.Append(part[1..].ToLowerInvariant());
                }
            }

            return builder.ToString();
        }

        return char.ToUpperInvariant(normalized[0]) + normalized[1..].ToLowerInvariant();
    }

    private static string StableId(string operationId, string kind, string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', operationId, kind, key)));
        return $"idx-{Convert.ToHexString(bytes)[..20].ToLowerInvariant()}";
    }

    private sealed class CompressionModelOutput
    {
        public string? Status { get; init; }

        public string? Title { get; init; }

        public string? Type { get; init; }

        public string? Summary { get; init; }

        public string? Content { get; init; }

        public IReadOnlyList<string> KeyPoints { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

        public IReadOnlyList<CompressionIndexHintOutput> IndexHints { get; init; } = Array.Empty<CompressionIndexHintOutput>();

        public IReadOnlyList<CompressionIssueOutput> Warnings { get; init; } = Array.Empty<CompressionIssueOutput>();

        public IReadOnlyList<CompressionIssueOutput> Errors { get; init; } = Array.Empty<CompressionIssueOutput>();

        public bool? RequiresReview { get; init; }

        public double? Confidence { get; init; }
    }

    private sealed class CompressionIndexHintOutput
    {
        public string? Key { get; init; }

        public string? Kind { get; init; }

        public double? Weight { get; init; }
    }

    private sealed class CompressionIssueOutput
    {
        public string? Code { get; init; }

        public string? Message { get; init; }

        public string? Detail { get; init; }
    }
}
