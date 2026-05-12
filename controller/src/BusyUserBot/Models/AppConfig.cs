using System.Text.Json.Serialization;

namespace BusyUserBot.Models;

public sealed class AppConfig
{
    public DongleConfig Dongle { get; set; } = new();
    public AiConfig Ai { get; set; } = new();
    public LoopConfig Loop { get; set; } = new();
}

public sealed class DongleConfig
{
    /// <summary>
    /// BLE name advertised by the dongle (matches DEVICE_NAME in firmware
    /// secrets.h). Used for first-time discovery if <see cref="DeviceId"/>
    /// is empty.
    /// </summary>
    public string Name { get; set; } = "BusyUserBot";

    /// <summary>
    /// Cached WinRT BluetoothLEDevice.DeviceId (e.g.
    /// "BluetoothLE#BluetoothLE..."). Populated by the controller after the
    /// first successful connection so subsequent runs don't have to scan.
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Shared token; must match DEVICE_TOKEN in firmware secrets.h.</summary>
    public string Token { get; set; } = "";
}

public enum AiEngineKind { LMStudio, AzureOpenAI, Fake }

public sealed class AiConfig
{
    public const string DefaultLmStudioModel = "lmstudio-community/Qwen3.5-9B-GGUF";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AiEngineKind Engine { get; set; } = AiEngineKind.LMStudio;

    public string Endpoint { get; set; } = "http://127.0.0.1:1234/v1";
    public string Model { get; set; } = DefaultLmStudioModel;
    public string ApiKey { get; set; } = "";
    public string AzureDeployment { get; set; } = "";
    public string AzureApiVersion { get; set; } = "2024-10-21";

    /// <summary>
    /// Reasoning / thinking budget for hybrid models. Different vendors accept
    /// different vocabularies, so this is a free-form string rather than an
    /// enum:
    ///   <list type="bullet">
    ///     <item><c>""</c> or <c>"default"</c> &#8211; send no reasoning hint; let the model do whatever its template defaults to.</item>
    ///     <item><c>"off"</c> &#8211; suppress reasoning (Nemotron-style + sets <c>enable_thinking=false</c> for Qwen3/GLM/DeepSeek templates).</item>
    ///     <item><c>"on"</c> &#8211; force reasoning on (Nemotron-style + sets <c>enable_thinking=true</c>).</item>
    ///     <item><c>"minimal"</c> / <c>"low"</c> / <c>"medium"</c> / <c>"high"</c> &#8211; OpenAI / Qwen3-style reasoning_effort levels.</item>
    ///   </list>
    /// Defaulting to <c>"off"</c> avoids the Qwen3-9B "thinks for the entire
    /// 120 s budget and never emits the JSON plan" failure mode we hit
    /// previously.
    /// </summary>
    public string ReasoningEffort { get; set; } = "off";
}

public sealed class LoopConfig
{
    /// <summary>
    /// Path to the external playbook control file (see
    /// <see cref="Playbook"/> and docs/control-flow.md). When empty, the
    /// controller falls back to <see cref="PlaybookStore.DefaultPath"/>.
    /// </summary>
    public string PlaybookPath { get; set; } = "";

    /// <summary>Idle time between consecutive plan iterations.</summary>
    public int IntervalMs { get; set; } = 1500;

    /// <summary>Hard cap on total iterations (a safety stop).</summary>
    public int MaxIterations { get; set; } = 50;

    /// <summary>
    /// Timeout budget for each AI request (planner/validator/executor and
    /// per-step validation), in seconds.
    /// </summary>
    public int AiTimeoutSeconds { get; set; } = 60;
}
