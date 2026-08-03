using System.Text.RegularExpressions;

namespace ContextCore.Storage.Postgres.Infrastructure;

internal static partial class PostgresNames
{
    public static string Table(PostgresOptions options, string suffix)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!SafeIdentifier().IsMatch(options.TablePrefix) || !SafeIdentifier().IsMatch(suffix))
        {
            throw new InvalidOperationException("PostgreSQL 表名前缀只能包含字母、数字和下划线，且必须以字母或下划线开头。");
        }

        var tableName = options.TablePrefix + suffix;
        if (string.IsNullOrWhiteSpace(options.SchemaName))
        {
            return tableName;
        }

        if (!SafeIdentifier().IsMatch(options.SchemaName))
        {
            throw new InvalidOperationException("PostgreSQL schema 名称只能包含字母、数字和下划线，且必须以字母或下划线开头。");
        }

        return $"{options.SchemaName}.{tableName}";
    }

    public static string Index(PostgresOptions options, string tableSuffix, string indexSuffix)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!SafeIdentifier().IsMatch(options.TablePrefix) ||
            !SafeIdentifier().IsMatch(tableSuffix) ||
            !SafeIdentifier().IsMatch(indexSuffix))
        {
            throw new InvalidOperationException("PostgreSQL 索引名称只能包含字母、数字和下划线，且必须以字母或下划线开头。");
        }

        return $"ix_{options.TablePrefix}{tableSuffix}_{indexSuffix}";
    }

    public static string QualifiedIndex(PostgresOptions options, string tableSuffix, string indexSuffix)
    {
        var indexName = Index(options, tableSuffix, indexSuffix);
        if (string.IsNullOrWhiteSpace(options.SchemaName))
        {
            return indexName;
        }

        if (!SafeIdentifier().IsMatch(options.SchemaName))
        {
            throw new InvalidOperationException("PostgreSQL schema 名称只能包含字母、数字和下划线，且必须以字母或下划线开头。");
        }

        return $"{options.SchemaName}.{indexName}";
    }

    public static string Constraint(PostgresOptions options, string tableSuffix, string constraintSuffix)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!SafeIdentifier().IsMatch(options.TablePrefix) ||
            !SafeIdentifier().IsMatch(tableSuffix) ||
            !SafeIdentifier().IsMatch(constraintSuffix))
        {
            throw new InvalidOperationException("PostgreSQL 约束名称只能包含字母、数字和下划线，且必须以字母或下划线开头。");
        }

        return $"{options.TablePrefix}{tableSuffix}_{constraintSuffix}";
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
}
