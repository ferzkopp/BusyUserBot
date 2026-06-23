using BusyUserBot.AI;
using BusyUserBot.Models;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BusyUserBot;

/// <summary>
/// Single window: configuration, playbook loading, task selection, Start
/// button and a log box. When Start is pressed we hide to the tray and run
/// <see cref="BotLoop"/>; clicking the tray icon stops the loop and shows
/// the window again.
/// </summary>
public sealed class MainForm : Form
{
    private readonly bool _dryRun;
    private readonly bool _fakeAi;
    private AppConfig _cfg;

    // Hardware / AI settings.
    private readonly TextBox _name = new();
    private readonly TextBox _deviceId = new() { ReadOnly = true };
    private readonly TextBox _token = new() { UseSystemPasswordChar = true };
    private readonly ComboBox _engine = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _endpoint = new();
    private readonly ComboBox _model = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _refreshModels = new() { Text = "Refresh models" };
    /// <summary>
    /// Free-text-with-suggestions because vendors disagree on the vocabulary:
    /// Nvidia Nemotron only accepts on/off, OpenAI/Qwen3 want low/medium/high,
    /// Qwen3/GLM/DeepSeek toggle via enable_thinking. See
    /// <see cref="AiConfig.ReasoningEffort"/>.
    /// </summary>
    private readonly ComboBox _reasoning = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _apiKey = new() { UseSystemPasswordChar = true };
    private readonly TextBox _azureDeployment = new();
    // Labels for Azure-only fields, captured at row construction so we can
    // grey them out together with their text boxes when the engine isn't Azure.
    private Label? _apiKeyLabel;
    private Label? _azureDeploymentLabel;

    // Playbook / loop settings.
    private readonly TextBox _playbookPath = new();
    private readonly Button _browsePlaybook = new();
    private readonly Button _reloadPlaybook = new() { Text = "Reload" };
    private readonly Button _previewPlaybook = new() { Text = "Preview" };
    private readonly Label _playbookStats = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly NumericUpDown _interval = new() { Minimum = 100, Maximum = 60000, Value = 1500, Increment = 100 };
    private readonly NumericUpDown _maxIter = new() { Minimum = 1, Maximum = 10000, Value = 50 };
    private readonly NumericUpDown _aiTimeoutSec = new() { Minimum = 10, Maximum = 600, Value = 180, Increment = 5 };

    // Action buttons.
    private const string TestHwLabel = "Test HW";
    private const string TestAiLabel = "Test AI";
    private const string TestMouseLabel = "Test mouse";
    private const string CalibrateMouseLabel = "Calibrate mouse";
    private const string StartLabel = "Start";
    private const string StopLabel = "Stop";
    private readonly Button _start = new() { Text = StartLabel };
    private readonly CheckBox _keepOpen = new() { Text = "Keep window open", AutoSize = true };
    private readonly Button _testHw = new() { Text = TestHwLabel };
    private readonly Button _testAi = new() { Text = TestAiLabel };
    private readonly Button _testMouse = new() { Text = TestMouseLabel, Enabled = false };
    private readonly Button _calibrateMouse = new() { Text = CalibrateMouseLabel, Enabled = false };

    // Status indicators sitting next to each test button: an animated spinner
    // while the test runs, a green check on success, a red cross on failure.
    private readonly Label _hwStatus = new();
    private readonly Label _aiStatus = new();
    private readonly Label _mouseStatus = new();
    private readonly Label _calibrateMouseStatus = new();

    private enum TestState { Idle, Running, Pass, Fail }

    // Classic ASCII spinner — renders reliably in the default WinForms font.
    private static readonly string[] SpinnerFrames = { "|", "/", "-", "\\" };
    private readonly System.Windows.Forms.Timer _spinnerTimer = new() { Interval = 110 };
    private int _spinnerFrame;

