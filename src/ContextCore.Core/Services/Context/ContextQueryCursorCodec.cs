using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Context;

/// <summary>
/// 不透明、版本化并带签名的 FTS Keyset 分页游标编解码器。
/// 调用方只应透传 <see cref="ContextQueryPage.NextCursor"/> 返回的 token，
/// 不得解析或自行构造其内部排序字段（SourceOrder / TsRank / Importance / UpdatedAt / Id）。
/// </summary>
/// <remarks>
/// token 结构：<c>cqc.v1.&lt;base64url(payload)&gt;.&lt;base64url(hmac-sha256)&gt;</c>。
/// 签名覆盖 payload 本身，任何篡改（改字段 / 换 payload）都会导致 Decode 抛
/// <see cref="InvalidDataException"/>；版本前缀不匹配同样拒绝。签名密钥未配置时使用
/// 内置开发密钥——生产环境必须通过 options 注入独立密钥，否则游标可被离线伪造。
/// </remarks>
public sealed class ContextQueryCursorCodec
{
    private const string VersionPrefix = "cqc.v1";

    /// <summary>开发默认密钥（仅测试/本地；生产必须注入独立签名密钥）。</summary>
    private static readonly byte[] DefaultSigningKey =
        Encoding.UTF8.GetBytes("context-core-dev-cursor-signing-key-v1-do-not-use-in-production");

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly byte[] _signingKey;

    public ContextQueryCursorCodec(byte[]? signingKey = null)
    {
        _signingKey = signingKey ?? DefaultSigningKey;
    }

    /// <summary>将类型化游标编码为不透明签名 token。</summary>
    public string Encode(ContextQueryCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (string.IsNullOrEmpty(cursor.Id))
        {
            throw new ArgumentException("Cursor.Id is required.", nameof(cursor));
        }

        var payload = JsonSerializer.Serialize(cursor, PayloadJsonOptions);
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signature = Sign(payloadB64);
        return $"{VersionPrefix}.{payloadB64}.{Base64UrlEncode(signature)}";
    }

    /// <summary>解码并验证不透明 token。签名或版本无效时抛 <see cref="InvalidDataException"/>。</summary>
    public ContextQueryCursor Decode(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidDataException("Cursor token is empty.");
        }

        var parts = token.Split('.');
        if (parts.Length < 3)
        {
            throw new InvalidDataException(
                $"Cursor token 版本不匹配或结构非法（期望 '{VersionPrefix}' 格式）。");
        }

        // 版本前缀可能含 '.'（如 cqc.v1），因此版本 = 除末两段外的所有段。
        var version = string.Join('.', parts[..^2]);
        if (!string.Equals(version, VersionPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Cursor token 版本不匹配或结构非法（期望 '{VersionPrefix}' 格式）。");
        }

        var payloadB64 = parts[^2];
        var expectedSignature = Base64UrlDecode(parts[^1]);
        var actualSignature = Sign(payloadB64);
        if (!CryptographicOperations.FixedTimeEquals(actualSignature, expectedSignature))
        {
            throw new InvalidDataException("Cursor token 签名校验失败——token 已被篡改或密钥不匹配。");
        }

        try
        {
            var payload = Encoding.UTF8.GetString(Base64UrlDecode(payloadB64));
            var cursor = JsonSerializer.Deserialize<ContextQueryCursor>(payload, PayloadJsonOptions);
            if (cursor is null || string.IsNullOrEmpty(cursor.Id))
            {
                throw new InvalidDataException("Cursor token payload 缺少必需字段（Id）。");
            }
            return cursor;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Cursor token payload 无法解析。", ex);
        }
    }

    private byte[] Sign(string payloadB64)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadB64));
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// 查询修订标识计算。基于查询语义（workspace/collection/query text/过滤条件）的稳定哈希；
/// 语义变化后修订标识变化，调用方应重置游标重新开始分页。
/// </summary>
public static class ContextQueryRevision
{
    /// <summary>计算查询修订标识（前缀 + SHA-256 十六进制，确定性）。</summary>
    public static string Compute(ContextQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var canonical = string.Join('\u001f',
        [
            query.WorkspaceId,
            query.CollectionId ?? string.Empty,
            query.QueryText ?? string.Empty,
            string.Join('\u001e', query.Tags.OrderBy(t => t, StringComparer.Ordinal)),
            string.Join('\u001e', query.Types.OrderBy(t => t, StringComparer.Ordinal)),
            string.Join('\u001e', query.ExcludedTypes.OrderBy(t => t, StringComparer.Ordinal)),
            string.Join('\u001e', query.ExcludedIds.OrderBy(t => t, StringComparer.Ordinal)),
            string.Join('\u001e', query.Refs.OrderBy(t => t, StringComparer.Ordinal)),
            query.IncludeContent.ToString(),
            query.IncludeDerived.ToString()
        ]);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "qrv1:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
