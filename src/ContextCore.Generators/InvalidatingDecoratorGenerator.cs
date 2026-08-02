using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace ContextCore.Generators;

/// <summary>
/// 源生成器：根据 <see cref="GenerateInvalidatingDecoratorAttribute"/> 生成失效装饰器的基础结构。
/// 生成：_inner 字段、构造函数（调用 base(invalidator, versionStore)）、只读方法（透传到 _inner）。
/// 写入方法（标注 [StoreOperation(Write)]）由手写 partial 类提供，调用 AfterCommitAsync 触发失效。
/// 附加能力接口（如 IContextStoreBatchLookup）会被加入 base list 并透传其只读方法，
/// 避免 Decorator 在 DI 解析后隐藏 inner store 实现的能力接口。
/// </summary>
/// <remarks>
/// 读写分类改为基于 [StoreOperation] attribute，不再使用方法名前缀猜测。
/// 未标注方法的接口会触发编译诊断 CCGEN001。
/// </remarks>
[Generator]
public sealed class InvalidatingDecoratorGenerator : IIncrementalGenerator
{
    private const string AttributeFullyQualifiedName = "ContextCore.Core.GenerateInvalidatingDecoratorAttribute";

    // 未标注 [StoreOperation] 的存储接口方法触发编译诊断。
    private static readonly DiagnosticDescriptor UnannotatedStoreOperationDiagnostic = new(
        id: "CCGEN001",
        title: "Store operation method missing [StoreOperation] annotation",
        messageFormat: "存储接口方法 '{0}' on '{1}' 缺少 [StoreOperation(Read)] 或 [StoreOperation(Write)] 标注。InvalidatingDecorator 生成器要求显式标注，不再按方法名前缀猜测。",
        category: "ContextCore.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "P0-8: source generator no longer guesses read/write semantics by method name prefix (Get/Query/List/Search/BatchGet). Annotate interface methods explicitly with [StoreOperation(StoreOperationKind.Read)] or [StoreOperation(StoreOperationKind.Write)].");

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeFullyQualifiedName,
            predicate: static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax,
            transform: static (ctx, ct) =>
            {
                var classSymbol = (INamedTypeSymbol)ctx.TargetSymbol;
                var attrData = ctx.Attributes[0];

                var interfaceType = attrData.ConstructorArguments[0].Value as INamedTypeSymbol;

                // 收集附加能力接口（params Type[] 在 ConstructorArguments[1] 中以 ImmutableArray<TypedConstant> 形式出现）
                var capabilities = new List<INamedTypeSymbol>();
                if (attrData.ConstructorArguments.Length > 1)
                {
                    var capabilityArray = attrData.ConstructorArguments[1];
                    if (capabilityArray.Kind == TypedConstantKind.Array)
                    {
                        foreach (var tc in capabilityArray.Values)
                        {
                            if (tc.Value is INamedTypeSymbol cap)
                            {
                                capabilities.Add(cap);
                            }
                        }
                    }
                }

                // 收集未标注 [StoreOperation] 的方法，用于编译诊断。
                var unannotated = new List<UnannotatedMethod>();
                CollectUnannotatedMethods(interfaceType!, unannotated);
                foreach (var cap in capabilities)
                {
                    CollectUnannotatedMethods(cap, unannotated);
                }

                ct.ThrowIfCancellationRequested();

                return new DecoratorSpec(
                    classSymbol.ContainingNamespace.ToDisplayString(),
                    classSymbol.Name,
                    interfaceType!,
                    capabilities,
                    unannotated);
            });

