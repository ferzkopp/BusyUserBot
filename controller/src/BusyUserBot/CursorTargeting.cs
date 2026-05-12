using BusyUserBot.AI;
using BusyUserBot.Models;

namespace BusyUserBot;

/// <summary>
/// Iterative cursor-targeting stage. After a coarse absolute move puts the
/// cursor near the intended UI element, this asks the vision model — using a
/// native-resolution crop centred on the current cursor — whether the
/// crosshair is over the target, and if not, by how much to nudge. Apply the
/// nudge as a DPI-compensated *relative* HID move and repeat. Three rounds is
/// empirically enough to converge from the typical 50-100 px coarse-stage
/// error.
///
/// <paramref name="targetDescription"/> is the natural-language phrase
/// describing what the model should aim at; it is interpolated into the
/// targeting prompt verbatim. Keep it short, specific, and unambiguous.
/// </summary>
internal static class CursorTargeting
{
    private const int Rounds = 3;
    private const int RegionSize = 1024;
    private const int OnTargetThreshold = 8;

    public static async Task RefineAsync(
        IAiEngine ai,
        IHardwareClient hw,
        double dpiScale,
        string targetDescription,
        string targetShortName,
        int aiTimeoutSeconds,
        Action<string> log,
        CancellationToken outerCt)
    {
        string sysPrompt = $$"""
You are calibrating a mouse cursor for a "busy user" automation bot.

The image you see is a native-resolution screenshot of a region of the
screen. A red crosshair with a black halo is drawn at the EXACT cursor
pixel (it is the cursor's true position, not the visible Windows arrow
which may render with a small offset). The crosshair is USUALLY near
the centre of the image, but when the cursor is close to a screen edge
the crop is clamped to stay on-screen, so the crosshair can be anywhere
inside the image — always trust where you see the crosshair, not where
you expect it to be.

Your job: determine whether the crosshair is centred on the TARGET.
TARGET: {{targetDescription}}

Output strict JSON only — no prose, no Markdown fences:
{
  "reasoning": "short note on where the crosshair is vs. the target",
  "actions": [
    {"type":"move","x":<dx>,"y":<dy>,"absolute":false}
  ],
  "done": <true if crosshair is on the target, else false>
}

Rules:
- dx,dy are pixel offsets in this image. Positive dx moves the cursor
  right; positive dy moves it down. The image is at the screen's native
  resolution, so 1 image pixel == 1 screen pixel.
- If the crosshair is already on the target, set done=true and emit
  an empty actions array.
- If the target is visible, compute the offset from the crosshair to
  the target's visual centre (or the centre of its clickable hit area).
- If the target is NOT visible in this crop, pick the direction you
  would expect it to be and emit a single capped move with
  |dx|,|dy| <= 480.
- Only "move" actions with "absolute":false are allowed.
""";

        for (int r = 0; r < Rounds; r++)
        {
            outerCt.ThrowIfCancellationRequested();
            var cur = Cursor.Position;
            var crop = ScreenCapture.GrabRegionAroundCursor(cur, RegionSize, RegionSize, out var crossInImg);
            var userPrompt =
                $"MODE: TARGETING\n\n" +
                $"GOAL: centre the crosshair on {targetShortName}.\n" +
                $"TARGET (full): {targetDescription}\n\n" +
                $"IMAGE: {crop.SentWidth}x{crop.SentHeight} native pixels; crosshair is at ({crossInImg.X},{crossInImg.Y}).\n" +
                $"Reply with TARGETING JSON only.";

            AiDecision decision;
            try
            {
                using var rcts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                rcts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, aiTimeoutSeconds)));
                decision = await ai.GenerateActionsAsync(sysPrompt, userPrompt, crop, rcts.Token);
            }
            catch (OperationCanceledException)
            {
                log($"  refine[{r + 1}]: AI call timed out, stopping refinement");
                return;
            }
            catch (Exception ex)
            {
                log($"  refine[{r + 1}]: AI call failed: {ex.Message}");
                return;
            }

            int dx = 0, dy = 0;
            bool gotMove = false;
            foreach (var a in decision.Actions)
            {
                if (a.Type == "move" && (a.X is not null || a.Y is not null))
                {
                    dx = a.X ?? 0; dy = a.Y ?? 0; gotMove = true; break;
                }
            }

            bool onTarget = decision.Done
                            || !gotMove
                            || (Math.Abs(dx) <= OnTargetThreshold && Math.Abs(dy) <= OnTargetThreshold);
            if (onTarget)
            {
                log($"  refine[{r + 1}]: cursor at ({cur.X},{cur.Y}) — on target ({decision.Reasoning})");
                return;
            }

            log($"  refine[{r + 1}]: cursor at ({cur.X},{cur.Y}); nudge=({dx:+#;-#;0},{dy:+#;-#;0}) — {decision.Reasoning}");

            var nudge = new HidAction[] { new("move", X: dx, Y: dy, Absolute: false) };
            var nudgeHid = HidScaler.CompensateForDpi(nudge, dpiScale);
            try
            {
                using var ncts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                ncts.CancelAfter(TimeSpan.FromSeconds(8));
                await hw.SendAsync(nudgeHid, ncts.Token);
            }
            catch (Exception ex)
            {
                log($"  refine[{r + 1}]: nudge send failed: {ex.Message}");
                return;
            }
            await Task.Delay(150, outerCt);
        }

        var finalCur = Cursor.Position;
        log($"  refine: exhausted {Rounds} rounds, final cursor=({finalCur.X},{finalCur.Y})");
    }
}
