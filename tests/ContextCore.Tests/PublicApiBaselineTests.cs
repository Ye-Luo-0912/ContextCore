using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

/// <summary>
/// 反射驱动的 PublicAPI baseline 测试。
/// 替代 PublicApiGenerator（与 net10.0 不兼容）和 Microsoft.PublicApi.MSBuild（NuGet 不存在）方案。
/// 通过反射枚举 ContextCore.Abstractions 程序集的所有公共类型与成员，
/// 与签入的 baseline 文件对比；新增/删除公共 API 时测试失败，提示更新 baseline。
/// </summary>
[TestClass]
[TestCategory("Contract")]
public sealed class PublicApiBaselineTests
{
    private const string BaselineRelativePath = "Baselines/ContextCore.Abstractions.PublicApi.txt";

    private static readonly string BaselineFullPath = LocateBaselinePath();

    /// <summary>
    /// 当 baseline 文件不存在时，写入当前快照并失败，提示用户签入。
    /// 当 baseline 已存在时，对比当前反射结果，列出新增/删除项，相同时通过。
    /// </summary>
    [TestMethod]
    public void PublicApi_MatchesBaselineOrBaselineNeedsUpdate()
    {
        var actual = BuildCurrentPublicApi();
        var actualText = actual.ToBaselineText();

        if (!File.Exists(BaselineFullPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BaselineFullPath)!);
            File.WriteAllText(BaselineFullPath, actualText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert.Fail(
                "PublicAPI baseline 文件不存在，已生成首版到：{0}。请检查后签入。" +
                "若需更新已有 baseline，请删除该文件后重新运行本测试。",
                BaselineFullPath);
        }

        var baselineText = File.ReadAllText(BaselineFullPath);
        if (string.Equals(baselineText, actualText, StringComparison.Ordinal))
        {
            return;
        }

        var baselineApi = PublicApiSnapshot.Parse(baselineText);
        var added = actual.Except(baselineApi).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var removed = baselineApi.Except(actual).OrderBy(s => s, StringComparer.Ordinal).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("PublicAPI baseline drift detected.");
        sb.AppendLine();
        sb.AppendLine("Baseline file: " + BaselineFullPath);
        sb.AppendLine();
        if (added.Count > 0)
        {
            sb.AppendLine("Added API entries (need to be added to baseline):");
            foreach (var entry in added)
            {
                sb.AppendLine("  + " + entry);
            }
            sb.AppendLine();
        }
        if (removed.Count > 0)
        {
            sb.AppendLine("Removed API entries (need to be removed from baseline):");
            foreach (var entry in removed)
            {
                sb.AppendLine("  - " + entry);
            }
            sb.AppendLine();
        }
        sb.AppendLine("To update baseline, overwrite the file with the content below:");
        sb.AppendLine();
        sb.AppendLine("----- BEGIN BASELINE -----");
        sb.Append(actualText);
        sb.AppendLine("----- END BASELINE -----");

        Assert.Fail(sb.ToString());
    }

