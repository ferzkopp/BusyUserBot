# Control Flow & Playbook Architecture

## Overview

BusyUserBot drives a Windows desktop with a Bluetooth-controlled HID
dongle. Instead of executing pre-authored task scripts, the controller
runs an **open-loop "busy user" simulation** built from three AI stages:

1. **Planner** — picks one small goal from a random subset of seeded
   scenarios and proposes a few steps to reach it.
2. **Validator** — inspects the proposed plan and rejects anything that
   would be destructive (file/disk deletions), shut down or reboot the
   PC, lock or sign out the session, or otherwise prevent the bot from
   continuing to run (e.g. closing the controller, killing the AI
   server).
3. **Executor** — turns each plan step into HID actions one screenshot at
   a time, with a per-step success check.

A **playbook** (JSON) configures the loop:

- A list of scenario seeds (e.g. *"open Settings and look at the System
  page"*).
- Global safety constraints (forbidden text, forbidden key chords).
- A `systemPrompt` and a few knobs for each of the three stages.

Related docs: [ai-setup.md](ai-setup.md) (model recommendations and
backend setup), [protocol.md](protocol.md) (HID action schema sent to the
dongle), [controller-setup.md](controller-setup.md) (how to point the
controller at a playbook).

## Loop

The controller cycles until cancelled (or `MaxIterations` is reached):

```
loop iteration:
  Plan & Validate (with up to validator.maxRetries replans):
    pick a random subset of scenarios          (size = scenarioSampleSize)
    Planner   → { goal, steps[] }
    Validator → { approved, reasons[] }
    if rejected, retry the planner with the rejection reasons as feedback
  if no plan was approved, skip execution and wait
  Execute the approved plan, step by step (bounded by executorTimeoutSeconds):
    for each step:
      for attempt in 1..stepRetries:
        screenshot → Executor → HID actions
        constraint check; if blocked, retry with feedback
        send to dongle (see protocol.md)
        sleep stepDelayMs
        screenshot → AI validation against step.successCriteria
        if validation succeeds → next step
      else → log and continue to next step
  wait IntervalMs and start the next iteration
```

There is **no reset task**. The Planner is asked, in its system prompt,
to include 1–2 cleanup steps at the start of any plan if the desktop is
cluttered (close stray windows, press ESC, Win+D).

## Playbook JSON

### Root object

```json
{
  "scenarios": ["…", "…"],
  "scenarioSampleSize": 3,
  "constraints": { … },
  "planner":   { "systemPrompt": "", "maxStepsPerPlan": 6 },
  "validator": { "systemPrompt": "", "maxRetries": 2 },
  "executor":  { "systemPrompt": "", "stepRetries": 2,
                 "stepDelayMs": 500, "executorTimeoutSeconds": 180 }
}
```

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `scenarios` | string[] | (required, non-empty) | Short seed sentences. The example playbook ships ~100. |
| `scenarioSampleSize` | int | 3 | How many scenarios are shown to the Planner per iteration. Clamped 1..10. |
| `constraints` | object | `{}` | Hard safety rails enforced by the controller before any HID is sent. See below. |
| `planner.systemPrompt` | string | `""` (built-in default) | Override the Planner system prompt. |
| `planner.maxStepsPerPlan` | int | 8 | Hard cap on plan length. Clamped 1..20. |
| `validator.systemPrompt` | string | `""` (built-in default) | Override the Validator system prompt. |
| `validator.maxRetries` | int | 2 | Extra planner→validator cycles before giving up this iteration. Clamped 0..5. |
| `executor.systemPrompt` | string | `""` (built-in default) | Override the Executor system prompt. |
| `executor.stepRetries` | int | 2 | Attempts per step before moving on. Clamped 1..5. |
| `executor.stepDelayMs` | int | 500 | Settle time after the dongle ack, before validation screenshot. Clamped 0..10000. |
| `executor.executorTimeoutSeconds` | int | 180 | Wall-clock budget to execute one whole plan. Clamped 10..1800. |

### Constraints

Hard safety rails enforced by the controller before any HID command
reaches the dongle. The Validator should reject dangerous plans first;
constraints are the second line of defence.

```json
{
  "forbiddenText": ["rm -rf", "format c:", "DROP TABLE"],
  "forbiddenKeyChords": [["GUI", "L"], ["CTRL", "ALT", "DELETE"]]
}
```

- `forbiddenText` — case-insensitive substring match against any `type`
  action's `text`.
- `forbiddenKeyChords` — order- and case-insensitive set match against any
  `key` action's `keys[]`. `WIN`/`WINDOWS`/`CMD` all alias to `GUI` (see
  [protocol.md](protocol.md#key-names)).

### Plan output (Planner → Validator → Executor)

Internal contract; not part of the playbook on disk.

```json
{
  "goal": "Open Notepad and type a short note",
  "steps": [
    { "description": "Press ESC then Win+D to clear the foreground.",
      "successCriteria": "The Windows desktop is visible." },
    { "description": "Open Start, type 'notepad', press Enter.",
      "successCriteria": "A Notepad window is open with an empty document." }
  ]
}
```

### Validator verdict

```json
{ "approved": true, "reasons": ["no destructive actions detected"] }
```

When `approved` is `false`, `reasons[]` is sent back to the Planner as
feedback for the next attempt.

### Executor action output

The Executor emits the same JSON as the dongle accepts on its `COMMAND`
characteristic — see [protocol.md](protocol.md#commands-controller--dongle-on-command):

```json
{
  "reasoning": "open the Run dialog",
  "actions": [ { "type": "key", "keys": ["GUI", "R"] } ],
  "done": false
}
```

## Stage prompts

Each stage has a built-in default system prompt in
[Prompts.cs](../controller/src/BusyUserBot/AI/Prompts.cs). Leave the
playbook's `systemPrompt` field empty (`""`) to use the default;
otherwise the playbook string fully replaces the default. The controller
appends a human-readable summary of the active constraints to whichever
prompt is used.

The default prompts are tuned for the M-tier model recommended in
[ai-setup.md](ai-setup.md) (Qwen 3.5-9B class, vision-capable). They:

- enforce strict JSON-only output for every stage,
- ban Markdown fences, prose preambles, and chain-of-thought leakage,
- repeat the same refusal list (deletions, shutdown, lock, credentials,
  closing the bot, etc.) at every stage so all three layers agree,
- limit the Executor to 1–4 HID actions per turn.

## Authoring scenarios

A scenario is one sentence describing something a normal Windows user
might do in a few steps. Good scenarios:

- have a clear, observable end state ("close Notepad without saving"),
- are short — ideally completable in 2–5 plan steps,
- are safe by construction (browsing settings is fine; "delete a file" is
  not — and would be rejected by the Validator anyway),
- keep variety: include keyboard, mouse, browser, file explorer,
  settings, accessibility, system tray, etc.

The example playbook ships ~100 scenarios across these categories.

## Per-step retries and step-failure policy

Within `executor.stepRetries` attempts, a step succeeds when:

1. The Executor returns at least one valid HID action.
2. None of the actions violates the global constraints.
3. The dongle ACKs the actions.
4. Either the step has no `successCriteria`, or the validation screenshot
   call returns `success: true`.

If all attempts fail, the executor **logs the failure and moves on to the
next step**. Plans are not aborted: a "busy user" makes partial progress
and that's fine. The next iteration starts a fresh plan.

If the Executor hits `executorTimeoutSeconds`, all remaining steps are
abandoned and the loop moves on.

## Validator retry policy

If the Validator rejects a plan, the controller asks the Planner again,
passing the rejection reasons as `PREVIOUS PLAN WAS REJECTED:` feedback.
After `validator.maxRetries` extra attempts, the iteration is abandoned
and the loop waits `IntervalMs` before trying again with a fresh
scenario sample.

## Files

- [playbook.example.json](../controller/src/BusyUserBot/playbook.example.json) — example playbook with ~100 scenarios.
- [Playbook.cs](../controller/src/BusyUserBot/Models/Playbook.cs) — playbook + plan + verdict data models.
- [PlaybookStore.cs](../controller/src/BusyUserBot/PlaybookStore.cs) — JSON load/save with normalisation and clamping.
- [BotLoop.cs](../controller/src/BusyUserBot/BotLoop.cs) — Planner → Validator → Executor driver.
- [Prompts.cs](../controller/src/BusyUserBot/AI/Prompts.cs) — default system prompts and prompt builders for each stage.
- [CommandParser.cs](../controller/src/BusyUserBot/AI/CommandParser.cs) — JSON parsers for plans, verdicts, decisions and validations.
- [protocol.md](protocol.md) — wire protocol for the dongle.
- [ai-setup.md](ai-setup.md) — backend and model selection.
- [controller-setup.md](controller-setup.md) — how to point the controller at a playbook.
