using Microsoft.ML.Tokenizers;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Embedding;

/// <summary>根据本地 tokenizer 文件格式创建 ONNX embedding tokenizer。</summary>
public static class EmbeddingTokenizerFactory
{
    public static IEmbeddingTokenizer Create(
        string tokenizerPath,
        string modelPath,
        bool lowercase = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        var fullTokenizerPath = Path.GetFullPath(tokenizerPath);
        var tokenizerDirectory = Path.GetDirectoryName(fullTokenizerPath)
            ?? throw new InvalidOperationException($"无法解析 tokenizer 目录：{tokenizerPath}");

        if (fullTokenizerPath.EndsWith("tokenizer.json", StringComparison.OrdinalIgnoreCase)
            || fullTokenizerPath.EndsWith("vocab.json", StringComparison.OrdinalIgnoreCase))
        {
            var vocabPath = fullTokenizerPath.EndsWith("vocab.json", StringComparison.OrdinalIgnoreCase)
                ? fullTokenizerPath
                : Path.Combine(tokenizerDirectory, "vocab.json");
            var mergesPath = Path.Combine(tokenizerDirectory, "merges.txt");
            var tokenizerConfigPath = Path.Combine(tokenizerDirectory, "tokenizer_config.json");
            return QwenBpeEmbeddingTokenizer.FromFiles(vocabPath, mergesPath, tokenizerConfigPath);
        }

        return BertWordPieceTokenizer.FromVocabularyFile(fullTokenizerPath, lowercase);
    }
}

/// <summary>Qwen/Qwen2 系列 byte-level BPE tokenizer 适配层。</summary>
public sealed class QwenBpeEmbeddingTokenizer : IEmbeddingTokenizer
{
    private const string DefaultPadToken = "<|endoftext|>";

    private readonly BpeTokenizer _tokenizer;
    private readonly int _padTokenId;

    private QwenBpeEmbeddingTokenizer(BpeTokenizer tokenizer, int padTokenId)
    {
        _tokenizer = tokenizer;
        _padTokenId = padTokenId;
    }

    public static QwenBpeEmbeddingTokenizer FromFiles(
        string vocabPath,
        string mergesPath,
        string? tokenizerConfigPath = null)
    {
        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException($"未找到 Qwen BPE vocab 文件：{vocabPath}", vocabPath);
        }

        if (!File.Exists(mergesPath))
        {
            throw new FileNotFoundException($"未找到 Qwen BPE merges 文件：{mergesPath}", mergesPath);
        }

        var specialTokens = LoadSpecialTokens(tokenizerConfigPath);
        var padToken = LoadPadToken(tokenizerConfigPath) ?? DefaultPadToken;
        var options = new BpeOptions(vocabPath, mergesPath)
        {
            ByteLevel = true,
            SpecialTokens = specialTokens,
            FuseUnknownTokens = true
        };
        var tokenizer = BpeTokenizer.Create(options);
        var padId = specialTokens.TryGetValue(padToken, out var configuredPadId)
            ? configuredPadId
            : tokenizer.Vocabulary.TryGetValue(padToken, out var vocabularyPadId)
                ? vocabularyPadId
                : -1;
        if (padId < 0)
        {
            throw new InvalidOperationException($"Qwen tokenizer 未找到 pad token：{padToken}");
        }

        return new QwenBpeEmbeddingTokenizer(tokenizer, padId);
    }

    public EmbeddingTokenizationResult Tokenize(
        IReadOnlyList<string> texts,
        int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var sequenceLength = Math.Clamp(maxTokens, 2, 8192);
        var inputIds = new long[texts.Count * sequenceLength];
        var attentionMask = new long[texts.Count * sequenceLength];
        var tokenTypeIds = new long[texts.Count * sequenceLength];
        Array.Fill(inputIds, _padTokenId);

        for (var batchIndex = 0; batchIndex < texts.Count; batchIndex++)
        {
            var tokenIds = _tokenizer.EncodeToIds(texts[batchIndex] ?? string.Empty, considerNormalization: true, considerPreTokenization: true);
            var offset = batchIndex * sequenceLength;
            var limit = Math.Min(tokenIds.Count, sequenceLength);
            for (var tokenIndex = 0; tokenIndex < limit; tokenIndex++)
            {
                inputIds[offset + tokenIndex] = tokenIds[tokenIndex];
                attentionMask[offset + tokenIndex] = 1;
            }
        }

        return new EmbeddingTokenizationResult
        {
            BatchSize = texts.Count,
            SequenceLength = sequenceLength,
            InputIds = inputIds,
            AttentionMask = attentionMask,
            TokenTypeIds = tokenTypeIds,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tokenizer"] = "qwen-byte-bpe"
            }
        };
    }

    private static IReadOnlyDictionary<string, int> LoadSpecialTokens(string? tokenizerConfigPath)
    {
        var tokens = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(tokenizerConfigPath) || !File.Exists(tokenizerConfigPath))
        {
            return tokens;
        }

        using var stream = File.OpenRead(tokenizerConfigPath);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("added_tokens_decoder", out var decoder)
            || decoder.ValueKind != JsonValueKind.Object)
        {
            return tokens;
        }

        foreach (var property in decoder.EnumerateObject())
        {
            if (!int.TryParse(property.Name, out var id)
                || !property.Value.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var token = content.GetString();
            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens[token] = id;
            }
        }

        return tokens;
    }

    private static string? LoadPadToken(string? tokenizerConfigPath)
    {
        if (string.IsNullOrWhiteSpace(tokenizerConfigPath) || !File.Exists(tokenizerConfigPath))
        {
            return null;
        }

        using var stream = File.OpenRead(tokenizerConfigPath);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.TryGetProperty("pad_token", out var padToken)
               && padToken.ValueKind == JsonValueKind.String
            ? padToken.GetString()
            : null;
    }
}
