namespace ContextCore.Runtime;

/// <summary>
/// 运行时组装的能力级别。不同宿主按需选择对应级别，由 <see cref="ContextRuntimeBuilder"/> 统一组装主链服务。
/// </summary>
public enum RuntimeCapabilityProfile
{
    /// <summary>
    /// 最小集：仅核心检索/包构建/晋升主链，无 shadow trace、无 learning、无 job dispatcher。
    /// 用于 Evaluation 隔离 InMemory 评测场景。
    /// </summary>
    Minimal,

    /// <summary>
    /// 标准集：在 Minimal 基础上支持 filesystem/postgres store 切换与交互式 CLI 所需的完整 store 矩阵。
    /// 用于 ControlRoom Direct Mode。
    /// </summary>
    Standard,

    /// <summary>
    /// 完整集：在 Standard 基础上接入 shadow trace builders、learning dataset services、vector lifecycle、
    /// job dispatcher、governance services 等。用于 Service ASP.NET DI 生产路径。
    /// </summary>
    Full
}
