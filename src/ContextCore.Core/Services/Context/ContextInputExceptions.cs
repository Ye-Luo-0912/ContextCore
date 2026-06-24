using ContextCore.Abstractions;

namespace ContextCore.Core.Services;

/// <summary>输入层校验失败异常，保留结构化 issue 以便 Service 映射统一错误契约。</summary>
public sealed class ContextInputValidationException : ArgumentException
{
    public ContextInputValidationException(
        string message,
        IReadOnlyList<ContextValidationIssue> issues)
        : base(message)
    {
        Issues = issues;
    }

    public IReadOnlyList<ContextValidationIssue> Issues { get; }
}
