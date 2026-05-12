using System.Text.Json;
using BusyUserBot.Models;

namespace BusyUserBot;

internal static class ConfigStore
{
    private const string LegacyLmStudioModel = "qwen2.5-vl-7b-instruct";

    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "BusyUserBot", "settings.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
            {
                var json = File.ReadAllText(DefaultPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Opts) ?? new AppConfig();
                if (ApplyMigrations(cfg))
                    Save(cfg);
                return cfg;
            }
        }
        catch
        {
            // fall through to default
        }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
        File.WriteAllText(DefaultPath, JsonSerializer.Serialize(cfg, Opts));
    }

    private static bool ApplyMigrations(AppConfig cfg)
    {
        if (cfg.Ai.Engine != AiEngineKind.LMStudio) return false;

        if (string.IsNullOrWhiteSpace(cfg.Ai.Model) ||
            string.Equals(cfg.Ai.Model, LegacyLmStudioModel, StringComparison.OrdinalIgnoreCase))
        {
            cfg.Ai.Model = AiConfig.DefaultLmStudioModel;
            return true;
        }

        return false;
    }
}
