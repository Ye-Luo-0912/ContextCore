using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace ContextCore.Service;

/// <summary>Loads machine-private configuration from the current user's home directory.</summary>
public static class UserPrivateConfiguration
{
    public const string DirectoryName = ".contextcore";
    public const string JsonFileName = "secrets.json";
    public const string EnvFileName = ".env";
    public const string PrivateApiKeysSectionName = "PrivateApiKeys";

    /// <summary>Adds optional user-private JSON configuration and loads user-private environment variables.</summary>
    public static UserPrivateConfigurationStatus Load(ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var directoryPath = ResolveDirectoryPath();
        var jsonPath = Path.Combine(directoryPath, JsonFileName);
        var envPath = Path.Combine(directoryPath, EnvFileName);
        var loadedJsonApiKeys = LoadPrivateApiKeysFile(jsonPath, overwriteExisting: false);
        var loadedEnvironmentVariables = LoadEnvironmentFile(envPath, overwriteExisting: false);

        // 仅加载用户目录下的私有文件，仓库内 appsettings 继续只保存可提交的占位配置。
        configuration.AddJsonFile(jsonPath, optional: true, reloadOnChange: true);
        configuration.AddEnvironmentVariables();

        return new UserPrivateConfigurationStatus
        {
            DirectoryPath = directoryPath,
            JsonPath = jsonPath,
            JsonExists = File.Exists(jsonPath),
            EnvPath = envPath,
            EnvExists = File.Exists(envPath),
            LoadedJsonApiKeyCount = loadedJsonApiKeys,
            LoadedEnvironmentVariableCount = loadedEnvironmentVariables
        };
    }

    public static string ResolveDirectoryPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        return Path.Combine(userProfile, DirectoryName);
    }

    public static int LoadPrivateApiKeysFile(string jsonPath, bool overwriteExisting)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
        {
            return 0;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        if (!document.RootElement.TryGetProperty(PrivateApiKeysSectionName, out var apiKeys)
            || apiKeys.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var loaded = 0;
        foreach (var property in apiKeys.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name)
                || property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!overwriteExisting
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(property.Name)))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(property.Name, value);
            loaded++;
        }

        return loaded;
    }

    public static int LoadEnvironmentFile(string envPath, bool overwriteExisting)
    {
        if (string.IsNullOrWhiteSpace(envPath) || !File.Exists(envPath))
        {
            return 0;
        }

        var loaded = 0;
        foreach (var rawLine in File.ReadAllLines(envPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].TrimStart();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = Unquote(line[(separatorIndex + 1)..].Trim());
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!overwriteExisting
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, value);
            loaded++;
        }

        return loaded;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}

/// <summary>Describes which user-private configuration files were discovered at startup.</summary>
public sealed class UserPrivateConfigurationStatus
{
    public string DirectoryPath { get; init; } = string.Empty;

    public string JsonPath { get; init; } = string.Empty;

    public bool JsonExists { get; init; }

    public string EnvPath { get; init; } = string.Empty;

    public bool EnvExists { get; init; }

    public int LoadedJsonApiKeyCount { get; init; }

    public int LoadedEnvironmentVariableCount { get; init; }
}
