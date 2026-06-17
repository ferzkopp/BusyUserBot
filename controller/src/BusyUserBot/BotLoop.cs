using BusyUserBot.AI;
using BusyUserBot.Models;

namespace BusyUserBot;

/// <summary>
/// Open-loop "busy user" driver. Each iteration: ask the Planner to produce
/// a goal + steps from a random subset of scenarios, ask the Validator to
/// gate the plan for safety (with a small retry budget), then run the plan
/// step-by-step through the Executor with a fresh screenshot per step.
/// See docs/control-flow.md.
/// </summary>
public sealed class BotLoop
{
    private readonly IAiEngine _ai;
    private readonly IHardwareClient _hw;
    private readonly LoopConfig _cfg;
    private readonly MouseConfig _mouseCfg;
    private readonly Playbook _playbook;
    private readonly Action<string> _log;
    private readonly TimeSpan _aiTimeout;
    private readonly Random _rng = new();

    public BotLoop(
        IAiEngine ai,
        IHardwareClient hw,
        LoopConfig cfg,
        MouseConfig mouseCfg,
        Playbook playbook,
        Action<string> log)
    {
        _ai = ai;
        _hw = hw;
        _cfg = cfg;
        _mouseCfg = mouseCfg;
        _playbook = playbook;
        _log = log;
        _aiTimeout = TimeSpan.FromSeconds(Math.Max(10, _cfg.AiTimeoutSeconds));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_playbook.Scenarios.Count == 0)
        {
            _log("Loop: playbook has no scenarios; aborting.");
            return;
        }

        _log($"Loop start: {_playbook.Scenarios.Count} scenario(s), max {_cfg.MaxIterations} iteration(s).");

        if (!await _hw.ConnectAsync(ct).ConfigureAwait(false))
        {
            _log("Dongle connect failed; aborting.");
            return;
        }

