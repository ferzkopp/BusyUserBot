using BusyUserBot.Models;

namespace BusyUserBot;

/// <summary>
/// Mouse movement calibration mode. Reverse-engineers the Windows pointer
/// "acceleration" (Enhance Pointer Precision) curve by measuring how many
/// screen pixels a single HID mouse report of N counts actually moves the
/// cursor — independently for the X and Y axes.
///
/// Why per-report? The firmware emits exactly one USB HID report per
/// &lt;=120-count chunk (see mouseMoveChunked). Windows derives the pointer
/// "velocity" used to index the acceleration curve from the COUNT in each
/// report (it cannot read the real polling rate, so timing is irrelevant).
/// Therefore the pixels produced by a report depend only on its count
/// magnitude, giving a deterministic transfer function f(N). Capturing f(N)
/// across many magnitudes yields the full non-linear curve (plus DPI scaling),
/// which the <see cref="MousePositioner"/> later inverts to plan moves that
/// taper down to single-count (~1px) reports near the target.
///
/// Strategy:
///   1. For each magnitude N, park the cursor near one edge (long runway).
///   2. Send a batch of K single N-count relative reports in one BLE command.
///   3. Measure total displacement via GetCursorPos; f(N) = |displacement| / K.
///      Averaging K reports captures sub-pixel remainder accumulation for
///      small N (where f(N) may be below 1px/report).
///   4. Repeat for X axis (N,0) and Y axis (0,N) independently.
///   5. Run a diagonal (N,N) sanity check and warn if the per-axis model
///      deviates (would indicate a radial-magnitude acceleration effect).
///   6. Store the curve (and a derived scalar gain for the AI pipeline).
/// </summary>
internal static class MouseCalibrator
{
    // HID report magnitudes to sample. Dense at the low end (where the curve
    // bends most and where final-approach precision matters), sparser at the
    // top. Capped at 100 (single-chunk firmware limit is 120).
    private static readonly int[] SampleCounts =
        { 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 30, 40, 50, 60, 80, 100 };

    private const int SafeMargin = 80;        // keep this far from any screen edge
    private const double EstMaxGain = 4.0;    // px-per-count estimate for sizing the target report count
    private const double EdgePredictGain = 6.0; // higher (conservative) estimate used to stop before an edge
    private const int EdgeGuard = 8;          // keep the cursor at least this far from any screen edge
    private const int SubBatch = 1;           // reports sent per transmission (1 = fully serialized, no dongle queue buildup)
    private const int MaxReps = 3;            // reports sampled per magnitude (kept low so the test finishes quickly)
    private const int SettleDelayMs = 250;    // pause after each transmission so the dongle drains its queue and Windows applies the move before we read the cursor
    private const double TravelBudget = 0.55; // fraction of runway to consume per sample

    private enum MoveAxis { X, Y, Diagonal }

