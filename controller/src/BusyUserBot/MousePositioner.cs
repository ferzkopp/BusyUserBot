using BusyUserBot.Models;

namespace BusyUserBot;

/// <summary>
/// Moves the mouse to an absolute target position (X, Y) on screen using
/// iterative relative moves with GetCursorPos ground-truth feedback.
///
/// Instead of one scalar gain, this uses the per-report acceleration curve
/// measured by <see cref="MouseCalibrator"/> (pixels-per-report f(N) for each
/// HID count magnitude N, independently per axis). Each iteration it:
///   1. Reads the current cursor position (ground truth).
///   2. Computes the remaining delta per axis.
///   3. Inverts the curve to pick the largest count N whose pixel output stays
///      below the remaining distance, then emits a batch of identical single
///      reports in one BLE command (the firmware sends one report per move).
///   4. Re-measures and repeats. As the cursor nears the target the chosen
///      count shrinks naturally to 1, producing ~1px steps with no overshoot.
/// Because Windows derives acceleration purely from the per-report count, this
/// coarse-to-fine decomposition tames "Enhance pointer precision" without any
/// firmware change.
/// </summary>
public static class MousePositioner
{
    private enum Axis { X, Y }

    /// <summary>
    /// Move the mouse to an absolute target position using iterative relative moves.
    /// </summary>
    /// <param name="hw">Hardware client to send HID commands</param>
    /// <param name="targetX">Target X coordinate in screen pixels</param>
    /// <param name="targetY">Target Y coordinate in screen pixels</param>
    /// <param name="cfg">Mouse config carrying the measured per-report acceleration curve</param>
    /// <param name="log">Optional logging callback</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if reached target within tolerance; false on timeout</returns>
    public static async Task<bool> MoveToAsync(
        IHardwareClient hw,
        int targetX,
        int targetY,
        MouseConfig cfg,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        const int maxIterations = 40;       // coarse-to-fine converges well under this
        const int settleMs = 250;           // pause after each transmission so the dongle drains its queue (no ack) and Windows applies the move before we read the cursor — matches MouseCalibrator.SettleDelayMs
        const int timeout = 60;             // total timeout in seconds
        const int maxBatch = 1;             // one report per BLE command (fully serialized) — the dongle has no ack, so batching queues unprocessed moves and wrecks accuracy
        const double approach = 0.8;        // aim for 80% of remaining distance per batch

        bool haveCurve = cfg.AccelProfileValid;

        // Smallest achievable step per axis = a single 1-count report. Tolerance
        // can never be tighter than that (otherwise we'd oscillate by < 1 step).
        double f1x = PixelsForCount(cfg, 1, Axis.X);
        double f1y = PixelsForCount(cfg, 1, Axis.Y);
        int tolX = Math.Max(2, (int)Math.Ceiling(f1x));
        int tolY = Math.Max(2, (int)Math.Ceiling(f1y));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

        try
        {
            if (!haveCurve)
                log?.Invoke($"[MousePos] No acceleration profile — run calibration for best precision. Using 1:1 fallback.");
            log?.Invoke($"[MousePos] Moving to ({targetX}, {targetY}), tol=({tolX},{tolY})px, profiled={haveCurve}");

            for (int iter = 0; iter < maxIterations; iter++)
            {
                // Read current position via GetCursorPos (ground truth)
                var current = Cursor.Position;
                int deltaX = targetX - current.X;
                int deltaY = targetY - current.Y;

                bool xDone = Math.Abs(deltaX) <= tolX;
                bool yDone = Math.Abs(deltaY) <= tolY;
                if (xDone && yDone)
                {
                    double dist = Math.Sqrt((double)deltaX * deltaX + (double)deltaY * deltaY);
                    log?.Invoke($"[MousePos] Reached target (residual {dist:F1}px) after {iter} iterations");
                    return true;
                }

                // Invert the curve per axis: largest count whose pixel output
                // does not exceed `approach` * remaining distance (>= 1 count).
                int nx = xDone ? 0 : CountForPixels(cfg, Math.Abs(deltaX) * approach, Axis.X);
                int ny = yDone ? 0 : CountForPixels(cfg, Math.Abs(deltaY) * approach, Axis.Y);

                double pxPerX = nx > 0 ? PixelsForCount(cfg, nx, Axis.X) : 0;
                double pxPerY = ny > 0 ? PixelsForCount(cfg, ny, Axis.Y) : 0;

                // Reports each axis needs to cover ~approach of its remaining
                // distance. Use the smaller so we never overshoot either axis;
                // the nearer axis finishes first, then the far one moves alone.
                int repsX = pxPerX > 0.01 ? (int)Math.Floor(Math.Abs(deltaX) * approach / pxPerX) : int.MaxValue;
                int repsY = pxPerY > 0.01 ? (int)Math.Floor(Math.Abs(deltaY) * approach / pxPerY) : int.MaxValue;
                int reps = Math.Min(repsX, repsY);
                if (reps == int.MaxValue) reps = 1;          // both ~0 px/report; nudge once
                reps = Math.Clamp(reps, 1, maxBatch);

                int stepX = Math.Sign(deltaX) * nx;
                int stepY = Math.Sign(deltaY) * ny;
                if (stepX == 0 && stepY == 0)
                {
                    // Within tolerance on the active axis but rounding produced a
                    // zero step — force a single 1-count nudge on the worse axis.
                    if (!xDone) stepX = Math.Sign(deltaX);
                    else if (!yDone) stepY = Math.Sign(deltaY);
                    reps = 1;
                }

                log?.Invoke($"[MousePos] Iter {iter}: pos=({current.X},{current.Y}) Δ=({deltaX},{deltaY}) " +
                            $"-> {reps}× report({stepX},{stepY}) [N=({nx},{ny}), ~{pxPerX:F1}/{pxPerY:F1}px each]");

                var actions = new List<HidAction>(reps + 1);
                for (int r = 0; r < reps; r++)
                    actions.Add(new HidAction("move", X: stepX, Y: stepY, Absolute: false));
                actions.Add(new HidAction("wait", Ms: settleMs));

                try
                {
                    var resp = await hw.SendAsync(actions, linked.Token);
                    if (!resp.Ok)
                    {
                        log?.Invoke($"[MousePos] Dongle error: {resp.Error}");
                        return false;
                    }
                }
                catch (OperationCanceledException)
                {
                    log?.Invoke($"[MousePos] Dongle send timed out");
                    return false;
                }

                await Task.Delay(settleMs, linked.Token);
            }

            log?.Invoke($"[MousePos] Failed to reach target after {maxIterations} iterations");
            return false;
        }
        catch (OperationCanceledException)
        {
            log?.Invoke($"[MousePos] Cancelled or timed out");
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[MousePos] Exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Interpolate the measured per-report transfer function: pixels produced by
    /// a single <paramref name="count"/>-count HID report on the given axis.
    /// Falls back to 1:1 when no profile has been measured.
    /// </summary>
    private static double PixelsForCount(MouseConfig cfg, int count, Axis axis)
    {
        if (count <= 0) return 0;
        if (!cfg.AccelProfileValid) return count; // 1:1 fallback

        var counts = cfg.AccelSampleCounts;
        var px = axis == Axis.X ? cfg.AccelSamplePixelsX : cfg.AccelSamplePixelsY;

        // Below/at the first sample: linear from the origin.
        if (count <= counts[0])
            return px[0] * count / Math.Max(counts[0], 1);

        // Within the sampled range: piecewise-linear interpolation.
        for (int i = 1; i < counts.Length; i++)
        {
            if (count <= counts[i])
            {
                double t = (double)(count - counts[i - 1]) / (counts[i] - counts[i - 1]);
                return px[i - 1] + t * (px[i] - px[i - 1]);
            }
        }

        // Beyond the last sample: extrapolate along the final segment's slope.
        int n = counts.Length;
        double slope = (px[n - 1] - px[n - 2]) / (counts[n - 1] - counts[n - 2]);
        return px[n - 1] + slope * (count - counts[n - 1]);
    }

    /// <summary>
    /// Inverse of <see cref="PixelsForCount"/>: the largest report count whose
    /// pixel output does not exceed <paramref name="budgetPx"/> (clamped to
    /// [1, 120], the firmware single-chunk limit). Since f(N) is monotonic the
    /// scan can stop at the first count that overshoots.
    /// </summary>
    private static int CountForPixels(MouseConfig cfg, double budgetPx, Axis axis)
    {
        if (budgetPx <= 0) return 1;
        int maxN = cfg.AccelProfileValid
            ? Math.Min(120, cfg.AccelSampleCounts[^1])
            : 30; // conservative cap when uncalibrated
        int best = 1;
        for (int n = 1; n <= maxN; n++)
        {
            if (PixelsForCount(cfg, n, axis) <= budgetPx) best = n;
            else break;
        }
        return best;
    }

    /// <summary>
    /// Measure accuracy of movement to a single point. Returns the final error
    /// distance after attempting to move to the target.
    /// </summary>
    public static async Task<double> MeasureAccuracyAsync(
        IHardwareClient hw,
        int targetX,
        int targetY,
        MouseConfig cfg,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        bool ok = await MoveToAsync(hw, targetX, targetY, cfg, log, ct);

        if (!ok)
        {
            log?.Invoke($"[MouseAccuracy] Failed to move to ({targetX}, {targetY})");
            return double.MaxValue;
        }

        // Measure final error
        var final = Cursor.Position;
        int deltaX = targetX - final.X;
        int deltaY = targetY - final.Y;
        double error = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

        log?.Invoke($"[MouseAccuracy] Final position: ({final.X}, {final.Y}), error: {error:F1}px");
        return error;
    }
}
