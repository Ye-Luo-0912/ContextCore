namespace ContextCore.Client;

/// <summary>
/// 逐参数拼接查询字符串的轻量帮助器，按插入顺序输出 <c>?k=v&amp;k2=v2</c>，
/// 空（无参数）时返回 <see cref="string.Empty"/>。语义与原内联 <c>string.Join('&amp;', parts)</c> 完全一致，
/// 用于集中化查询字符串构建并保持逐字节一致的 URL 输出。
/// </summary>
internal struct QueryBuilder
{
    private List<string>? _parts;

    private List<string> Parts => _parts ??= new List<string>();

    // 字符串值：null/空/空白跳过；通过 Uri.EscapeDataString 转义（与原 Escape() 相同）。
    public QueryBuilder Add(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }

        return this;
    }

    // 始终出现的值类型（int/long）：数字字符串无特殊字符，无需转义。
    public QueryBuilder Add(string name, int value)
    {
        Parts.Add($"{name}={value}");
        return this;
    }

    public QueryBuilder Add(string name, long value)
    {
        Parts.Add($"{name}={value}");
        return this;
    }

    // 可空 double（如 minConfidence/minImportance）：保留原 $"{value.Value}" 语义，仅在非空时输出。
    public QueryBuilder Add(string name, double? value)
    {
        if (value is not null)
        {
            Parts.Add($"{name}={value.Value}");
        }

        return this;
    }

    // 可空枚举：渲染为枚举名（如 "PendingReview"）；为 null 时跳过。枚举名无特殊字符，无需转义。
    public QueryBuilder AddEnum<T>(string name, T? value) where T : struct, Enum
    {
        if (value is not null)
        {
            Parts.Add($"{name}={value.Value}");
        }

        return this;
    }

    // 原始预格式化字面量段（如 "runtimeFeedback=true"）：不转义，始终添加。
    public QueryBuilder AddRaw(string name, string literalValue)
    {
        Parts.Add($"{name}={literalValue}");
        return this;
    }

    public override string ToString()
        => _parts is null || _parts.Count == 0 ? string.Empty : "?" + string.Join('&', _parts);
}
