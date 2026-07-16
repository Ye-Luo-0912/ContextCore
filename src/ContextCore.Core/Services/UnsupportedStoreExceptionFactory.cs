namespace ContextCore.Core;

/// <summary>
/// Unsupported Store 异常工厂，集中 provider 名称归一化与消息格式化。
/// 消除每个生成类各自重复的 _provider 字段与 CreateException 方法。
/// </summary>
internal static class UnsupportedStoreExceptionFactory
{
    /// <summary>
    /// 创建 NotSupportedException，消息包含 provider 名称与 store 显示名。
    /// provider 为空或空白时归一化为 "unknown"。
    /// </summary>
    public static NotSupportedException Create(string provider, string displayName)
    {
        var normalizedProvider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
        var displayNameLower = char.ToLowerInvariant(displayName[0]) + displayName.Substring(1);
        return new NotSupportedException($"{displayNameLower} is not implemented for storage provider '{normalizedProvider}'.");
    }
}
