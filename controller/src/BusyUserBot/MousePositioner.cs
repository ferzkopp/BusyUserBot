using BusyUserBot.Models;

namespace BusyUserBot;

/// <summary>
/// Moves the mouse to an absolute target position (X, Y) on screen using iterative
/// relative moves with GetCursorPos ground truth feedback. The controller:
///   1. Measures current cursor position
///   2. Calculates delta to target
///   3. Sends a relative HID move (firmware chunks it at 120px)
///   4. Measures actual landing position
///   5. Repeats with smaller steps near target
/// 
/// The calibration gain compensates for Windows pointer driver acceleration/deceleration.
/// </summary>
public static class MousePositioner
{
    /// <summary>
    /// Move the mouse to an absolute target position using iterative relative moves.
    /// </summary>
    /// <param name="hw">Hardware client to send HID commands</param>
    /// <param name="targetX">Target X coordinate in screen pixels</param>
    /// <param name="targetY">Target Y coordinate in screen pixels</param>
    /// <param name="calibrationGain">Calibration gain factor from mouse config (applied to deltas)</param>
    /// <param name="log">Optional logging callback</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if reached target within tolerance; false on timeout</returns>
    public static async Task<bool> MoveToAsync(
        IHardwareClient hw,
        int targetX,
        int targetY,
        double calibrationGain,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        const int tolerance = 15;           // pixels - success if within this distance
        const int maxIterations = 20;       // enough iterations for multi-step approach
        const int settleMs = 400;           // wait for cursor settling after each move
        const int timeout = 60;             // total timeout in seconds
        const int largeStepThreshold = 100; // use large steps when > this distance
        const int largeStepSize = 100;      // max step size for large moves
        const int smallStepSize = 30;       // max step size for fine adjustments

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

        try
        {
            log?.Invoke($"[MousePos] Moving to ({targetX}, {targetY}), gain={calibrationGain:F3}");

            double previousDistance = double.MaxValue;
            int noProgressCount = 0;        // tracks consecutive iterations without improvement
            double effectiveGain = calibrationGain;  // dynamically reduced if oscillating

            for (int iter = 0; iter < maxIterations; iter++)
            {
                // Read current position via GetCursorPos (ground truth)
                var current = Cursor.Position;
                log?.Invoke($"[MousePos] Iter {iter}: current=({current.X}, {current.Y})");

                int deltaX = targetX - current.X;
                int deltaY = targetY - current.Y;
                double distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

                // Success if close enough to target
                if (distance <= tolerance)
                {
                    log?.Invoke($"[MousePos] ✓ Reached target within {tolerance}px (distance={distance:F1})");
                    return true;
                }

                // Detect oscillation: if distance increased, we're bouncing around
                if (distance >= previousDistance)
                {
                    noProgressCount++;
                    if (noProgressCount >= 2)
                    {
                        // We're oscillating; reduce gain to prevent overshooting
                        effectiveGain = Math.Max(0.1, effectiveGain * 0.5);
                        log?.Invoke($"[MousePos] Oscillation detected, reducing gain to {effectiveGain:F3}");
                        noProgressCount = 0;
                    }
                }
                else
                {
                    // Distance improved, reset counter
                    noProgressCount = 0;
                }

                previousDistance = distance;

                // Determine step size: larger steps for distant targets, smaller for fine adjustments
                int maxStep = distance > largeStepThreshold ? largeStepSize : smallStepSize;

                // Clamp delta to max step size
                int stepX = deltaX;
                int stepY = deltaY;
                
                if (Math.Abs(stepX) > maxStep || Math.Abs(stepY) > maxStep)
                {
                    double scale = (double)maxStep / Math.Max(Math.Abs(stepX), Math.Abs(stepY));
                    stepX = (int)Math.Round(stepX * scale);
                    stepY = (int)Math.Round(stepY * scale);
                }

                // Apply calibration gain to the relative move
                double gainX = stepX * effectiveGain;
                double gainY = stepY * effectiveGain;
                int finalX = (int)Math.Round(gainX);
                int finalY = (int)Math.Round(gainY);

                // Ensure we never scale a move to 0 (always move at least 1px in the intended direction)
                if (stepX != 0 && finalX == 0) finalX = stepX > 0 ? 1 : -1;
                if (stepY != 0 && finalY == 0) finalY = stepY > 0 ? 1 : -1;

                log?.Invoke($"[MousePos] Sending relative move ({finalX}, {finalY}) [step=({stepX}, {stepY}), distance={distance:F1}px]");

                // Send the relative move
                var moveAction = new HidAction("move", X: finalX, Y: finalY, Absolute: false);
                var actions = new List<HidAction>
                {
                    moveAction,
                    new("wait", Ms: settleMs),
                };

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

                // Wait for settling
                await Task.Delay(settleMs, linked.Token);
            }

            log?.Invoke($"[MousePos] ✗ Failed to reach target after {maxIterations} iterations");
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
    /// Measure accuracy of movement to a single point. Returns the final error distance
    /// after attempting to move to the target.
    /// </summary>
    public static async Task<double> MeasureAccuracyAsync(
        IHardwareClient hw,
        int targetX,
        int targetY,
        double calibrationGain,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        bool ok = await MoveToAsync(hw, targetX, targetY, calibrationGain, log, ct);
        
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
