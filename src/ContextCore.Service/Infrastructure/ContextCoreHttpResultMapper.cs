using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Core.Services;

namespace ContextCore.Service.Infrastructure;

internal static class ContextCoreHttpResultMapper
{
    public static IResult Success(ContextInputIngestionResult result)
    {
        return Results.Ok(result);
    }

    public static IResult InvalidRequest(
        HttpContext httpContext,
        string operationId,
        string target,
        string message,
        string? field = null,
        int statusCode = StatusCodes.Status400BadRequest)
    {
        return Results.Json(
            CreateErrorResponse(
                operationId,
                ContextCoreErrorCodes.InvalidRequest,
                message,
                target,
                ResolveTraceId(httpContext),
                [
                    new ContextCoreErrorDetail
                    {
                        Code = ContextCoreErrorCodes.InvalidRequest,
                        Field = field,
                        Target = target,
                        Message = message
                    }
                ]),
            statusCode: statusCode);
    }

    public static IResult NotFound(
        HttpContext httpContext,
        string operationId,
        string target,
        string message,
        string? detailCode = null)
    {
        return Results.Json(
            CreateErrorResponse(
                operationId,
                ContextCoreErrorCodes.NotFound,
                message,
                target,
                ResolveTraceId(httpContext),
                [
                    new ContextCoreErrorDetail
                    {
                        Code = detailCode ?? ContextCoreErrorCodes.NotFound,
                        Target = target,
                        Message = message
                    }
                ]),
            statusCode: StatusCodes.Status404NotFound);
    }

    public static IResult StorageUnavailable(
        HttpContext httpContext,
        string operationId,
        string target,
        string message)
    {
        return Results.Json(
            CreateErrorResponse(
                operationId,
                ContextCoreErrorCodes.StorageUnavailable,
                message,
                target,
                ResolveTraceId(httpContext)),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    public static IResult Misconfigured(
        HttpContext httpContext,
        string operationId,
        string target,
        string message)
    {
        return Results.Json(
            CreateErrorResponse(
                operationId,
                ContextCoreErrorCodes.Misconfigured,
                message,
                target,
                ResolveTraceId(httpContext)),
            statusCode: StatusCodes.Status500InternalServerError);
    }

    public static IResult InternalError(
        HttpContext httpContext,
        string operationId,
        string target,
        string message)
    {
        return Results.Json(
            CreateErrorResponse(
                operationId,
                ContextCoreErrorCodes.InternalError,
                message,
                target,
                ResolveTraceId(httpContext)),
            statusCode: StatusCodes.Status500InternalServerError);
    }

    public static IResult Error(HttpContext httpContext, Exception exception, string operationId, string target)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);
        operationId = ResolveOperationId(operationId);
        var traceId = ResolveTraceId(httpContext);

        if (exception is ContextInputValidationException validationException)
        {
            return Results.Json(
                CreateErrorResponse(
                    operationId,
                    ContextCoreErrorCodes.ValidationFailed,
                    "Input validation failed.",
                    target,
                    traceId,
                    validationException.Issues.Select(issue => new ContextCoreErrorDetail
                    {
                        Code = issue.Code,
                        Field = issue.Path,
                        Target = target,
                        Message = issue.Message
                    }).ToArray()),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (exception is ArgumentException argumentException)
        {
            return Results.Json(
                CreateErrorResponse(
                    operationId,
                    ContextCoreErrorCodes.InvalidRequest,
                    argumentException.Message,
                    target,
                    traceId,
                    [
                        new ContextCoreErrorDetail
                        {
                            Code = ContextCoreErrorCodes.InvalidRequest,
                            Field = argumentException.ParamName,
                            Target = target,
                            Message = argumentException.Message
                        }
                    ]),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (exception is DirectoryNotFoundException or TimeoutException)
        {
            return Results.Json(
                CreateErrorResponse(
                    operationId,
                    ContextCoreErrorCodes.StorageUnavailable,
                    exception.Message,
                    target,
                    traceId),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (exception is IOException or UnauthorizedAccessException)
        {
            return Results.Json(
                CreateErrorResponse(
                    operationId,
                    ContextCoreErrorCodes.StoreWriteFailed,
                    exception.Message,
                    target,
                    traceId),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        if (exception is InvalidOperationException)
        {
            return Results.Json(
                CreateErrorResponse(
                    operationId,
                    ContextCoreErrorCodes.Misconfigured,
                    exception.Message,
                    target,
                    traceId),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Json(
            CreateErrorResponse(
                operationId,
                ContextCoreErrorCodes.InternalError,
                exception.Message,
                target,
                traceId),
            statusCode: StatusCodes.Status500InternalServerError);
    }

    private static ContextCoreErrorResponse CreateErrorResponse(
        string operationId,
        string errorCode,
        string message,
        string target,
        string traceId,
        IReadOnlyList<ContextCoreErrorDetail>? details = null)
    {
        return new ContextCoreErrorResponse
        {
            OperationId = ResolveOperationId(operationId),
            ErrorCode = errorCode,
            Message = message,
            Target = target,
            TraceId = traceId,
            Details = details ?? [],
            Warnings = []
        };
    }

    private static string ResolveTraceId(HttpContext httpContext)
    {
        return Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
    }

    private static string ResolveOperationId(string operationId)
    {
        return string.IsNullOrWhiteSpace(operationId)
            ? Guid.NewGuid().ToString("N")
            : operationId;
    }
}
