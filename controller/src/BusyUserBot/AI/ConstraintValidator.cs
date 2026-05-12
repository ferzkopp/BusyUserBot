using BusyUserBot.Models;

namespace BusyUserBot.AI;

/// <summary>
/// Enforces the safety rails declared in the playbook. Constraints stack:
/// global → task → action; an action proposed by the AI must clear all of
/// them or the controller refuses to send it to the dongle.
/// </summary>
internal static class ConstraintValidator
{
    /// <summary>Combine a stack of constraints into one effective set.</summary>
    public static Constraints Merge(params Constraints?[] layers)
    {
        var merged = new Constraints();
        foreach (var l in layers)
        {
            if (l is null) continue;
            merged.ForbiddenText.AddRange(l.ForbiddenText);
            merged.ForbiddenKeyChords.AddRange(l.ForbiddenKeyChords);
        }
        return merged;
    }

    /// <summary>
    /// Returns null when every action is allowed; otherwise a human-readable
    /// reason describing the first violation.
    /// </summary>
    public static string? FindViolation(IReadOnlyList<HidAction> actions, Constraints rules)
    {
        if (rules.IsEmpty) return null;

        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            var why = Check(a, rules);
            if (why is not null) return $"action[{i}] ({a.Type}): {why}";
        }
        return null;
    }

    private static string? Check(HidAction a, Constraints rules)
    {
        switch (a.Type?.ToLowerInvariant())
        {
            case "type":
                if (a.Text is { Length: > 0 } text)
                {
                    foreach (var bad in rules.ForbiddenText)
                    {
                        if (string.IsNullOrEmpty(bad)) continue;
                        if (text.Contains(bad, StringComparison.OrdinalIgnoreCase))
                            return $"text contains forbidden substring '{bad}'";
                    }
                }
                break;

            case "move":
            case "click":
            case "down":
            case "up":
                // No coordinate-based constraints. Coordinates are scaled
                // and clamped to screen bounds before reaching this point.
                break;

            case "key":
                if (a.Keys is { Length: > 0 } keys)
                {
                    var lowered = keys.Select(NormaliseKeyToken).ToHashSet();
                    foreach (var chord in rules.ForbiddenKeyChords)
                    {
                        if (chord is null || chord.Length == 0) continue;
                        var needed = chord.Select(NormaliseKeyToken).ToArray();
                        if (needed.All(k => lowered.Contains(k)))
                            return $"key chord [{string.Join("+", needed)}] is forbidden";
                    }
                }
                break;
        }
        return null;
    }

    private static string NormaliseKeyToken(string? token)
    {
        var u = (token ?? "").Trim().ToUpperInvariant();
        return u switch
        {
            "WIN" or "WINDOWS" or "CMD" => "GUI",
            _ => u,
        };
    }
}
