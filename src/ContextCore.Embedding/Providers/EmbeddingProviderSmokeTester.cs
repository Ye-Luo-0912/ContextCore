using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Embedding.Providers;

/// <summary>本地 embedding provider smoke test；只验证 provider 可用性，不写入 vector index。</summary>
public sealed class EmbeddingProviderSmokeTester
{
    private static readonly string[] SmokeTexts =
    [
        "ContextCore embedding provider smoke test.",
        "第二条 smoke 输入用于验证 batch embedding。"
    ];

    public async Task<EmbeddingProviderSmokeReport> RunAsync(
        EmbeddingProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var diagnostics = new List<VectorIndexDiagnostic>(
            EmbeddingProviderDiagnosticsBuilder.Build(options));
        var tokenizerWorks = false;
        var inferenceWorks = false;
        var dimensionMatches = false;
        var normalizationWorks = false;
        var batchWorks = false;
        var actualDimension = 0;

        var modelPath = ResolveModelPath(options);
        var tokenizerPath = ResolveTokenizerPath(options, modelPath);
        var modelExists = !string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath);
        var tokenizerExists = !string.IsNullOrWhiteSpace(tokenizerPath) && File.Exists(tokenizerPath);

        if (IsDeterministic(options))
        {
            actualDimension = options.Dimension > 0 ? options.Dimension : 16;
            dimensionMatches = actualDimension == (options.Dimension > 0 ? options.Dimension : actualDimension);
            normalizationWorks = options.Normalize;
            batchWorks = true;
            inferenceWorks = true;
            return BuildReport(
                options,
                modelPath,
                tokenizerPath,
                modelExists,
                tokenizerExists,
                tokenizationWorks: true,
                inferenceWorks,
                dimensionMatches,
                normalizationWorks,
                batchWorks,
                actualDimension,
                diagnostics,
                ["DeterministicHash provider 不需要本地模型文件，仅用于可重复基础设施测试。"]);
        }

        if (IsOnnx(options) && tokenizerExists)
        {
            try
            {
                var tokenizer = EmbeddingTokenizerFactory.Create(tokenizerPath!, modelPath ?? string.Empty);
                var tokenized = tokenizer.Tokenize(SmokeTexts, options.MaxTokens > 0 ? options.MaxTokens : 256);
                tokenizerWorks = tokenized.BatchSize == SmokeTexts.Length
                                 && tokenized.SequenceLength > 0
                                 && tokenized.InputIds.Length == tokenized.BatchSize * tokenized.SequenceLength;
            }
            catch (Exception ex)
            {
                diagnostics.Add(NewDiagnostic(
                    VectorIndexDiagnosticTypes.TokenizerUnavailable,
                    $"tokenizer smoke test 失败：{ex.Message}",
                    "检查 tokenizer/vocab 文件格式是否与模型匹配。"));
            }
        }

        if (IsOnnx(options) && diagnostics.All(item => !item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var generator = new OnnxEmbeddingGenerator(options);
                var result = await generator.GenerateAsync(new EmbeddingGeneratorRequest
                {
                    OperationId = $"embedding-provider-smoke-{Guid.NewGuid():N}",
                    WorkspaceId = "smoke",
                    CollectionId = "provider",
                    Inputs =
                    [
                        .. SmokeTexts.Select((text, index) => new EmbeddingGeneratorInput
                        {
                            ItemId = $"smoke-{index + 1}",
                            Text = text,
                            ItemKind = "smoke",
                            Layer = "smoke",
                            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        })
                    ]
                }, cancellationToken).ConfigureAwait(false);

                var entries = result.Entries.ToArray();
                actualDimension = result.Dimension > 0
                    ? result.Dimension
                    : entries.FirstOrDefault()?.Dimension ?? 0;
                inferenceWorks = entries.Length > 0;
                batchWorks = entries.Length == SmokeTexts.Length;
                dimensionMatches = options.Dimension <= 0 || actualDimension == options.Dimension;
                normalizationWorks = !options.Normalize || entries.All(entry => IsUnitVector(entry.Vector));

                if (!dimensionMatches)
                {
                    diagnostics.Add(NewDiagnostic(
                        VectorIndexDiagnosticTypes.DimensionMismatch,
                        $"ONNX 输出维度 {actualDimension} 与配置维度 {options.Dimension} 不一致。",
                        "修正 Dimension 配置或使用匹配模型重新 reindex。"));
                }

                if (options.Normalize && !normalizationWorks)
                {
                    diagnostics.Add(NewDiagnostic(
                        VectorIndexDiagnosticTypes.NormalizationMismatch,
                        "ONNX 输出未满足单位向量 normalization 检查。",
                        "确认 Normalize=true 的 provider 输出路径。"));
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(NewDiagnostic(
                    VectorIndexDiagnosticTypes.OnnxSessionFailed,
                    $"ONNX inference smoke test 失败：{ex.Message}",
                    "检查模型输入/输出名称、runtime 依赖、pooling 策略与 tokenizer。"));
            }
        }

        return BuildReport(
            options,
            modelPath,
            tokenizerPath,
            modelExists,
            tokenizerExists,
            tokenizerWorks,
            inferenceWorks,
            dimensionMatches,
            normalizationWorks,
            batchWorks,
            actualDimension,
            diagnostics,
            []);
    }

