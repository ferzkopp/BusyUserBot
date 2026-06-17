namespace BusyUserBot;

/// <summary>
/// A small, clickable dialog positioned at a random screen location.
/// Used by the "Test mouse" button to verify that moves and clicks land accurately.
/// Accepts clicks (from the dongle or manually) and tracks three kinds:
/// left single click, left double click, and right click. Once all three
/// kinds have been received the dialog reports success and closes itself.
/// </summary>
public sealed class MouseTestDialog : Form
{
    private readonly Button _targetButton;
    private readonly Action<string>? _log;
    private bool _leftClicked;
    private bool _doubleClicked;
    private bool _rightClicked;

    // Timestamp of the previous left click, used to detect a double click by
    // timing two rapid clicks (a WinForms Button raises two Click events for a
    // double click rather than the DoubleClick event).
    private DateTime _lastLeftClickUtc = DateTime.MinValue;

    /// <param name="log">Optional callback to surface click events in the main log.</param>
    public MouseTestDialog(Action<string>? log = null)
    {
        _log = log;

        Text = "Click me!";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(200, 140);
        BackColor = Color.LightBlue;
        Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold);

        _targetButton = new Button
        {
            Text = "Click here\n(left, double, right)",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.CornflowerBlue,
            ForeColor = Color.White,
            Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold),
            Enabled = true,
            Visible = true,
            AutoSize = false,
            TabStop = true,
        };

        // Single left click -> Click event (fires for left button only).
        _targetButton.Click += (_, _) => RegisterLeftClick();

        // Double left click -> DoubleClick event.
        _targetButton.DoubleClick += (_, _) => RegisterDoubleClick();

        // Right click -> detect via MouseUp (most reliable for the right button).
        _targetButton.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) RegisterRightClick();
        };

        Controls.Add(_targetButton);

        // Also accept clicks that land on the form itself (belt-and-suspenders).
        MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) RegisterRightClick();
        };
        Click += (_, _) => RegisterLeftClick();
        DoubleClick += (_, _) => RegisterDoubleClick();

        // Keep the dialog focused so it reliably receives the injected HID clicks.
        Activated += (_, _) => _targetButton.Focus();

        // Trap Escape to abort the test without satisfying the remaining clicks.
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                _log?.Invoke("Mouse test dialog: Escape pressed — aborting");
                Close();
            }
        };
    }

    private void RegisterLeftClick()
    {
        var now = DateTime.UtcNow;

        // Two left clicks within the system double-click time count as a double.
        double sinceLastMs = (now - _lastLeftClickUtc).TotalMilliseconds;
        if (sinceLastMs <= SystemInformation.DoubleClickTime && !_doubleClicked)
        {
            _doubleClicked = true;
            _log?.Invoke("Mouse test dialog: ✓ received LEFT DOUBLE click");
        }
        _lastLeftClickUtc = now;

        if (!_leftClicked)
        {
            _leftClicked = true;
            _log?.Invoke("Mouse test dialog: ✓ received LEFT SINGLE click");
        }
        CheckAllReceived();
    }

    private void RegisterDoubleClick()
    {
        // A double click also raises a single Click first; make sure both flags set.
        if (!_leftClicked)
        {
            _leftClicked = true;
            _log?.Invoke("Mouse test dialog: ✓ received LEFT SINGLE click");
        }
        if (!_doubleClicked)
        {
            _doubleClicked = true;
            _log?.Invoke("Mouse test dialog: ✓ received LEFT DOUBLE click");
        }
        CheckAllReceived();
    }

    private void RegisterRightClick()
    {
        if (!_rightClicked)
        {
            _rightClicked = true;
            _log?.Invoke("Mouse test dialog: ✓ received RIGHT click");
        }
        CheckAllReceived();
    }

    private void CheckAllReceived()
    {
        if (_leftClicked && _doubleClicked && _rightClicked)
        {
            _log?.Invoke("Mouse test dialog: all 3 click types received — closing");
            BeginInvoke(new Action(Close));
        }
    }

    /// <summary>
    /// Place the dialog at a random position on the primary screen,
    /// ensuring it stays fully visible.
    /// </summary>
    public void PlaceRandomly()
    {
        var screen = Screen.PrimaryScreen;
        if (screen == null) return;

        var bounds = screen.WorkingArea;

        // Leave some margin so the dialog doesn't touch screen edges.
        const int margin = 50;
        int maxX = bounds.Right - Width - margin;
        int maxY = bounds.Bottom - Height - margin;

        int x = Math.Max(bounds.Left + margin, new Random().Next(bounds.Left + margin, maxX + 1));
        int y = Math.Max(bounds.Top + margin, new Random().Next(bounds.Top + margin, maxY + 1));

        Location = new Point(x, y);
    }

    /// <summary>
    /// Get the center point of this dialog in screen coordinates.
    /// </summary>
    public Point GetCenterPoint()
    {
        var bounds = Bounds;
        return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    }

    /// <summary>
    /// True if left single click was detected.
    /// </summary>
    public bool LeftClicked => _leftClicked;

    /// <summary>
    /// True if left double click was detected.
    /// </summary>
    public bool DoubleClicked => _doubleClicked;

    /// <summary>
    /// True if right click was detected.
    /// </summary>
    public bool RightClicked => _rightClicked;

    /// <summary>
    /// True once all three click kinds (left, double, right) have been received.
    /// </summary>
    public bool AllReceived => _leftClicked && _doubleClicked && _rightClicked;
}
