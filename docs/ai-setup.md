# AI engine setup

The controller runs three AI calls per iteration of the main loop — a
**Planner**, a **Validator** and an **Executor** — all against the same
chat endpoint. See [control-flow.md](control-flow.md) for what each stage
does. This document covers how to provide that endpoint.

Two backends are supported out of the box:

1. **LM Studio** running locally (default).
2. **Azure OpenAI** (or any OpenAI-compatible endpoint with vision support).

Both speak the OpenAI Chat Completions API, so the controller has a single
`IAiEngine` implementation in
[`OpenAiCompatibleEngine.cs`](../controller/src/BusyUserBot/AI/OpenAiCompatibleEngine.cs)
that just changes the base URL and authentication style.

> **Vision is required.** Every prompt sent by the controller embeds the
> current screenshot as an inline `image_url`. Text-only models will not
> work.

## Quick start

| Backend       | When to use it                                   | Setup time |
| ------------- | ------------------------------------------------ | ---------- |
| LM Studio     | Local-only, no per-call cost, full privacy.      | ~10 min + model download |
| Azure OpenAI  | Strongest models, no local GPU needed.           | ~5 min if you already have a resource |

The controller's **Test AI** button runs one Planner + Validator round-trip
against the configured endpoint and logs the parsed result, so you can
sanity-check any setup without flashing the dongle.

---

## Local — LM Studio

