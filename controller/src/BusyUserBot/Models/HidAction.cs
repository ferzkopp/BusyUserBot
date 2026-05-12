using System.Text.Json.Serialization;

namespace BusyUserBot.Models;

/// <summary>
/// One HID action. Mirrors the schema in docs/protocol.md and the JSON the AI is
/// instructed to emit, so the same record can be deserialised from the model's
/// output and re-serialised onto the wire to the dongle.
/// </summary>
public sealed record HidAction(
    [property: JsonPropertyName("type")]     string Type,
    [property: JsonPropertyName("x")]        int? X        = null,
    [property: JsonPropertyName("y")]        int? Y        = null,
    [property: JsonPropertyName("absolute")] bool? Absolute = null,
    [property: JsonPropertyName("button")]   string? Button = null,
    [property: JsonPropertyName("count")]    int? Count    = null,
    [property: JsonPropertyName("dy")]       int? Dy       = null,
    [property: JsonPropertyName("text")]     string? Text  = null,
    [property: JsonPropertyName("keys")]     string[]? Keys = null,
    [property: JsonPropertyName("ms")]       int? Ms       = null,
    // Controller-only metadata used by the iterative cursor-targeting stage.
    // Populated by the executor model on `move` actions (a short, specific
    // description of what to click, e.g. "the Start button on the taskbar",
    // "the close (X) button of the active File Explorer window"). Ignored
    // by the dongle's JSON parser since unknown fields are skipped.
    [property: JsonPropertyName("target")]   string? Target = null
);

public sealed record CommandRequest(
    [property: JsonPropertyName("actions")] IReadOnlyList<HidAction> Actions
);

/// <summary>What the AI must return on every turn.</summary>
public sealed record AiDecision(
    [property: JsonPropertyName("reasoning")] string? Reasoning,
    [property: JsonPropertyName("actions")]   IReadOnlyList<HidAction> Actions,
    [property: JsonPropertyName("done")]      bool Done
);