    // Log.
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font(FontFamily.GenericMonospace, 8) };

    private NotifyIcon? _tray;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Playbook? _playbook;

    private bool _hwTestPassed;
    private bool _aiTestPassed;
    private bool _isRefreshingModels;

    /// <summary>
    /// Cancels any in-flight <see cref="OnTestAiClicked"/> /
    /// <see cref="OnTestAiMouseClicked"/> work. Tripped when the user changes
    /// the AI engine / endpoint / model so that a still-running test against
    /// the previous configuration is aborted instead of silently completing
    /// (and re-enabling Start) for the wrong model.
    /// </summary>
    private CancellationTokenSource? _aiTestCts;

    public MainForm(bool dryRun, bool fakeAi)
    {
        _dryRun = dryRun;
        _fakeAi = fakeAi;
        _cfg = ConfigStore.Load();

        Text = "Busy User Bot" + (dryRun ? "  [dry-run]" : "") + (fakeAi ? "  [fake-ai]" : "");
        Width = 1120;
        Height = 1120;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 960);

        BuildUi();
        BindFromConfig();
        BuildTray();
        SeedDefaultPlaybook();
        TryLoadPlaybook(silent: true);

        _start.Enabled = false;
        Load += OnFormLoad;
        FormClosing += OnClosing;
    }

    private void OnFormLoad(object? sender, EventArgs e)
    {
        WarnIfEnhancePointerPrecisionEnabled();
        WarnIfMouseSpeedNotDefault();
        var scale = HidScaler.GetPrimaryScale();
        Log($"Display scale (primary monitor) = {scale * 100:0}% ({(scale > 1.01 ? "outgoing mouse coords will be divided by this" : "no compensation needed")}).");
        _ = RefreshModelChoicesAsync(userInitiated: false);
        OnTestHardwareClicked(null, EventArgs.Empty);
        OnTestAiClicked(null, EventArgs.Empty);
    }

    // -------------------------------------------------------------------
    // "Enhance pointer precision" detector
    // -------------------------------------------------------------------
    // Windows non-linearly damps small/medium relative HID mouse deltas when
    // this is on, which breaks our slam-to-origin + walk approximation of
    // absolute positioning. Detect and warn so the user knows to disable it.

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
                                                    [Out] int[] pvParam, uint fWinIni);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfoOut(uint uiAction, uint uiParam,
                                                       out int pvParam, uint fWinIni);
    private const uint SPI_GETMOUSE = 0x0003;
    private const uint SPI_GETMOUSESPEED = 0x0070;

    private void WarnIfEnhancePointerPrecisionEnabled()
    {
        try
        {
            var buf = new int[3];
            if (!SystemParametersInfo(SPI_GETMOUSE, 0, buf, 0)) return;
            // [threshold1, threshold2, accelerationEnabled]
            // "Enhance pointer precision" is on iff all three are non-zero.
            bool epp = buf[0] != 0 && buf[1] != 0 && buf[2] != 0;
            if (epp)
            {
                Log("WARNING: Windows 'Enhance pointer precision' (mouse acceleration) is ENABLED on this PC.");
                Log("  This damps relative HID deltas non-linearly and makes absolute mouse positioning inaccurate.");
                Log("  Disable it: Settings → Bluetooth & devices → Mouse → Additional mouse settings → Pointer Options.");
            }
            else
            {
                Log("Mouse: 'Enhance pointer precision' is off (good for absolute positioning).");
            }
        }
        catch (Exception ex)
        {
            Log("Mouse: could not read pointer-precision setting: " + ex.Message);
        }
    }

    // Windows mouse "speed" slider (Pointer Options, 1..20). Even with EPP off
    // the slider scales every relative HID delta: 10 ≈ 1.0×, <10 attenuates,
    // >10 amplifies. Anything other than 10 makes our chunked-relative "absolute"
    // positioning over- or under-shoot.
    private void WarnIfMouseSpeedNotDefault()
    {
        try
        {
            if (!SystemParametersInfoOut(SPI_GETMOUSESPEED, 0, out int speed, 0)) return;
            if (speed == 10)
            {
                Log($"Mouse: pointer speed slider = {speed}/20 (default, 1.0× scaling).");
            }
            else
            {
                Log($"WARNING: Windows mouse pointer speed slider = {speed}/20 (default is 10).");
                Log("  This scales every relative HID delta and will make absolute mouse positioning over/undershoot.");
                Log("  Set it to 10: Settings → Bluetooth & devices → Mouse → Mouse pointer speed (or Additional mouse settings → Pointer Options).");
            }
        }
        catch (Exception ex)
        {
            Log("Mouse: could not read pointer-speed setting: " + ex.Message);
        }
    }

    // -------------------------------------------------------------------
    // UI construction
    // -------------------------------------------------------------------

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16, 12, 16, 12),
            AutoSize = false,
        };
        // Let the label column grow to fit its widest caption (e.g. "Azure
        // deployment:") instead of a fixed width that clips longer labels,
        // especially once DPI scaling enlarges the font.
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        const int rowH = 34;

        void AddRow(string label, Control control, int height = rowH)
        {
            AddRowReturnLabel(label, control, height);
        }

        Label AddRowReturnLabel(string label, Control control, int height = rowH)
        {
            int row = root.RowCount;
            root.RowCount++;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 0, 12, 0),
            };
            root.Controls.Add(lbl, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 3, 0, 3);
            root.Controls.Add(control, 1, row);
            return lbl;
        }

        void AddSectionHeader(string text)
        {
            int row = root.RowCount;
            root.RowCount++;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var lbl = new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
                ForeColor = SystemColors.ControlDarkDark,
                Padding = new Padding(0, 0, 0, 0),
            };
            root.Controls.Add(lbl, 0, row);
            root.SetColumnSpan(lbl, 2);
        }

        void AddSeparator()
        {
            int row = root.RowCount;
            root.RowCount++;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            root.Controls.Add(new Label { Dock = DockStyle.Fill }, 0, row);
        }

        // ---- Dongle section ----
        AddSectionHeader("Dongle");
        AddRow("BLE name:", _name);
        AddRow("Cached device id:", _deviceId);
        AddRow("Token:", _token);

        AddSeparator();

        // ---- AI section ----
        AddSectionHeader("AI Engine");
        _engine.Items.AddRange(Enum.GetNames(typeof(AiEngineKind)));
        _engine.SelectedIndexChanged += async (_, _) =>
        {
            UpdateAzureFieldsEnabled();
            InvalidateAiTest("engine changed");
            await RefreshModelChoicesAsync(userInitiated: false);
        };
        _endpoint.Leave += async (_, _) =>
        {
            InvalidateAiTest("endpoint changed");
            await RefreshModelChoicesAsync(userInitiated: false);
        };
        // Picking a different model from the dropdown (or typing one) must
        // invalidate the previous AI test result so the user is forced to
        // re-run it against the new model. _isRefreshingModels suppresses
        // the reset while RefreshModelChoicesAsync is repopulating items.
        _model.SelectedIndexChanged += (_, _) => InvalidateAiTest("model changed");
        _model.TextChanged          += (_, _) => InvalidateAiTest("model changed");
        AddRow("Engine:", _engine);
        AddRow("Endpoint:", _endpoint);
        AddRow("Model:", _model);
        _refreshModels.Click += OnRefreshModelsClicked;
        AddRow("", _refreshModels, height: 40);

        // Reasoning effort. Options match the vocabularies we know about.
        // Editable so users can type vendor-specific values we haven't
        // catalogued. Changing this counts as an AI-config change so we wipe
        // the previous green tick.
        _reasoning.Items.AddRange(new object[]
        {
            "default", "off", "minimal", "low", "medium", "high", "on",
        });
        _reasoning.SelectedIndexChanged += (_, _) => InvalidateAiTest("reasoning changed");
        _reasoning.TextChanged          += (_, _) => InvalidateAiTest("reasoning changed");
        AddRow("Reasoning:", _reasoning);
        var reasoningTip = new ToolTip();
        reasoningTip.SetToolTip(_reasoning,
            "Reasoning / thinking budget.\n" +
            "  default \u2014 send no hint; use the model's template default.\n" +
            "  off \u2014 suppress reasoning (Nemotron 'off' + Qwen3/GLM/DeepSeek enable_thinking=false).\n" +
            "  on \u2014 force reasoning on (Nemotron 'on' + enable_thinking=true).\n" +
            "  minimal/low/medium/high \u2014 OpenAI / Qwen3-style reasoning_effort levels.");

        _apiKeyLabel          = AddRowReturnLabel("API key:", _apiKey);
        _azureDeploymentLabel = AddRowReturnLabel("Azure deployment:", _azureDeployment);

        AddSeparator();

        // ---- Playbook section ----
        AddSectionHeader("Playbook");
        AddRow("File:", _playbookPath);
        _playbookStats.Dock = DockStyle.Fill;
        _playbookStats.TextAlign = ContentAlignment.MiddleLeft;
        AddRow("", _playbookStats);

        var pbButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 4),
        };
        _browsePlaybook.Text = "Browse\u2026";
        foreach (var btn in new[] { _browsePlaybook, _reloadPlaybook, _previewPlaybook })
        {
            btn.AutoSize = true;
            btn.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            btn.MinimumSize = new Size(85, 32);
            btn.Padding = new Padding(8, 0, 8, 0);
            btn.Margin = new Padding(0, 0, 6, 0);
        }
        _browsePlaybook.Click += OnBrowsePlaybook;
        _reloadPlaybook.Click += (_, _) => TryLoadPlaybook(silent: false);
        _previewPlaybook.Click += (_, _) => ShowPlaybookPreview();
        pbButtons.Controls.AddRange(new Control[] { _browsePlaybook, _reloadPlaybook, _previewPlaybook });
        AddRow("", pbButtons, height: 46);

        AddRow("Interval (ms):", _interval);
        AddRow("Max task runs:", _maxIter);
        AddRow("AI timeout (s):", _aiTimeoutSec);

        AddSeparator();

        // ---- Action buttons ----
        _start.Click += OnStartClicked;
        _testHw.Click += OnTestHardwareClicked;
        _testAi.Click += OnTestAiClicked;
        _testMouse.Click += OnTestMouseClicked;
        _calibrateMouse.Click += OnCalibratMouseClicked;

        _start.Size = new Size(110, 40);
        _start.Font = new Font(_start.Font, FontStyle.Bold);

        var startRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 4),
        };
        _start.Margin = new Padding(0, 0, 12, 0);
        _keepOpen.Margin = new Padding(0, 14, 0, 0);
        startRow.Controls.Add(_start);
        startRow.Controls.Add(_keepOpen);

        var tip = new ToolTip();
        tip.SetToolTip(_keepOpen, "When checked, keep this window visible while the loop runs and use the Stop button here instead of the system tray.");

        var testButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, 6, 0, 6),
        };
        foreach (var btn in new[] { _testHw, _testAi, _testMouse, _calibrateMouse })
        {
            btn.AutoSize = true;
            btn.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            btn.MinimumSize = new Size(0, 38);
            btn.Padding = new Padding(10, 0, 10, 0);
            btn.Margin = new Padding(0, 0, 4, 0);
        }

        // Configure the per-test status indicators (spinner / check / cross).
        foreach (var status in new[] { _hwStatus, _aiStatus, _mouseStatus, _calibrateMouseStatus })
        {
            status.AutoSize = false;
            status.Size = new Size(24, 38);
            status.TextAlign = ContentAlignment.MiddleCenter;
            status.Font = new Font(status.Font.FontFamily, 9f, FontStyle.Bold);
            status.Margin = new Padding(0, 0, 16, 0);
            status.Tag = TestState.Idle;
        }

        _spinnerTimer.Tick += OnSpinnerTick;

        testButtons.Controls.Add(_testHw);
        testButtons.Controls.Add(_hwStatus);
        testButtons.Controls.Add(_testAi);
        testButtons.Controls.Add(_aiStatus);
        testButtons.Controls.Add(_calibrateMouse);
        testButtons.Controls.Add(_calibrateMouseStatus);
        testButtons.Controls.Add(_testMouse);
        testButtons.Controls.Add(_mouseStatus);

        AddRow("", startRow, height: 56);
        AddRow("Test:", testButtons, height: 58);

        // ---- Log ----
        int logRow = root.RowCount;
        root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logLabel = new Label
        {
            Text = "Log:",
            TextAlign = ContentAlignment.TopLeft,
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
            ForeColor = SystemColors.ControlDarkDark,
            Padding = new Padding(0, 6, 0, 0),
        };
        root.Controls.Add(logLabel, 0, logRow);
        _log.Dock = DockStyle.Fill;
        _log.Margin = new Padding(0, 4, 0, 0);
        root.Controls.Add(_log, 1, logRow);

        Controls.Add(root);
    }

    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = false,
            Text = "Busy User Bot",
        };
        _tray.MouseClick += (_, _) => StopAndShow();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Stop and show", null, (_, _) => StopAndShow());
        menu.Items.Add("Exit", null, (_, _) => { StopAndShow(); Close(); });
        _tray.ContextMenuStrip = menu;
    }

    private void BindFromConfig()
    {
        _name.Text = _cfg.Dongle.Name;
        _deviceId.Text = _cfg.Dongle.DeviceId;
        _token.Text = _cfg.Dongle.Token;
        _engine.SelectedItem = _cfg.Ai.Engine.ToString();
        _endpoint.Text = _cfg.Ai.Endpoint;
        _model.Text = _cfg.Ai.Model;
        _reasoning.Text = string.IsNullOrWhiteSpace(_cfg.Ai.ReasoningEffort) ? "default" : _cfg.Ai.ReasoningEffort;
        _apiKey.Text = _cfg.Ai.ApiKey;
        _azureDeployment.Text = _cfg.Ai.AzureDeployment;
        _playbookPath.Text = string.IsNullOrWhiteSpace(_cfg.Loop.PlaybookPath)
            ? PlaybookStore.DefaultPath
            : _cfg.Loop.PlaybookPath;
        _interval.Value = _cfg.Loop.IntervalMs;
        _maxIter.Value = _cfg.Loop.MaxIterations;
        _aiTimeoutSec.Value = Math.Clamp(_cfg.Loop.AiTimeoutSeconds, (int)_aiTimeoutSec.Minimum, (int)_aiTimeoutSec.Maximum);
        UpdateAzureFieldsEnabled();
    }

    /// <summary>
    /// API key + Azure deployment are only meaningful for the Azure OpenAI
    /// engine; grey them out (label + textbox) for LM Studio / Fake so it's
    /// obvious they're inert.
    /// </summary>
    private void UpdateAzureFieldsEnabled()
    {
        bool azure = _engine.SelectedItem is string s
                     && Enum.TryParse<AiEngineKind>(s, out var k)
                     && k == AiEngineKind.AzureOpenAI;
        _apiKey.Enabled = azure;
        _azureDeployment.Enabled = azure;
        if (_apiKeyLabel is not null)
            _apiKeyLabel.ForeColor = azure ? SystemColors.ControlText : SystemColors.GrayText;
        if (_azureDeploymentLabel is not null)
            _azureDeploymentLabel.ForeColor = azure ? SystemColors.ControlText : SystemColors.GrayText;
    }

    private void BindToConfig()
    {
        _cfg.Dongle.Name = _name.Text.Trim();
        _cfg.Dongle.Token = _token.Text;
        _cfg.Ai.Engine = Enum.Parse<AiEngineKind>((string)_engine.SelectedItem!);
        _cfg.Ai.Endpoint = _endpoint.Text.Trim();
        _cfg.Ai.Model = _model.Text.Trim();
        // Normalise "default"/empty to "" in storage so the engine treats it
        // as "send no reasoning hint".
        var reasoning = _reasoning.Text.Trim();
        _cfg.Ai.ReasoningEffort = reasoning.Equals("default", StringComparison.OrdinalIgnoreCase) ? "" : reasoning;
        _cfg.Ai.ApiKey = _apiKey.Text;
        _cfg.Ai.AzureDeployment = _azureDeployment.Text.Trim();
        _cfg.Loop.PlaybookPath = _playbookPath.Text.Trim();
        _cfg.Loop.IntervalMs = (int)_interval.Value;
        _cfg.Loop.MaxIterations = (int)_maxIter.Value;
        _cfg.Loop.AiTimeoutSeconds = (int)_aiTimeoutSec.Value;
    }

    // -------------------------------------------------------------------
    // Playbook loading
    // -------------------------------------------------------------------

    private void OnBrowsePlaybook(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select playbook file",
            Filter = "JSON files|*.json|All files|*.*",
        };
        if (!string.IsNullOrWhiteSpace(_playbookPath.Text))
            dlg.FileName = _playbookPath.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _playbookPath.Text = dlg.FileName;
            TryLoadPlaybook(silent: false);
        }
    }

    /// <summary>
    /// On first launch the default playbook location won't exist yet. Copy
    /// the bundled example there so the user gets a working starting point.
    /// </summary>
    private void SeedDefaultPlaybook()
    {
        var target = PlaybookStore.DefaultPath;
        if (File.Exists(target)) return;

        // Look next to the running exe for playbook.example.json.
        var exeDir = AppContext.BaseDirectory;
        var example = Path.Combine(exeDir, "playbook.example.json");
        if (!File.Exists(example)) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(example, target);
        }
        catch { /* best-effort; user can browse manually */ }
    }

    private void TryLoadPlaybook(bool silent)
    {
        var path = _playbookPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
            path = PlaybookStore.DefaultPath;

        try
        {
            _playbook = PlaybookStore.Load(path);
            UpdatePlaybookStats();
            if (!silent) Log($"Playbook loaded: {_playbook.Scenarios.Count} scenario(s) from {path}");
        }
        catch (Exception ex)
        {
            _playbook = null;
            UpdatePlaybookStats();
            if (!silent) Log($"Playbook load failed: {ex.Message}");
        }
    }

    private void UpdatePlaybookStats()
    {
        if (_playbook is null)
        {
            _playbookStats.Text = "(not loaded)";
            return;
        }
        int scenarios = _playbook.Scenarios.Count;
        var constraints = _playbook.Constraints.IsEmpty ? "" : ", constraints";
        _playbookStats.Text =
            $"{scenarios} scenario(s), sample={_playbook.ScenarioSampleSize}, "
            + $"planner→validator→executor configured{constraints}";
    }

    private void ShowPlaybookPreview()
    {
        var path = _playbookPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
            path = PlaybookStore.DefaultPath;

        string content;
        try
        {
            if (!File.Exists(path))
            {
                Log($"Preview: file not found — {path}");
                return;
            }
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Log($"Preview: cannot read file — {ex.Message}");
            return;
        }

        // Build a human-readable summary from the parsed playbook (if
        // loaded), falling back to raw JSON if parsing fails.
        string display;
        if (_playbook is not null)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Playbook: {path}");
            sb.AppendLine(new string('─', 60));

            sb.AppendLine($"Scenarios: {_playbook.Scenarios.Count} (sample size = {_playbook.ScenarioSampleSize})");
            int show = Math.Min(8, _playbook.Scenarios.Count);
            for (int i = 0; i < show; i++)
                sb.AppendLine($"  • {_playbook.Scenarios[i]}");
            if (_playbook.Scenarios.Count > show)
                sb.AppendLine($"  … and {_playbook.Scenarios.Count - show} more");

            if (!_playbook.Constraints.IsEmpty)
            {
                sb.AppendLine();
                sb.AppendLine("Global constraints:");
                foreach (var t in _playbook.Constraints.ForbiddenText)
                    sb.AppendLine($"  • forbidden text: \"{t}\"");
                foreach (var c in _playbook.Constraints.ForbiddenKeyChords)
                    sb.AppendLine($"  • forbidden keys: [{string.Join("+", c)}]");
            }

            sb.AppendLine();
            sb.AppendLine("Planner:");
            sb.AppendLine($"  systemPrompt: {(string.IsNullOrWhiteSpace(_playbook.Planner.SystemPrompt) ? "(built-in default)" : $"override, {_playbook.Planner.SystemPrompt.Length} chars")}");
            sb.AppendLine($"  maxStepsPerPlan: {_playbook.Planner.MaxStepsPerPlan}");

            sb.AppendLine();
            sb.AppendLine("Validator:");
            sb.AppendLine($"  systemPrompt: {(string.IsNullOrWhiteSpace(_playbook.Validator.SystemPrompt) ? "(built-in default)" : $"override, {_playbook.Validator.SystemPrompt.Length} chars")}");
            sb.AppendLine($"  maxRetries: {_playbook.Validator.MaxRetries}");

            sb.AppendLine();
            sb.AppendLine("Executor:");
            sb.AppendLine($"  systemPrompt: {(string.IsNullOrWhiteSpace(_playbook.Executor.SystemPrompt) ? "(built-in default)" : $"override, {_playbook.Executor.SystemPrompt.Length} chars")}");
            sb.AppendLine($"  stepRetries: {_playbook.Executor.StepRetries}");
            sb.AppendLine($"  stepDelayMs: {_playbook.Executor.StepDelayMs}");
            sb.AppendLine($"  executorTimeoutSeconds: {_playbook.Executor.ExecutorTimeoutSeconds}");

            display = sb.ToString();
        }
        else
        {
            display = content;
        }

        using var form = new Form
        {
            Text = "Playbook Preview — " + Path.GetFileName(path),
            Width = 700,
            Height = 560,
            StartPosition = FormStartPosition.CenterParent,
        };
        var txt = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9.5f),
            Text = display.Replace("\n", "\r\n"),
        };
        form.Controls.Add(txt);
        form.ShowDialog(this);
    }

    // -------------------------------------------------------------------
    // Logging
    // -------------------------------------------------------------------

    private void Log(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action<string>(Log), line); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
    }

    private void UpdateStartButton()
    {
        _start.Enabled = _hwTestPassed && _aiTestPassed;
        // Dependent test buttons.
        _testMouse.Enabled = _hwTestPassed;
        _calibrateMouse.Enabled = _hwTestPassed;
    }

    // -------------------------------------------------------------------
    // Test-status indicators (spinner / check / cross)
    // -------------------------------------------------------------------

    private IEnumerable<Label> AllStatusLabels
        => new[] { _hwStatus, _aiStatus, _mouseStatus, _calibrateMouseStatus };

    /// <summary>Flag a test as in-progress and start the spinner animation.</summary>
    private void SetTestRunning(Label status)
    {
        status.Tag = TestState.Running;
        status.ForeColor = SystemColors.ControlText;
        status.Text = SpinnerFrames[_spinnerFrame];
        if (!_spinnerTimer.Enabled) _spinnerTimer.Start();
    }

    /// <summary>Flag a finished test with a green check or red cross.</summary>
    private void SetTestResult(Label status, bool ok)
    {
        status.Tag = ok ? TestState.Pass : TestState.Fail;
        status.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        status.Text = ok ? "\u2705" : "\u274c";
        StopSpinnerIfIdle();
    }

    /// <summary>Clear a test's status indicator entirely.</summary>
    private void SetTestIdle(Label status)
    {
        status.Tag = TestState.Idle;
        status.Text = "";
        StopSpinnerIfIdle();
    }

    private void StopSpinnerIfIdle()
    {
        bool anyRunning = AllStatusLabels.Any(l => (l.Tag as TestState?) == TestState.Running);
        if (!anyRunning) _spinnerTimer.Stop();
    }

    private void OnSpinnerTick(object? sender, EventArgs e)
    {
        _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
        foreach (var l in AllStatusLabels)
            if ((l.Tag as TestState?) == TestState.Running)
                l.Text = SpinnerFrames[_spinnerFrame];
    }


    /// <summary>
    /// Reset AI-test state and cancel any in-flight AI test. Called whenever
    /// the user changes a setting that would make the previous green tick
    /// meaningless (engine / endpoint / model). Safe to call repeatedly;
    /// no-ops while <see cref="_isRefreshingModels"/> is true so programmatic
    /// repopulation of the model dropdown doesn't fight the user.
    /// </summary>
    private void InvalidateAiTest(string reason)
    {
        if (_isRefreshingModels) return;

        // Cancel an in-flight test (if any) so its completion handler can't
        // race in and re-flag _aiTestPassed for the previous model.
        try { _aiTestCts?.Cancel(); } catch { /* already disposed */ }

        if (_aiTestPassed || (_aiStatus.Tag as TestState?) != TestState.Idle)
        {
            _aiTestPassed = false;
            _testAi.Text = TestAiLabel;
            SetTestIdle(_aiStatus);
            Log($"AI test reset ({reason}) \u2014 re-run \"{TestAiLabel}\".");
            UpdateStartButton();
        }
    }

    // -------------------------------------------------------------------
    // Hardware test
    // -------------------------------------------------------------------

    private IHardwareClient? _testHwClient;

    private async void OnTestHardwareClicked(object? sender, EventArgs e)
    {
        BindToConfig();
        ConfigStore.Save(_cfg);
        _testHw.Text = TestHwLabel;
        _testHw.Enabled = false;
        _hwTestPassed = false;
        SetTestRunning(_hwStatus);
        UpdateStartButton();

        // Release old test client if any
        if (_testHwClient is not null)
        {
            (_testHwClient as IDisposable)?.Dispose();
        }

        _testHwClient = CreateHardware();
        try
        {
            bool ok = await _testHwClient.ConnectAsync(CancellationToken.None);
            Log(ok ? "HW: connected & authed" : "HW: connect failed");
            _deviceId.Text = _cfg.Dongle.DeviceId;
            SetTestResult(_hwStatus, ok);
            _hwTestPassed = ok;
        }
        catch (Exception ex)
        {
            Log("HW test failed: " + ex.Message);
            SetTestResult(_hwStatus, false);
        }
        finally
        {
            // Do NOT disconnect/dispose immediately! The WinRT BLE stack can 
            // corrupt its GATT cache or fail to reconnect if we drop the physical link 
            // right before starting the bot loop. We just leave it connected.
            _testHw.Enabled = true;
            UpdateStartButton();
        }
    }

    // -------------------------------------------------------------------
    // Mouse test (visible cursor wiggle to confirm HID mouse works)
    // -------------------------------------------------------------------

    private async void OnTestMouseClicked(object? sender, EventArgs e)
    {
        _testMouse.Enabled = false;
        SetTestRunning(_mouseStatus);
        MouseTestDialog? dialog = null;
        try
        {
            // Reuse an already-connected hardware client (from a recent
            // Test HW) to avoid double-disconnect-induced GATT cache issues.
            // Otherwise spin up a fresh one and dispose at the end.
            IHardwareClient hw;
            bool ownsClient = false;
            if (_testHwClient is not null)
            {
                hw = _testHwClient;
            }
            else
            {
                BindToConfig();
                ConfigStore.Save(_cfg);
                hw = CreateHardware();
                ownsClient = true;
                Log("Mouse test: connecting\u2026");
                bool ok = await hw.ConnectAsync(CancellationToken.None);
                if (!ok)
                {
                    Log("Mouse test: connect failed");
                    SetTestResult(_mouseStatus, false);
                    (hw as IDisposable)?.Dispose();
                    return;
                }
            }

            Log("Mouse test: showing target dialog\u2026");

            // Create and show the dialog at a random location on screen.
            // The dialog logs each click it receives and closes itself once all
            // three kinds (left, double, right) have been observed.
            dialog = new MouseTestDialog(Log);
            dialog.PlaceRandomly();
            dialog.TopMost = true;      // Keep dialog on top
            dialog.Show();
            dialog.Activate();          // Activate the dialog window
            dialog.BringToFront();      // Ensure dialog is in front
            dialog.Focus();             // Give focus to the dialog so it receives clicks
            
            // Give the dialog time to fully appear and activate
            await Task.Delay(500);

            // Get the center of the dialog in screen coordinates.
            var dialogCenter = dialog.GetCenterPoint();
            Log($"Mouse test: target at ({dialogCenter.X}, {dialogCenter.Y}), moving cursor\u2026");

            // Use iterative positioning to move the cursor to the dialog center.
            bool reached = await MousePositioner.MoveToAsync(
                hw,
                dialogCenter.X,
                dialogCenter.Y,
                cfg: _cfg.Mouse,
                log: (msg) => Log($"  {msg}"),
                ct: CancellationToken.None);

            if (!reached)
            {
                Log("Mouse test: failed to move to target position");
                SetTestResult(_mouseStatus, false);
                return;
            }

            Log("Mouse test: cursor moved to target");

            // Send all three click types. The dialog records each one and
            // closes itself once all three have arrived.
            Log("Mouse test: [1/3] sending LEFT SINGLE CLICK\u2026");
            await SendClick(hw, "click", "left", 1);
            await Task.Delay(600);  // let the OS register the single click distinctly

            Log("Mouse test: [2/3] sending LEFT DOUBLE CLICK\u2026");
            await SendClick(hw, "click", "left", 2);
            await Task.Delay(600);

            Log("Mouse test: [3/3] sending RIGHT CLICK\u2026");
            await SendClick(hw, "click", "right", 1);

            // Wait (up to 15s) for the dialog to report all three click types.
            Log("Mouse test: waiting for dialog to confirm all click types\u2026");
            const int clickTimeoutSeconds = 15;
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(clickTimeoutSeconds));
            while (!dialog.IsDisposed && !dialog.AllReceived)
            {
                try
                {
                    await Task.Delay(100, waitCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            // Report results based on which click types the dialog observed.
            var clickResults = new List<string>();
            if (dialog.LeftClicked) clickResults.Add("left");
            if (dialog.DoubleClicked) clickResults.Add("double");
            if (dialog.RightClicked) clickResults.Add("right");

            if (dialog.AllReceived)
            {
                Log("Mouse test: ✓ All 3 click types received: left, double, right");
                SetTestResult(_mouseStatus, true);
            }
            else if (clickResults.Count > 0)
            {
                Log($"Mouse test: ⚠ Partial ({clickResults.Count}/3): {string.Join(", ", clickResults)}");
                SetTestResult(_mouseStatus, false);
            }
            else
            {
                Log("Mouse test: ✗ No click types detected");
                SetTestResult(_mouseStatus, false);
            }

            if (ownsClient) (hw as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Log("Mouse test failed: " + ex.Message);
            SetTestResult(_mouseStatus, false);
        }
        finally
        {
            // Clean up the dialog if it's still open.
            if (dialog is not null && !dialog.IsDisposed)
            {
                try { dialog.Close(); } catch { }
                try { dialog.Dispose(); } catch { }
            }
            _testMouse.Enabled = true;
        }
    }

    private async Task SendClick(
        IHardwareClient hw,
        string actionType,
        string button,
        int count)
    {
        try
        {
            var clickAction = new HidAction(actionType, Button: button, Count: count);
            var clickActions = new List<HidAction> { clickAction };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var resp = await hw.SendAsync(clickActions, cts.Token);

            if (!resp.Ok)
                Log($"Mouse test: {button}x{count} send failed: {resp.Error}");
            else
                Log($"Mouse test: {button}x{count} sent to dongle");
        }
        catch (Exception ex)
        {
            Log($"Mouse test: {button}x{count} exception: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------
    // AI test (single action turn against first task/action in playbook)
    // -------------------------------------------------------------------

    private async void OnTestAiClicked(object? sender, EventArgs e)
    {
        BindToConfig();
        ConfigStore.Save(_cfg);

        if (_playbook is null || _playbook.Scenarios.Count == 0)
        {
            Log("AI test: load a playbook with at least one scenario first.");
            return;
        }

        _testAi.Text = TestAiLabel;
        _testAi.Enabled = false;
        _aiTestPassed = false;
        SetTestRunning(_aiStatus);
        UpdateStartButton();

        // Fresh external-cancellation source so a model/engine/endpoint
        // change while this test is running aborts the HTTP call instead of
        // letting it complete and re-flag _aiTestPassed for the old model.
        _aiTestCts?.Dispose();
        _aiTestCts = new CancellationTokenSource();
        var externalToken = _aiTestCts.Token;
        try
        {
            IAiEngine ai = _fakeAi ? new FakeAiEngine() : new OpenAiCompatibleEngine(_cfg.Ai, Log);
            try
            {
                Log("AI test: capturing screen\u2026");
                var shot = ScreenCapture.Grab();
                Log($"AI test: sending {shot.SentWidth}x{shot.SentHeight} screenshot of {shot.OriginalWidth}x{shot.OriginalHeight} screen ({shot.PngBytes.Length / 1024} KiB)");

                var rules = _playbook.Constraints;
                var sample = SampleScenariosForTest(_playbook.ScenarioSampleSize);
                Log($"AI test: scenarios → {string.Join(" | ", sample)}");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

                var plannerSys = Prompts.BuildPlannerSystem(_playbook.Planner.SystemPrompt, rules);
                var plannerUser = Prompts.BuildPlannerUser(sample, _playbook.Planner.MaxStepsPerPlan, shot.SentWidth, shot.SentHeight, null);
                string rawPlan;
                using (var plannerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                {
                    plannerTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _cfg.Loop.AiTimeoutSeconds)));
                    rawPlan = await ai.ChatAsync(plannerSys, plannerUser, shot, plannerTimeout.Token);
                }
                var plan = CommandParser.ParsePlan(rawPlan);
                Log($"AI test: planner goal=\"{plan.Goal}\", {plan.Steps.Count} step(s)");
                for (int i = 0; i < plan.Steps.Count; i++)
                    Log($"  [{i + 1}] {plan.Steps[i].Description}");

                var validatorSys = Prompts.BuildValidatorSystem(_playbook.Validator.SystemPrompt, rules);
                var validatorUser = Prompts.BuildValidatorUser(plan);
                string rawVerdict;
                using (var validatorTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                {
                    validatorTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _cfg.Loop.AiTimeoutSeconds)));
                    rawVerdict = await ai.ChatAsync(validatorSys, validatorUser, shot, validatorTimeout.Token);
                }
                var verdict = CommandParser.ParseValidatorVerdict(rawVerdict);
                Log($"AI test: validator approved={verdict.Approved} ({string.Join("; ", verdict.Reasons)})");

                _aiTestPassed = true;
                SetTestResult(_aiStatus, true);
            }
            finally { (ai as IDisposable)?.Dispose(); }
        }
        catch (OperationCanceledException)
        {
            if (externalToken.IsCancellationRequested)
            {
                Log("AI test: cancelled (config changed)");
                SetTestIdle(_aiStatus);
            }
            else
            {
                Log($"AI test: timed out after {Math.Max(10, _cfg.Loop.AiTimeoutSeconds)}s");
                SetTestResult(_aiStatus, false);
            }
        }
        catch (Exception ex)
        {
            Log("AI test: failed \u2014 " + ex.Message);
            SetTestResult(_aiStatus, false);
        }
        finally
        {
            _testAi.Enabled = true;
            UpdateStartButton();
        }
    }

    // -------------------------------------------------------------------
    // AI Test Mouse: pop up a distinctive dialog and ask the AI to dismiss
    // it by clicking its button. End-to-end check of screen capture +
    // model + coordinate mapping + HID mouse path.
    // -------------------------------------------------------------------

    // -------------------------------------------------------------------
    // Calibrate mouse: measure cursor landing accuracy and store gain
    // -------------------------------------------------------------------

    private async void OnCalibratMouseClicked(object? sender, EventArgs e)
    {
        BindToConfig();
        ConfigStore.Save(_cfg);

        _calibrateMouse.Text = CalibrateMouseLabel;
        _calibrateMouse.Enabled = false;
        SetTestRunning(_calibrateMouseStatus);

        // Hardware client (reuse the connected one from Test HW so we don't
        // churn the GATT cache).
        IHardwareClient hw;
        bool ownsClient = false;
        if (_testHwClient is not null)
        {
            hw = _testHwClient;
        }
        else
        {
            hw = CreateHardware();
            ownsClient = true;
            try
            {
                Log("Calibration: connecting hardware\u2026");
                if (!await hw.ConnectAsync(CancellationToken.None))
                {
                    Log("Calibration: hardware connect failed");
                    SetTestResult(_calibrateMouseStatus, false);
                    (hw as IDisposable)?.Dispose();
                    _calibrateMouse.Enabled = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                Log("Calibration: hardware connect failed: " + ex.Message);
                SetTestResult(_calibrateMouseStatus, false);
                (hw as IDisposable)?.Dispose();
                _calibrateMouse.Enabled = true;
                return;
            }
        }

        try
        {
            Log("Calibration: starting cursor movement accuracy test\u2026");
            bool ok = await MouseCalibrator.CalibrateAsync(
                hw, _cfg.Mouse, Log, CancellationToken.None);

            if (ok)
            {
                ConfigStore.Save(_cfg);
                Log("Calibration: saved to settings.json");
                SetTestResult(_calibrateMouseStatus, true);
            }
            else
            {
                Log("Calibration: FAILED");
                SetTestResult(_calibrateMouseStatus, false);
            }
        }
        catch (Exception ex)
        {
            Log($"Calibration: failed — {ex.Message}");
            SetTestResult(_calibrateMouseStatus, false);
        }
        finally
        {
            if (ownsClient) (hw as IDisposable)?.Dispose();
            _calibrateMouse.Enabled = true;
        }
    }

    private IReadOnlyList<string> SampleScenariosForTest(int count)
    {
        if (_playbook is null) return Array.Empty<string>();
        var rng = new Random();
        int n = Math.Min(count, _playbook.Scenarios.Count);
        var indices = Enumerable.Range(0, _playbook.Scenarios.Count).ToList();
        var picked = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            int j = rng.Next(indices.Count);
            picked.Add(_playbook.Scenarios[indices[j]]);
            indices.RemoveAt(j);
        }
        return picked;
    }

    private IHardwareClient CreateHardware() =>
        _dryRun
            ? new DryRunHardwareClient(Log)
            : new BleHardwareClient(_cfg.Dongle, Log, persisted =>
            {
                _cfg.Dongle = persisted;
                ConfigStore.Save(_cfg);
            });

    private static string FormatAction(HidAction a) => a.Type?.ToLowerInvariant() switch
    {
        "move"    => $"move x={a.X} y={a.Y}{(a.Absolute == true ? " abs" : "")}",
        "click"   => $"click {a.Button ?? "left"}{(a.Count is > 1 ? $" x{a.Count}" : "")}",
        "down"    => $"down {a.Button ?? "left"}",
        "up"      => $"up {a.Button ?? "left"}",
        "scroll"  => $"scroll dy={a.Dy}",
        "type"    => $"type \"{a.Text}\"",
        "key"     => $"key [{string.Join("+", a.Keys ?? Array.Empty<string>())}]",
        "wait"    => $"wait {a.Ms}ms",
        "display" => "display",
        _         => a.ToString(),
    };

    private async void OnRefreshModelsClicked(object? sender, EventArgs e)
    {
        await RefreshModelChoicesAsync(userInitiated: true);
    }

    private static string BuildModelsEndpoint(string endpoint)
    {
        var e = endpoint.Trim();
        if (e.Length == 0) return "";

        e = e.TrimEnd('/');
        const string chatSuffix = "/chat/completions";
        if (e.EndsWith(chatSuffix, StringComparison.OrdinalIgnoreCase))
            e = e[..^chatSuffix.Length];

        return e + "/models";
    }

    private async Task RefreshModelChoicesAsync(bool userInitiated)
    {
        if (_isRefreshingModels) return;
        if (_cfg.Ai.Engine != AiEngineKind.LMStudio && _engine.SelectedItem?.ToString() != AiEngineKind.LMStudio.ToString())
            return;

        var modelsEndpoint = BuildModelsEndpoint(_endpoint.Text);
        if (string.IsNullOrWhiteSpace(modelsEndpoint))
        {
            if (userInitiated) Log("AI: endpoint is empty; cannot fetch models.");
            return;
        }

        _isRefreshingModels = true;
        _refreshModels.Enabled = false;
        var prevLabel = _refreshModels.Text;
        _refreshModels.Text = "Refreshing...";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            if (!string.IsNullOrWhiteSpace(_apiKey.Text))
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey.Text);

            using var resp = await http.GetAsync(modelsEndpoint);
            if (!resp.IsSuccessStatusCode)
            {
                if (userInitiated)
                    Log($"AI: model list request failed ({(int)resp.StatusCode} {resp.ReasonPhrase}) at {modelsEndpoint}");
                return;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                if (userInitiated) Log("AI: model list response missing data array.");
                return;
            }

            var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) models.Add(id.Trim());
                }
            }

            var sorted = models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            var current = _model.Text.Trim();
            var currentInList = current.Length > 0 && sorted.Any(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase));
            var selected = current;
            if (sorted.Count > 0 && (current.Length == 0 || !currentInList))
                selected = sorted[0];

            _model.BeginUpdate();
            _model.Items.Clear();
            foreach (var m in sorted) _model.Items.Add(m);
            _model.Text = selected;
            _model.EndUpdate();

            if (sorted.Count > 0 && !string.Equals(selected, current, StringComparison.OrdinalIgnoreCase))
            {
                var previous = current.Length == 0 ? "(empty)" : current;
                Log($"AI: auto-selected model '{selected}' (previous '{previous}' not available).");
            }

            if (userInitiated || sorted.Count > 0)
                Log($"AI: loaded {sorted.Count} model(s) from {modelsEndpoint}");
        }
        catch (Exception ex)
        {
            if (userInitiated)
                Log("AI: failed to fetch models: " + ex.Message);
        }
        finally
        {
            _refreshModels.Text = prevLabel;
            _refreshModels.Enabled = true;
            _isRefreshingModels = false;
        }
    }

    // -------------------------------------------------------------------
    // Start / Stop
    // -------------------------------------------------------------------

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        // If a loop is already running and the window is still visible (Keep
        // open mode), the Start button doubles as a Stop button.
        if (_loopTask != null && !_loopTask.IsCompleted)
        {
            _cts?.Cancel();
            return;
        }

        BindToConfig();
        ConfigStore.Save(_cfg);

        // Force a fresh reload so the user can edit the playbook between runs.
        TryLoadPlaybook(silent: false);
        if (_playbook is null || _playbook.Scenarios.Count == 0)
        {
            Log("Start: no playbook or no scenarios. Load a playbook first.");
            return;
        }

        IAiEngine ai = _fakeAi
            ? new FakeAiEngine()
            : new OpenAiCompatibleEngine(_cfg.Ai, Log);

        // Pass ownership of the already-connected test hardware client to the loop
        // so we don't drop the physical BLE link resulting in a stale GATT cache.
        IHardwareClient hw = _testHwClient ?? CreateHardware();
        _testHwClient = null;

        _cts = new CancellationTokenSource();
        var loop = new BotLoop(ai, hw, _cfg.Loop, _cfg.Mouse, _playbook, Log);

        bool keepOpen = _keepOpen.Checked;
        if (keepOpen)
        {
            _start.Text = StopLabel;
            _keepOpen.Enabled = false;
        }
        else
        {
            Hide();
            _tray!.Visible = true;
            _tray.ShowBalloonTip(2000, "Busy User Bot", "Running. Click the tray icon to stop.", ToolTipIcon.Info);
        }

        try
        {
            _loopTask = loop.RunAsync(_cts.Token);
            await _loopTask;
        }
        catch (OperationCanceledException)
        {
            if (_cts?.IsCancellationRequested == true)
                Log("Loop cancelled by user.");
            else
                Log("Loop cancelled due to cancellation/timeout.");
        }
        catch (Exception ex) { Log("Loop crashed: " + ex); }
        finally
        {
            await SafeResetAsync(hw);
            (ai as IDisposable)?.Dispose();
            (hw as IDisposable)?.Dispose();
            _loopTask = null;
            if (keepOpen)
            {
                if (InvokeRequired) BeginInvoke(new Action(RestoreStartButton));
                else RestoreStartButton();
            }
            else
            {
                ShowFromTray();
            }
        }
    }

    private void RestoreStartButton()
    {
        _start.Text = StartLabel;
        _keepOpen.Enabled = true;
    }

    private async Task SafeResetAsync(IHardwareClient hw)
    {
        try { await hw.ResetAsync(CancellationToken.None); }
        catch (Exception ex) { Log("reset failed: " + ex.Message); }
    }

    private void StopAndShow()
    {
        _cts?.Cancel();
    }

    private void ShowFromTray()
    {
        if (InvokeRequired) { BeginInvoke(new Action(ShowFromTray)); return; }
        _tray!.Visible = false;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // -------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        BindToConfig();
        ConfigStore.Save(_cfg);
        _cts?.Cancel();
        _tray?.Dispose();
    }
}
