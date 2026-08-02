using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace ContextCore.Generators;

/// <summary>共享的 Roslyn 符号 → C# 签名格式化辅助方法。</summary>
internal static class CodeGenerationHelpers
{
    /// <summary>类型格式化：完全限定名 + 可空标注 + 泛型参数 + special types 别名。</summary>
    public static readonly SymbolDisplayFormat TypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                              | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>枚举接口的所有普通方法（包括基接口）。</summary>
    public static IEnumerable<IMethodSymbol> EnumerateInterfaceMethods(INamedTypeSymbol interfaceType)
    {
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<INamedTypeSymbol>();
        queue.Enqueue(interfaceType);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var member in current.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.MethodKind == MethodKind.Ordinary && seen.Add(member.OriginalDefinition))
                {
                    yield return member;
                }
            }
            foreach (var baseInterface in current.Interfaces)
            {
                queue.Enqueue(baseInterface);
            }
        }
    }

    /// <summary>生成方法签名（返回类型 + 名称 + 类型参数 + 参数列表），不含方法体。</summary>
    public static string FormatMethodSignature(IMethodSymbol method)
    {
        var sb = new StringBuilder();

        // 返回类型
        sb.Append(method.ReturnType.ToDisplayString(TypeFormat));
        sb.Append(' ');

        // 方法名
        sb.Append(method.Name);

        // 泛型类型参数
        if (method.IsGenericMethod)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", method.TypeParameters.Select(t => t.Name)));
            sb.Append('>');
        }

        // 参数列表
        sb.Append('(');
        sb.Append(string.Join(", ", method.Parameters.Select(FormatParameter)));
        sb.Append(')');

        return sb.ToString();
    }

    /// <summary>生成单个参数的字符串表示（含修饰符、类型、名称、默认值）。</summary>
    public static string FormatParameter(IParameterSymbol param)
    {
        var sb = new StringBuilder();

        // ref/out/in 修饰符
        switch (param.RefKind)
        {
            case RefKind.Ref:
                sb.Append("ref ");
                break;
            case RefKind.Out:
                sb.Append("out ");
                break;
            case RefKind.In:
                sb.Append("in ");
                break;
        }

        // params 修饰符
        if (param.IsParams)
        {
            sb.Append("params ");
        }

        // 类型
        sb.Append(param.Type.ToDisplayString(TypeFormat));
        sb.Append(' ');

        // 名称
        sb.Append(param.Name);

        // 默认值
        if (param.HasExplicitDefaultValue)
        {
            sb.Append(" = ");
            sb.Append(FormatDefaultValue(param));
        }

        return sb.ToString();
    }

    /// <summary>格式化参数的默认值表达式。</summary>
    private static string FormatDefaultValue(IParameterSymbol param)
    {
        if (!param.HasExplicitDefaultValue)
        {
            return string.Empty;
        }

        var value = param.ExplicitDefaultValue;

        // 值类型的 default 关键字（如 CancellationToken = default）
        // Roslyn 将 default(CancellationToken) 的 ExplicitDefaultValue 报告为 null，但它是值类型，需用 default 而非 null。
        if (value is null && param.Type.IsValueType)
        {
            return "default";
        }

        // 引用类型或 Nullable<T> 的 null 默认值
        if (value is null)
        {
            return "null";
        }

        // bool
        if (value is bool b)
        {
            return b ? "true" : "false";
        }

        // 数值类型
        if (value is int i)
        {
            return i.ToString();
        }
        if (value is long l)
        {
            return l.ToString() + "L";
        }
        if (value is double d)
        {
            return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // 枚举
        if (param.Type.TypeKind == TypeKind.Enum && value is { })
        {
            var enumMember = param.Type.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.ConstantValue?.Equals(value) == true);
            if (enumMember is not null)
            {
                return $"{param.Type.ToDisplayString(TypeFormat)}.{enumMember.Name}";
            }
        }

        // 字符串
        if (value is string s)
        {
            return $"\"{s}\"";
        }

        // 回退：使用 default
        return "default";
    }

    /// <summary>生成方法调用的参数列表（仅参数名，用于透传调用）。</summary>
    public static string FormatCallArguments(ImmutableArray<IParameterSymbol> parameters)
    {
        return string.Join(", ", parameters.Select(p =>
        {
            var sb = new StringBuilder();
            if (p.RefKind == RefKind.Ref)
            {
                sb.Append("ref ");
            }
            else if (p.RefKind == RefKind.Out)
            {
                sb.Append("out ");
            }
            else if (p.RefKind == RefKind.In)
            {
                sb.Append("in ");
            }
            sb.Append(p.Name);
            return sb.ToString();
        }));
    }

    /// <summary>
    /// 判断方法是否为只读方法（按 [StoreOperation(Read)] attribute）。
    /// 取代基于方法名前缀（Get/Query/List/Search/BatchGet）的脆弱推断；只认可显式标注的 Read 操作。
    /// 未标注方法的诊断由 InvalidatingDecoratorGenerator 负责。
    /// </summary>
    public static bool IsReadMethod(IMethodSymbol method)
        => GetStoreOperationKind(method) == StoreOperationKindValue.Read;

    private const string StoreOperationAttributeFullyQualifiedName = "ContextCore.Abstractions.StoreOperationAttribute";

    /// <summary>
    /// 读取方法上的 [StoreOperation] attribute，返回操作语义值。未标注返回 null。
    /// 源生成器不引用 Abstractions 程序集（避免运行时依赖），
    /// 通过符号 API 读取 attribute 构造参数的 int 值。
    /// </summary>
    public static StoreOperationKindValue? GetStoreOperationKind(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass is null) continue;
            var fullName = attr.AttributeClass.ToDisplayString();
            if (!fullName.Equals(StoreOperationAttributeFullyQualifiedName, StringComparison.Ordinal)) continue;
            if (attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is int kindValue
                && kindValue >= 0 && kindValue <= 1)
            {
                return (StoreOperationKindValue)kindValue;
            }
        }
        return null;
    }
}

/// <summary>
/// 本地镜像 ContextCore.Abstractions.StoreOperationKind 枚举值。
/// 源生成器不引用 Abstractions 程序集（避免运行时依赖），通过符号 API 读取 attribute 构造参数的 int 值。
/// 如果 Abstractions 的枚举值变化，需要同步更新这里。
/// </summary>
internal enum StoreOperationKindValue
{
    /// <summary>读取操作（对应 StoreOperationKind.Read = 0）。</summary>
    Read = 0,

    /// <summary>写入操作（对应 StoreOperationKind.Write = 1）。</summary>
    Write = 1
}