    /// <summary>
    /// 辅助测试：将当前反射结果写到 baseline 文件，方便人工对比后签入。
    /// 默认 [Ignore]，仅在主动调用时执行。
    /// </summary>
    [TestMethod]
    [Ignore("Manual trigger: rewrite baseline file from current reflection snapshot.")]
    public void PublicApi_RegenerateBaselineFile()
    {
        var actual = BuildCurrentPublicApi();
        var actualText = actual.ToBaselineText();
        Directory.CreateDirectory(Path.GetDirectoryName(BaselineFullPath)!);
        File.WriteAllText(BaselineFullPath, actualText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static PublicApiSnapshot BuildCurrentPublicApi()
    {
        var assembly = typeof(ContextMemoryLayer).Assembly;
        return PublicApiSnapshotBuilder.Build(assembly);
    }

    private static string LocateBaselinePath()
    {
        // 测试 bin 目录位于 tests/ContextCore.Tests/bin/<Config>/net10.0/
        // baseline 文件签入到 tests/ContextCore.Tests/Baselines/
        var assemblyLocation = typeof(PublicApiBaselineTests).Assembly.Location;
        var binDir = Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;
        // 逐级向上查找直到 tests/ContextCore.Tests 目录
        var dir = binDir;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "ContextCore.Tests.csproj");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir, BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));
            }
            dir = Path.GetDirectoryName(dir);
        }
        // 回退：使用 bin 目录下的 Baselines 子目录
        return Path.Combine(binDir, BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>PublicAPI 快照：一组 API 条目，提供集合运算与文本序列化。</summary>
internal sealed class PublicApiSnapshot : IEquatable<PublicApiSnapshot>
{
    private readonly HashSet<string> _entries;

    private PublicApiSnapshot(HashSet<string> entries)
    {
        _entries = entries;
    }

    public int Count => _entries.Count;

    public static PublicApiSnapshot Parse(string text)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.Length > 0 && line[0] == '#') continue;
            entries.Add(line);
        }
        return new PublicApiSnapshot(entries);
    }

    public List<string> Except(PublicApiSnapshot other)
    {
        var result = new List<string>(_entries.Count);
        foreach (var entry in _entries)
        {
            if (!other._entries.Contains(entry))
            {
                result.Add(entry);
            }
        }
        return result;
    }

    public string ToBaselineText()
    {
        var sorted = _entries.OrderBy(s => s, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder(sorted.Count * 64);
        sb.AppendLine("# ContextCore.Abstractions Public API Baseline");
        sb.AppendLine("# Format:");
        sb.AppendLine("#   T:<FullTypeName> [(Enum|Delegate|Interface)]");
        sb.AppendLine("#   N:<FullTypeName>+<NestedTypeName>");
        sb.AppendLine("#   M:<FullTypeName>.<MethodSignature>");
        sb.AppendLine("#   P:<FullTypeName>.<PropertyName> (get|set|init)");
        sb.AppendLine("#   F:<FullTypeName>.<FieldName>");
        sb.AppendLine("#   E:<FullTypeName>.<EventName>");
        sb.AppendLine("# Entries are sorted alphabetically; re-running the test will produce identical output.");
        sb.AppendLine();
        foreach (var entry in sorted)
        {
            sb.AppendLine(entry);
        }
        return sb.ToString();
    }

    public bool Equals(PublicApiSnapshot? other) => other is not null && _entries.SetEquals(other._entries);
    public override bool Equals(object? obj) => obj is PublicApiSnapshot other && Equals(other);
    public override int GetHashCode() => _entries.Count;

    internal static PublicApiSnapshot From(HashSet<string> entries) => new(entries);
}

