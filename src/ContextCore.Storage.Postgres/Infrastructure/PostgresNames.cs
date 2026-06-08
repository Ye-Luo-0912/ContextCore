using System.Text.RegularExpressions;

namespace ContextCore.Storage.Postgres;

internal static partial class PostgresNames
{
    public static string Table(PostgresOptions options, string suffix)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!SafeIdentifier().IsMatch(options.TablePrefix) || !SafeIdentifier().IsMatch(suffix))
        {
            throw new InvalidOperationException("PostgreSQL 表名前缀只能包含字母、数字和下划线，且必须以字母或下划线开头。");
        }

        return options.TablePrefix + suffix;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
}