using BusyUserBot.Models;

namespace BusyUserBot;

/// <summary>
/// Mouse movement calibration mode. Measures how accurately the HID dongle
/// positions the cursor despite Windows pointer settings and HID report
/// coalescing, then stores a correction factor for use in the live pipeline.
///
/// Strategy:
///   1. Send a series of known-magnitude absolute moves to different screen
///      locations.
///   2. After each move, measure the actual cursor position via GetCursorPos.
///   3. Compare requested vs. actual to compute a gain factor.
///   4. Store the gain (and screen dimensions) in MouseConfig for future runs.
///
/// The algorithm is robust to animations because it relies on GetCursorPos
/// (not screenshot diffs) and uses multiple calibration points to filter noise.
/// </summary>
internal static class MouseCalibrator
{
    /// <summary>
    /// Run calibration by measuring the actual acceleration curve. Sends increasingly
    /// larger relative moves (1,1), (2,2), ..., (10,10) and measures actual cursor
    /// displacement to understand how pointer acceleration affects different magnitudes.
    /// </summary>
    public static async Task<bool> CalibrateAsync(
        IHardwareClient hw,
        MouseConfig cfg,
        Action<string> log,
        CancellationToken ct)
    {
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        log($"Calibration: screen {screen.Width}x{screen.Height}");
        log($"Calibration: measuring acceleration curve across multiple delta sizes");

        var measurements = new List<(int requested, double actual, double multiplier)>();
        
        // Test with moves of varying magnitudes to understand full acceleration curve
        // Small moves: 1-10px (detect fine behavior)
        // Medium moves: 25, 50px (mid-range behavior)  
        // Large moves: 75px (behavior at typical step size)
        var testMagnitudes = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 25, 50, 75 };
        
        log($"Calibration: testing deltas: ({string.Join(", ", testMagnitudes)})");

        // Calculate a safe center position where we have room for all moves in any direction
        // The largest test move is 75px diagonal, so we need at least 75px margin on all sides
        const int safeMargin = 100; // extra margin for safety
        int safeX = Math.Max(screen.Left + safeMargin, Math.Min(screen.Left + screen.Width / 2, screen.Right - safeMargin));
        int safeY = Math.Max(screen.Top + safeMargin, Math.Min(screen.Top + screen.Height / 2, screen.Bottom - safeMargin));
        
        if (safeX <= screen.Left + safeMargin || safeX >= screen.Right - safeMargin ||
            safeY <= screen.Top + safeMargin || safeY >= screen.Bottom - safeMargin)
        {
            log($"Calibration: WARNING - screen too small for safe measurements (need ~{safeMargin * 2}px margin)");
        }

        log($"Calibration: safe center position = ({safeX}, {safeY})");
        
        foreach (int magnitude in testMagnitudes)
        {
            ct.ThrowIfCancellationRequested();
            
            // Before each measurement, reposition cursor to safe center
            // This ensures we have room for the move without hitting screen edges
            log($"Calibration:   repositioning to ({safeX}, {safeY}) before Δ={magnitude}px test");
            Cursor.Position = new Point(safeX, safeY);
            await Task.Delay(100, ct); // brief settle time after repositioning
            int testDx = magnitude;
            int testDy = magnitude;
            
            // Record current position
            var before = Cursor.Position;

            // Send the test relative move
            var moveAction = new HidAction("move", X: testDx, Y: testDy, Absolute: false);
            var actions = new List<HidAction>
            {
                moveAction,
                new("wait", Ms: 200),
            };

            try
            {
                var resp = await hw.SendAsync(actions, ct);
                if (!resp.Ok)
                {
                    log($"Calibration: move ({testDx},{testDy}) failed: {resp.Error}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log($"Calibration: move ({testDx},{testDy}) send failed: {ex.Message}");
                return false;
            }

            await Task.Delay(200, ct);

            // Measure actual displacement
            var after = Cursor.Position;
            int actualDx = after.X - before.X;
            int actualDy = after.Y - before.Y;

            double distanceRequested = Math.Sqrt(testDx * testDx + testDy * testDy);
            double distanceActual = Math.Sqrt(actualDx * actualDx + actualDy * actualDy);
            double multiplier = distanceActual > 0.1 ? distanceActual / distanceRequested : 0;

            log($"  Δ={magnitude:3d}px: measured {distanceActual:6.1f}px → multiplier {multiplier:5.2f}x");

            measurements.Add((magnitude, distanceActual, multiplier));
        }

        // Analyze the curve
        log($"Calibration: acceleration curve summary:");
        
        var validMultipliers = measurements.Where(m => m.multiplier > 0).Select(m => m.multiplier).ToList();
        
        if (validMultipliers.Count == 0)
        {
            log($"Calibration: ERROR - no valid measurements");
            return false;
        }

        foreach (var (req, actual, mult) in measurements)
        {
            log($"  Input {req:3d}px → Actual {actual:6.1f}px (mult {mult:5.2f}x)");
        }

        double minMult = validMultipliers.Min();
        double maxMult = validMultipliers.Max();
        double avgMult = validMultipliers.Average();
        double variance = maxMult / Math.Max(minMult, 0.01);

        log($"Calibration: min={minMult:F2}x, max={maxMult:F2}x, avg={avgMult:F2}x, variance={variance:F2}x");

        // Determine gain: inverse of average multiplier
        double gain = 1.0 / Math.Max(avgMult, 0.01);

        // Check if acceleration is too non-linear
        if (variance > 2.0)
        {
            log($"Calibration: ⚠ WARNING: Acceleration is highly non-linear (variance {variance:F2}x)");
            log($"Calibration: STRONGLY RECOMMEND disabling 'Enhance pointer precision':");
            log($"Calibration:   Settings → Bluetooth & devices → Mouse → Additional mouse settings → Pointer Options");
            log($"Calibration: MousePositioner will auto-reduce gain if oscillation is detected");
            gain = 0.5;
        }
        else if (Math.Abs(avgMult - 1.0) < 0.1)
        {
            log($"Calibration: ✓ Acceleration is minimal (multiplier ≈ 1.0x)");
            log($"Calibration: Using measured gain = {gain:F3}");
        }
        else
        {
            log($"Calibration: ✓ Measured average acceleration = {avgMult:F2}x");
            log($"Calibration: Using compensation gain = {gain:F3}");
        }

        cfg.CalibrationGain = gain;
        cfg.CalibratedScreenWidth = screen.Width;
        cfg.CalibratedScreenHeight = screen.Height;

        log($"Calibration: DONE. Gain={cfg.CalibrationGain:F3}, screen={screen.Width}x{screen.Height}");
        return true;
    }
}
