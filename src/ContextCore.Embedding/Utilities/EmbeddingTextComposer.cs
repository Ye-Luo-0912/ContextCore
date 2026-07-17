namespace ContextCore.Embedding.Utilities;

/// <summary>
/// P0-7.6: Embedding 文本组合器，单一负责 instruction + text 的拼接格式。
/// 调用方（Retrieval 层）通过 <c>EmbeddingInput.Instruction</c> 传入指令，
/// Provider 通过本类统一拼接，避免 Retrieval 层与 Embedding Provider 各自拼接导致双重 instruction。
/// 规范格式：<c>instruction + "\n\n" + text</c>（instruction 非空时）。
/// </summary>
public static class EmbeddingTextComposer
{
    /// <summary>
    /// 将 instruction 前缀拼接到 text 前。
    /// 规范格式：instruction 非空时返回 <c>instruction + "\n\n" + text</c>，否则直接返回 text。
    /// </summary>
    public static string Compose(string? instruction, string text)
    {
        if (string.IsNullOrEmpty(instruction))
        {
            return text;
        }

        return instruction + "\n\n" + text;
    }
}
