using BusyUserBot.Models;

namespace BusyUserBot.AI;

/// <summary>
/// Hard-coded scripted "AI" used by --fake-ai. Lets you run the full
/// Planner → Validator → Executor pipeline without an LLM. Returns canned
/// JSON for plans and verdicts, and scripted HID actions for the Executor.
/// </summary>
public sealed class FakeAiEngine : IAiEngine
{
    public Task<string> ChatAsync(
        string systemPrompt, string userPrompt,
        ScreenCapture.Capture screenshot, CancellationToken ct)
    {
        // Plain substring detection of the prompt mode.
        if (Contains(userPrompt, "MODE: PLAN"))
        {
            const string plan = """
{
  "goal": "Open Notepad and type a short note",
  "steps": [
    { "description": "Press ESC to dismiss any popup, then Win+D to show the desktop.",
      "successCriteria": "The Windows desktop is visible with no foreground app." },
    { "description": "Open Start, type 'notepad', press Enter.",
      "successCriteria": "A Notepad window is in the foreground with an empty document." },
    { "description": "Type 'hello world'.",
      "successCriteria": "Notepad shows the text 'hello world'." }
  ]
}
""";
            return Task.FromResult(plan);
        }

        if (Contains(userPrompt, "MODE: VALIDATE_PLAN"))
        {
            const string verdict = """
{ "approved": true, "reasons": ["fake validator: no destructive actions detected"] }
""";
            return Task.FromResult(verdict);
        }

        if (Contains(userPrompt, "MODE: VALIDATION"))
        {
            const string ok = """
{ "success": true, "reason": "fake engine assumes success" }
""";
            return Task.FromResult(ok);
        }

        // ACTION mode default — single noop wait.
        const string noop = """
{ "reasoning": "fake noop", "actions": [ {"type":"wait","ms":250} ], "done": false }
""";
        return Task.FromResult(noop);
    }

    public Task<AiDecision> GenerateActionsAsync(
        string systemPrompt, string userPrompt,
        ScreenCapture.Capture screenshot, CancellationToken ct)
    {
        // Scripted reply derived from substring matches on the user prompt.
        AiDecision d;
        if (Contains(userPrompt, "win+d") || Contains(userPrompt, "show the desktop"))
            d = new("show desktop",
                    new[] { new HidAction("key", Keys: new[] { "GUI", "D" }) }, false);
        else if (Contains(userPrompt, "esc") && Contains(userPrompt, "dismiss"))
            d = new("dismiss popup",
                    new[] { new HidAction("key", Keys: new[] { "ESC" }) }, false);
        else if (Contains(userPrompt, "notepad") && (Contains(userPrompt, "open start") || Contains(userPrompt, "press enter")))
            d = new("launch notepad",
                    new[]
                    {
                        new HidAction("key", Keys: new[] { "GUI" }),
                        new HidAction("type", Text: "notepad"),
                        new HidAction("key", Keys: new[] { "ENTER" }),
                    }, false);
        else if (Contains(userPrompt, "type 'hello world'") || (Contains(userPrompt, "hello world") && Contains(userPrompt, "type")))
            d = new("type hello world",
                    new[] { new HidAction("type", Text: "hello world") }, false);
        else if (Contains(userPrompt, "type"))
            d = new("type some text",
                    new[] { new HidAction("type", Text: "hello") }, false);
        else
            d = new("noop wait",
                    new[] { new HidAction("wait", Ms: 250) }, false);

        return Task.FromResult(d);
    }

    public Task<AiValidation> ValidateAsync(
        string systemPrompt, string userPrompt,
        ScreenCapture.Capture screenshot, CancellationToken ct)
    {
        return Task.FromResult(new AiValidation(true, "fake engine assumes success"));
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
