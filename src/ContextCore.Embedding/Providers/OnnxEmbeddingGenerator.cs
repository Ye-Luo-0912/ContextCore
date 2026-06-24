using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Embedding;

/// <summary>将本地 ONNX embedding provider 适配为 V1 vector index generator。</summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator, IEmbeddingGeneratorDescriptor
{
    private readonly EmbeddingProviderOptions _options;
    private readonly IEmbeddingTokenizer? _tokenizer;
    private readonly IOnnxEmbeddingSessionFactory? _sessionFactory;
    private readonly Lazy<OnnxEmbeddingProvider> _provider;

    public OnnxEmbeddingGenerator(
        EmbeddingProviderOptions options,
        IEmbeddingTokenizer? tokenizer = null,
        IOnnxEmbeddingSessionFactory? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _tokenizer = tokenizer;
        _sessionFactory = sessionFactory;
        _provider = new Lazy<OnnxEmbeddingProvider>(CreateProvider);
    }

    public string Provider => string.IsNullOrWhiteSpace(_options.ProviderId)
        ? "onnx-local"
        : _options.ProviderId;

    public string Model => string.IsNullOrWhiteSpace(_options.EmbeddingModel)
        ? EmbeddingModelPaths.DefaultModelName
        : _options.EmbeddingModel;

    public int Dimension => _options.Dimension;

    public string ProviderType => EmbeddingProviderTypes.OnnxLocal;

    public bool Normalize => _options.Normalize;

    public string PoolingStrategy => string.IsNullOrWhiteSpace(_options.PoolingStrategy)
        ? nameof(EmbeddingPoolingStrategy.Mean)
        : _options.PoolingStrategy;

    public async Task<EmbeddingGeneratorResult> GenerateAsync(
        EmbeddingGeneratorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var blockingDiagnostics = EmbeddingProviderDiagnosticsBuilder.Build(_options)
            .Where(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (blockingDiagnostics.Length > 0)
        {
            var message = string.Join("; ", blockingDiagnostics.Select(item => item.Message));
            throw new InvalidOperationException($"ONNX embedding provider 不可用：{message}");
        }

        var result = await _provider.Value.EmbedAsync(new EmbeddingRequest
        {
            OperationId = request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            ModelName = Model,
            InputKind = request.Inputs.Count == 1 && request.Inputs[0].ItemKind.Equals("query", StringComparison.OrdinalIgnoreCase)
                ? EmbeddingInputKind.Query
                : EmbeddingInputKind.ContextItem,
            Normalize = Normalize,
            Inputs =
            [
                .. request.Inputs.Select(input => new EmbeddingInput
                {
                    Id = input.ItemId,
                    SourceRef = input.ItemId,
                    Text = input.Text,
                    Metadata = new Dictionary<string, string>(input.Metadata, StringComparer.OrdinalIgnoreCase)
                })
            ]
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.ErrorMessage ?? "ONNX embedding provider 执行失败。");
        }

        var inputById = request.Inputs.ToDictionary(input => input.ItemId, StringComparer.OrdinalIgnoreCase);
        var entries = result.Vectors.Select(vector =>
        {
            var input = inputById.TryGetValue(vector.InputId, out var found)
                ? found
                : request.Inputs.First();
            var values = Normalize
                ? EmbeddingVectorMath.Normalize(vector.Values)
                : vector.Values.ToArray();
            return CreateEntry(
                request.WorkspaceId,
                request.CollectionId,
                input,
                values,
                result.Dimensions > 0 ? result.Dimensions : values.Count);
        }).ToArray();

        return new EmbeddingGeneratorResult
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? Guid.NewGuid().ToString("N")
                : request.OperationId,
            EmbeddingProvider = Provider,
            EmbeddingModel = Model,
            Dimension = entries.FirstOrDefault()?.Dimension ?? Dimension,
            Entries = entries
        };
    }

    private OnnxEmbeddingProvider CreateProvider()
    {
        var options = ToEmbeddingOptions(_options);
        var factory = _sessionFactory ?? new OnnxRuntimeEmbeddingSessionFactory(_tokenizer);
        var manager = new OnnxEmbeddingSessionManager(options, factory);
        return new OnnxEmbeddingProvider(options, manager);
    }

    private VectorIndexEntry CreateEntry(
        string workspaceId,
        string collectionId,
        EmbeddingGeneratorInput input,
        IReadOnlyList<float> vector,
        int dimension)
    {
        var now = DateTimeOffset.UtcNow;
        return new VectorIndexEntry
        {
            EntryId = $"{workspaceId}:{collectionId}:{input.ItemId}:{Model}:{Provider}",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ItemId = input.ItemId,
            ItemKind = input.ItemKind,
            Layer = input.Layer,
            ContentHash = HashContent(input.Text),
            EmbeddingModel = Model,
            EmbeddingProvider = Provider,
            Dimension = dimension,
            Vector = vector.ToArray(),
            CreatedAt = now,
            UpdatedAt = now,
            Metadata = new Dictionary<string, string>(input.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["createdFrom"] = "onnx_embedding_generator",
                ["embeddingProviderType"] = ProviderType,
                ["normalize"] = Normalize ? "true" : "false",
                ["poolingStrategy"] = PoolingStrategy,
                ["device"] = _options.Device
            }
        };
    }

    private static EmbeddingOptions ToEmbeddingOptions(EmbeddingProviderOptions options)
    {
        return new EmbeddingOptions
        {
            ModelName = string.IsNullOrWhiteSpace(options.EmbeddingModel)
                ? EmbeddingModelPaths.DefaultModelName
                : options.EmbeddingModel,
            Dimensions = Math.Max(0, options.Dimension),
            MaxBatchSize = options.BatchSize > 0 ? options.BatchSize : 32,
            Normalize = options.Normalize,
            ModelPath = options.ModelPath,
            VocabularyPath = options.TokenizerPath,
            MaxSequenceLength = options.MaxTokens > 0 ? options.MaxTokens : 256,
            PoolingStrategy = ParsePoolingStrategy(options.PoolingStrategy),
            EnableContentHashCache = true
        };
    }

    private static EmbeddingPoolingStrategy? ParsePoolingStrategy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<EmbeddingPoolingStrategy>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    private static string HashContent(string? text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>embedding provider 配置诊断；只报告问题，不自动下载或修复模型。</summary>
public static class EmbeddingProviderDiagnosticsBuilder
{
    public static IReadOnlyList<VectorIndexDiagnostic> Build(EmbeddingProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var diagnostics = new List<VectorIndexDiagnostic>();
        if (!options.Enabled || options.ProviderType.Equals(EmbeddingProviderTypes.Disabled, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.ProviderUnavailable,
                "embedding provider 已禁用。",
                "设置 Enabled=true 并选择 DeterministicHash 或 OnnxLocal。"));
            return diagnostics;
        }

        if (options.ProviderType.Equals(EmbeddingProviderTypes.DeterministicHash, StringComparison.OrdinalIgnoreCase))
        {
            return diagnostics;
        }

        if (!options.ProviderType.Equals(EmbeddingProviderTypes.OnnxLocal, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.ProviderUnavailable,
                $"不支持的 embedding provider type：{options.ProviderType}",
                "使用 DeterministicHash、OnnxLocal 或 Disabled。"));
            return diagnostics;
        }

        if (!IsSupportedPooling(options.PoolingStrategy))
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.UnsupportedPoolingStrategy,
                $"不支持的 pooling strategy：{options.PoolingStrategy}",
                "使用 Mean 或 Cls。"));
        }

        var modelPath = EmbeddingModelPaths.ResolveModelPath(options.ModelPath);
        if (!File.Exists(modelPath))
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.ProviderUnavailable,
                $"ONNX 模型文件不存在：{modelPath}",
                "在本地私有配置中设置 ModelPath；不要提交模型文件。"));
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.ModelFileMissing,
                $"ONNX 模型文件不存在：{modelPath}",
                "下载模型到本地专用目录并通过配置引用。"));
            return diagnostics;
        }

        var tokenizerPath = EmbeddingModelPaths.ResolveVocabularyPath(options.TokenizerPath, modelPath);
        if (!File.Exists(tokenizerPath))
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.TokenizerUnavailable,
                $"tokenizer 词表文件不存在：{tokenizerPath}",
                "在本地私有配置中设置 TokenizerPath。"));
        }
        else if (tokenizerPath.EndsWith("tokenizer.json", StringComparison.OrdinalIgnoreCase)
                 || tokenizerPath.EndsWith("vocab.json", StringComparison.OrdinalIgnoreCase))
        {
            var tokenizerDirectory = Path.GetDirectoryName(Path.GetFullPath(tokenizerPath)) ?? string.Empty;
            var vocabPath = tokenizerPath.EndsWith("vocab.json", StringComparison.OrdinalIgnoreCase)
                ? tokenizerPath
                : Path.Combine(tokenizerDirectory, "vocab.json");
            var mergesPath = Path.Combine(tokenizerDirectory, "merges.txt");
            if (!File.Exists(vocabPath) || !File.Exists(mergesPath))
            {
                diagnostics.Add(NewDiagnostic(
                    VectorIndexDiagnosticTypes.TokenizerUnavailable,
                    $"Qwen BPE tokenizer 文件不完整：vocab={vocabPath}; merges={mergesPath}",
                    "确认 tokenizer.json、vocab.json、merges.txt 位于同一个 provider 模型目录。"));
            }
        }

        return diagnostics;
    }

    private static bool IsSupportedPooling(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
               || Enum.TryParse<EmbeddingPoolingStrategy>(value, ignoreCase: true, out _);
    }

    private static VectorIndexDiagnostic NewDiagnostic(
        string type,
        string message,
        string suggestedAction)
    {
        return new VectorIndexDiagnostic
        {
            DiagnosticId = $"{type}:embedding-provider",
            Type = type,
            Severity = "Error",
            Message = message,
            SuggestedAction = suggestedAction,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }
}

internal static class EmbeddingVectorMath
{
    public static IReadOnlyList<float> Normalize(IReadOnlyList<float> vector)
    {
        var norm = Math.Sqrt(vector.Sum(value => value * value));
        return norm <= 0 ? vector.ToArray() : vector.Select(value => (float)(value / norm)).ToArray();
    }
}
