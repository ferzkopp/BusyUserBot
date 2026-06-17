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
        CancellationToken outerCt,
        double calibrationGain = 1.0)
    {
        string sysPrompt = $$"""
You are calibrating a mouse cursor for a "busy user" automation bot.

The image you see is a native-resolution screenshot of a region of the
screen. A red crosshair with a black halo is drawn at the EXACT cursor
pixel (it is the cursor's true position, not the visible Windows arrow
which may render with a small offset). The crosshair is USUALLY near
the centre of the image, but when the cursor is close to a screen edge
the crop is clamped to stay on-screen, so the crosshair can be anywhere
inside the image — always trust where you see the crosshair.

IMPORTANT: the controller already knows the cursor's exact pixel and can
move it ACCURATELY to any pixel you name. You do NOT need to compute
movement offsets, deltas, or do any arithmetic relative to the crosshair —
attempting that only introduces errors. Your ONLY job is to locate the
TARGET and report its pixel coordinates in this image. The controller will
move the cursor precisely onto the coordinates you report.

TARGET: {{targetDescription}}

Output strict JSON only — no prose, no Markdown fences:
{
  "reasoning": "short note on where the target is in the image",
  "actions": [
    {"type":"move","x":<targetX>,"y":<targetY>,"absolute":true}
  ],
  "done": <true if the crosshair is already on the target, else false>
}

Rules:
- x,y are the TARGET's pixel coordinates in THIS image: its visual centre,
  or the centre of its clickable hit area. Origin (0,0) is the image's
  top-left; x grows right, y grows down. 1 image pixel == 1 screen pixel.
- Do NOT output offsets or deltas from the crosshair. Always report the
  target's ABSOLUTE position in the image; the controller computes the
  move itself.
- If the crosshair is already centred on the target, set done=true and
  emit an empty actions array.
- If the target is NOT visible in this crop, set done=false and report the
  pixel on the image edge in the direction you expect the target to be, so
  the controller can pan toward it.
- Only "move" actions with "absolute":true are allowed.
""";

        for (int r = 0; r < Rounds; r++)
        {
            outerCt.ThrowIfCancellationRequested();
            var cur = Cursor.Position;
            var crop = ScreenCapture.GrabRegionAroundCursor(cur, RegionSize, RegionSize, out var crossInImg);
            var userPrompt =
                $"MODE: TARGETING\n\n" +
                $"GOAL: report the pixel coordinates of {targetShortName} in this image.\n" +
                $"TARGET (full): {targetDescription}\n\n" +
                $"IMAGE: {crop.SentWidth}x{crop.SentHeight} native pixels; the crosshair (current cursor) is at ({crossInImg.X},{crossInImg.Y}).\n" +
                $"The controller will move the cursor precisely onto the coordinates you report, so just give the target's pixel position.\n" +
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
                    // The model reports the TARGET's absolute pixel in the image.
                    // The controller knows the crosshair pixel exactly, so it
                    // computes the nudge here rather than trusting the model's
                    // arithmetic. Tolerate a model that still emits a relative
                    // offset (absolute:false) by using the values as-is.
                    if (a.Absolute == false)
                    {
                        dx = a.X ?? 0;
                        dy = a.Y ?? 0;
                    }
                    else
                    {
                        dx = (a.X ?? crossInImg.X) - crossInImg.X;
                        dy = (a.Y ?? crossInImg.Y) - crossInImg.Y;
                    }
                    gotMove = true;
                    break;
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
            var nudgeHid = HidScaler.CompensateForDpi(nudge, dpiScale, calibrationGain);
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
