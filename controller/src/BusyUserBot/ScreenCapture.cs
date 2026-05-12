using System.Drawing.Imaging;
using BusyUserBot.Models;

namespace BusyUserBot;

/// <summary>
/// Captures the primary screen as a PNG byte array suitable for sending to a
/// vision model. Resizes large screens to keep the prompt cheap.
/// </summary>
public static class ScreenCapture
{
    public sealed record Capture(byte[] PngBytes, int OriginalWidth, int OriginalHeight,
                                 int SentWidth, int SentHeight)
    {
        /// <summary>
        /// Horizontal pixels on the real screen per pixel in the sent image.
        /// </summary>
        public double ScaleX => SentWidth  > 0 ? (double)OriginalWidth  / SentWidth  : 1.0;
        public double ScaleY => SentHeight > 0 ? (double)OriginalHeight / SentHeight : 1.0;

        /// <summary>
        /// Map an x/y coordinate authored against the sent image back to real
        /// screen pixels and clamp to the screen bounds. Safe to call on
        /// already-real coordinates only when SentWidth==OriginalWidth.
        /// </summary>
        public (int x, int y) MapPoint(int x, int y)
        {
            int mx = (int)Math.Round(x * ScaleX, MidpointRounding.AwayFromZero);
            int my = (int)Math.Round(y * ScaleY, MidpointRounding.AwayFromZero);
            if (mx < 0) mx = 0;
            if (my < 0) my = 0;
            if (OriginalWidth  > 0 && mx > OriginalWidth  - 1) mx = OriginalWidth  - 1;
            if (OriginalHeight > 0 && my > OriginalHeight - 1) my = OriginalHeight - 1;
            return (mx, my);
        }

        /// <summary>
        /// Returns a copy of <paramref name="action"/> whose x/y (if present)
        /// have been scaled from sent-image coordinates to real screen
        /// coordinates and clamped to the screen.
        /// </summary>
        public HidAction MapAction(HidAction action)
        {
            if (action.X is null && action.Y is null) return action;
            int sx = action.X ?? 0;
            int sy = action.Y ?? 0;
            var (mx, my) = MapPoint(sx, sy);
            return action with
            {
                X = action.X is null ? null : mx,
                Y = action.Y is null ? null : my,
            };
        }

        public IReadOnlyList<HidAction> MapActions(IReadOnlyList<HidAction> actions)
        {
            // Avoid allocating when nothing has coordinates.
            bool anyCoords = false;
            for (int i = 0; i < actions.Count; i++)
                if (actions[i].X is not null || actions[i].Y is not null) { anyCoords = true; break; }
            if (!anyCoords) return actions;

            var result = new HidAction[actions.Count];
            for (int i = 0; i < actions.Count; i++) result[i] = MapAction(actions[i]);
            return result;
        }
    }

    public static Capture Grab(int maxLongEdge = 1920)
    {        var bounds = Screen.PrimaryScreen?.Bounds
                     ?? new Rectangle(0, 0, 1920, 1080);

        using var raw = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(raw))
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        // Down-scale if needed so the image stays small for the LLM.
        int longEdge = Math.Max(bounds.Width, bounds.Height);
        if (longEdge <= maxLongEdge)
        {
            return new Capture(ToPng(raw), bounds.Width, bounds.Height, bounds.Width, bounds.Height);
        }

        double scale = (double)maxLongEdge / longEdge;
        int w = (int)Math.Round(bounds.Width * scale);
        int h = (int)Math.Round(bounds.Height * scale);
        using var scaled = new Bitmap(w, h);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(raw, 0, 0, w, h);
        }
        return new Capture(ToPng(scaled), bounds.Width, bounds.Height, w, h);
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// Grab a native-resolution rectangle around a screen point (used by the
    /// iterative cursor targeting stage). The crop is clamped to the primary
    /// screen bounds, so when the cursor is near a screen edge it will NOT
    /// sit at the centre of the returned image — <paramref name="crosshairInImage"/>
    /// is set to the cursor's actual pixel inside the crop, and a red
    /// crosshair with a black halo is drawn there so the vision model knows
    /// where the pointer really is.
    /// </summary>
    public static Capture GrabRegionAroundCursor(Point cursor, int width, int height,
                                                 out Point crosshairInImage)
    {
        var screen = Screen.PrimaryScreen?.Bounds
                     ?? new Rectangle(0, 0, 1920, 1080);
        // If the requested crop is larger than the screen, shrink it to fit.
        int w = Math.Min(width, screen.Width);
        int h = Math.Min(height, screen.Height);

        int x = cursor.X - w / 2;
        int y = cursor.Y - h / 2;
        // Clamp so the crop stays inside the screen.
        if (x < screen.X) x = screen.X;
        if (y < screen.Y) y = screen.Y;
        if (x + w > screen.Right)  x = screen.Right  - w;
        if (y + h > screen.Bottom) y = screen.Bottom - h;
        var rect = new Rectangle(x, y, w, h);

        // Cursor's pixel inside the crop (may be anywhere in [0,w) x [0,h)
        // depending on clamping near screen edges).
        int cx = Math.Clamp(cursor.X - rect.X, 0, w - 1);
        int cy = Math.Clamp(cursor.Y - rect.Y, 0, h - 1);
        crosshairInImage = new Point(cx, cy);

        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        try
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(rect.Location, Point.Empty, rect.Size);

                using var penOuter = new Pen(Color.Black, 4);
                using var penInner = new Pen(Color.Red, 2);
                const int arm = 18;
                // Black halo first so the crosshair stays visible on any background.
                g.DrawLine(penOuter, cx - arm, cy, cx + arm, cy);
                g.DrawLine(penOuter, cx, cy - arm, cx, cy + arm);
                g.DrawLine(penInner, cx - arm, cy, cx + arm, cy);
                g.DrawLine(penInner, cx, cy - arm, cx, cy + arm);
                g.DrawEllipse(penInner, cx - 4, cy - 4, 8, 8);
            }
            return new Capture(ToPng(bmp), w, h, w, h);
        }
        finally
        {
            bmp.Dispose();
        }
    }
}
