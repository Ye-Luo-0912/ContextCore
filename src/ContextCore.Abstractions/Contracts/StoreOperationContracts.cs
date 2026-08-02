namespace ContextCore.Abstractions;

/// <summary>
/// 标注存储契约方法的读写语义，供源生成器精确分类。
/// </summary>
/// <remarks>
/// 取代基于方法名前缀（Get/Query/List/Search/BatchGet）的脆弱推断。
/// 源生成器（<c>InvalidatingDecoratorGenerator</c>）读取此 attribute 决定：
/// <list type="bullet">
///   <item><see cref="Read"/> → 生成透传实现，直接调用 <c>_inner</c>。</item>
///   <item><see cref="Write"/> → 不生成，由手写 partial 实现并触发缓存失效。</item>
///   <item>未标注 → 编译诊断，不进行猜测。</item>
/// </list>
/// </remarks>
public enum StoreOperationKind
{
    /// <summary>读取操作：不修改存储状态，可由失效装饰器透传。</summary>
    Read,

    /// <summary>写入操作：修改存储状态，必须在手写 partial 中实现并触发缓存失效。</summary>
    Write
}

/// <summary>
/// 标注存储契约方法的读写语义。
/// 源生成器据此决定生成透传（<see cref="StoreOperationKind.Read"/>）还是要求手写实现（<see cref="StoreOperationKind.Write"/>）。
/// </summary>
/// <remarks>
/// 适用范围：被 <c>[GenerateInvalidatingDecorator]</c> 引用的存储契约接口。
/// 标注在接口方法上，由源生成器在编译期读取。
/// 未标注方法的接口将触发源生成器编译诊断，不进行前缀猜测。
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class StoreOperationAttribute : Attribute
{
    /// <param name="kind">操作语义（读取或写入）。</param>
    public StoreOperationAttribute(StoreOperationKind kind)
    {
        Kind = kind;
    }

    /// <summary>操作语义。</summary>
    public StoreOperationKind Kind { get; }
}
