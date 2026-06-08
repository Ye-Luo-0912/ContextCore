namespace ContextCore.Service;

/// <summary>最小 API Key 安全配置。从 appsettings.json 的 Security 节读取，私钥建议放在 ~/.contextcore/secrets.json。</summary>
public sealed class SecurityOptions
{
    /// <summary>是否要求调用方在每个请求中携带 API Key。默认 true；开发环境可设为 false。</summary>
    public bool RequireApiKey { get; init; } = true;

    /// <summary>请求头名称，默认 X-ContextCore-Key。</summary>
    public string ApiKeyHeaderName { get; init; } = "X-ContextCore-Key";

    /// <summary>
    /// 服务端期望的 API Key 值。
    /// 建议通过 ~/.contextcore/secrets.json 的 Security:ApiKey 字段注入，不要写入仓库。
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>不需要校验 API Key 的路径前缀列表（精确或前缀匹配）。</summary>
    public IReadOnlyList<string> PublicPaths { get; init; } =
    [
        "/health",
        "/api/health",
        "/openapi",
        "/scalar",
        "/",
    ];

    /// <summary>
    /// 允许的跨源列表（CORS）。
    /// 空列表：仅允许同源请求（支持 no-cors fetch 和 curl，拒绝跨源 XHR/fetch）。
    /// 含有 "*"：允许所有来源（仅限内网测试或开放 API 场景，不建议生产）。
    /// 具体来源如 ["http://localhost:3000", "https://myapp.example.com"]：只允许指定地址跨源请求。
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = Array.Empty<string>();
}