/// <summary>反射枚举程序集的公共 API。</summary>
internal static class PublicApiSnapshotBuilder
{
    public static PublicApiSnapshot Build(Assembly assembly)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in assembly.GetTypes())
        {
            if (!IsPublicApiType(type)) continue;
            AppendType(entries, type);
        }

        return PublicApiSnapshot.From(entries);
    }

    private static bool IsPublicApiType(Type type)
    {
        if (type.IsNested)
        {
            // 只在嵌套类型本身可见时考虑；可见性在 AppendType 内统一判断
            return IsNestedVisible(type);
        }
        return type.IsPublic || type.IsNestedPublic;
    }

    private static bool IsNestedVisible(Type type)
    {
        // 递归检查每个嵌套层级都可见
        var current = type;
        while (current.IsNested)
        {
            if (!current.IsNestedPublic && !current.IsNestedFamily && !current.IsNestedFamORAssem)
            {
                return false;
            }
            current = current.DeclaringType!;
        }
        return current.IsPublic;
    }

    private static void AppendType(HashSet<string> entries, Type type)
    {
        // 跳过编译器生成的私有/internal 嵌套类型（匿名类型、closure、匿名 lambda display class）
        if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)) return;

        if (type.IsNested && !IsNestedVisible(type)) return;

        var kind = GetTypeKind(type);
        var typeName = FormatTypeName(type);
        entries.Add($"T:{typeName}{kind}");

        // 嵌套公共类型作为独立 T: 条目（用 + 分隔的完整名称）
        // 不在此处递归枚举——外层 GetTypes() 已覆盖所有公共嵌套类型

        AppendMembers(entries, type, typeName);
    }

    private static string GetTypeKind(Type type)
    {
        if (type.IsEnum) return " (Enum)";
        if (typeof(Delegate).IsAssignableFrom(type)) return " (Delegate)";
        if (type.IsInterface) return " (Interface)";
        if (type.IsValueType) return " (Struct)";
        return string.Empty; // class 不标注
    }

    private static void AppendMembers(HashSet<string> entries, Type type, string typeName)
    {
        if (type.IsEnum)
        {
            // 枚举值作为 F: 条目
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsSpecialName) continue; // value__ 已被 SpecialName 过滤
                entries.Add($"F:{typeName}.{field.Name}");
            }
            return;
        }

        // 实例/静态构造函数
        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            entries.Add($"M:{typeName}.{FormatConstructor(ctor)}");
        }

        // 属性（含 get/set/init）
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var accessors = new List<string>(2);
            var get = prop.GetMethod;
            var set = prop.SetMethod;
            if (get is not null && (get.IsPublic || get.IsFamily || get.IsFamilyOrAssembly))
            {
                accessors.Add("get");
            }
            if (set is not null && (set.IsPublic || set.IsFamily || set.IsFamilyOrAssembly))
            {
                accessors.Add(IsInitOnly(set) ? "init" : "set");
            }
            if (accessors.Count == 0) continue;
            entries.Add($"P:{typeName}.{prop.Name} ({string.Join('|', accessors)})");
        }

        // 字段（含常量、只读字段）
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (field.IsSpecialName) continue;
            entries.Add($"F:{typeName}.{field.Name}");
        }

        // 事件
        foreach (var evt in type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            entries.Add($"E:{typeName}.{evt.Name}");
        }

        // 方法（排除属性/事件的 accessor、运算符可保留以便追踪）
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (method.IsSpecialName) continue; // get_/set_/add_/remove_/op_ 的 SpecialName
            entries.Add($"M:{typeName}.{FormatMethod(method)}");
        }
    }

    private static bool IsInitOnly(MethodInfo setter)
    {
        // init-only setter 的返回类型修饰符是 IsExternalInit（modreq(System.Runtime.CompilerServices.IsExternalInit)）
        return setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    private static string FormatConstructor(ConstructorInfo ctor)
    {
        var sb = new StringBuilder();
        if (ctor.IsStatic) sb.Append("static ");
        sb.Append("#ctor");
        AppendParameters(sb, ctor.GetParameters());
        return sb.ToString();
    }

    private static string FormatMethod(MethodInfo method)
    {
        var sb = new StringBuilder();
        if (method.IsStatic) sb.Append("static ");
        sb.Append(method.Name);
        if (method.IsGenericMethod)
        {
            sb.Append("``");
            sb.Append(method.GetGenericArguments().Length);
        }
        AppendParameters(sb, method.GetParameters());
        return sb.ToString();
    }

    private static void AppendParameters(StringBuilder sb, ParameterInfo[] parameters)
    {
        sb.Append('(');
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            var p = parameters[i];
            if (p.IsOut) sb.Append("out ");
            else if (p.ParameterType.IsByRef)
            {
                sb.Append("ref ");
            }
            else if (p.IsIn) sb.Append("in ");
            sb.Append(FormatTypeName(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType));
        }
        sb.Append(')');
    }

    private static string FormatTypeName(Type type)
    {
        // 处理泛型类型名：Foo`1 -> Foo<T>
        if (type.IsNested)
        {
            var outer = FormatTypeName(type.DeclaringType!);
            return outer + "+" + FormatTypeNameCore(type);
        }
        return FormatTypeNameCore(type);
    }

    private static string FormatTypeNameCore(Type type)
    {
        if (type.IsArray)
        {
            return FormatTypeName(type.GetElementType()!) + "[]";
        }
        if (type.IsGenericParameter)
        {
            // 简化：泛型形参用 !T0 / !!T0 表示位置
            return type.DeclaringMethod is not null ? "!!" + type.GenericParameterPosition : "!" + type.GenericParameterPosition;
        }

        var name = type.IsNested ? type.Name : type.FullName ?? type.Name;
        if (type.IsGenericType)
        {
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            // 防御性处理：ContainsGenericParameters 为 true 时表示这是一个开放泛型
            // （未绑定具体类型参数），GetGenericArguments 可能抛 InvalidOperationException。
            // 这种情况常见于 out T / in T 等带有协变/逆变修饰的开放泛型接口。
            if (type.ContainsGenericParameters)
            {
                name += "<>";
            }
            else
            {
                var args = type.GetGenericArguments();
                name += "<";
                for (var i = 0; i < args.Length; i++)
                {
                    if (i > 0) name += ", ";
                    name += FormatTypeName(args[i]);
                }
                name += ">";
            }
        }
        return name;
    }
}
