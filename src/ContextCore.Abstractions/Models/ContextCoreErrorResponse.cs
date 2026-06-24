namespace ContextCore.Abstractions.Models;

/// <summary>Service 对外错误响应的统一契约。</summary>
public sealed class ContextCoreErrorResponse
{
    public string OperationId { get; init; } = string.Empty;

    public string ErrorCode { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? Target { get; init; }

    public string TraceId { get; init; } = string.Empty;

    public IReadOnlyList<ContextCoreErrorDetail> Details { get; init; } = Array.Empty<ContextCoreErrorDetail>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>错误响应中的单条细节。</summary>
public sealed class ContextCoreErrorDetail
{
    public string Code { get; init; } = string.Empty;

    public string? Field { get; init; }

    public string? Target { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>ContextCore Service 对外稳定错误码。</summary>
public static class ContextCoreErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";
    public const string StorageUnavailable = "storage_unavailable";
    public const string StoreWriteFailed = "store_write_failed";
    public const string Misconfigured = "misconfigured";
    public const string InternalError = "internal_error";
}
