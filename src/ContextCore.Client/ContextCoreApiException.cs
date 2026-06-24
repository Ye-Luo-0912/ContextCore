using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

/// <summary>ContextCore HTTP API 返回结构化错误时抛出的异常。</summary>
public sealed class ContextCoreApiException : Exception
{
    public ContextCoreApiException(ContextCoreErrorResponse errorResponse, System.Net.HttpStatusCode statusCode)
        : base(errorResponse.Message)
    {
        ErrorResponse = errorResponse;
        StatusCode = statusCode;
    }

    public ContextCoreErrorResponse ErrorResponse { get; }

    public System.Net.HttpStatusCode StatusCode { get; }
}
