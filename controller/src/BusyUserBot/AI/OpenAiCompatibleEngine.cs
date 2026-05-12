using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BusyUserBot.Models;

namespace BusyUserBot.AI;

/// <summary>
/// Talks to any OpenAI-compatible Chat Completions endpoint with vision support.
/// LM Studio (local) and Azure OpenAI (with a small URL/headers tweak) both fit.
/// </summary>
public sealed class OpenAiCompatibleEngine : IAiEngine, IDisposable
{
    private readonly HttpClient _http;
    private readonly AiConfig _cfg;
    private readonly string _chatPath;
    private readonly Action<string>? _log;

    public OpenAiCompatibleEngine(AiConfig cfg, Action<string>? log = null)
    {
        _cfg = cfg;
        _log = log;
        // Use an infinite HttpClient timeout and rely on the per-call
        // CancellationToken (driven by LoopConfig.AiTimeoutSeconds) instead.
        // Otherwise this hard cap silently overrides the user's setting and
        // can kill slow / reasoning-heavy generations mid-flight.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        if (cfg.Engine == AiEngineKind.AzureOpenAI)
        {
            // Azure: https://<resource>.openai.azure.com/openai/deployments/<dep>/chat/completions?api-version=...
            _http.BaseAddress = new Uri(cfg.Endpoint.TrimEnd('/') + "/");
            _chatPath = $"openai/deployments/{cfg.AzureDeployment}/chat/completions?api-version={cfg.AzureApiVersion}";
            if (!string.IsNullOrEmpty(cfg.ApiKey))
                _http.DefaultRequestHeaders.Add("api-key", cfg.ApiKey);
        }
        else
        {
            // LM Studio / vanilla OpenAI: <endpoint>/chat/completions
            _http.BaseAddress = new Uri(cfg.Endpoint.TrimEnd('/') + "/");
            _chatPath = "chat/completions";
            if (!string.IsNullOrEmpty(cfg.ApiKey))
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        }
    }

    public async Task<AiDecision> GenerateActionsAsync(
        string systemPrompt, string userPrompt,
        ScreenCapture.Capture screenshot, CancellationToken ct)
    {
        var raw = await ChatAsync(systemPrompt, userPrompt, screenshot, ct).ConfigureAwait(false);
        return CommandParser.Parse(raw);
    }

    public async Task<AiValidation> ValidateAsync(
        string systemPrompt, string userPrompt,
        ScreenCapture.Capture screenshot, CancellationToken ct)
    {
        var raw = await ChatAsync(systemPrompt, userPrompt, screenshot, ct).ConfigureAwait(false);
        return CommandParser.ParseValidation(raw);
    }

    public async Task<string> ChatAsync(
        string systemPrompt, string userPrompt,
        ScreenCapture.Capture screenshot, CancellationToken ct)
    {
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(screenshot.PngBytes);

        var requestBody = new JsonObject
        {
            ["model"] = _cfg.Engine == AiEngineKind.AzureOpenAI ? null : _cfg.Model,
            ["temperature"] = 0.0,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = systemPrompt,
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = userPrompt,
                        },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = dataUrl },
                        },
                    },
                },
            },
        };

        if (requestBody["model"] is null) requestBody.Remove("model");

        // Azure OpenAI honours response_format=json_object; LM Studio doesn't,
        // so we still rely on CommandParser to peel JSON out of any reply.
        if (_cfg.Engine == AiEngineKind.AzureOpenAI)
            requestBody["response_format"] = new JsonObject { ["type"] = "json_object" };

        // Reasoning / thinking control. Different model families speak
        // different dialects (Nemotron: on/off only; Qwen3/OpenAI: low/medium/
        // high; Qwen3/GLM/DeepSeek templates: enable_thinking bool). We send
        // both knobs when applicable; servers that don't recognise one
        // typically just warn and ignore it.
        ApplyReasoningSettings(requestBody);

        using var resp = await _http.PostAsJsonAsync(_chatPath, requestBody, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 1000) body = body[..1000] + "\u2026";
            throw new HttpRequestException(
                $"AI endpoint returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("empty response from AI");

        var content = doc["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                      ?? throw new InvalidOperationException("no content in AI response");

        if (_log is not null)
        {
            var trimmed = content.Length > 2000 ? content[..2000] + "\u2026" : content;
            _log("AI raw: " + trimmed.Replace("\r", "").Replace("\n", " \u21B5 "));
        }

        return content;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Translate <see cref="AiConfig.ReasoningEffort"/> into the request
    /// body. We only emit reasoning fields when the user explicitly opted in
    /// to a non-default mode. Empty / "default" / "auto" sends nothing.
    /// </summary>
    private void ApplyReasoningSettings(JsonObject requestBody)
    {
        var raw = (_cfg.ReasoningEffort ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0 || raw == "default" || raw == "auto") return;

        switch (raw)
        {
            case "off":
                // Nemotron understands "off" as a reasoning_effort value.
                // Qwen3 / GLM / DeepSeek templates honour enable_thinking.
                requestBody["reasoning_effort"] = "off";
                requestBody["chat_template_kwargs"] = new JsonObject
                {
                    ["enable_thinking"] = false,
                };
                break;

            case "on":
                requestBody["reasoning_effort"] = "on";
                requestBody["chat_template_kwargs"] = new JsonObject
                {
                    ["enable_thinking"] = true,
                };
                break;

            // OpenAI / Qwen3-style graded effort. Sending enable_thinking=true
            // alongside is harmless for templates that ignore it and required
            // for Qwen3 templates that gate reasoning behind it.
            case "minimal":
            case "low":
            case "medium":
            case "high":
                requestBody["reasoning_effort"] = raw;
                requestBody["chat_template_kwargs"] = new JsonObject
                {
                    ["enable_thinking"] = true,
                };
                break;

            default:
                // Unknown vocabulary — forward verbatim and let the server warn.
                requestBody["reasoning_effort"] = raw;
                break;
        }
    }
}