        context.RegisterSourceOutput(provider, static (spc, spec) =>
        {
            // 先报告未标注方法诊断，再生成源（若存在未标注方法，生成器只跳过它们，
            // 装饰器类会因未实现接口方法而触发 CS0535，配合本诊断给出明确指引）。
            foreach (var m in spec.UnannotatedMethods)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    UnannotatedStoreOperationDiagnostic,
                    m.Location,
                    m.MethodName,
                    m.InterfaceName));
            }

            var source = GenerateSource(spec);
            spc.AddSource($"{spec.ClassName}_Decorator.g.cs", source);
        });
    }

    /// <summary>枚举接口（含基接口）的所有方法，收集未标注 [StoreOperation] 的方法。</summary>
    private static void CollectUnannotatedMethods(INamedTypeSymbol interfaceType, List<UnannotatedMethod> unannotated)
    {
        foreach (var method in CodeGenerationHelpers.EnumerateInterfaceMethods(interfaceType))
        {
            if (CodeGenerationHelpers.GetStoreOperationKind(method) is null)
            {
                var interfaceName = interfaceType.ToDisplayString(CodeGenerationHelpers.TypeFormat);
                // 优先使用方法定义位置的 Location；缺失时回退到接口声明位置。
                var location = method.Locations.FirstOrDefault()
                    ?? interfaceType.Locations.FirstOrDefault();
                unannotated.Add(new UnannotatedMethod(
                    $"{method.Name}({string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString(CodeGenerationHelpers.TypeFormat)))})",
                    interfaceName,
                    location));
            }
        }
    }

    private static string GenerateSource(DecoratorSpec spec)
    {
        var interfaceName = spec.InterfaceType.ToDisplayString(CodeGenerationHelpers.TypeFormat);

        // base list: InvalidatingStoreDecoratorBase, <主接口>, <附加能力接口>...
        var baseList = new StringBuilder($"InvalidatingStoreDecoratorBase, {interfaceName}");
        foreach (var cap in spec.AdditionalCapabilities)
        {
            var capName = cap.ToDisplayString(CodeGenerationHelpers.TypeFormat);
            baseList.Append(", ").Append(capName);
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>由 ContextCore.Generators.InvalidatingDecoratorGenerator 生成。请勿手动编辑。</auto-generated>");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {spec.Namespace};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// 失效边界 Decorator 基础结构（自动生成）：包装 <see cref=\"{interfaceName}\"/>，");
        sb.AppendLine("/// 在写入成功后触发缓存失效。只读方法透传到 _inner；写入方法在手写 partial 中实现。");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"public sealed partial class {spec.ClassName} : {baseList}");
        sb.AppendLine("{");
        sb.AppendLine($"    private readonly {interfaceName} _inner;");
        sb.AppendLine();
        sb.AppendLine($"    public {spec.ClassName}(");
        sb.AppendLine($"        {interfaceName} inner,");
        sb.AppendLine("        ContextCore.Abstractions.IStateCacheInvalidator invalidator,");
        sb.AppendLine("        ContextCore.Abstractions.IContextStateVersionStore? versionStore = null)");
        sb.AppendLine("        : base(invalidator, versionStore)");
        sb.AppendLine("    {");
        sb.AppendLine("        _inner = inner;");
        sb.AppendLine("    }");
        sb.AppendLine();

        int readMethodCount = 0;

        // 主接口的只读方法透传（按 [StoreOperation(Read)] attribute 判定）
        foreach (var method in CodeGenerationHelpers.EnumerateInterfaceMethods(spec.InterfaceType))
        {
            if (!CodeGenerationHelpers.IsReadMethod(method))
            {
                continue;
            }

            AppendPassthrough(sb, method, castTarget: null);
            readMethodCount++;
        }

        // 附加能力接口的只读方法透传
        // 注意：能力接口的写方法必须同样不被生成（与主接口规则一致），由手写 partial 实现。
        // 能力接口的方法在 _inner（主接口类型）上不存在，需要显式 cast 到能力接口再调用。
        foreach (var cap in spec.AdditionalCapabilities)
        {
            var capName = cap.ToDisplayString(CodeGenerationHelpers.TypeFormat);
            foreach (var method in CodeGenerationHelpers.EnumerateInterfaceMethods(cap))
            {
                if (!CodeGenerationHelpers.IsReadMethod(method))
                {
                    continue;
                }

                AppendPassthrough(sb, method, castTarget: capName);
                readMethodCount++;
            }
        }

        if (readMethodCount == 0)
        {
            sb.AppendLine("    // 此接口无只读方法，所有方法均为写入方法，需在手写 partial 中实现。");
            sb.AppendLine();
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendPassthrough(StringBuilder sb, IMethodSymbol method, string? castTarget)
    {
        var signature = CodeGenerationHelpers.FormatMethodSignature(method);
        var callArgs = CodeGenerationHelpers.FormatCallArguments(method.Parameters);
        var genericSuffix = method.IsGenericMethod
            ? $"<{string.Join(", ", method.TypeParameters.Select(t => t.Name))}>"
            : "";

        // castTarget 非 null 时表示该方法属于附加能力接口，需将 _inner 显式 cast 到能力接口类型。
        // cast 与调用合并为一个表达式：(cast)_inner.Method(args)
        var receiver = castTarget is null
            ? "_inner"
            : $"(({castTarget})_inner)";

        sb.AppendLine($"    public {signature}");
        sb.AppendLine($"        => {receiver}.{method.Name}{genericSuffix}({callArgs});");
        sb.AppendLine();
    }

    private sealed class DecoratorSpec
    {
        public string Namespace { get; }
        public string ClassName { get; }
        public INamedTypeSymbol InterfaceType { get; }
        public IReadOnlyList<INamedTypeSymbol> AdditionalCapabilities { get; }
        public IReadOnlyList<UnannotatedMethod> UnannotatedMethods { get; }

        public DecoratorSpec(
            string ns,
            string className,
            INamedTypeSymbol interfaceType,
            IReadOnlyList<INamedTypeSymbol> additionalCapabilities,
            IReadOnlyList<UnannotatedMethod> unannotatedMethods)
        {
            Namespace = ns;
            ClassName = className;
            InterfaceType = interfaceType;
            AdditionalCapabilities = additionalCapabilities;
            UnannotatedMethods = unannotatedMethods;
        }
    }

    private sealed class UnannotatedMethod
    {
        public string MethodName { get; }
        public string InterfaceName { get; }
        public Location? Location { get; }

        public UnannotatedMethod(string methodName, string interfaceName, Location? location)
        {
            MethodName = methodName;
            InterfaceName = interfaceName;
            Location = location;
        }
    }
}
