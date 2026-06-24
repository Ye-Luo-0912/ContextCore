using System.Globalization;
using System.Text;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>pgvector 文本格式工具。</summary>
public static class PostgresVectorFormat
{
    /// <summary>将浮点向量转换为 pgvector 可解析的文本字面量。</summary>
    public static string ToVectorLiteral(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var builder = new StringBuilder();
        builder.Append('[');
        for (var i = 0; i < vector.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        builder.Append(']');
        return builder.ToString();
    }
}