    public static async Task<bool> CalibrateAsync(
        IHardwareClient hw,
        MouseConfig cfg,
        Action<string> log,
        CancellationToken ct)
    {
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        log($"Calibration: screen {screen.Width}x{screen.Height}");
        log($"Calibration: reverse-engineering per-report acceleration curve (X and Y independently)");
        log($"Calibration: sampling N counts = ({string.Join(", ", SampleCounts)})");

        // Each run re-centers the cursor (see MeasureAxisAsync), so the usable
        // runway in either direction is half the screen minus the safety margin.
        int halfRunwayX = screen.Width / 2 - SafeMargin;
        int halfRunwayY = screen.Height / 2 - SafeMargin;
        if (halfRunwayX < 150 || halfRunwayY < 150)
            log($"Calibration: WARNING - screen is small; half-runway X={halfRunwayX} Y={halfRunwayY}px limits large samples");

        var pixelsX = new double[SampleCounts.Length];
        var pixelsY = new double[SampleCounts.Length];

        try
        {
            // ---- X axis: re-center, drive along X ----
            log($"Calibration: --- X axis ---");
            for (int i = 0; i < SampleCounts.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                int n = SampleCounts[i];
                int target = RepsFor(n, halfRunwayX);
                var (f, used) = await MeasureAxisAsync(hw, n, target, MoveAxis.X, screen, log, ct);
                if (f < 0) return false;
                pixelsX[i] = f;
                log($"  X  N={n,3} -> {f,7:F2} px/report  (gain {f / n,5:F2}x, {used} reports)");
            }

            // ---- Y axis: re-center, drive along Y ----
            log($"Calibration: --- Y axis ---");
            for (int i = 0; i < SampleCounts.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                int n = SampleCounts[i];
                int target = RepsFor(n, halfRunwayY);
                var (f, used) = await MeasureAxisAsync(hw, n, target, MoveAxis.Y, screen, log, ct);
                if (f < 0) return false;
                pixelsY[i] = f;
                log($"  Y  N={n,3} -> {f,7:F2} px/report  (gain {f / n,5:F2}x, {used} reports)");
            }
        }
        catch (OperationCanceledException)
        {
            log($"Calibration: cancelled");
            return false;
        }

        // Enforce monotonic non-decreasing curves (filter measurement noise:
        // a larger report can never legitimately move fewer pixels).
        EnforceMonotonic(pixelsX);
        EnforceMonotonic(pixelsY);

        // ---- Diagonal sanity check ----
        log($"Calibration: --- diagonal check ---");
        try
        {
            await DiagonalCheckAsync(hw, screen, pixelsX, pixelsY, log, ct);
        }
        catch (OperationCanceledException)
        {
            log($"Calibration: cancelled");
            return false;
        }

        // ---- Derived scalar gain (backward compat for AI pipeline/HidScaler) ----
        double avgMult = 0; int cnt = 0;
        for (int i = 0; i < SampleCounts.Length; i++)
        {
            avgMult += (pixelsX[i] + pixelsY[i]) / (2.0 * SampleCounts[i]);
            cnt++;
        }
        avgMult = cnt > 0 ? avgMult / cnt : 1.0;
        double gain = 1.0 / Math.Max(avgMult, 0.01);

        cfg.AccelSampleCounts = (int[])SampleCounts.Clone();
        cfg.AccelSamplePixelsX = pixelsX;
        cfg.AccelSamplePixelsY = pixelsY;
        cfg.CalibrationGain = gain;
        cfg.CalibratedScreenWidth = screen.Width;
        cfg.CalibratedScreenHeight = screen.Height;

        double f1x = pixelsX[0], f1y = pixelsY[0];
        log($"Calibration: 1-count report moves ~{f1x:F2}px (X) / {f1y:F2}px (Y) — finest step");
        if (f1x > 2.5 || f1y > 2.5)
            log($"Calibration: NOTE single-count step is coarse; disabling 'Enhance pointer precision' or lowering DPI scaling would improve precision");
        log($"Calibration: derived scalar gain = {gain:F3} (avg multiplier {avgMult:F2}x)");
        log($"Calibration: DONE. Profiled {SampleCounts.Length} magnitudes per axis, screen={screen.Width}x{screen.Height}");
        return true;
    }

    /// <summary>
    /// Choose how many single-N-count reports to send so the batch consumes
    /// roughly <see cref="TravelBudget"/> of the available runway. Small N
    /// naturally gets many reps (good sub-pixel averaging); large N gets few.
    /// This is only an upper bound — <see cref="MeasureAxisAsync"/> stops early
    /// if the cursor approaches a screen edge.
    /// </summary>
    private static int RepsFor(int n, int runway)
    {
        double budget = Math.Max(runway, 0) * TravelBudget;
        int reps = (int)(budget / (EstMaxGain * n));
        return Math.Clamp(reps, 1, MaxReps);
    }

