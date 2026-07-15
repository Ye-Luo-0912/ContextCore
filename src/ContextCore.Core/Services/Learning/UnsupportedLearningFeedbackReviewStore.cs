using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>未实现 provider 的反馈审核存储占位，返回明确配置错误。</summary>
[GenerateUnsupportedStore(typeof(ILearningFeedbackReviewStore), "Learning feedback review store")]
public sealed partial class UnsupportedLearningFeedbackReviewStore;
