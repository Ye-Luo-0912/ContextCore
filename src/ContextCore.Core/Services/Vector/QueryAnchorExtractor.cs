using System.Globalization;
using System.Text;

namespace ContextCore.Core.Services;

/// <summary>
/// P3-01：从文本中提取锚点关键词的纯文本处理工具。
/// 从 VectorMissSetRepresentationAuditRunner.ExtractAnchors 提取到 Core，
/// 解除 Core 对 Evaluation 的唯一引用，使 Evaluation 代码可安全移出。
/// </summary>
public static class QueryAnchorExtractor
{
    public static IReadOnlyList<string> ExtractAnchors(string text, int maxAnchors = 24)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in EnumerateTokens(text))
        {
            if (token.Length < 2)
            {
                continue;
            }

            counts[token] = counts.GetValueOrDefault(token) + 1;
        }

        return counts
            .OrderByDescending(item => item.Value)
            .ThenByDescending(item => item.Key.Length)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxAnchors))
            .Select(item => item.Key)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateTokens(string text)
    {
        var current = new StringBuilder();
        CharacterClass currentClass = CharacterClass.Other;
        foreach (var rune in text.EnumerateRunes())
        {
            var nextClass = Classify(rune);
            if (nextClass == CharacterClass.Other)
            {
                foreach (var token in Flush(current, currentClass))
                {
                    yield return token;
                }

                currentClass = CharacterClass.Other;
                continue;
            }

            if (current.Length > 0 && nextClass != currentClass)
            {
                foreach (var token in Flush(current, currentClass))
                {
                    yield return token;
                }

                currentClass = CharacterClass.Other;
            }

            currentClass = nextClass;
            current.Append(rune.ToString().ToLowerInvariant());
        }

        foreach (var token in Flush(current, currentClass))
        {
            yield return token;
        }

        IEnumerable<string> Flush(StringBuilder buffer, CharacterClass characterClass)
        {
            if (buffer.Length == 0)
            {
                yield break;
            }

            var value = buffer.ToString();
            buffer.Clear();
            if (characterClass == CharacterClass.Cjk)
            {
                foreach (var token in EnumerateCjkNgrams(value))
                {
                    yield return token;
                }
            }
            else if (value.Length >= 3)
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> EnumerateCjkNgrams(string value)
    {
        if (value.Length <= 4)
        {
            yield return value;
            yield break;
        }

        for (var i = 0; i + 2 <= value.Length; i++)
        {
            yield return value.Substring(i, 2);
        }

        for (var i = 0; i + 3 <= value.Length; i++)
        {
            yield return value.Substring(i, 3);
        }
    }

    private static CharacterClass Classify(Rune rune)
    {
        if (IsCjk(rune))
        {
            return CharacterClass.Cjk;
        }

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.LowercaseLetter
            or UnicodeCategory.UppercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            ? CharacterClass.AlphaNumeric
            : CharacterClass.Other;
    }

    private static bool IsCjk(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2A6DF
            or >= 0x2A700 and <= 0x2B73F
            or >= 0x2B740 and <= 0x2B81F
            or >= 0x2B820 and <= 0x2CEAF;
    }

    private enum CharacterClass
    {
        Other,
        AlphaNumeric,
        Cjk
    }
}
