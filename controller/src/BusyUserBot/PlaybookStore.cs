using System.Text.Json;
using BusyUserBot.Models;

namespace BusyUserBot;

/// <summary>
/// Loads and saves the external playbook control file. Kept in JSON so users
/// can edit it in any text editor between runs and reload from the UI.
/// </summary>
internal static class PlaybookStore
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Default location for a brand-new install:
    /// <c>%APPDATA%\BusyUserBot\playbook.json</c>.
    /// </summary>
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "BusyUserBot", "playbook.json");

    public static Playbook Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Playbook path is empty.");
        if (!File.Exists(path))
            throw new FileNotFoundException("Playbook file not found.", path);

        var json = File.ReadAllText(path);
        var pb = JsonSerializer.Deserialize<Playbook>(json, Opts)
                 ?? throw new InvalidDataException("Playbook deserialised to null.");

        Normalise(pb);
        return pb;
    }

    public static void Save(string path, Playbook pb)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(pb, Opts));
    }

    private static void Normalise(Playbook pb)
    {
        pb.Constraints ??= new Constraints();
        pb.Constraints.ForbiddenText ??= new List<string>();
        pb.Constraints.ForbiddenKeyChords ??= new List<string[]>();
        pb.Scenarios ??= new List<string>();
        pb.Scenarios.RemoveAll(string.IsNullOrWhiteSpace);

        pb.ScenarioSampleSize = Clamp(pb.ScenarioSampleSize, 1, 10);

        pb.Planner ??= new PlannerConfig();
        pb.Planner.SystemPrompt ??= "";
        pb.Planner.MaxStepsPerPlan = Clamp(pb.Planner.MaxStepsPerPlan, 1, 20);

        pb.Validator ??= new ValidatorConfig();
        pb.Validator.SystemPrompt ??= "";
        pb.Validator.MaxRetries = Clamp(pb.Validator.MaxRetries, 0, 5);

        pb.Executor ??= new ExecutorConfig();
        pb.Executor.SystemPrompt ??= "";
        pb.Executor.StepRetries = Clamp(pb.Executor.StepRetries, 1, 5);
        pb.Executor.StepDelayMs = Clamp(pb.Executor.StepDelayMs, 0, 10_000);
        pb.Executor.ExecutorTimeoutSeconds = Clamp(pb.Executor.ExecutorTimeoutSeconds, 10, 1800);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}
