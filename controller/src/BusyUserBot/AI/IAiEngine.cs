using BusyUserBot.Models;

namespace BusyUserBot.AI;

/// <summary>
/// Stateless transport over a vision-capable chat model. The controller
/// builds the prompts (system + user) per turn and asks the engine for
/// either a set of HID actions (<see cref="GenerateActionsAsync"/>) or a
/// success/failure judgement on a screenshot
/// (<see cref="ValidateAsync"/>).
/// </summary>
public interface IAiEngine
{
    /// <summary>
    /// Generic chat call: send a system + user prompt plus a screenshot and
    /// return the raw model reply. Used by Planner and Validator stages.
    /// </summary>
    Task<string> ChatAsync(
        string systemPrompt,
        string userPrompt,
        ScreenCapture.Capture screenshot,
        CancellationToken ct);

    Task<AiDecision> GenerateActionsAsync(
        string systemPrompt,
        string userPrompt,
        ScreenCapture.Capture screenshot,
        CancellationToken ct);

    Task<AiValidation> ValidateAsync(
        string systemPrompt,
        string userPrompt,
        ScreenCapture.Capture screenshot,
        CancellationToken ct);
}
