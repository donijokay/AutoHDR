using System.Text.Json;
using AutoHDR.Models;

namespace AutoHDR.Services;

/// <summary>
/// Loads/saves config from %AppData%\AutoHDR\config.json
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string ConfigDirectory { get; }
    public string ConfigPath { get; }

    public ConfigService()
    {
        ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AutoHDR");
        ConfigPath = Path.Combine(ConfigDirectory, "config.json");
    }

    public AppConfig Load()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            if (!File.Exists(ConfigPath))
            {
                var defaults = new AppConfig();
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            Normalize(config);
            return config;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        Normalize(config);
        Directory.CreateDirectory(ConfigDirectory);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    private static void Normalize(AppConfig config)
    {
        config.PollIntervalMs = Math.Clamp(config.PollIntervalMs, 500, 10000);
        config.FullscreenCoverageThreshold = Math.Clamp(config.FullscreenCoverageThreshold, 0.5, 1.0);
        config.Whitelist ??= new List<string>();
        config.ExtraExclusions ??= new List<string>();

        // Normalize exe names: strip .exe, trim
        config.Whitelist = config.Whitelist
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeExeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        config.ExtraExclusions = config.ExtraExclusions
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeExeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizeExeName(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }
}
