using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Embedding;

/// <summary>轻量 BERT WordPiece tokenizer，用于本地 ONNX embedding 推理。</summary>
public sealed class BertWordPieceTokenizer : IEmbeddingTokenizer
{
    private const int MaxInputCharsPerWord = 100;
    private readonly Dictionary<string, int> _vocabulary;
    private readonly bool _lowercase;
    private readonly int _clsTokenId;
    private readonly int _sepTokenId;
    private readonly int _padTokenId;
    private readonly int _unkTokenId;

    private BertWordPieceTokenizer(
        Dictionary<string, int> vocabulary,
        bool lowercase)
    {
        _vocabulary = vocabulary;
        _lowercase = lowercase;
        _clsTokenId = RequiredTokenId("[CLS]");
        _sepTokenId = RequiredTokenId("[SEP]");
        _padTokenId = RequiredTokenId("[PAD]");
        _unkTokenId = RequiredTokenId("[UNK]");
    }

    public static BertWordPieceTokenizer FromVocabularyFile(
        string vocabularyPath,
        bool lowercase = true)
    {
        if (!File.Exists(vocabularyPath))
        {
            throw new FileNotFoundException(
                $"未找到 tokenizer 词表文件：{vocabularyPath}",
                vocabularyPath);
        }

        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var line in File.ReadLines(vocabularyPath, Encoding.UTF8))
        {
            var token = line.TrimEnd('\r', '\n');
            if (!vocabulary.ContainsKey(token))
            {
                vocabulary[token] = index;
            }

            index++;
        }

        return new BertWordPieceTokenizer(vocabulary, lowercase);
    }

    public TokenizedTextBatch EncodeBatch(
        IReadOnlyList<string> texts,
        int maxSequenceLength)
    {
        var tokenized = Tokenize(texts, maxSequenceLength);
        return new TokenizedTextBatch(
            tokenized.BatchSize,
            tokenized.SequenceLength,
            tokenized.InputIds,
            tokenized.AttentionMask,
            tokenized.TokenTypeIds);
    }

    public EmbeddingTokenizationResult Tokenize(
        IReadOnlyList<string> texts,
        int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var sequenceLength = Math.Clamp(maxTokens, 2, 512);
        var inputIds = new long[texts.Count * sequenceLength];
        var attentionMask = new long[texts.Count * sequenceLength];
        var tokenTypeIds = new long[texts.Count * sequenceLength];

        for (var batchIndex = 0; batchIndex < texts.Count; batchIndex++)
        {
            var tokenIds = EncodeSingle(texts[batchIndex], sequenceLength);
            var offset = batchIndex * sequenceLength;
            for (var tokenIndex = 0; tokenIndex < sequenceLength; tokenIndex++)
            {
                if (tokenIndex < tokenIds.Count)
                {
                    inputIds[offset + tokenIndex] = tokenIds[tokenIndex];
                    attentionMask[offset + tokenIndex] = 1;
                    continue;
                }

                inputIds[offset + tokenIndex] = _padTokenId;
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
                ["tokenizer"] = "bert-wordpiece"
            }
        };
    }

    private IReadOnlyList<int> EncodeSingle(string? text, int maxSequenceLength)
    {
        var tokenIds = new List<int>(maxSequenceLength)
        {
            _clsTokenId
        };
        var maxContentLength = maxSequenceLength - 2;

        foreach (var token in BasicTokenize(text ?? string.Empty))
        {
            foreach (var wordPieceTokenId in WordPieceTokenize(token))
            {
                if (tokenIds.Count > maxContentLength)
                {
                    tokenIds.Add(_sepTokenId);
                    return tokenIds;
                }

                tokenIds.Add(wordPieceTokenId);
            }
        }

        tokenIds.Add(_sepTokenId);
        return tokenIds;
    }

    private IReadOnlyList<string> BasicTokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsControl(ch))
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                FlushCurrent();
                continue;
            }

            if (IsPunctuation(ch) || IsChineseChar(ch))
            {
                FlushCurrent();
                tokens.Add(NormalizeToken(ch.ToString()));
                continue;
            }

            current.Append(ch);
        }

        FlushCurrent();
        return tokens;

        void FlushCurrent()
        {
            if (current.Length == 0)
            {
                return;
            }

            var token = NormalizeToken(current.ToString());
            current.Clear();
            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens.Add(token);
            }
        }
    }

    private IEnumerable<int> WordPieceTokenize(string token)
    {
        switch (token.Length)
        {
            case 0:
                yield break;
            case > MaxInputCharsPerWord:
                yield return _unkTokenId;
                yield break;
        }

        var subTokens = new List<int>();
        var start = 0;
        while (start < token.Length)
        {
            var end = token.Length;
            int? currentId = null;
            while (start < end)
            {
                var piece = token[start..end];
                if (start > 0)
                {
                    piece = "##" + piece;
                }

                if (_vocabulary.TryGetValue(piece, out var tokenId))
                {
                    currentId = tokenId;
                    break;
                }

                end--;
            }

            if (currentId is null)
            {
                yield return _unkTokenId;
                yield break;
            }

            subTokens.Add(currentId.Value);
            start = end;
        }

        foreach (var subToken in subTokens)
        {
            yield return subToken;
        }
    }

    private int RequiredTokenId(string token)
    {
        return _vocabulary.TryGetValue(token, out var tokenId)
            ? tokenId
            : throw new InvalidOperationException($"tokenizer 词表缺少必要 token：{token}");
    }

    private string NormalizeToken(string token)
    {
        if (!_lowercase)
        {
            return token;
        }

        var lowered = token.ToLowerInvariant();
        var normalized = lowered.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(ch);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsPunctuation(char ch)
    {
        var code = (int)ch;
        if (code is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126)
        {
            return true;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    private static bool IsChineseChar(char ch)
    {
        var code = (int)ch;
        return code is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0xF900 and <= 0xFAFF;
    }
}

public sealed record TokenizedTextBatch(
    int BatchSize,
    int SequenceLength,
    long[] InputIds,
    long[] AttentionMask,
    long[] TokenTypeIds);
