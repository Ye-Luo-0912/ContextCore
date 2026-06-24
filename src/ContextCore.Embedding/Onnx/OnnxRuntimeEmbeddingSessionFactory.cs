using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Embedding;

/// <summary>基于 Microsoft.ML.OnnxRuntime 创建本地 embedding 会话。</summary>
public sealed class OnnxRuntimeEmbeddingSessionFactory : IOnnxEmbeddingSessionFactory
{
    private readonly IEmbeddingTokenizer? _tokenizer;

    public OnnxRuntimeEmbeddingSessionFactory(IEmbeddingTokenizer? tokenizer = null)
    {
        _tokenizer = tokenizer;
    }

    public Task<IOnnxEmbeddingSession> CreateAsync(
        EmbeddingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var modelPath = EmbeddingModelPaths.ResolveModelPath(options.ModelPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"未找到 ONNX 模型文件：{modelPath}。请确认模型已下载到项目内 Models 目录，或通过 Embedding:ModelPath 指定路径。",
                modelPath);
        }

        var vocabularyPath = EmbeddingModelPaths.ResolveVocabularyPath(options.VocabularyPath, modelPath);
        if (!File.Exists(vocabularyPath))
        {
            throw new FileNotFoundException(
                $"未找到 tokenizer 词表文件：{vocabularyPath}。请确认 vocab.txt 与模型文件位于同一个项目模型目录。",
                vocabularyPath);
        }

        var modelRoot = GetModelRoot(modelPath);
        var dimensions = ResolveDimensions(options, modelRoot);
        var lowercase = ResolveTokenizerLowercase(options, modelRoot);
        var poolingStrategy = ResolvePoolingStrategy(options, modelPath);
        var tokenizer = _tokenizer
            ?? EmbeddingTokenizerFactory.Create(
                vocabularyPath,
                modelPath,
                lowercase);
        var sessionOptions = CreateSessionOptions(options);
        var session = new InferenceSession(modelPath, sessionOptions);

        return Task.FromResult<IOnnxEmbeddingSession>(new OnnxRuntimeEmbeddingSession(
            options.ModelName,
            dimensions,
            Math.Max(2, options.MaxSequenceLength),
            poolingStrategy,
            options.UseTokenTypeIds,
            tokenizer,
            session));
    }

    private static SessionOptions CreateSessionOptions(EmbeddingOptions options)
    {
        var sessionOptions = new SessionOptions
        {
            EnableMemoryPattern = true
        };

        if (options.OnnxIntraOpNumThreads > 0)
        {
            sessionOptions.IntraOpNumThreads = options.OnnxIntraOpNumThreads;
        }

        if (options.OnnxInterOpNumThreads > 0)
        {
            sessionOptions.InterOpNumThreads = options.OnnxInterOpNumThreads;
        }

        return sessionOptions;
    }

    private static int ResolveDimensions(EmbeddingOptions options, string modelRoot)
    {
        if (options.Dimensions > 0)
        {
            return options.Dimensions;
        }

        var configPath = Path.Combine(modelRoot, "config.json");
        if (File.Exists(configPath)
            && TryReadJsonPropertyInt(configPath, "hidden_size", out var hiddenSize)
            && hiddenSize > 0)
        {
            return hiddenSize;
        }

        return 384;
    }

    private static bool ResolveTokenizerLowercase(EmbeddingOptions options, string modelRoot)
    {
        if (options.TokenizerLowercase is not null)
        {
            return options.TokenizerLowercase.Value;
        }

        var tokenizerConfigPath = Path.Combine(modelRoot, "tokenizer_config.json");
        if (File.Exists(tokenizerConfigPath)
            && TryReadJsonPropertyBool(tokenizerConfigPath, "do_lower_case", out var lowercase))
        {
            return lowercase;
        }

        return false;
    }

    private static EmbeddingPoolingStrategy ResolvePoolingStrategy(
        EmbeddingOptions options,
        string modelPath)
    {
        if (options.PoolingStrategy is not null)
        {
            return options.PoolingStrategy.Value;
        }

        var probe = $"{options.ModelName} {modelPath}";
        return probe.Contains("bge", StringComparison.OrdinalIgnoreCase)
            ? EmbeddingPoolingStrategy.Cls
            : EmbeddingPoolingStrategy.Mean;
    }

    private static string GetModelRoot(string modelPath)
    {
        var modelDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath))
            ?? throw new InvalidOperationException($"无法解析 ONNX 模型目录：{modelPath}");
        if (File.Exists(Path.Combine(modelDirectory, "config.json"))
            || File.Exists(Path.Combine(modelDirectory, "tokenizer_config.json")))
        {
            return modelDirectory;
        }

        return Directory.GetParent(modelDirectory)?.FullName ?? modelDirectory;
    }

    private static bool TryReadJsonPropertyInt(
        string path,
        string propertyName,
        out int value)
    {
        value = 0;
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.TryGetProperty(propertyName, out var property)
            && property.TryGetInt32(out value);
    }

    private static bool TryReadJsonPropertyBool(
        string path,
        string propertyName,
        out bool value)
    {
        value = false;
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        return false;
    }
}

