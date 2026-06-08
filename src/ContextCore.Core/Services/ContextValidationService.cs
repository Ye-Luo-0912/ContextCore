using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// <see cref="IContextValidationService"/> 的基础实现，对上下文条目、记忆条目和打包请求执行字段校验。
/// </summary>
public sealed class ContextValidationService : IContextValidationService
{
    public ContextValidationResult ValidateContextItem(ContextItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var issues = new List<ContextValidationIssue>();
        Require(issues, item.WorkspaceId, "WorkspaceId", "WorkspaceId is required.");
        Require(issues, item.CollectionId, "CollectionId", "CollectionId is required.");
        Require(issues, item.Type, "Type", "Type is required.");

        if (item.ContentFormat != ContextContentFormat.BinaryRef && string.IsNullOrWhiteSpace(item.Content))
        {
            issues.Add(Error("ContentRequired", "Content is required unless ContentFormat is BinaryRef.", "Content"));
        }

        if (item.Importance is < 0 or > 1)
        {
            issues.Add(Warning("ImportanceRange", "Importance should normally be between 0 and 1.", "Importance"));
        }

        return CreateResult(issues);
    }

    public ContextValidationResult ValidateMemoryItem(ContextMemoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var issues = new List<ContextValidationIssue>();
        Require(issues, item.WorkspaceId, "WorkspaceId", "WorkspaceId is required.");
        Require(issues, item.CollectionId, "CollectionId", "CollectionId is required.");
        Require(issues, item.Type, "Type", "Type is required.");

        if (string.IsNullOrWhiteSpace(item.Content))
        {
            issues.Add(Error("ContentRequired", "Memory content is required.", "Content"));
        }

        if (item.Confidence is < 0 or > 1)
        {
            issues.Add(Warning("ConfidenceRange", "Confidence should normally be between 0 and 1.", "Confidence"));
        }

        return CreateResult(issues);
    }

    public ContextValidationResult ValidatePackageRequest(ContextPackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ContextValidationIssue>();
        Require(issues, request.WorkspaceId, "WorkspaceId", "WorkspaceId is required.");
        Require(issues, request.CollectionId, "CollectionId", "CollectionId is required.");

        if (request.TokenBudget < 0)
        {
            issues.Add(Error("InvalidTokenBudget", "TokenBudget cannot be negative.", "TokenBudget"));
        }

        if (request.Policy is not null && request.Policy.TokenBudget < 0)
        {
            issues.Add(Error("InvalidPolicyTokenBudget", "Policy TokenBudget cannot be negative.", "Policy.TokenBudget"));
        }

        return CreateResult(issues);
    }

    private static void Require(
        ICollection<ContextValidationIssue> issues,
        string value,
        string path,
        string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(Error("Required", message, path));
        }
    }

    private static ContextValidationResult CreateResult(IReadOnlyList<ContextValidationIssue> issues)
    {
        return new ContextValidationResult
        {
            Succeeded = issues.All(issue => issue.Severity != ContextValidationSeverity.Error),
            Issues = issues.ToArray()
        };
    }

    private static ContextValidationIssue Error(string code, string message, string path)
    {
        return new ContextValidationIssue
        {
            Code = code,
            Message = message,
            Path = path,
            Severity = ContextValidationSeverity.Error
        };
    }

    private static ContextValidationIssue Warning(string code, string message, string path)
    {
        return new ContextValidationIssue
        {
            Code = code,
            Message = message,
            Path = path,
            Severity = ContextValidationSeverity.Warning
        };
    }
}