    /// <summary>
    /// Re-center the cursor, then drive it along the requested axis in small
    /// sub-batches of single N-count reports, checking the cursor position after
    /// each sub-batch. Movement stops before the cursor can reach a screen edge
    /// (so it never disappears off-screen), and the measured pixels-per-report
    /// is averaged only over reports that actually registered. Returns
    /// (pixelsPerReport, reportsUsed), or (-1, 0) on a hardware error.
    /// </summary>
    private static async Task<(double pixelsPerReport, int reports)> MeasureAxisAsync(
        IHardwareClient hw, int n, int targetReps, MoveAxis axis,
        Rectangle screen, Action<string> log, CancellationToken ct)
    {
        // Always start each run from the screen center so a previous run can
        // never leave the cursor stuck against (or past) an edge.
        int centerX = screen.Left + screen.Width / 2;
        int centerY = screen.Top + screen.Height / 2;
        Cursor.Position = new Point(centerX, centerY);
        await Task.Delay(SettleDelayMs, ct);
        var before = Cursor.Position;

        // Drive toward whichever side has the most room from the center.
        int sx = 0, sy = 0;
        if (axis is MoveAxis.X or MoveAxis.Diagonal)
            sx = (before.X - screen.Left) >= (screen.Right - before.X) ? -1 : 1;
        if (axis is MoveAxis.Y or MoveAxis.Diagonal)
            sy = (before.Y - screen.Top) >= (screen.Bottom - before.Y) ? -1 : 1;

        int reportsSent = 0;
        var last = before;

        while (reportsSent < targetReps)
        {
            ct.ThrowIfCancellationRequested();

            // Check the live cursor position after each movement and compute how
            // much room remains before the edge guard in the travel direction.
            var pos = Cursor.Position;
            int roomX = sx > 0 ? screen.Right - EdgeGuard - pos.X
                      : sx < 0 ? pos.X - (screen.Left + EdgeGuard)
                      : int.MaxValue;
            int roomY = sy > 0 ? screen.Bottom - EdgeGuard - pos.Y
                      : sy < 0 ? pos.Y - (screen.Top + EdgeGuard)
                      : int.MaxValue;
            int room = Math.Min(roomX, roomY);

            // How many reports safely fit before risking an edge clamp?
            int fit = (int)(room / (EdgePredictGain * n));
            int want = Math.Min(SubBatch, targetReps - reportsSent);
            int thisBatch = Math.Min(want, fit);
            if (thisBatch <= 0)
            {
                if (reportsSent > 0) break;          // out of runway — stop with what we have
                thisBatch = 1;                       // guarantee at least one measurement
            }

            var actions = new List<HidAction>(thisBatch + 1);
            for (int r = 0; r < thisBatch; r++)
                actions.Add(new HidAction("move", X: n * sx, Y: n * sy, Absolute: false));
            actions.Add(new HidAction("wait", Ms: SettleDelayMs));

            try
            {
                var resp = await hw.SendAsync(actions, ct);
                if (!resp.Ok)
                {
                    log($"Calibration: report batch (N={n}) failed: {resp.Error}");
                    return (-1, 0);
                }
            }
            catch (Exception ex)
            {
                log($"Calibration: report batch (N={n}) send failed: {ex.Message}");
                return (-1, 0);
            }

            // Wait out the dongle-side queue plus an equal controller-side margin
            // so the cursor has fully settled before we sample it. Without an ack
            // from the dongle this is the only way to keep reads in lock-step with
            // the moves it has actually applied.
            await Task.Delay(SettleDelayMs, ct);
            var now = Cursor.Position;

            // No whole-pixel advance this sub-batch. Two very different causes:
            //   (a) the cursor hit a screen edge despite the guard (real stall), or
            //   (b) sub-pixel accumulation — common for small N, where a few
            //       low-velocity reports round to 0px while the remainder builds
            //       up internally. This happens mid-screen and is NOT an edge stall.
            if (now == last)
            {
                bool nearEdge =
                    (sx > 0 && now.X >= screen.Right - EdgeGuard - 1) ||
                    (sx < 0 && now.X <= screen.Left + EdgeGuard + 1) ||
                    (sy > 0 && now.Y >= screen.Bottom - EdgeGuard - 1) ||
                    (sy < 0 && now.Y <= screen.Top + EdgeGuard + 1);

                if (nearEdge)
                {
                    // Real edge clamp: discard this sub-batch and stop.
                    log($"Calibration: (N={n}) cursor stalled near edge after {reportsSent} reports");
                    break;
                }

                // Sub-pixel stall mid-screen: count the reports (their true output
                // is ~0px, so they belong in the average) and keep going. Bounded
                // by targetReps, so this cannot loop forever.
                reportsSent += thisBatch;
                continue;
            }

            last = now;
            reportsSent += thisBatch;
        }

        if (reportsSent == 0) return (-1, 0);
        double moved = axis switch
        {
            MoveAxis.X => Math.Abs(last.X - before.X),
            MoveAxis.Y => Math.Abs(last.Y - before.Y),
            _ => Math.Sqrt(Math.Pow(last.X - before.X, 2) + Math.Pow(last.Y - before.Y, 2)),
        };
        return (moved / reportsSent, reportsSent);
    }

    /// <summary>
    /// Send a few diagonal (N,N) batches and compare the measured diagonal
    /// magnitude to the per-axis prediction sqrt(fX^2 + fY^2). A large deviation
    /// means Windows accelerates on the vector magnitude (radial), so the
    /// per-axis tables under-predict diagonal moves — worth a warning.
    /// </summary>
    private static async Task DiagonalCheckAsync(
        IHardwareClient hw, Rectangle screen,
        double[] pixelsX, double[] pixelsY, Action<string> log, CancellationToken ct)
    {
        int half = Math.Min(screen.Width, screen.Height) / 2 - SafeMargin;
        int[] checkNs = { 5, 20, 50 };

        foreach (int n in checkNs)
        {
            ct.ThrowIfCancellationRequested();
            int idx = Array.IndexOf(SampleCounts, n);
            if (idx < 0) continue;
            int target = RepsFor(n, half);

            var (measuredDiag, _) = await MeasureAxisAsync(hw, n, target, MoveAxis.Diagonal, screen, log, ct);
            if (measuredDiag < 0) { log($"  diag N={n}: measurement failed"); continue; }

            double predicted = Math.Sqrt(pixelsX[idx] * pixelsX[idx] + pixelsY[idx] * pixelsY[idx]);
            double devPct = predicted > 0.1 ? (measuredDiag - predicted) / predicted * 100.0 : 0;

            string flag = Math.Abs(devPct) > 20 ? " WARN (radial accel — per-axis model under/over-predicts diagonals)" : " ok";
            log($"  diag N={n,3}: measured {measuredDiag,6:F2} vs predicted {predicted,6:F2} px/report ({devPct,5:F0}%){flag}");
        }
    }

    private static void EnforceMonotonic(double[] f)
    {
        for (int i = 1; i < f.Length; i++)
            if (f[i] < f[i - 1]) f[i] = f[i - 1];
    }
}