internal sealed class OnnxPastKeyValueInput
{
    private readonly int[] _dimensions;

    public OnnxPastKeyValueInput(string name, int[] dimensions)
    {
        Name = name;
        _dimensions = dimensions;
    }

    public string Name { get; }

    public int[] ToInitialCacheDimensions(int batchSize)
    {
        if (_dimensions.Length != 4)
        {
            return [batchSize, 1, 0, 1];
        }

        return
        [
            _dimensions[0] > 0 ? _dimensions[0] : batchSize,
            _dimensions[1] > 0 ? _dimensions[1] : 1,
            0,
            _dimensions[3] > 0 ? _dimensions[3] : 1
        ];
    }
}

/// <summary>封装 ONNX Runtime 推理、mean pooling 与向量输出。</summary>
public sealed class OnnxRuntimeEmbeddingSession : IOnnxEmbeddingSession
{
    private readonly InferenceSession _session;
    private readonly IEmbeddingTokenizer _tokenizer;
    private readonly int _maxSequenceLength;
    private readonly bool _useTokenTypeIds;
    private readonly string _inputIdsName;
    private readonly string? _attentionMaskName;
    private readonly string? _tokenTypeIdsName;
    private readonly string? _positionIdsName;
    private readonly IReadOnlyList<OnnxPastKeyValueInput> _pastKeyValueInputs;
    private readonly string _outputName;

    public OnnxRuntimeEmbeddingSession(
        string modelName,
        int dimensions,
        int maxSequenceLength,
        EmbeddingPoolingStrategy poolingStrategy,
        bool useTokenTypeIds,
        IEmbeddingTokenizer tokenizer,
        InferenceSession session)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(session);

