using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>在生成条目持久化前校验 LLM 压缩输出。</summary>
public sealed class CompressionResultValidator
{
    public CompressionValidationResult Validate(
        CompressionResponse response,
        CompressionRequest request)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<ContextError>();
        var warnings = new List<ContextWarning>();

        if (string.IsNullOrWhiteSpace(response.OperationId))
        {
            errors.Add(Error("MissingOperationId", "压缩响应缺少操作 ID。"));
        }

        if (response.Status != CompressionStatus.Failed && response.GeneratedItems.Count == 0)
        {
            errors.Add(Error("NoGeneratedItems", "压缩已成功但没有生成任何上下文条目。"));
        }

        foreach (var item in response.GeneratedItems)
        {
            ValidateGeneratedItem(item, request, errors, warnings);
        }

        foreach (var hint in response.IndexHints)
        {
            if (string.IsNullOrWhiteSpace(hint.Key))
            {
                warnings.Add(Warning("EmptyIndexHintKey", "生成的索引提示包含空 key，存储层应忽略该提示。"));
            }

            if (hint.ContextRefs.Count == 0)
            {
                warnings.Add(Warning("IndexHintWithoutContextRefs", $"索引提示 '{hint.Key}' 没有关联上下文引用。"));
            }
        }

        return new CompressionValidationResult(errors, warnings);
    }

    private static void ValidateGeneratedItem(
        ContextItem item,
        CompressionRequest request,
        ICollection<ContextError> errors,
        ICollection<ContextWarning> warnings)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            errors.Add(Error("GeneratedItemMissingId", "生成条目缺少 ID。"));
        }

        if (string.IsNullOrWhiteSpace(item.WorkspaceId))
        {
            errors.Add(Error("GeneratedItemMissingWorkspace", $"生成条目 '{item.Id}' 缺少工作区 ID。"));
        }

        if (string.IsNullOrWhiteSpace(item.CollectionId))
        {
            errors.Add(Error("GeneratedItemMissingCollection", $"生成条目 '{item.Id}' 缺少集合 ID。"));
        }

        if (string.IsNullOrWhiteSpace(item.Type))
        {
            errors.Add(Error("GeneratedItemMissingType", $"生成条目 '{item.Id}' 缺少类型。"));
        }

        if (string.IsNullOrWhiteSpace(item.Content))
        {
            errors.Add(Error("GeneratedItemEmptyContent", $"生成条目 '{item.Id}' 内容为空。"));
        }

        if (request.Inputs.Count > 0
            && (!item.Metadata.TryGetValue("derivedFrom", out var derivedFrom)
                || string.IsNullOrWhiteSpace(derivedFrom)))
        {
            errors.Add(Error("GeneratedItemMissingDerivedFrom", $"生成条目 '{item.Id}' 缺少 derivedFrom 元数据。"));
        }

        if (!item.Metadata.TryGetValue("isDerived", out var isDerived)
            || !string.Equals(isDerived, "true", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(Warning("GeneratedItemNotMarkedDerived", $"生成条目 '{item.Id}' 未标记为派生内容。"));
        }
    }

    private static ContextError Error(string code, string message)
    {
        return new ContextError
        {
            Code = code,
            Message = message
        };
    }

    private static ContextWarning Warning(string code, string message)
    {
        return new ContextWarning
        {
            Code = code,
            Message = message
        };
    }
}

/// <summary>压缩响应校验结果。</summary>
public sealed class CompressionValidationResult
{
    public CompressionValidationResult(
        IReadOnlyList<ContextError> errors,
        IReadOnlyList<ContextWarning> warnings)
    {
        Errors = errors;
        Warnings = warnings;
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<ContextError> Errors { get; }

    public IReadOnlyList<ContextWarning> Warnings { get; }
}
