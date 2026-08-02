namespace ContextCore.Core;

/// <summary>标记 partial 类由源生成器生成完整的 Unsupported 存储实现：所有方法抛出 <see cref="NotSupportedException"/>。</summary>
/// <remarks>
/// 适用于尚未实现持久化后端的存储契约。构造时传入 provider 名称，所有方法抛出
/// <see cref="NotSupportedException"/>，便于在调用方显式感知能力缺失。
/// 生成器读取接口元数据，生成构造函数、字段和全部方法体。
/// attribute 改为 internal，减少公开 API 面（只服务编译期内部实现）。
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class GenerateUnsupportedStoreAttribute : Attribute
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
/// 写入方法（标注 [StoreOperation(Write)]）必须在手写 partial 类中实现，调用 <c>AfterCommitAsync</c> 触发失效。
/// </summary>
/// <remarks>
/// attribute 改为 internal，减少公开 API 面（只服务编译期内部实现）。
/// 读写分类改为基于接口方法上的 [StoreOperation] attribute，不再使用方法名前缀猜测。
/// 未标注方法的接口会触发编译诊断 CCGEN001。
/// 特殊批量写入装饰器（如 BatchUpsertAsync 需要物化 IEnumerable）继续手写。
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class GenerateInvalidatingDecoratorAttribute : Attribute
{
    /// <param name="interfaceType">要包装的存储契约接口类型。</param>
    /// <param name="additionalCapabilities">
    /// 附加能力接口（如 <c>IContextStoreBatchLookup</c>）。生成器将这些接口加入 Decorator 的 base list
    /// 并透传其只读方法（按 [StoreOperation(Read)] attribute 判定）。用于让 Decorator 在保留缓存失效语义的同时透传可选能力接口，
    /// 避免能力接口在 DI 解析后被 Decorator 隐藏。
    /// </param>
    public GenerateInvalidatingDecoratorAttribute(Type interfaceType, params Type[] additionalCapabilities)
    {
        InterfaceType = interfaceType;
        AdditionalCapabilities = additionalCapabilities;
    }

    public Type InterfaceType { get; }
    public Type[] AdditionalCapabilities { get; }
}
