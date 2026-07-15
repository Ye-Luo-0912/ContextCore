using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>未实现持久化后端时的显式占位存储，避免运行时静默丢弃决策记录。</summary>
[GenerateUnsupportedStore(typeof(IDecisionTraceStore), "Decision trace store")]
public sealed partial class UnsupportedDecisionTraceStore;
