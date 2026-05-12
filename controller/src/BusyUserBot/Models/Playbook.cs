using System.Text.Json.Serialization;

namespace BusyUserBot.Models;

/// <summary>
/// External, user-editable control file describing the open-loop
/// "busy user" simulation. Each iteration the bot picks a random subset of
/// scenarios, asks the Planner to turn one into a small goal + steps, asks
/// the Validator to gate the plan for safety, then asks the Executor to
/// turn each step into HID actions one screenshot at a time.
/// See docs/control-flow.md.
/// </summary>
public sealed class Playbook
{
    /// <summary>
    /// Short scenario seeds, e.g. "Browse a news website for a minute".
    /// One iteration of the bot picks a random subset of these and asks
    /// the Planner to pick (or blend) one.
    /// </summary>
    public List<string> Scenarios { get; set; } = new();

    /// <summary>How many scenarios to show the Planner per iteration. Clamped 1..10.</summary>
    public int ScenarioSampleSize { get; set; } = 3;

    /// <summary>Constraints applied to every HID action the Executor produces.</summary>
    public Constraints Constraints { get; set; } = new();

    public PlannerConfig Planner { get; set; } = new();
    public ValidatorConfig Validator { get; set; } = new();
    public ExecutorConfig Executor { get; set; } = new();
}

/// <summary>Planner-stage configuration.</summary>
public sealed class PlannerConfig
{
    /// <summary>
    /// System prompt sent to the Planner. When empty, the built-in default
    /// in <see cref="AI.Prompts.DefaultPlannerSystem"/> is used.
    /// </summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>Hard cap on number of steps in a plan. Clamped 1..20.</summary>
    public int MaxStepsPerPlan { get; set; } = 8;
}

/// <summary>Validator-stage configuration.</summary>
public sealed class ValidatorConfig
{
    /// <summary>
    /// System prompt sent to the Validator. When empty, the built-in default
    /// in <see cref="AI.Prompts.DefaultValidatorSystem"/> is used.
    /// </summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>
    /// How many additional planner→validator cycles to try if the validator
    /// rejects the first plan. 0 means "no retry". Clamped 0..5.
    /// </summary>
    public int MaxRetries { get; set; } = 2;
}

/// <summary>Executor-stage configuration.</summary>
public sealed class ExecutorConfig
{
    /// <summary>
    /// System prompt sent to the Executor. When empty, the built-in default
    /// in <see cref="AI.Prompts.DefaultExecutorSystem"/> is used.
    /// </summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>How many attempts per step. Clamped 1..5.</summary>
    public int StepRetries { get; set; } = 2;

    /// <summary>Settle time before validation screenshot. Clamped 0..10000ms.</summary>
    public int StepDelayMs { get; set; } = 500;

    /// <summary>
    /// Wall-clock budget for executing one whole plan (all steps). Once
    /// elapsed the executor stops and the loop moves on. Clamped 10..1800s.
    /// </summary>
    public int ExecutorTimeoutSeconds { get; set; } = 180;
}

/// <summary>
/// Safety rails enforced by the controller before any HID command is sent to
/// the dongle. Empty fields mean "no constraint at this level".
/// </summary>
public sealed class Constraints
{
    /// <summary>Substrings that must never appear (case-insensitively) in a "type" action's text.</summary>
    public List<string> ForbiddenText { get; set; } = new();

    /// <summary>Key chords that may never be sent. Order- and case-insensitive.</summary>
    public List<string[]> ForbiddenKeyChords { get; set; } = new();

    public bool IsEmpty =>
        ForbiddenText.Count == 0
        && ForbiddenKeyChords.Count == 0;
}

/// <summary>One step inside a plan produced by the Planner.</summary>
public sealed class PlanStep
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// What the screen should look like after the step. Used by the
    /// Executor to validate per-step success. May be empty in which case
    /// the step is accepted blindly.
    /// </summary>
    [JsonPropertyName("successCriteria")]
    public string SuccessCriteria { get; set; } = "";
}

/// <summary>Output of the Planner stage.</summary>
public sealed class PlanOutput
{
    [JsonPropertyName("goal")]
    public string Goal { get; set; } = "";

    [JsonPropertyName("steps")]
    public List<PlanStep> Steps { get; set; } = new();
}

/// <summary>Output of the Validator stage.</summary>
public sealed class ValidatorVerdict
{
    [JsonPropertyName("approved")]
    public bool Approved { get; set; }

    [JsonPropertyName("reasons")]
    public List<string> Reasons { get; set; } = new();
}

/// <summary>Result of validating that a screenshot reflects a step's success criterion.</summary>
public sealed record AiValidation(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("reason")] string? Reason
);
