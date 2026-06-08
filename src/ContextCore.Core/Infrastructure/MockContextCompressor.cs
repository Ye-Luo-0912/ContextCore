using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// <see cref="IContextCompressor"/> 的模拟实现，仅用于 demo/test/development。
/// 不调用任何模型 API，直接将输入内容拼接为摘要并返回，不能作为生产压缩结果使用。
/// </summary>
/// <remarks>
/// TODO-DEMO [P0-6]：此类保留给本地演示和单元测试，不具备任何语义压缩能力。
/// 生产环境应配置 Compression:Provider = "llm"，真实 LLM 压缩器在 P2 阶段实现。
/// </remarks>
public sealed class MockContextCompressor : IContextCompressor
{
    public Task<CompressionResponse> CompressAsync(
        CompressionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId;

        var sourceRefs = ResolveSourceRefs(request.Inputs);
        var tags = request.Inputs
            .SelectMany(item => item.Tags)
            .Append("summary")
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var summaryContent = BuildSummaryContent(request, operationId);
        if (request.Options.TargetTokenBudget is > 0)
        {
            var maxChars = request.Options.TargetTokenBudget.Value * 2;
            summaryContent = summaryContent.Length <= maxChars
                ? summaryContent
                : summaryContent[..maxChars];
        }

        var summaryItem = new ContextItem
        {
            Id = $"{operationId}-summary",
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Type = "summary",
            Title = "Summary",
            Content = summaryContent,
            ContentFormat = ContextContentFormat.Markdown,
            Tags = tags,
            Refs = request.Inputs.Select(item => item.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray(),
            SourceRefs = sourceRefs,
            Metadata = new Dictionary<string, string>
            {
                ["isDerived"] = "true",
                ["operationId"] = operationId,
                ["taskKind"] = request.TaskKind.ToString(),
                ["derivedFrom"] = string.Join(",", request.Inputs.Select(item => item.Id).Where(id => !string.IsNullOrWhiteSpace(id)))
            },
            Importance = request.Inputs.Count == 0 ? 0.5 : request.Inputs.Average(item => item.Importance),
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        var indexHints = request.Options.GenerateIndexHints
            ? BuildIndexHints(request, summaryItem, operationId, now)
            : Array.Empty<ContextIndexEntry>();

        var warnings = request.Inputs.Count == 0
            ? new[]
            {
                new ContextWarning
                {
                    Code = "NoInputs",
                    Message = "Compression request did not include input items."
                }
            }
            : Array.Empty<ContextWarning>();

        var response = new CompressionResponse
        {
            OperationId = operationId,
            Status = CompressionStatus.Succeeded,
            GeneratedItems = [summaryItem],
            Patches = [],
            IndexHints = indexHints,
            Warnings = warnings,
            Errors = [],
            Usage = new ContextOperationUsage
            {
                InputTokens = request.Inputs.Sum(item => BasicContextPackageBuilder.EstimateTokens(item.Content)),
                OutputTokens = BasicContextPackageBuilder.EstimateTokens(summaryContent),
                ModelCalls = 0
            },
            CreatedAt = now,
            CompletedAt = DateTimeOffset.UtcNow
        };

        var quality = new CompressionQualityEvaluator().Evaluate(request, response);
        var sourceIds = request.Inputs
            .Where(i => !string.IsNullOrEmpty(i.Id))
            .Select(i => i.Id)
            .ToArray();
        var sourceHash = ComputeSourceHash(request);
        var sourceVersion = request.Inputs.Count > 0 ? (int?)request.Inputs.Max(i => i.Version) : null;

        var trace = new CompressionTrace
        {
            ModelName = "mock",
            Provider = "mock",
            LatencyMs = 0,
            PromptVersion = "mock-v1",
            SourceItemIds = sourceIds,
            SchemaVersion = LlmContextCompressor.CompressSchemaVersion,
            SourceHash = sourceHash,
            SourceVersion = sourceVersion,
            GeneratedBy = "mock/mock-v1",
            ReviewStatus = "approved",
            CreatedAt = now
        };

        var evidenceBinding = new CompressionEvidenceBinding
        {
            SourceChunkIds = sourceIds,
            SourceHash = sourceHash,
            SourceVersion = sourceVersion,
            GeneratedAt = now,
            GeneratedBy = "mock/mock-v1",
            Confidence = (quality.CompletenessScore + quality.ConsistencyScore + quality.UsabilityScore) / 3.0,
            ReviewStatus = "approved"
        };

        return Task.FromResult(new CompressionResponse
        {
            OperationId = response.OperationId,
            Status = response.Status,
            GeneratedItems = response.GeneratedItems
                .Select(item => CompressionQualityEvaluator.WithQualityMetadata(item, quality))
                .ToArray(),
            Patches = response.Patches,
            IndexHints = response.IndexHints,
            Warnings = response.Warnings,
            Errors = response.Errors,
            Usage = response.Usage,
            QualityReport = quality,
            Trace = trace,
            EvidenceBinding = evidenceBinding,
            CreatedAt = response.CreatedAt,
            CompletedAt = response.CompletedAt
        });
    }

    private static string BuildSummaryContent(CompressionRequest request, string operationId)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Summary");
        builder.AppendLine();
        builder.AppendLine($"Operation: {operationId}");
        builder.AppendLine($"Task: {request.TaskKind}");

        if (!string.IsNullOrWhiteSpace(request.SubKind))
        {
            builder.AppendLine($"SubKind: {request.SubKind}");
        }

        builder.AppendLine();
        builder.AppendLine("Inputs:");

        foreach (var input in request.Inputs)
        {
            builder.Append("- ");
            builder.Append(string.IsNullOrWhiteSpace(input.Title) ? input.Id : input.Title);
            builder.Append(" [");
            builder.Append(input.Type);
            builder.AppendLine("]");

            if (!string.IsNullOrWhiteSpace(input.Content))
            {
                var preview = input.Content.Length <= 240 ? input.Content : input.Content[..240];
                builder.AppendLine(preview);
                builder.AppendLine();
            }
        }

        if (request.Inputs.Count == 0)
        {
            builder.AppendLine("- No input content.");
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> ResolveSourceRefs(IReadOnlyList<ContextItem> inputs)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            foreach (var sourceRef in input.SourceRefs)
            {
                refs.Add(sourceRef);
            }

            if (!string.IsNullOrWhiteSpace(input.Id))
            {
                refs.Add(input.Id);
            }
        }

        return refs.ToArray();
    }

    private static IReadOnlyList<ContextIndexEntry> BuildIndexHints(
        CompressionRequest request,
        ContextItem summaryItem,
        string operationId,
        DateTimeOffset now)
    {
        var entries = new List<ContextIndexEntry>();
        var keys = new HashSet<(string Kind, string Key)>();

        foreach (var tag in summaryItem.Tags.Where(tag => !string.Equals(tag, "summary", StringComparison.OrdinalIgnoreCase)))
        {
            keys.Add(("tag", tag));
        }

        foreach (var type in request.Inputs.Select(item => item.Type).Append(summaryItem.Type))
        {
            if (!string.IsNullOrWhiteSpace(type))
            {
                keys.Add(("type", type));
            }
        }

        foreach (var (kind, key) in keys)
        {
            entries.Add(new ContextIndexEntry
            {
                Id = $"{operationId}-{kind}-{StableKey(key)}",
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                Key = key,
                Kind = kind,
                ContextRefs = [summaryItem.Id],
                Weight = 1.0,
                Metadata = new Dictionary<string, string>
                {
                    ["operationId"] = operationId
                },
                CreatedAt = now
            });
        }

        return entries;
    }

    private static string StableKey(string key)
    {
        var chars = key
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray();

        return new string(chars).Trim('-');
    }

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
}