[LM Studio](https://lmstudio.ai/) is a free desktop app that downloads
GGUF / MLX models and exposes them through an OpenAI-compatible HTTP
server. It runs natively on:

- **x64 Windows** with NVIDIA, AMD, or Intel discrete GPUs (via CUDA, ROCm,
  Vulkan or DirectML runtimes that LM Studio bundles).
- **ARM64 Windows** (Snapdragon X / Copilot+ PCs) using the bundled
  Snapdragon-optimised runtime.
- **CPU-only** machines — slower, but functional for the smaller models
  in the table below.

### Choosing a model

The controller sends short text prompts plus a single full-screen
screenshot per call. Latency matters: each loop iteration runs three AI
calls back-to-back (Planner, Validator, Executor) and one Executor +
validation pair per plan step. Aim for end-to-end response times of a few
seconds per call; that constrains model size to whatever fits in fast
memory (VRAM if you have a GPU, otherwise system RAM).

Pick the highest tier your hardware comfortably fits, then leave 2–4 GB of
headroom for the OS, the controller, the browser the bot might open, etc.

| Tier | Hardware budget | Recommended model | Approx. download | LM Studio settings |
| ---- | --------------- | ----------------- | ---------------- | ------------------ |
| **S — Lightweight** | 8 GB VRAM **or** 16 GB system RAM | `lmstudio-community/Qwen2.5-VL-7B-Instruct-GGUF` (Q4_K_M) | ~5 GB | `n_ctx` 8192 · GPU offload: all layers if VRAM ≥ 6 GB · flash attention on |
| **M — Mainstream (recommended default)** | 12 GB VRAM **or** 32 GB system RAM | `lmstudio-community/Qwen3.5-9B-GGUF` (Q4_K_M) | ~7 GB | `n_ctx` 8192–16384 · GPU offload: all layers · flash attention on |
| **L — Large GPU** | 16–24 GB VRAM | `lmstudio-community/Qwen2.5-VL-32B-Instruct-GGUF` (Q4_K_M) **or** `lmstudio-community/Llama-3.2-11B-Vision-Instruct-GGUF` (Q5_K_M) | ~18 GB / ~9 GB | `n_ctx` 16384 · GPU offload: all layers · flash attention on |
| **XL — Workstation GPU** | ≥32 GB VRAM (or ≥48 GB unified memory) | `lmstudio-community/Qwen2.5-VL-72B-Instruct-GGUF` (Q4_K_M) | ~40 GB | `n_ctx` 16384–32768 · GPU offload: all layers · flash attention on |
| **CPU-only fallback** | Any modern CPU, ≥16 GB RAM, no GPU | `lmstudio-community/Qwen2.5-VL-3B-Instruct-GGUF` (Q4_K_M) | ~2 GB | `n_ctx` 4096 · GPU offload: 0 · flash attention on |

Notes:

- **GGUF Q4_K_M** is a good "smallest acceptable quality" sweet spot.
  Stepping up to Q5 or Q6 gains a little quality at a 15–25 % size cost;
  drop to Q3 only if Q4 doesn't fit.
- **Flash attention** in LM Studio's per-model loader cuts VRAM and
  speeds up generation; leave it on unless a specific model misbehaves.
- **Context length (`n_ctx`)**: each call sends ~1–2 KB of text plus one
  screenshot encoded as a few hundred image tokens. 8192 is plenty for the
  M-tier defaults; only raise it if you set very long custom prompts.
- **Avoid 70 B+ models on anything below the XL tier** — they will load
  but each loop iteration will take tens of seconds, defeating the
  "busy user" feel.

ARM64 (Snapdragon X) machines should pick the **M tier** as the default.
The Snapdragon NPU is used automatically by LM Studio's ARM runtime on
supported models; the 9 B class typically responds in 2–4 s per call.

### Steps

1. Install [LM Studio](https://lmstudio.ai/). On ARM64 Windows pick the
   ARM build; on x64 Windows pick the x64 build.
2. **Discover** tab → search for the model from the table that matches
   your hardware tier → **Download**.
3. **My Models** → click the downloaded model → set **Context length**,
   **GPU offload** and **Flash attention** as suggested above. Save.
4. **Developer** tab (LM Studio 0.3+) or **Local Server** tab (older
   versions) → load the model → **Start Server**.
5. Note the listening URL (default `http://127.0.0.1:1234/v1`) and the
   exact "model identifier" string LM Studio shows for the loaded model
   (it usually mirrors the Hugging Face slug).
6. In the controller, set:
   - **Engine:** `LMStudio`
   - **Endpoint:** `http://127.0.0.1:1234/v1`
   - **Model:** the exact identifier from step 5 (e.g.
     `lmstudio-community/Qwen3.5-9B-GGUF`). You can also click
     **Refresh models** in the controller to pull the live list from the
     server and pick from a dropdown.
   - **API key:** leave empty.
7. Click **Test AI** in the controller. Expect a log line with a goal,
   2–6 plan steps, and `validator approved=true` within a few seconds.

Manual sanity check from a terminal:

```powershell
curl http://127.0.0.1:1234/v1/models
```

The response should include the model id you loaded.

### Bootstrap helper

`scripts/dev-env-setup.ps1 -InstallLMStudio` installs LM Studio via winget
and uses the bundled `lms` CLI to download the M-tier default model
automatically. Override the model with `-LMStudioModel <id>` or skip the
download with `-LMStudioModel ''`. See
[scripts.md](scripts.md#dev-env-setupps1--bootstrap-the-dev-environment).

---

## Cloud — Azure OpenAI

1. Create an Azure OpenAI resource and deploy a **vision-capable** model
   (e.g. `gpt-4o`, `gpt-4o-mini`, `gpt-4.1-mini`, `gpt-4.1`).
2. In the controller, set:
   - **Engine:** `AzureOpenAI`
   - **Endpoint:** `https://<your-resource>.openai.azure.com/`
   - **Azure deployment:** the *deployment name* you chose, **not** the
     model name.
   - **API key:** from the resource's *Keys & Endpoint* blade. Leave
     empty if you've configured Entra ID and modify the engine accordingly.
   - **API version:** `2024-10-21` or newer.

Any OpenAI-compatible endpoint with vision support (OpenRouter, vLLM,
text-generation-webui in OpenAI mode, …) can be used by setting
**Engine: `LMStudio`** and pointing the endpoint and model fields at it.
The "LMStudio" label is just the controller's label for "OpenAI-compatible
chat completions, no Azure deployment routing".

---

## Prompt contract

The three stages each send a `system` + `user` chat message plus the
screenshot, and parse a strict JSON reply. The exact schemas (plan,
validator verdict, executor action, per-step validation) and the default
system prompts are defined in
[`Prompts.cs`](../controller/src/BusyUserBot/AI/Prompts.cs) and documented
in [control-flow.md](control-flow.md).

You can override any of the three default prompts from the playbook
(`planner.systemPrompt`, `validator.systemPrompt`,
`executor.systemPrompt`) without recompiling. The controller always
appends a generated summary of the active hard constraints to whichever
system prompt is in effect, so the model sees the same safety rules the
controller will enforce.
