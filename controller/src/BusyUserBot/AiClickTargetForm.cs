namespace BusyUserBot;

/// <summary>
/// Popup used by the AI Test Mouse self-test. Intentionally styled like a
/// generic Windows dialog (system colours, regular caption bar, normal-sized
/// buttons) so the vision model has to localise it the way it would localise
/// any real dialog rather than chase a giant high-contrast lure. The window
/// is placed at a random on-screen location each run (kept fully visible
/// inside the primary monitor's working area) so a model that memorised
/// "centre of screen" can't cheat.
/// </summary>
internal sealed class AiClickTargetForm : Form
{
    public event EventHandler? ButtonClicked;

    private readonly Button _okButton;

    /// <summary>Screen rectangle of the OK button (for diagnostics).</summary>
    public Rectangle DismissButtonScreenRect =>
        _okButton.IsHandleCreated
            ? _okButton.RectangleToScreen(_okButton.ClientRectangle)
            : Rectangle.Empty;

    public AiClickTargetForm()
    {
        Text = "Self-test";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(380, 160);
        // Use system colours so the dialog blends in with normal Windows UI.
        BackColor = SystemColors.Control;
        ForeColor = SystemColors.ControlText;
        Font = SystemFonts.MessageBoxFont!;
        StartPosition = FormStartPosition.Manual; // we'll place it ourselves

        var prompt = new Label
        {
            Text = "BusyUserBot self-test.\r\nClick OK to continue.",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(16, 16),
            Size = new Size(ClientSize.Width - 32, 60),
        };

        var ok = new Button
        {
            Text = "OK",
            Size = new Size(96, 30),
            UseVisualStyleBackColor = true,
            TabStop = true,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            Size = new Size(96, 30),
            UseVisualStyleBackColor = true,
            TabStop = true,
            Enabled = false, // visible decoy; not the test target
        };
        ok.Location     = new Point(ClientSize.Width - ok.Width - cancel.Width - 24,
                                     ClientSize.Height - ok.Height - 12);
        cancel.Location = new Point(ClientSize.Width - cancel.Width - 12,
                                     ClientSize.Height - cancel.Height - 12);

        AcceptButton = ok;
        ok.Click += (_, _) =>
        {
            ButtonClicked?.Invoke(this, EventArgs.Empty);
            Close();
        };

        Controls.Add(prompt);
        Controls.Add(ok);
        Controls.Add(cancel);
        _okButton = ok;

        Load += (_, _) => PlaceRandomly();
    }

    private void PlaceRandomly()
    {
        var area = Screen.PrimaryScreen?.WorkingArea
                   ?? new Rectangle(0, 0, 1920, 1080);
        // Keep a margin so the dialog never hugs the edge (and so the title
        // bar / buttons stay fully readable).
        const int margin = 60;
        int maxX = Math.Max(area.X + margin + 1, area.Right  - Width  - margin);
        int maxY = Math.Max(area.Y + margin + 1, area.Bottom - Height - margin);
        var rng = Random.Shared;
        int x = rng.Next(area.X + margin, maxX);
        int y = rng.Next(area.Y + margin, maxY);
        Location = new Point(x, y);
    }
}