        ModelName = string.IsNullOrWhiteSpace(modelName)
            ? EmbeddingModelPaths.DefaultModelName
            : modelName;
        Dimensions = Math.Max(1, dimensions);
        _maxSequenceLength = Math.Clamp(maxSequenceLength, 2, 512);
        PoolingStrategy = poolingStrategy;
        _useTokenTypeIds = useTokenTypeIds;
        _tokenizer = tokenizer;
        _session = session;
        _inputIdsName = ResolveRequiredInputName(session, "input_ids");
        _attentionMaskName = ResolveOptionalInputName(session, "attention_mask");
        _tokenTypeIdsName = ResolveOptionalInputName(session, "token_type_ids");
        _positionIdsName = ResolveOptionalInputName(session, "position_ids");
        _pastKeyValueInputs = ResolvePastKeyValueInputs(session);
        _outputName = ResolveOutputName(session);
    }

    public string ModelName { get; }

    public int Dimensions { get; }

    public EmbeddingPoolingStrategy PoolingStrategy { get; }

    public Task<IReadOnlyList<IReadOnlyList<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        cancellationToken.ThrowIfCancellationRequested();

        if (texts.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<IReadOnlyList<float>>>(Array.Empty<IReadOnlyList<float>>());
        }

        var encoded = _tokenizer.Tokenize(texts, _maxSequenceLength);
        var inputs = CreateInputs(encoded);
        using var results = _session.Run(inputs);
        var output = results.FirstOrDefault(result => string.Equals(result.Name, _outputName, StringComparison.Ordinal))
            ?? results.First();
        var tensor = output.AsTensor<float>();
        var vectors = PoolVectors(tensor, encoded);

        return Task.FromResult<IReadOnlyList<IReadOnlyList<float>>>(vectors);
    }

    public ValueTask DisposeAsync()
    {
        _session.Dispose();
        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<NamedOnnxValue> CreateInputs(EmbeddingTokenizationResult encoded)
    {
        var dimensions = new[] { encoded.BatchSize, encoded.SequenceLength };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                _inputIdsName,
                new DenseTensor<long>(encoded.InputIds, dimensions))
        };

        if (_attentionMaskName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                _attentionMaskName,
                new DenseTensor<long>(encoded.AttentionMask, dimensions)));
        }

        if (_useTokenTypeIds && _tokenTypeIdsName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                _tokenTypeIdsName,
                new DenseTensor<long>(encoded.TokenTypeIds, dimensions)));
        }

        if (_positionIdsName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                _positionIdsName,
                new DenseTensor<long>(CreatePositionIds(encoded), dimensions)));
        }

        foreach (var pastInput in _pastKeyValueInputs)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                pastInput.Name,
                new DenseTensor<float>(Array.Empty<float>(), pastInput.ToInitialCacheDimensions(encoded.BatchSize))));
        }

        return inputs;
    }

    private static long[] CreatePositionIds(EmbeddingTokenizationResult encoded)
    {
        var values = new long[encoded.BatchSize * encoded.SequenceLength];
        for (var batchIndex = 0; batchIndex < encoded.BatchSize; batchIndex++)
        {
            var offset = batchIndex * encoded.SequenceLength;
            var position = 0L;
            for (var tokenIndex = 0; tokenIndex < encoded.SequenceLength; tokenIndex++)
            {
                if (encoded.AttentionMask[offset + tokenIndex] == 0)
                {
                    continue;
                }

                values[offset + tokenIndex] = position++;
            }
        }

        return values;
    }

    private static IReadOnlyList<OnnxPastKeyValueInput> ResolvePastKeyValueInputs(InferenceSession session)
    {
        return session.InputMetadata
            .Where(static item => item.Key.StartsWith("past_key_values.", StringComparison.Ordinal))
            .Select(static item => new OnnxPastKeyValueInput(item.Key, item.Value.Dimensions.ToArray()))
            .OrderBy(static item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<IReadOnlyList<float>> PoolVectors(
        Tensor<float> tensor,
        EmbeddingTokenizationResult encoded)
    {
        var outputDimensions = tensor.Dimensions.ToArray();
        return outputDimensions.Length switch
        {
            2 => ReadTwoDimensionalOutput(tensor.ToArray(), outputDimensions),
            3 => PoolingStrategy == EmbeddingPoolingStrategy.Cls
                ? ReadClsPooledSequenceOutput(tensor.ToArray(), outputDimensions)
                : MeanPoolSequenceOutput(tensor.ToArray(), outputDimensions, encoded),
            _ => throw new InvalidOperationException(
                $"ONNX embedding 输出维度不支持：rank={outputDimensions.Length}。")
        };
    }

    private IReadOnlyList<IReadOnlyList<float>> ReadTwoDimensionalOutput(
        float[] values,
        IReadOnlyList<int> outputDimensions)
    {
        var batchSize = outputDimensions[0];
        var hiddenSize = outputDimensions[1];
        var vectors = new List<IReadOnlyList<float>>(batchSize);
        for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
        {
            var vector = new float[hiddenSize];
            Array.Copy(values, batchIndex * hiddenSize, vector, 0, hiddenSize);
            vectors.Add(vector);
        }

        return vectors;
    }

    private IReadOnlyList<IReadOnlyList<float>> ReadClsPooledSequenceOutput(
        float[] values,
        IReadOnlyList<int> outputDimensions)
    {
        var batchSize = outputDimensions[0];
        var outputSequenceLength = outputDimensions[1];
        var hiddenSize = outputDimensions[2];
        var vectors = new List<IReadOnlyList<float>>(batchSize);

        for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
        {
            var vector = new float[hiddenSize];
            var sourceOffset = batchIndex * outputSequenceLength * hiddenSize;
            Array.Copy(values, sourceOffset, vector, 0, hiddenSize);
            vectors.Add(vector);
        }

        return vectors;
    }

    private IReadOnlyList<IReadOnlyList<float>> MeanPoolSequenceOutput(
        float[] values,
        IReadOnlyList<int> outputDimensions,
        EmbeddingTokenizationResult encoded)
    {
        var batchSize = outputDimensions[0];
        var outputSequenceLength = outputDimensions[1];
        var hiddenSize = outputDimensions[2];
        var vectors = new List<IReadOnlyList<float>>(batchSize);

        for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
        {
            var vector = new float[hiddenSize];
            var tokenCount = 0;
            var usableSequenceLength = Math.Min(outputSequenceLength, encoded.SequenceLength);

            for (var tokenIndex = 0; tokenIndex < usableSequenceLength; tokenIndex++)
            {
                var mask = encoded.AttentionMask[(batchIndex * encoded.SequenceLength) + tokenIndex];
                if (mask == 0)
                {
                    continue;
                }

                var sourceOffset = ((batchIndex * outputSequenceLength) + tokenIndex) * hiddenSize;
                for (var hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++)
                {
                    vector[hiddenIndex] += values[sourceOffset + hiddenIndex];
                }

                tokenCount++;
            }

            var divisor = Math.Max(1, tokenCount);
            for (var hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++)
            {
                vector[hiddenIndex] /= divisor;
            }

            vectors.Add(vector);
        }

        return vectors;
    }

    private static string ResolveRequiredInputName(InferenceSession session, string expectedName)
    {
        return ResolveOptionalInputName(session, expectedName)
            ?? throw new InvalidOperationException($"ONNX 模型缺少必要输入：{expectedName}");
    }

    private static string? ResolveOptionalInputName(InferenceSession session, string expectedName)
    {
        return session.InputMetadata.Keys.FirstOrDefault(
            name => string.Equals(name, expectedName, StringComparison.Ordinal));
    }

    private static string ResolveOutputName(InferenceSession session)
    {
        return session.OutputMetadata.Keys.FirstOrDefault(
                name => string.Equals(name, "last_hidden_state", StringComparison.Ordinal))
            ?? session.OutputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException("ONNX 模型没有可用输出。");
    }
}
