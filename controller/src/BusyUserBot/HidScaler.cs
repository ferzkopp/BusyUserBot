using System.Runtime.InteropServices;
using BusyUserBot.Models;

namespace BusyUserBot;

/// <summary>
/// Compensates outgoing relative HID mouse deltas for Windows display scaling.
///
/// The dongle sends raw HID Mouse X/Y reports (mickeys). With "Enhance pointer
/// precision" off and the speed slider at 10, Windows multiplies each mickey
/// by the active display scale factor before moving the cursor: at 150% DPI,
/// a 100-mickey delta moves the cursor 150 physical pixels. To make the
/// "slam-to-origin then walk to (x,y)" approximation of absolute positioning
/// land on the requested screen pixel, divide the requested deltas by the
/// scale factor before handing them to the firmware.
/// </summary>
internal static class HidScaler
{
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>Effective DPI scale of the primary monitor (1.0 = 96 DPI).</summary>
    public static double GetPrimaryScale()
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch { /* fall through */ }
        return 1.0;
    }

    /// <summary>
    /// Returns a copy of <paramref name="actions"/> with x/y of move actions
    /// (and dx/dy of relative-only moves) divided by the host display scale,
    /// so that the dongle's relative-HID emulation lands on the requested
    /// physical pixel. No-op when scale ≈ 1.
    /// </summary>
    public static IReadOnlyList<HidAction> CompensateForDpi(IReadOnlyList<HidAction> actions, double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.01) return actions;
        var inv = 1.0 / scale;
        var result = new HidAction[actions.Count];
        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            if (a.Type == "move" && (a.X is not null || a.Y is not null))
            {
                result[i] = a with
                {
                    X = a.X is int ax ? (int)Math.Round(ax * inv, MidpointRounding.AwayFromZero) : null,
                    Y = a.Y is int ay ? (int)Math.Round(ay * inv, MidpointRounding.AwayFromZero) : null,
                };
            }
            else
            {
                result[i] = a;
            }
        }
        return result;
    }
}