        for (int iteration = 1; iteration <= _cfg.MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            _log($"=== iteration {iteration}/{_cfg.MaxIterations} ===");

            PlanOutput? plan;
            try
            {
                plan = await PlanAndValidateAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log("Plan/validate error: " + ex.Message);
                plan = null;
            }

            if (plan is null)
            {
                _log("No approved plan this iteration; waiting and trying again.");
            }
            else
            {
                try
                {
                    await ExecutePlanAsync(plan, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log("Execute error: " + ex.Message);
                }
            }

            try { await Task.Delay(_cfg.IntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        _log("Max iterations reached; stopping.");
    }

    // -------------------------------------------------------------------
    // Plan + validate
    // -------------------------------------------------------------------

    private async Task<PlanOutput?> PlanAndValidateAsync(CancellationToken ct)
    {
        var rules = _playbook.Constraints;
        var plannerSys = Prompts.BuildPlannerSystem(_playbook.Planner.SystemPrompt, rules);
        var validatorSys = Prompts.BuildValidatorSystem(_playbook.Validator.SystemPrompt, rules);

        string? rejectionFeedback = null;
        int totalAttempts = 1 + Math.Max(0, _playbook.Validator.MaxRetries);

        for (int attempt = 1; attempt <= totalAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _log($"  plan attempt {attempt}/{totalAttempts}");

            var sample = SampleScenarios(_playbook.ScenarioSampleSize);
            _log($"  scenarios: {string.Join(" | ", sample)}");

            var shot = ScreenCapture.Grab();
            var plannerUser = Prompts.BuildPlannerUser(
                sample, _playbook.Planner.MaxStepsPerPlan,
                shot.SentWidth, shot.SentHeight, rejectionFeedback);

            PlanOutput plan;
            try
            {
                var raw = await WithAiTimeoutAsync(
                    tk => _ai.ChatAsync(plannerSys, plannerUser, shot, tk),
                    "planner", ct).ConfigureAwait(false);
                plan = CommandParser.ParsePlan(raw);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log("  planner error: " + ex.Message);
                rejectionFeedback = "previous reply was malformed: " + ex.Message;
                continue;
            }

            if (plan.Steps.Count > _playbook.Planner.MaxStepsPerPlan)
            {
                _log($"  planner produced {plan.Steps.Count} steps (>{_playbook.Planner.MaxStepsPerPlan}); trimming.");
                plan.Steps = plan.Steps.Take(_playbook.Planner.MaxStepsPerPlan).ToList();
            }

            _log($"  goal: {plan.Goal}");
            for (int i = 0; i < plan.Steps.Count; i++)
                _log($"    [{i + 1}] {plan.Steps[i].Description}");

            var validatorUser = Prompts.BuildValidatorUser(plan);
            ValidatorVerdict verdict;
            try
            {
                var raw = await WithAiTimeoutAsync(
                    tk => _ai.ChatAsync(validatorSys, validatorUser, shot, tk),
                    "validator", ct).ConfigureAwait(false);
                verdict = CommandParser.ParseValidatorVerdict(raw);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log("  validator error: " + ex.Message);
                rejectionFeedback = "validator failed to parse plan; rephrase goal and steps simply.";
                continue;
            }

            if (verdict.Approved)
            {
                _log($"  validator: APPROVED ({string.Join("; ", verdict.Reasons)})");
                return plan;
            }

            rejectionFeedback = verdict.Reasons.Count > 0
                ? string.Join("; ", verdict.Reasons)
                : "validator rejected the plan without giving reasons";
            _log($"  validator: REJECTED ({rejectionFeedback})");
        }

        return null;
    }

    private IReadOnlyList<string> SampleScenarios(int count)
    {
        int n = Math.Min(count, _playbook.Scenarios.Count);
        if (n <= 0) return Array.Empty<string>();
        // Reservoir-style sample without replacement.
        var indices = Enumerable.Range(0, _playbook.Scenarios.Count).ToList();
        var picked = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            int j = _rng.Next(indices.Count);
            picked.Add(_playbook.Scenarios[indices[j]]);
            indices.RemoveAt(j);
        }
        return picked;
    }

    // -------------------------------------------------------------------
    // Execute plan, step by step
    // -------------------------------------------------------------------

    private async Task ExecutePlanAsync(PlanOutput plan, CancellationToken ct)
    {
        var rules = _playbook.Constraints;
        var execSys = Prompts.BuildExecutorSystem(_playbook.Executor.SystemPrompt, rules);

        using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        execCts.CancelAfter(TimeSpan.FromSeconds(_playbook.Executor.ExecutorTimeoutSeconds));
        var execCt = execCts.Token;

        for (int i = 0; i < plan.Steps.Count; i++)
        {
            if (execCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _log($"  executor timed out after {_playbook.Executor.ExecutorTimeoutSeconds}s; abandoning remaining steps.");
                return;
            }
            ct.ThrowIfCancellationRequested();

            var step = plan.Steps[i];
            var prev = i > 0 ? plan.Steps[i - 1].Description : null;
            var next = i < plan.Steps.Count - 1 ? plan.Steps[i + 1].Description : null;

            _log($"  step {i + 1}/{plan.Steps.Count}: {step.Description}");
            bool ok;
            try
            {
                ok = await RunStepAsync(execSys, rules, plan.Goal, step, prev, next, i, plan.Steps.Count, execCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && execCts.IsCancellationRequested)
            {
                _log($"  executor timed out after {_playbook.Executor.ExecutorTimeoutSeconds}s; abandoning remaining steps.");
                return;
            }

            if (!ok)
                _log($"  step {i + 1} did not validate; continuing to next step.");
        }
        _log("  plan complete.");
    }

    private async Task<bool> RunStepAsync(
        string execSys, Constraints rules,
        string goal, PlanStep step, string? prev, string? next,
        int stepIndex, int stepCount, CancellationToken ct)
    {
        string? lastFailure = null;
        int attempts = Math.Max(1, _playbook.Executor.StepRetries);

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _log($"    attempt {attempt}/{attempts}");

            var shot = ScreenCapture.Grab();
            var cursor = Cursor.Position;
            var (cursorImgX, cursorImgY) = shot.MapPointToImage(cursor.X, cursor.Y);
            var actionUser = Prompts.BuildExecutorActionUser(
                goal, prev, step, next, stepIndex, stepCount,
                shot.SentWidth, shot.SentHeight, cursorImgX, cursorImgY, lastFailure);

            AiDecision decision;
            try
            {
                decision = await WithAiTimeoutAsync(
                    tk => _ai.GenerateActionsAsync(execSys, actionUser, shot, tk),
                    "executor", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException ex)
            {
                lastFailure = "AI timeout: " + ex.Message;
                _log("    " + lastFailure);
                continue;
            }
            catch (Exception ex)
            {
                lastFailure = "AI error: " + ex.Message;
                _log("    " + lastFailure);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(decision.Reasoning))
                _log($"    reasoning: {decision.Reasoning}");

            if (decision.Actions.Count == 0)
            {
                lastFailure = "AI returned no actions";
                _log("    " + lastFailure);
                continue;
            }

            var pipeline = HidCoordinatePipeline.Transform(shot, decision.Actions, _mouseCfg.CalibrationGain);
            var mappedActions = pipeline.ScreenActions;
            var hidActions = pipeline.HidActions;

            var violation = ConstraintValidator.FindViolation(mappedActions, rules);
            if (violation is not null)
            {
                lastFailure = "constraint violation: " + violation;
                _log("    rejected — " + lastFailure);
                continue;
            }

            try
            {
                int totalExecuted = await SendWithRefinementAsync(
                    mappedActions, hidActions, pipeline.DpiScale, step.Description, ct).ConfigureAwait(false);
                _log($"    executed {totalExecuted} action(s)");
            }
            catch (DongleFailureException dex)
            {
                lastFailure = $"dongle error after {dex.Executed} actions: {dex.Error}";
                _log("    " + lastFailure);
                continue;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastFailure = "dongle exception: " + ex.Message;
                _log("    " + lastFailure);
                continue;
            }

            if (_playbook.Executor.StepDelayMs > 0)
            {
                try { await Task.Delay(_playbook.Executor.StepDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }

            if (string.IsNullOrWhiteSpace(step.SuccessCriteria))
            {
                _log("    no step success criterion; assuming ok");
                return true;
            }

            var shot2 = ScreenCapture.Grab();
            var validateUser = Prompts.BuildExecutorValidationUser(
                $"step {stepIndex + 1}: {step.Description}",
                step.SuccessCriteria,
                shot2.SentWidth, shot2.SentHeight);

            try
            {
                var v = await WithAiTimeoutAsync(
                    tk => _ai.ValidateAsync(execSys, validateUser, shot2, tk),
                    "step validation", ct).ConfigureAwait(false);
                _log($"    validation: success={v.Success} ({v.Reason})");
                if (v.Success) return true;
                lastFailure = "validation failed: " + (v.Reason ?? "unspecified");
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException ex)
            {
                lastFailure = "validation timeout: " + ex.Message;
                _log("    " + lastFailure);
            }
            catch (Exception ex)
            {
                lastFailure = "validation error: " + ex.Message;
                _log("    " + lastFailure);
            }
        }

        return false;
    }

    private async Task<T> WithAiTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string phase,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_aiTimeout);
        try
        {
            return await operation(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"{phase} exceeded {_aiTimeout.TotalSeconds:0}s");
        }
    }

    /// <summary>
    /// Send the action batch to the dongle, but interleave the iterative
    /// cursor-targeting refinement after every absolute <c>move</c>. The move's
    /// own <c>target</c> description (set by the executor) is preferred; when
    /// the model omitted one, <paramref name="fallbackTarget"/> (the current
    /// step description) is used so calibration still runs after every move.
    /// Returns total actions executed across all sub-batches.
    /// </summary>
    private async Task<int> SendWithRefinementAsync(
        IReadOnlyList<HidAction> screenActions,
        IReadOnlyList<HidAction> hidActions,
        double dpiScale,
        string fallbackTarget,
        CancellationToken ct)
    {
        // Walk through actions; for each absolute move, send everything up to
        // and including that move, run refinement, then continue with the rest.
        int total = 0;
        int i = 0;
        while (i < hidActions.Count)
        {
            // Find the next refinement boundary: any absolute move with
            // coordinates. Refinement happens AFTER the move, so the boundary
            // is inclusive (we send through index `boundary`).
            int boundary = -1;
            for (int j = i; j < screenActions.Count; j++)
            {
                var sa = screenActions[j];
                if (sa.Type == "move"
                    && sa.Absolute != false
                    && (sa.X is not null || sa.Y is not null))
                {
                    boundary = j;
                    break;
                }
            }

            int sendEnd = boundary >= 0 ? boundary + 1 : hidActions.Count;

            // ---- UIA fast path ----------------------------------------------------
            // If this slice ends on a targeted absolute move, ask the UIA tree to
            // resolve the target's on-screen rect *before* we send. A confident
            // hit lets us replace the model's coarse coordinates with a pixel-
            // exact centre and skip the AI-driven refinement entirely; a miss
            // logs a single line and we proceed exactly as before.
            UiaTargetResolver.UiaHit? uiaHit = null;
            if (boundary >= 0 && !string.IsNullOrWhiteSpace(screenActions[boundary].Target))
            {
                var moveScreen = screenActions[boundary];
                uiaHit = UiaTargetResolver.Resolve(
                    moveScreen.Target!,
                    _log,
                    ct,
                    cursorHint: Cursor.Position);

                if (uiaHit is not null)
                {
                    _log($"  UIA: matched '{uiaHit.Name}' [{uiaHit.ControlType}] " +
                         $"@ ({uiaHit.Centre.X},{uiaHit.Centre.Y}) " +
                         $"rect=({uiaHit.ScreenRect.X},{uiaHit.ScreenRect.Y} {uiaHit.ScreenRect.Width}x{uiaHit.ScreenRect.Height}) " +
                         $"score={uiaHit.Confidence:0.00} — overriding model coords, skipping refinement");

                    // Override the move (in screen coords) with the UIA centre,
                    // then re-DPI-compensate just that one action to keep the
                    // HID slice in sync.
                    var fixedScreen = moveScreen with { X = uiaHit.Centre.X, Y = uiaHit.Centre.Y };
                    var fixedHid = HidScaler.CompensateForDpi(new[] { fixedScreen }, dpiScale, _mouseCfg.CalibrationGain)[0];

                    // Materialise the slice with the patched HID move at the boundary.
                    var patched = new List<HidAction>(sendEnd - i);
                    for (int k = i; k < sendEnd; k++)
                        patched.Add(k == boundary ? fixedHid : hidActions[k]);

                    var respUia = await _hw.SendAsync(patched, ct).ConfigureAwait(false);
                    if (!respUia.Ok)
                        throw new DongleFailureException(total + respUia.Executed, respUia.Error ?? "unknown");
                    total += respUia.Executed;

                    i = sendEnd;
                    continue; // No vision refinement — UIA gave us the exact rect.
                }
                else
                {
                    _log($"  UIA: no confident match for '{Trunc(moveScreen.Target!)}' — falling back to vision refinement");
                }
            }

            var slice = new List<HidAction>(sendEnd - i);
            for (int k = i; k < sendEnd; k++) slice.Add(hidActions[k]);

            var resp = await _hw.SendAsync(slice, ct).ConfigureAwait(false);
            if (!resp.Ok)
                throw new DongleFailureException(total + resp.Executed, resp.Error ?? "unknown");
            total += resp.Executed;

            if (boundary < 0) break;

            // Run refinement against the move's stated target (or the step
            // description when the model omitted one), then continue.
            var target = string.IsNullOrWhiteSpace(screenActions[boundary].Target)
                ? fallbackTarget
                : screenActions[boundary].Target!;
            await CursorTargeting.RefineAsync(
                _ai, _hw, dpiScale,
                targetDescription: target,
                targetShortName: target,
                aiTimeoutSeconds: _cfg.AiTimeoutSeconds,
                log: _log,
                outerCt: ct,
                calibrationGain: _mouseCfg.CalibrationGain).ConfigureAwait(false);

            i = sendEnd;
        }
        return total;
    }

    private static string Trunc(string s, int n = 60) =>
        s.Length <= n ? s : s.Substring(0, n) + "\u2026";
}

internal sealed class DongleFailureException : Exception
{
    public int Executed { get; }
    public string Error { get; }
    public DongleFailureException(int executed, string error) : base(error)
    { Executed = executed; Error = error; }
}
