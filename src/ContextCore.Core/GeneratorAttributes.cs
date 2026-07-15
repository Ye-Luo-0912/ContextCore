namespace ContextCore.Core;

/// <summary>标记 partial 类由源生成器生成完整的 Unsupported 存储实现：所有方法抛出 <see cref="NotSupportedException"/>。</summary>
/// <remarks>
/// 适用于尚未实现持久化后端的存储契约。构造时传入 provider 名称，所有方法抛出
/// <see cref="NotSupportedException"/>，便于在调用方显式感知能力缺失。
/// 生成器读取接口元数据，生成构造函数、字段和全部方法体。
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateUnsupportedStoreAttribute : Attribute
{
    /// <param name="interfaceType">要实现的存储契约接口类型。</param>
    /// <param name="displayName">异常消息中使用的存储显示名称（如 "Short term memory store"）。</param>
    public GenerateUnsupportedStoreAttribute(Type interfaceType, string displayName)
    {
        InterfaceType = interfaceType;
        DisplayName = displayName;
    }

    public Type InterfaceType { get; }
    public string DisplayName { get; }
}

/// <summary>
/// 标记 partial 类由源生成器生成失效装饰器的基础结构：构造函数、<c>_inner</c> 字段和只读方法（透传）。
/// 写入方法（非 Get/Query/List/Search 开头）必须在手写 partial 类中实现，调用 <c>AfterCommitAsync</c> 触发失效。
/// </summary>
/// <remarks>
/// 生成器读取接口元数据，按方法名前缀分类：
/// <list type="bullet">
///   <item>Get*/Query*/List*/Search* → 生成透传实现。</item>
///   <item>其他 → 不生成，由手写 partial 提供。</item>
/// </list>
/// 特殊批量写入装饰器（如 BatchUpsertAsync 需要物化 IEnumerable）继续手写。
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateInvalidatingDecoratorAttribute : Attribute
{
    /// <param name="interfaceType">要包装的存储契约接口类型。</param>
    public GenerateInvalidatingDecoratorAttribute(Type interfaceType)
    {
        InterfaceType = interfaceType;
    }

    public Type InterfaceType { get; }
}
