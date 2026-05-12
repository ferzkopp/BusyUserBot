using BusyUserBot.Models;

namespace BusyUserBot;

/// <summary>
/// Single chokepoint for the two coordinate transforms that sit between the
/// vision model and the HID dongle:
///
///   1. <b>Screenshot scaling</b> — the model authors actions in the
///      coordinate space of the (often down-scaled) screenshot we sent it,
///      not the real screen. <see cref="ScreenCapture.Capture.MapActions"/>
///      converts those back to physical screen pixels.
///
///   2. <b>Display DPI scaling</b> — the dongle emits relative HID mickeys,
///      and Windows multiplies each mickey by the active monitor's effective
///      scale factor (e.g. 1.5× at 150% DPI) before moving the cursor. We
///      pre-divide by that scale so the slam-to-origin + walk approximation
///      of absolute positioning lands on the requested physical pixel.
///      See <see cref="HidScaler"/>.
///
/// The third "factor" — Windows mouse settings (Enhance pointer precision,
/// pointer-speed slider) — is *not* compensated here because it's not a
/// linear transform; instead we detect non-default values at startup and
/// warn the user (see MainForm.WarnIfEnhancePointerPrecisionEnabled /
/// WarnIfMouseSpeedNotDefault).
/// </summary>
internal static class HidCoordinatePipeline
{
    public sealed record Result(
        IReadOnlyList<HidAction> ScreenActions,
        IReadOnlyList<HidAction> HidActions,
        double DpiScale);

    /// <summary>
    /// Run the full image → screen → HID pipeline. Returns both the
    /// intermediate screen-pixel actions (useful for logging "where the
    /// model wanted to click") and the DPI-compensated actions to send to
    /// the firmware.
    /// </summary>
    public static Result Transform(ScreenCapture.Capture shot, IReadOnlyList<HidAction> actions)
    {
        var screen = shot.MapActions(actions);
        var dpi = HidScaler.GetPrimaryScale();
        var hid = HidScaler.CompensateForDpi(screen, dpi);
        return new Result(screen, hid, dpi);
    }
}
