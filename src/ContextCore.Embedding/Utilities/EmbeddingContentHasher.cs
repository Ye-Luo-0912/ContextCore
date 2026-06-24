using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Embedding.Utilities;

/// <summary>生成 embedding 输入的稳定内容哈希。</summary>
public static class EmbeddingContentHasher
{
    public static string HashInput(
        EmbeddingInput input,
        EmbeddingInputKind inputKind,
        string modelName)
    {
        ArgumentNullException.ThrowIfNull(input);
        return HashText(input.Text, inputKind, modelName);
    }

    /// <summary>对给定文本（已含 instruction 前缀）计算稳定哈希。</summary>
    public static string HashText(
        string text,
        EmbeddingInputKind inputKind,
        string modelName)
    {
        var combined = string.Join('\u001f', new[]
        {
            modelName,
            inputKind.ToString(),
            text
        });

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