    public static string ToMarkdown(EmbeddingProviderSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Embedding Provider Smoke Test");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- ProviderType: `{report.ProviderType}`");
        builder.AppendLine($"- EmbeddingModel: `{report.EmbeddingModel}`");
        builder.AppendLine($"- ModelPathExists: `{report.ModelPathExists}`");
        builder.AppendLine($"- TokenizerPathExists: `{report.TokenizerPathExists}`");
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- TokenizationWorks: `{report.TokenizationWorks}`");
        builder.AppendLine($"- OnnxInferenceWorks: `{report.OnnxInferenceWorks}`");
        builder.AppendLine($"- ExpectedDimension: `{report.ExpectedDimension}`");
        builder.AppendLine($"- ActualDimension: `{report.ActualDimension}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- DimensionMatchesConfig: `{report.DimensionMatchesConfig}`");
        builder.AppendLine($"- NormalizationWorks: `{report.NormalizationWorks}`");
        builder.AppendLine($"- BatchEmbeddingWorks: `{report.BatchEmbeddingWorks}`");
        builder.AppendLine($"- Succeeded: `{report.Succeeded}`");
        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        builder.AppendLine();
        if (report.Diagnostics.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            builder.AppendLine("| Type | Severity | Message | SuggestedAction |");
            builder.AppendLine("|---|---|---|---|");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"| {diagnostic.Type} | {diagnostic.Severity} | {Escape(diagnostic.Message)} | {Escape(diagnostic.SuggestedAction)} |");
            }
        }

        return builder.ToString();
    }

    private static EmbeddingProviderSmokeReport BuildReport(
        EmbeddingProviderOptions options,
        string? modelPath,
        string? tokenizerPath,
        bool modelExists,
        bool tokenizerExists,
        bool tokenizationWorks,
        bool inferenceWorks,
        bool dimensionMatches,
        bool normalizationWorks,
        bool batchWorks,
        int actualDimension,
        IReadOnlyList<VectorIndexDiagnostic> diagnostics,
        IReadOnlyList<string> warnings)
    {
        var hasErrors = diagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
        var isOnnx = IsOnnx(options);
        var succeeded = !hasErrors
                        && options.Enabled
                        && (!isOnnx || modelExists)
                        && (!isOnnx || tokenizerExists)
                        && (!isOnnx || tokenizationWorks)
                        && inferenceWorks
                        && dimensionMatches
                        && normalizationWorks
                        && batchWorks;

        return new EmbeddingProviderSmokeReport
        {
            OperationId = $"embedding-provider-smoke-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ProviderId = string.IsNullOrWhiteSpace(options.ProviderId) ? ResolveDefaultProviderId(options) : options.ProviderId,
            ProviderType = string.IsNullOrWhiteSpace(options.ProviderType) ? EmbeddingProviderTypes.DeterministicHash : options.ProviderType,
            EmbeddingModel = string.IsNullOrWhiteSpace(options.EmbeddingModel) ? ResolveDefaultModel(options) : options.EmbeddingModel,
            ModelPath = modelPath,
            TokenizerPath = tokenizerPath,
            ExpectedDimension = options.Dimension,
            ActualDimension = actualDimension,
            UseForRuntime = false,
            ProviderEnabled = options.Enabled,
            ModelPathExists = modelExists,
            TokenizerPathExists = tokenizerExists,
            TokenizationWorks = tokenizationWorks,
            OnnxInferenceWorks = inferenceWorks,
            DimensionMatchesConfig = dimensionMatches,
            NormalizationWorks = normalizationWorks,
            BatchEmbeddingWorks = batchWorks,
            Succeeded = succeeded,
            Diagnostics = diagnostics,
            Warnings = warnings
        };
    }

    private static bool IsUnitVector(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
        {
            return false;
        }

        var sum = values.Aggregate(0.0, (current, value) => current + value * value);

        var length = Math.Sqrt(sum);
        return Math.Abs(length - 1.0) <= 0.01;
    }

    private static string? ResolveModelPath(EmbeddingProviderOptions options)
    {
        return IsOnnx(options)
            ? EmbeddingModelPaths.ResolveModelPath(options.ModelPath)
            : options.ModelPath;
    }

    private static string? ResolveTokenizerPath(EmbeddingProviderOptions options, string? modelPath)
    {
        return IsOnnx(options)
            ? EmbeddingModelPaths.ResolveVocabularyPath(options.TokenizerPath, modelPath ?? string.Empty)
            : options.TokenizerPath;
    }

    private static bool IsOnnx(EmbeddingProviderOptions options)
    {
        return options.ProviderType.Equals(EmbeddingProviderTypes.OnnxLocal, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeterministic(EmbeddingProviderOptions options)
    {
        return options.ProviderType.Equals(EmbeddingProviderTypes.DeterministicHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDefaultProviderId(EmbeddingProviderOptions options)
    {
        return IsOnnx(options) ? "onnx-local" : "deterministic-hash";
    }

    private static string ResolveDefaultModel(EmbeddingProviderOptions options)
    {
        return IsOnnx(options) ? EmbeddingModelPaths.DefaultModelName : "deterministic-hash-v1";
    }

    private static VectorIndexDiagnostic NewDiagnostic(
        string type,
        string message,
        string suggestedAction)
    {
        return new VectorIndexDiagnostic
        {
            DiagnosticId = $"{type}:embedding-provider-smoke",
            Type = type,
            Severity = "Error",
            Message = message,
            SuggestedAction = suggestedAction,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "/");
    }
}
