using NAudio.CoreAudioApi;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DualAudio;

internal sealed class MainForm : Form
{
    private readonly Color _background = Color.FromArgb(13, 15, 18);
    private readonly Color _card = Color.FromArgb(24, 26, 30);
    private readonly Color _muted = Color.FromArgb(151, 157, 166);
    private readonly Color _accent = Color.FromArgb(34, 197, 139);
    private readonly AudioMirrorEngine _engine = new();
    private readonly VolumePolicy _volumePolicy = new();
    private VolumeSyncSession? _volumeSession;
    private readonly object _routingGate = new();
    private PlaybackRoutingSession? _routingSession;
    private bool _routingCheckInProgress;
    private CancellationTokenSource? _calibrationCancellation;
    private Task? _calibrationTask;
    private bool _waitingForCalibrationExit;
    private readonly bool _diagnosticsMode;
    private readonly bool _enableMediaKeysInDiagnostics;
    private readonly GlobalMediaKeyHook _mediaKeyHook = new();
    private VolumeOverlayForm? _volumeOverlay;
    private readonly AppSettings _settings = AppSettings.Load();

    private readonly ComboBox _sourceBox = new();
    private readonly ComboBox _targetBox = new();
    private readonly LevelSlider _sourceVolume = new();
    private readonly LevelSlider _targetVolume = new();
    private readonly LevelSlider _masterVolume = new();
    private readonly LevelSlider _targetDelay = new();
    private readonly TextBox _sourceValue = new();
    private readonly TextBox _targetValue = new();
    private readonly TextBox _masterValue = new();
    private readonly TextBox _delayValue = new();
    private readonly Label _status = new();
    private readonly RoundedButton _startButton = new() { CornerRadius = 10 };
    private readonly RoundedButton _calibrateButton = new() { CornerRadius = 10 };
    private readonly RoundedButton _refreshButton = new() { CornerRadius = 8 };
    private readonly NotifyIcon _trayIcon = new();
    private readonly ToolStripMenuItem _trayToggleItem = new();
    private readonly TrayVolumeEditor _trayMasterVolume = new("总音量");
    private readonly TrayVolumeEditor _traySourceVolume = new("设备 1 音量");
    private readonly TrayVolumeEditor _trayTargetVolume = new("设备 2 音量");
    private bool _syncingInputs;
    private bool _calibrationInProgress;
    private bool _activeNegativeDelay;
    private int _volumeBeforeMute = 50;
    private bool _exitRequested;
    private bool _trayHintShown;
    private readonly System.Windows.Forms.Timer _endpointVolumeTimer = new() { Interval = 250 };
    private bool _endpointVolumeSyncing;
    private bool _delayRestartPending;
    private bool _startInProgress;
    private bool _resourcesDisposed;
    private int _toggleHotKeyActivationCount;

    public MainForm(bool diagnosticsMode = false, bool enableMediaKeysInDiagnostics = false)
    {
        _diagnosticsMode = diagnosticsMode;
        _enableMediaKeysInDiagnostics = enableMediaKeysInDiagnostics;
        Text = "Dual Audio";
        ClientSize = new Size(720, 730);
        MinimumSize = new Size(640, 680);
        BackColor = _background;
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 10F);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        _volumePolicy.Initialize(_settings.MasterVolume);
        if (_settings.HasProductVolumeSettings
            && double.IsFinite(_settings.SourceCoefficient) && double.IsFinite(_settings.TargetCoefficient))
        {
            _volumePolicy.SetSource(_settings.SourceCoefficient);
            _volumePolicy.SetTarget(_settings.TargetCoefficient);
        }
        else
        {
            _volumePolicy.SetSource(_settings.SourceVolume);
            _volumePolicy.SetTarget(_settings.TargetVolume);
        }
        BuildUi();
        BuildTrayIcon();
        DisplayVolumePolicy();
        _endpointVolumeTimer.Tick += async (_, _) =>
        {
            SyncEndpointSlidersFromSystem();
            await CheckPlaybackRoutingAsync();
        };
        _engine.StoppedUnexpectedly += EngineOnStoppedUnexpectedly;
        _mediaKeyHook.KeyPressed += MediaKeyHookOnKeyPressed;
        _mediaKeyHook.ToggleRequested += ToggleHotKeyHookOnRequested;
        Shown += (_, _) =>
        {
            RefreshDevices();
            SyncEndpointSlidersFromSystem();
            _endpointVolumeTimer.Start();
            if (_diagnosticsMode && !_enableMediaKeysInDiagnostics) return;
            try { _mediaKeyHook.Start(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法启用音量快捷键", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        FormClosing += MainFormOnFormClosing;
        FormClosed += (_, _) => DisposeApplicationResources();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = _background,
            Padding = new Padding(30),
            ColumnCount = 1,
            RowCount = 11
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 12F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 188F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 12F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 16F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 10F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        Controls.Add(root);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112F));
        var headingStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        headingStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
        headingStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        headingStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var eyebrow = MakeLabel("DUAL AUDIO", 8F, FontStyle.Bold, _accent);
        eyebrow.Dock = DockStyle.Fill;
        eyebrow.TextAlign = ContentAlignment.TopLeft;
        headingStack.Controls.Add(eyebrow, 0, 0);

        var title = MakeLabel("Dual Audio", 19F, FontStyle.Bold, Color.FromArgb(242, 244, 247));
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        headingStack.Controls.Add(title, 0, 1);

        var subtitle = MakeLabel("把系统声音同步到两台设备，并自动校准到达时间", 9.5F, FontStyle.Regular, _muted);
        subtitle.Dock = DockStyle.Fill;
        subtitle.TextAlign = ContentAlignment.TopLeft;
        headingStack.Controls.Add(subtitle, 0, 2);
        header.Controls.Add(headingStack, 0, 0);

        _refreshButton.Text = "刷新设备";
        StyleSecondaryButton(_refreshButton);
        _refreshButton.Dock = DockStyle.Top;
        _refreshButton.Height = 36;
        _refreshButton.Margin = new Padding(8, 8, 0, 0);
        _refreshButton.Click += (_, _) => RefreshDevices();
        header.Controls.Add(_refreshButton, 1, 0);
        root.Controls.Add(header, 0, 0);

        var sourceCard = CreateDeviceCard("设备 1 · 输出", "设备系数 × 总音量 = 系统音量；负延迟作用于此设备", _sourceBox,
            _sourceVolume, _sourceValue, includeDelay: false);
        var targetCard = CreateDeviceCard("设备 2 · 输出", "设备系数 × 总音量 = 系统音量；正延迟作用于此设备", _targetBox,
            _targetVolume, _targetValue, includeDelay: true);
        var masterCard = CreateMasterVolumeCard();
        root.Controls.Add(sourceCard, 0, 1);
        root.Controls.Add(targetCard, 0, 3);
        root.Controls.Add(masterCard, 0, 5);

        _sourceVolume.Value = Math.Clamp(_settings.SourceVolume, 0, 100);
        _targetVolume.Value = Math.Clamp(_settings.TargetVolume, 0, 100);
        _masterVolume.Value = Math.Clamp(_settings.MasterVolume, 0, 100);
        _targetDelay.Value = Math.Clamp(_settings.TargetDelayMilliseconds, -1000, 1000);
        _sourceValue.Text = _sourceVolume.Value.ToString();
        _targetValue.Text = _targetVolume.Value.ToString();
        _masterValue.Text = _masterVolume.Value.ToString();
        _delayValue.Text = _targetDelay.Value.ToString();
        WireCoefficientInput(_sourceVolume, _sourceValue,
            OnSourceVolumeChanged);
        WireCoefficientInput(_targetVolume, _targetValue,
            OnTargetVolumeChanged);
        WireInputPair(_masterVolume, _masterValue, 100,
            OnMasterVolumeChanged);
        WireInputPair(_targetDelay, _delayValue, 1000, ApplyDelayWhileRunning, -1000);

        _startButton.Text = "开始同步播放";
        _startButton.FlatStyle = FlatStyle.Flat;
        _startButton.FlatAppearance.BorderSize = 0;
        _startButton.BackColor = _accent;
        _startButton.ForeColor = Color.FromArgb(7, 28, 21);
        _startButton.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
        _startButton.Dock = DockStyle.Fill;
        _startButton.Margin = Padding.Empty;
        _startButton.Cursor = Cursors.Hand;
        _startButton.Click += async (_, _) => await TogglePlaybackAsync();

        _calibrateButton.Text = "麦克风自动校准";
        StyleSecondaryButton(_calibrateButton);
        _calibrateButton.Dock = DockStyle.Fill;
        _calibrateButton.Margin = new Padding(0, 0, 10, 0);
        _calibrateButton.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
        _calibrateButton.Click += async (_, _) =>
        {
            if (_calibrationInProgress) { _calibrationCancellation?.Cancel(); return; }
            if (_calibrationTask is { IsCompleted: false }) return;
            var task = RunAutomaticCalibrationAsync();
            _calibrationTask = task;
            try { await task; }
            finally { if (ReferenceEquals(_calibrationTask, task)) _calibrationTask = null; }
        };

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
        actions.Controls.Add(_calibrateButton, 0, 0);
        actions.Controls.Add(_startButton, 1, 0);
        root.Controls.Add(actions, 0, 7);

        _status.Text = "●  尚未开始";
        _status.ForeColor = _muted;
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Dock = DockStyle.Fill;
        _status.Margin = Padding.Empty;
        root.Controls.Add(_status, 0, 9);

        var tip = MakeLabel("正数延迟设备 2，负数延迟设备 1；自动校准会判断方向。", 8.5F,
            FontStyle.Regular, Color.FromArgb(119, 124, 136));
        tip.TextAlign = ContentAlignment.MiddleCenter;
        tip.Dock = DockStyle.Fill;
        root.Controls.Add(tip, 0, 10);
    }

    private Panel CreateMasterVolumeCard()
    {
        var panel = new RoundedPanel
        {
            BackColor = Color.FromArgb(20, 39, 33),
            BorderColor = Color.FromArgb(40, 91, 72),
            CornerRadius = 12,
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 10, 18, 10),
            Margin = Padding.Empty
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 9F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 6F));

        var label = MakeLabel("总音量  ·  同时调节两台设备", 10F, FontStyle.Bold, Color.FromArgb(214, 240, 230));
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(label, 0, 0);

        var volumeText = MakeLabel("音量", 9F, FontStyle.Regular, _muted);
        volumeText.Dock = DockStyle.Fill;
        volumeText.TextAlign = ContentAlignment.MiddleRight;
        layout.Controls.Add(volumeText, 1, 0);

        _masterVolume.Minimum = 0;
        _masterVolume.Maximum = 100;
        _masterVolume.Dock = DockStyle.Fill;
        _masterVolume.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(_masterVolume, 2, 0);

        ConfigureNumberInput(_masterValue);
        _masterValue.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _masterValue.Margin = new Padding(4, 5, 2, 5);
        layout.Controls.Add(_masterValue, 3, 0);

        var unit = MakeLabel("%", 8.5F, FontStyle.Regular, _muted);
        unit.Dock = DockStyle.Fill;
        unit.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(unit, 4, 0);
        panel.Controls.Add(layout);
        return panel;
    }

    private void BuildTrayIcon()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(28, 30, 35),
            ForeColor = Color.FromArgb(238, 240, 243),
            ShowImageMargin = false
        };

        var showItem = new ToolStripMenuItem("显示主窗口") { Font = new Font(Font, FontStyle.Bold) };
        showItem.Click += (_, _) => RestoreWindow();
        _trayMasterVolume.Value = _masterVolume.Value;
        _traySourceVolume.Value = _sourceVolume.Value;
        _trayTargetVolume.Value = _targetVolume.Value;
        WireTrayVolume(_trayMasterVolume, _masterVolume, _masterValue, OnMasterVolumeChanged);
        WireTrayVolume(_traySourceVolume, _sourceVolume, _sourceValue, v => OnSourceVolumeChanged(v));
        WireTrayVolume(_trayTargetVolume, _targetVolume, _targetValue, v => OnTargetVolumeChanged(v));

        _trayToggleItem.Text = "开始同步播放";
        _trayToggleItem.ShortcutKeyDisplayString = "Alt+Insert";
        _trayToggleItem.Click += async (_, _) =>
        {
            if (!_engine.IsRunning) RestoreWindow();
            await TogglePlaybackAsync();
        };
        var exitItem = new ToolStripMenuItem("退出程序");
        exitItem.Click += (_, _) =>
        {
            _exitRequested = true;
            Close();
        };

        menu.Items.Add(showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateTrayVolumeHost(_trayMasterVolume));
        menu.Items.Add(CreateTrayVolumeHost(_traySourceVolume));
        menu.Items.Add(CreateTrayVolumeHost(_trayTargetVolume));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(exitItem);
        foreach (ToolStripItem item in menu.Items)
        {
            item.BackColor = menu.BackColor;
            item.ForeColor = menu.ForeColor;
        }

        _trayIcon.Icon = Icon;
        _trayIcon.Text = "双设备同步播放 · 尚未开始";
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();
        UpdateTrayTooltip();
    }

    private static ToolStripControlHost CreateTrayVolumeHost(TrayVolumeEditor editor) => new(editor)
    {
        AutoSize = false,
        Size = new Size(300, 52),
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };

    private void WireTrayVolume(TrayVolumeEditor trayEditor, LevelSlider mainSlider, TextBox mainInput,
        Action<int> apply)
    {
        trayEditor.ValueChanged += (_, _) =>
        {
            if (_syncingInputs) return;
            _syncingInputs = true;
            mainSlider.Value = trayEditor.Value;
            mainInput.Text = trayEditor.Value.ToString();
            _syncingInputs = false;
            apply(trayEditor.Value);
        };
    }

    private void SyncMainVolumeToTray(TrayVolumeEditor trayEditor, int value)
    {
        _syncingInputs = true;
        trayEditor.Value = value;
        _syncingInputs = false;
    }

    private void OnSourceVolumeChanged(double value)
    {
        if (_endpointVolumeSyncing || _startInProgress) return;
        _volumePolicy.SetSource(value);
        WritePolicyOutputs(true, false);
    }

    private void OnTargetVolumeChanged(double value)
    {
        if (_endpointVolumeSyncing || _startInProgress) return;
        _volumePolicy.SetTarget(value);
        WritePolicyOutputs(false, true);
    }

    private void OnMasterVolumeChanged(int value)
    {
        if (_endpointVolumeSyncing || _startInProgress) return;
        _volumePolicy.SetMaster(value);
        WritePolicyOutputs(true, true);
    }

    private void WritePolicyOutputs(bool source, bool target)
    {
        try
        {
            if (_engine.IsRunning) _volumeSession?.Apply(_volumePolicy, source, target);
        }
        catch (Exception ex)
        {
            _status.Text = "●  音量设置失败：" + ex.Message;
        }
        finally
        {
            DisplayVolumePolicy();
            SaveSettings();
        }
    }

    private void DisplayVolumePolicy()
    {
        _endpointVolumeSyncing = _syncingInputs = true;
        try
        {
            // Expanded ranges retain externally requested coefficients above 100%.
            _sourceVolume.Maximum = Math.Max(100, (int)Math.Ceiling(_volumePolicy.Source));
            _targetVolume.Maximum = Math.Max(100, (int)Math.Ceiling(_volumePolicy.Target));
            _traySourceVolume.Maximum = _sourceVolume.Maximum;
            _trayTargetVolume.Maximum = _targetVolume.Maximum;
            _sourceVolume.Value = (int)Math.Round(_volumePolicy.Source);
            _targetVolume.Value = (int)Math.Round(_volumePolicy.Target);
            _masterVolume.Value = _volumePolicy.Master;
            if (!_sourceValue.Focused) _sourceValue.Text = _volumePolicy.Source.ToString("0.##");
            if (!_targetValue.Focused) _targetValue.Text = _volumePolicy.Target.ToString("0.##");
            _masterValue.Text = _volumePolicy.Master.ToString();
            _traySourceVolume.Value = _sourceVolume.Value;
            _trayTargetVolume.Value = _targetVolume.Value;
            _trayMasterVolume.Value = _volumePolicy.Master;
            UpdateTrayTooltip();
        }
        finally { _endpointVolumeSyncing = _syncingInputs = false; }
    }

    private void SyncEndpointSlidersFromSystem()
    {
        if (_endpointVolumeSyncing || _startInProgress || !_engine.IsRunning || _volumeSession is null) return;
        try
        {
            if (_volumeSession.ReadExternalChanges(_volumePolicy))
            {
                DisplayVolumePolicy();
                SaveSettings();
            }
        }
        catch { }
    }

    private void ApplyDelayWhileRunning(int value)
    {
        if (!_engine.IsRunning || _delayRestartPending || _startInProgress) return;
        var negative = UseNegativeDelayDirection(value);
        if (negative == _activeNegativeDelay)
        {
            _engine.SetTargetDelay(Math.Abs(value));
            return;
        }

        SetOperationUi(true, "●  正在切换延迟方向…");
        _delayRestartPending = true;
        _ = RestartPlaybackAfterDirectionChangeAsync();
    }

    private bool UseNegativeDelayDirection(int delay)
        => delay < 0 || (delay == 0 && _settings.PreferNegativeDirectionAtZero
            && (_sourceBox.SelectedItem as DeviceItem)?.Id == _settings.CalibrationSourceDeviceId
            && (_targetBox.SelectedItem as DeviceItem)?.Id == _settings.CalibrationTargetDeviceId);

    private async Task RestartPlaybackAfterDirectionChangeAsync()
    {
        _startInProgress = true;
        try
        {
            await Task.Run(() => _engine.Stop());
            await Task.Delay(300);
            if (IsDisposed || _exitRequested) return;
            _delayRestartPending = _startInProgress = false;
            await TogglePlaybackAsync();
        }
        catch (Exception ex)
        {
            try { EndVolumeSession(); } catch (Exception restoreError) { ex = new AggregateException(ex, restoreError); }
            if (!IsDisposed && !_exitRequested)
            {
                SetRunningUi(false);
                _status.Text = "●  切换延迟方向失败";
                MessageBox.Show(this, ex.Message, "同步播放已停止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally { _delayRestartPending = _startInProgress = false; }
    }

    private void MediaKeyHookOnKeyPressed(object? sender, MediaVolumeKey key)
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (!_engine.IsRunning || !_mediaKeyHook.InterceptKeys) return;
        var generation = _mediaKeyHook.Generation;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || _exitRequested || !_engine.IsRunning || !_mediaKeyHook.InterceptKeys
                    || _startInProgress || _calibrationInProgress || generation != _mediaKeyHook.Generation) return;
                AdjustMasterVolume(key);
                _volumeOverlay ??= new VolumeOverlayForm();
                _volumeOverlay.ShowVolume(_masterVolume.Value);
            });
        }
        catch (InvalidOperationException) { }
    }

    private void ToggleHotKeyHookOnRequested(object? sender, EventArgs e)
    {
        _toggleHotKeyActivationCount++;
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(async () => await TogglePlaybackAsync()); }
        catch (InvalidOperationException) { }
    }

    private void AdjustMasterVolume(MediaVolumeKey key)
    {
        var current = _masterVolume.Value;
        var next = current;
        switch (key)
        {
            case MediaVolumeKey.VolumeUp:
                next = Math.Min(100, current + 2);
                break;
            case MediaVolumeKey.VolumeDown:
                next = Math.Max(0, current - 2);
                break;
            case MediaVolumeKey.Mute:
                if (current > 0)
                {
                    _volumeBeforeMute = current;
                    next = 0;
                }
                else
                {
                    next = Math.Clamp(_volumeBeforeMute, 1, 100);
                }
                break;
        }
        _masterVolume.Value = next;
    }

    private void UpdateTrayTooltip()
    {
        if (!_trayIcon.Visible) return;
        var state = _engine.IsRunning ? "同步中" : "已停止";
        _trayIcon.Text = $"{state} | 总{_masterVolume.Value}% · 设备1 {_sourceVolume.Value}% · 设备2 {_targetVolume.Value}%";
    }

    private void MainFormOnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if ((_exitRequested || e.CloseReason != CloseReason.UserClosing) && _calibrationInProgress)
        {
            e.Cancel = true;
            if (!_waitingForCalibrationExit)
            {
                _waitingForCalibrationExit = true;
                _exitRequested = true;
                _calibrationCancellation?.Cancel();
                _ = FinishCalibrationBeforeExitAsync();
            }
            return;
        }
        SyncEndpointSlidersFromSystem();
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            SaveSettings();
            HideToTray();
            return;
        }

        SaveSettings();
    }

    private async Task FinishCalibrationBeforeExitAsync()
    {
        // Let the worker's finally/Dispose restore temporary endpoint mutes
        // before returning from Application.Run and terminating background work.
        try { if (_calibrationTask is not null) await _calibrationTask; }
        catch (Exception ex)
        {
            if (!IsDisposed) MessageBox.Show(this, ex.Message, "校准收尾失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        if (!IsDisposed) Close();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        if (_trayHintShown) return;

        _trayHintShown = true;
        _trayIcon.BalloonTipTitle = "双设备同步播放仍在运行";
        _trayIcon.BalloonTipText = _engine.IsRunning
            ? "窗口已隐藏到系统托盘，同步播放不会中断。"
            : "窗口已隐藏到系统托盘，双击图标可重新打开。";
        _trayIcon.ShowBalloonTip(2500);
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private Panel CreateDeviceCard(string heading, string description, ComboBox box, LevelSlider slider,
        TextBox value, bool includeDelay)
    {
        var panel = new RoundedPanel
        {
            BackColor = _card,
            BorderColor = Color.FromArgb(44, 47, 53),
            CornerRadius = 12,
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            Margin = Padding.Empty
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = includeDelay ? 4 : 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 9F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 6F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
        if (includeDelay) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
        panel.Controls.Add(layout);

        var headingLabel = MakeLabel(heading, 11F, FontStyle.Bold, Color.White);
        headingLabel.Dock = DockStyle.Fill;
        headingLabel.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(headingLabel, 0, 0);
        layout.SetColumnSpan(headingLabel, 5);

        var descriptionLabel = MakeLabel(description, 8.5F, FontStyle.Regular, _muted);
        descriptionLabel.Dock = DockStyle.Fill;
        descriptionLabel.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(descriptionLabel, 0, 1);
        layout.SetColumnSpan(descriptionLabel, 5);

        box.DropDownStyle = ComboBoxStyle.DropDownList;
        box.FlatStyle = FlatStyle.Flat;
        box.BackColor = Color.FromArgb(34, 37, 42);
        box.ForeColor = Color.White;
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 24;
        box.DrawItem += DrawDeviceItem;
        box.HandleCreated += (_, _) => ApplyDarkControlTheme(box);
        box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        box.Margin = new Padding(0, 7, 12, 7);
        layout.Controls.Add(box, 0, 2);

        var volumeText = MakeLabel("音量", 9F, FontStyle.Regular, _muted);
        volumeText.Dock = DockStyle.Fill;
        volumeText.TextAlign = ContentAlignment.MiddleRight;
        layout.Controls.Add(volumeText, 1, 2);

        slider.Minimum = 0;
        slider.Maximum = 100;
        slider.Dock = DockStyle.Fill;
        slider.Margin = new Padding(0, 10, 0, 8);
        layout.Controls.Add(slider, 2, 2);

        ConfigureNumberInput(value);
        value.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        value.Margin = new Padding(4, 7, 2, 7);
        layout.Controls.Add(value, 3, 2);

        var percentUnit = MakeLabel("%", 8.5F, FontStyle.Regular, _muted);
        percentUnit.Dock = DockStyle.Fill;
        percentUnit.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(percentUnit, 4, 2);

        if (includeDelay)
        {
            var delayHint = MakeLabel("延迟方向（负数=设备 1，正数=设备 2）", 8.5F, FontStyle.Regular, _muted);
            delayHint.Dock = DockStyle.Fill;
            delayHint.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(delayHint, 0, 3);

            var delayText = MakeLabel("延迟", 9F, FontStyle.Regular, _muted);
            delayText.Dock = DockStyle.Fill;
            delayText.TextAlign = ContentAlignment.MiddleRight;
            layout.Controls.Add(delayText, 1, 3);

            _targetDelay.Minimum = -1000;
            _targetDelay.Maximum = 1000;
            _targetDelay.SmallChange = 10;
            _targetDelay.LargeChange = 50;
            _targetDelay.Dock = DockStyle.Fill;
            _targetDelay.Margin = new Padding(0, 10, 0, 8);
            layout.Controls.Add(_targetDelay, 2, 3);

            ConfigureNumberInput(_delayValue);
            _delayValue.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _delayValue.Margin = new Padding(4, 7, 2, 7);
            layout.Controls.Add(_delayValue, 3, 3);

            var delayUnit = MakeLabel("ms", 8.5F, FontStyle.Regular, _muted);
            delayUnit.Dock = DockStyle.Fill;
            delayUnit.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(delayUnit, 4, 3);
        }

        return panel;
    }

    private void RefreshDevices()
    {
        if (_engine.IsRunning)
        {
            MessageBox.Show(this, "请先停止同步，再刷新设备。", "双设备同步播放", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        string? sourceId = (_sourceBox.SelectedItem as DeviceItem)?.Id ?? _settings.SourceDeviceId;
        string? targetId = (_targetBox.SelectedItem as DeviceItem)?.Id ?? _settings.TargetDeviceId;
        _sourceBox.Items.Clear();
        _targetBox.Items.Clear();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new DeviceItem(d.ID, d.FriendlyName, d.ID == defaultDevice.ID))
                .OrderByDescending(d => d.IsDefault)
                .ThenBy(d => d.Name)
                .ToArray();

            foreach (var device in devices)
            {
                _sourceBox.Items.Add(device);
                _targetBox.Items.Add(device);
            }

            SelectDevice(_sourceBox, sourceId, preferDefault: true);
            SelectDevice(_targetBox, targetId, preferDefault: false);
            if (_targetBox.SelectedIndex == _sourceBox.SelectedIndex && _targetBox.Items.Count > 1)
                _targetBox.SelectedIndex = _sourceBox.SelectedIndex == 0 ? 1 : 0;
            _status.Text = devices.Length < 2 ? "●  至少需要两个已启用的输出设备" : $"●  已发现 {devices.Length} 个输出设备";
            _status.ForeColor = devices.Length < 2 ? Color.FromArgb(255, 178, 87) : _muted;
            _startButton.Enabled = devices.Length >= 2;
        }
        catch (Exception ex)
        {
            _status.Text = "●  无法读取音频设备";
            _status.ForeColor = Color.FromArgb(255, 105, 105);
            MessageBox.Show(this, ex.Message, "读取设备失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void SelectDevice(ComboBox box, string? id, bool preferDefault)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is DeviceItem item && item.Id == id)
            {
                box.SelectedIndex = i;
                return;
            }
        }

        if (preferDefault)
        {
            for (var i = 0; i < box.Items.Count; i++)
            {
                if (box.Items[i] is DeviceItem { IsDefault: true })
                {
                    box.SelectedIndex = i;
                    return;
                }
            }
        }

        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private async Task RunAutomaticCalibrationAsync()
    {
        if (_calibrationInProgress || _startInProgress || _delayRestartPending) return;
        if (_sourceBox.SelectedItem is not DeviceItem first || _targetBox.SelectedItem is not DeviceItem second)
            return;
        if (first.Id == second.Id)
        {
            MessageBox.Show(this, "请先选择两个不同的输出设备。", "麦克风自动校准",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if ((_engine.IsRunning && _activeNegativeDelay) || UseNegativeDelayDirection(_targetDelay.Value))
        {
            MessageBox.Show(this, "实播自动校准只使用设备 1 直出、设备 2 镜像，不会设置负延迟或交换方向。请先将延迟设为 0 或正数。",
                "正向实播校准", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _calibrationInProgress = true;
        _calibrationCancellation = new CancellationTokenSource();
        var temporarySync = !_engine.IsRunning;
        SetCalibrationControls();
        _status.Text = "●  实播校准：请持续播放音乐，将麦克风放在能听到两台设备的位置";
        _status.ForeColor = Color.FromArgb(255, 205, 96);

        try
        {
            if (temporarySync)
            {
                await TogglePlaybackAsync(calibrationTransition: true);
                if (!_engine.IsRunning) throw new InvalidOperationException("未能启动实播同步链路，无法进行隔离校准。");
                SetCalibrationControls();
            }
            _calibrationCancellation.Token.ThrowIfCancellationRequested();
            var progress = new Progress<string>(message =>
            {
                if (!IsDisposed && !_exitRequested && _calibrationInProgress) _status.Text = "●  " + message;
            });
            var result = await LiveAcousticCalibrationEngine.MeasureAsync(first.Id, second.Id,
                _targetDelay.Value, _engine.SetTargetDelay, () => _engine.IsRunning,
                _calibrationCancellation.Token, progress);
            if (IsDisposed || _exitRequested) return;
            _settings.CalibrationSourceDeviceId = first.Id;
            _settings.CalibrationTargetDeviceId = second.Id;
            _settings.PreferNegativeDirectionAtZero = false;
            _targetDelay.Value = result.DelayMilliseconds;
            _delayValue.Text = result.DelayMilliseconds.ToString();
            _engine.SetTargetDelay(result.DelayMilliseconds);
            SaveSettings();
            _status.Text = $"●  校准完成：设备 2 +{result.DelayMilliseconds} ms；复测残差 {Math.Abs(result.ResidualMilliseconds):F1} ms";
            _status.ForeColor = Color.FromArgb(90, 214, 143);
            MessageBox.Show(this,
                $"已验证并保存设备 2 正向延迟：+{result.DelayMilliseconds} ms\n\n" +
                $"设备 1 原音轨→麦克风：{result.FirstMilliseconds:F1} ms\n" +
                $"设备 2 原音轨→麦克风：{result.SecondMilliseconds:F1} ms\n" +
                $"实际相对差值：{result.FirstMilliseconds - result.SecondMilliseconds:F1} ms\n" +
                $"补偿后独立出声复测残差：{Math.Abs(result.ResidualMilliseconds):F1} ms\n" +
                $"两轮差值波动：{result.SpreadMilliseconds:F1} ms\n" +
                $"使用麦克风：{result.MicrophoneName}\n\n" +
                "测量与复核均使用正在播放的真实同步链路，没有另建镜像或使用负延迟。\n" +
                "绝对数值含参考采集固定偏移，补偿使用同轮相对差；设备或环境变化后应重新校准。\n" +
                "临时静音已恢复，记录位于\n%LOCALAPPDATA%\\DualAudio\\last-calibration.txt。",
                "校准完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !_exitRequested)
            {
                _status.Text = "●  校准已取消，原延迟和静音已恢复";
                _status.ForeColor = _muted;
            }
        }
        catch (Exception ex)
        {
            if (IsDisposed || _exitRequested) return;
            _status.Text = "●  自动校准失败";
            _status.ForeColor = Color.FromArgb(255, 105, 105);
            MessageBox.Show(this,
                "自动校准没有完成：\n\n" + ex.Message +
                "\n\n请持续播放有人声或节奏变化的内容，保持环境安静，两台设备均能被麦克风听到。原延迟未改。",
                "校准失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _calibrationInProgress = false;
            _calibrationCancellation?.Dispose();
            _calibrationCancellation = null;
            if (temporarySync && _engine.IsRunning && !IsDisposed && !_exitRequested)
                await TogglePlaybackAsync(calibrationTransition: true);
            if (!IsDisposed && !_exitRequested)
            {
                _calibrateButton.Text = "麦克风自动校准";
                _calibrateButton.Enabled = true;
                _startButton.Enabled = _sourceBox.Items.Count >= 2;
                _sourceBox.Enabled = _targetBox.Enabled = _refreshButton.Enabled = !_engine.IsRunning;
                _sourceVolume.Enabled = _targetVolume.Enabled = _masterVolume.Enabled = true;
                _sourceValue.Enabled = _targetValue.Enabled = _masterValue.Enabled = true;
                _traySourceVolume.Enabled = _trayTargetVolume.Enabled = _trayMasterVolume.Enabled = true;
                _targetDelay.Enabled = _delayValue.Enabled = true;
            }
        }
    }

    private void SetCalibrationControls()
    {
        _calibrateButton.Enabled = true;
        _calibrateButton.Text = "取消校准";
        _startButton.Enabled = _sourceBox.Enabled = _targetBox.Enabled = _refreshButton.Enabled = false;
        _sourceVolume.Enabled = _targetVolume.Enabled = _masterVolume.Enabled = false;
        _sourceValue.Enabled = _targetValue.Enabled = _masterValue.Enabled = false;
        _traySourceVolume.Enabled = _trayTargetVolume.Enabled = _trayMasterVolume.Enabled = false;
        _targetDelay.Enabled = _delayValue.Enabled = false;
    }

    private async Task TogglePlaybackAsync(bool calibrationTransition = false)
    {
        if ((_calibrationInProgress && !calibrationTransition) || _startInProgress || _delayRestartPending) return;
        _delayRestartPending = false;

        if (_engine.IsRunning)
        {
            SyncEndpointSlidersFromSystem();
            _startInProgress = true;
            SetOperationUi(false, "●  正在停止同步…");
            _activeNegativeDelay = false;
            try
            {
                await Task.Run(() =>
                {
                    _engine.Stop();
                });
                EndVolumeSession();
                if (!IsDisposed && !_exitRequested)
                {
                    SetRunningUi(false);
                    _status.Text = "●  已停止";
                    _status.ForeColor = _muted;
                }
            }
            catch (Exception ex)
            {
                try { EndVolumeSession(); } catch (Exception restoreError) { ex = new AggregateException(ex, restoreError); }
                if (!IsDisposed && !_exitRequested)
                {
                    SetRunningUi(false);
                    _status.Text = "●  停止失败";
                    _status.ForeColor = Color.FromArgb(255, 105, 105);
                    MessageBox.Show(this, "无法停止同步播放：\n\n" + ex.Message, "停止失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally { _startInProgress = false; }
            return;
        }

        if (_sourceBox.SelectedItem is not DeviceItem source || _targetBox.SelectedItem is not DeviceItem target)
            return;
        if (source.Id == target.Id)
        {
            MessageBox.Show(this, "设备 1 和设备 2 不能选择同一个设备。", "请选择两个设备", MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var activeNegativeDelay = UseNegativeDelayDirection(_targetDelay.Value);
        var captureDeviceId = activeNegativeDelay ? target.Id : source.Id;
        var delayedDeviceId = activeNegativeDelay ? source.Id : target.Id;
        var targetDelay = Math.Abs(_targetDelay.Value);

        _startInProgress = true;
        SetOperationUi(true, "●  正在启动同步…");
        try
        {
            // Internal direction restarts must retain the original pre-sync snapshot.
            BeginVolumeSession(source.Id, target.Id);
            SaveSettings();
            await Task.Run(() => StartPlaybackCore(
                captureDeviceId, delayedDeviceId, targetDelay));
            if (IsDisposed || _exitRequested)
            {
                await Task.Run(() =>
                {
                    _engine.Stop();
                });
                EndVolumeSession();
                return;
            }

            _activeNegativeDelay = activeNegativeDelay;
            SetRunningUi(true);
            _status.Text = _activeNegativeDelay
                ? "●  正在同步播放（设备 1 延迟；设备 2 已设为系统输出）"
                : "●  正在同步播放（设备 2 延迟）";
            _status.ForeColor = Color.FromArgb(90, 214, 143);
        }
        catch (Exception ex)
        {
            await Task.Run(() => _engine.Stop());
            try { EndVolumeSession(); } catch (Exception restoreError) { ex = new AggregateException(ex, restoreError); }
            _activeNegativeDelay = false;
            SetRunningUi(false);
            _status.Text = "●  启动失败";
            _status.ForeColor = Color.FromArgb(255, 105, 105);
            if (!IsDisposed && !_exitRequested && !_diagnosticsMode)
                MessageBox.Show(this, "无法启动同步播放：\n\n" + ex.Message, "启动失败", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
        }
        finally { _startInProgress = false; }
    }

    private void BeginVolumeSession(string sourceId, string targetId)
    {
        if (_volumeSession is not null) return;
        IVolumeEndpoint? source = null;
        IVolumeEndpoint? target = null;
        try
        {
            source = new WindowsVolumeEndpoint(sourceId);
            target = new WindowsVolumeEndpoint(targetId);
            _volumeSession = new VolumeSyncSession(source, target, _volumePolicy);
        }
        catch
        {
            source?.Dispose();
            target?.Dispose();
            throw;
        }
    }

    private void EndVolumeSession()
    {
        var session = _volumeSession;
        _volumeSession = null;
        var errors = new List<Exception>();
        try
        {
            Task.Run(() =>
            {
                lock (_routingGate)
                {
                    var routing = _routingSession;
                    _routingSession = null;
                    routing?.Dispose();
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) { errors.Add(ex); }
        try { session?.Dispose(); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException("恢复同步前的系统状态时出错。", errors);
    }

    private void StartPlaybackCore(string captureDeviceId, string delayedDeviceId, int targetDelay)
    {
        lock (_routingGate)
        {
            _routingSession ??= new PlaybackRoutingSession();
            _routingSession.Configure(captureDeviceId, delayedDeviceId);
        }

        if (_diagnosticsMode) _engine.Start(captureDeviceId, delayedDeviceId, 1f, 0f, targetDelay);
        else _engine.Start(captureDeviceId, delayedDeviceId, targetDelay);
    }

    private async Task CheckPlaybackRoutingAsync()
    {
        if (_routingCheckInProgress || _startInProgress || _delayRestartPending || !_engine.IsRunning || _exitRequested) return;
        _routingCheckInProgress = true;
        try
        {
            await Task.Run(() =>
            {
                WriteTimingDiagnostic();
                lock (_routingGate) _routingSession?.CheckForNewStreams();
            });
        }
        catch (Exception ex)
        {
            if (_startInProgress || _delayRestartPending || !_engine.IsRunning || IsDisposed || _exitRequested) return;
            _startInProgress = true;
            SetOperationUi(false, "●  检测到重复直出，正在停止镜像…");
            await Task.Run(() => _engine.Stop());
            await FinishInterruptedCalibrationAsync();
            try { EndVolumeSession(); } catch (Exception restoreError) { ex = new AggregateException(ex, restoreError); }
            if (!IsDisposed && !_exitRequested)
            {
                SetRunningUi(false);
                _status.Text = "●  播放器输出路由冲突，同步已停止";
                _status.ForeColor = Color.FromArgb(255, 105, 105);
                if (!_diagnosticsMode) MessageBox.Show(this, ex.Message, "避免双重声音", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            _startInProgress = false;
        }
        finally { _routingCheckInProgress = false; }
    }

    private long _lastTimingDiagnostic;
    private void WriteTimingDiagnostic()
    {
        var now = Environment.TickCount64;
        if (now - _lastTimingDiagnostic < 1000) return;
        _lastTimingDiagnostic = now;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualAudio");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "live-sync-timing.json"),
                System.Text.Json.JsonSerializer.Serialize(_engine.GetTimingSnapshot(),
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    }));
        }
        catch (IOException) { } // Diagnostics must never stop playback.
        catch (UnauthorizedAccessException) { }
    }

    private void SetOperationUi(bool starting, string status)
    {
        _startButton.Enabled = false;
        _startButton.Text = starting ? "正在启动…" : "正在停止…";
        _sourceBox.Enabled = false;
        _targetBox.Enabled = false;
        _refreshButton.Enabled = false;
        _calibrateButton.Enabled = false;
        _sourceVolume.Enabled = _targetVolume.Enabled = _masterVolume.Enabled = false;
        _sourceValue.Enabled = _targetValue.Enabled = _masterValue.Enabled = false;
        _traySourceVolume.Enabled = _trayTargetVolume.Enabled = _trayMasterVolume.Enabled = false;
        _volumeOverlay?.Hide();
        // Retain ownership throughout a direction restart AND asynchronous stop.
        // SetRunningUi(false) releases it after endpoint state has been restored.
        _mediaKeyHook.InterceptKeys = _volumeSession is not null && (_engine.IsRunning || _mediaKeyHook.InterceptKeys)
            && (!_diagnosticsMode || _enableMediaKeysInDiagnostics);
        _status.Text = status;
        _status.ForeColor = _muted;
    }

    private void SetRunningUi(bool running)
    {
        _sourceValue.Enabled = _targetValue.Enabled = _masterValue.Enabled = true;
        _traySourceVolume.Enabled = _trayTargetVolume.Enabled = _trayMasterVolume.Enabled = true;
        _startButton.Enabled = true;
        _startButton.Text = running ? "停止同步" : "开始同步播放";
        _startButton.BackColor = running ? Color.FromArgb(193, 72, 84) : _accent;
        _startButton.ForeColor = running ? Color.White : Color.FromArgb(7, 28, 21);
        _sourceBox.Enabled = !running;
        _targetBox.Enabled = !running;
        _refreshButton.Enabled = !running;
        _calibrateButton.Enabled = true;
        _sourceVolume.Enabled = true;
        _targetVolume.Enabled = true;
        _masterVolume.Enabled = true;
        _targetDelay.Enabled = true;
        _mediaKeyHook.InterceptKeys = running && (!_diagnosticsMode || _enableMediaKeysInDiagnostics);
        if (!running) _volumeOverlay?.Hide();
        _trayToggleItem.Text = running ? "停止同步播放" : "开始同步播放";
        UpdateTrayTooltip();
    }

    private void EngineOnStoppedUnexpectedly(object? sender, string message)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(async () =>
            {
                await FinishInterruptedCalibrationAsync();
                try { EndVolumeSession(); }
                catch (Exception ex) { message += "\n\n" + ex.Message; }
                SetRunningUi(false);
                _status.Text = "●  设备已停止或断开";
                _status.ForeColor = Color.FromArgb(255, 105, 105);
                MessageBox.Show(this, message, "同步播放已停止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
        }
        catch (InvalidOperationException) { }
    }

    private async Task FinishInterruptedCalibrationAsync()
    {
        // Restore calibration's temporary mutes before restoring pre-sync state.
        // Otherwise the worker's finally could undo the original mute snapshot.
        if (!_calibrationInProgress) return;
        _calibrationCancellation?.Cancel();
        var task = _calibrationTask;
        if (task is not null) await task;
    }

    private void SaveSettings()
    {
        if (_diagnosticsMode) return;
        _settings.SourceDeviceId = (_sourceBox.SelectedItem as DeviceItem)?.Id;
        _settings.TargetDeviceId = (_targetBox.SelectedItem as DeviceItem)?.Id;
        _settings.SourceVolume = _sourceVolume.Value;
        _settings.TargetVolume = _targetVolume.Value;
        _settings.MasterVolume = _masterVolume.Value;
        _settings.HasProductVolumeSettings = true;
        _settings.SourceCoefficient = _volumePolicy.Source;
        _settings.TargetCoefficient = _volumePolicy.Target;
        _settings.SourceOutput = _volumePolicy.SourceOutput;
        _settings.TargetOutput = _volumePolicy.TargetOutput;
        _settings.TargetDelayMilliseconds = _targetDelay.Value;
        _settings.Save();
    }

    private void DisposeApplicationResources()
    {
        if (_resourcesDisposed) return;
        _resourcesDisposed = true;
        _calibrationCancellation?.Cancel();
        _volumeOverlay?.Dispose();
        _volumeOverlay = null;
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _endpointVolumeTimer.Stop();
        _endpointVolumeTimer.Dispose();
        _mediaKeyHook.Dispose();
        _engine.Dispose();
        try { EndVolumeSession(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "恢复系统音量失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal bool IsHiddenInTrayForDiagnostics => !Visible && !_exitRequested && _trayIcon.Visible && !IsDisposed;

    internal async Task<bool> TestVolumeLifecycleAsync()
    {
        if (!_diagnosticsMode) throw new InvalidOperationException("此检查只能在诊断模式运行");
        using var enumerator = new MMDeviceEnumerator();
        using var originalDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var originalDefaultId = originalDefault.ID;
        if (_sourceBox.SelectedItem is not DeviceItem source || _targetBox.SelectedItem is not DeviceItem target) return false;
        using var sourceDevice = enumerator.GetDevice(source.Id);
        using var targetDevice = enumerator.GetDevice(target.Id);
        var s = sourceDevice.AudioEndpointVolume;
        var t = targetDevice.AudioEndpointVolume;
        var originalSource = (s.MasterVolumeLevelScalar, s.Mute);
        var originalTarget = (t.MasterVolumeLevelScalar, t.Mute);
        var log = Path.Combine(Path.GetTempPath(), "DualAudio-lifecycle-check.log");
        void Check(bool condition, string step)
        {
            File.AppendAllText(log, $"{step}: {condition}, x1={_volumePolicy.Source:F4}, x2={_volumePolicy.Target:F4}, z={_volumePolicy.Master}, y1={s.MasterVolumeLevelScalar * 100:F4}, y2={t.MasterVolumeLevelScalar * 100:F4}\n");
            if (!condition) throw new InvalidOperationException(step);
        }
        bool Near(double a, double b) => Math.Abs(a - b) < .5;
        try
        {
            var savedSource = _volumePolicy.Source;
            var savedTarget = _volumePolicy.Target;
            for (var i = 0; i < 10; i++) SyncEndpointSlidersFromSystem();
            Check(_volumePolicy.Source == savedSource && _volumePolicy.Target == savedTarget, "idle polling preserves saved x");
            OnMasterVolumeChanged(50);
            OnSourceVolumeChanged(20); OnTargetVolumeChanged(60);
            Check(s.MasterVolumeLevelScalar == originalSource.MasterVolumeLevelScalar && t.MasterVolumeLevelScalar == originalTarget.MasterVolumeLevelScalar,
                "idle software edits leave system volumes untouched");
            s.MasterVolumeLevelScalar = .17f; t.MasterVolumeLevelScalar = .23f;
            s.Mute = t.Mute = true;
            var baselineSource = s.MasterVolumeLevelScalar;
            var baselineTarget = t.MasterVolumeLevelScalar;
            await TogglePlaybackAsync();
            Check(_engine.IsRunning && !s.Mute && !t.Mute && Near(s.MasterVolumeLevelScalar * 100, 10) && Near(t.MasterVolumeLevelScalar * 100, 30),
                "start snapshots, unmutes and applies products");
            await Task.Delay(1500);
            Check(_volumePolicy.Source == 20 && _volumePolicy.Target == 60 && _volumePolicy.Master == 50, "own readbacks do not move sliders");
            s.MasterVolumeLevelScalar = .25f;
            SyncEndpointSlidersFromSystem();
            Check(Near(_volumePolicy.Source, s.MasterVolumeLevelScalar * 200) && _volumePolicy.Target == 60 && _volumePolicy.Master == 50,
                "external y1 changes only x1");
            t.MasterVolumeLevelScalar = .20f;
            SyncEndpointSlidersFromSystem();
            Check(Near(_volumePolicy.Source, 50) && Near(_volumePolicy.Target, t.MasterVolumeLevelScalar * 200) && _volumePolicy.Master == 50,
                "external y2 changes only x2");
            var x1 = _volumePolicy.Source; var x2 = _volumePolicy.Target;
            OnMasterVolumeChanged(25);
            Check(_volumePolicy.Source == x1 && _volumePolicy.Target == x2 && Near(s.MasterVolumeLevelScalar * 100, x1 / 4) && Near(t.MasterVolumeLevelScalar * 100, x2 / 4),
                "z changes both y and preserves x");
            var session = _volumeSession;
            _targetDelay.Value = -10;
            for (var i = 0; i < 300 && (_startInProgress || _delayRestartPending); i++) await Task.Delay(50);
            Check(_engine.IsRunning && ReferenceEquals(session, _volumeSession), "delay direction restart retains original snapshot");
            await TogglePlaybackAsync();
            Check(!_engine.IsRunning && s.MasterVolumeLevelScalar == baselineSource && t.MasterVolumeLevelScalar == baselineTarget && s.Mute && t.Mute,
                "stop restores pre-sync volume and mute after restart");
            SyncEndpointSlidersFromSystem();
            Check(_volumePolicy.Source == x1 && _volumePolicy.Target == x2, "restoring system state does not overwrite remembered x");
            File.AppendAllText(log, "PASSED\n");
            return true;
        }
        finally
        {
            _engine.Stop();
            EndVolumeSession();
            s.Mute = true; t.Mute = true;
            s.MasterVolumeLevelScalar = originalSource.MasterVolumeLevelScalar; s.Mute = originalSource.Mute;
            t.MasterVolumeLevelScalar = originalTarget.MasterVolumeLevelScalar; t.Mute = originalTarget.Mute;
            DefaultAudioDeviceManager.SetDefaultRenderDevice(originalDefaultId);
        }
    }

    internal bool TestStoppedVolumeOverlay()
    {
        _ = Handle;
        var original = _masterVolume.Value;
        try
        {
            _mediaKeyHook.InterceptKeys = false;
            MediaKeyHookOnKeyPressed(this, MediaVolumeKey.VolumeUp);
            Application.DoEvents();
            if (_masterVolume.Value != original || _volumeOverlay is not null) return false;
            // A stale hook state must not bypass the actual engine state.
            _mediaKeyHook.InterceptKeys = true;
            MediaKeyHookOnKeyPressed(this, MediaVolumeKey.Mute);
            Application.DoEvents();
            return _masterVolume.Value == original && _volumeOverlay is null;
        }
        finally { _mediaKeyHook.InterceptKeys = false; }
    }

    internal async Task<bool> TestShortcutVolumeFlowAsync()
    {
        if (!_diagnosticsMode || !_enableMediaKeysInDiagnostics) throw new InvalidOperationException("此检查需要专用快捷键诊断模式");
        using var enumerator = new MMDeviceEnumerator();
        using var originalDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var defaultId = originalDefault.ID;
        if (_sourceBox.SelectedItem is not DeviceItem source || _targetBox.SelectedItem is not DeviceItem target) return false;
        using var sourceDevice = enumerator.GetDevice(source.Id);
        using var targetDevice = enumerator.GetDevice(target.Id);
        var s = sourceDevice.AudioEndpointVolume;
        var t = targetDevice.AudioEndpointVolume;
        var originalSource = (s.MasterVolumeLevelScalar, s.Mute);
        var originalTarget = (t.MasterVolumeLevelScalar, t.Mute);
        var log = Path.Combine(Path.GetTempPath(), "DualAudio-shortcut-flow.log");
        void Check(bool value, string step)
        {
            File.AppendAllText(log, $"{step}={value}, z={_volumePolicy.Master}, x1={_volumePolicy.Source:F4}, x2={_volumePolicy.Target:F4}\n");
            if (!value) throw new InvalidOperationException(step);
        }
        void Press(byte key) { KeybdEvent(key, 0, 0, UIntPtr.Zero); KeybdEvent(key, 0, 2, UIntPtr.Zero); }
        try
        {
            OnMasterVolumeChanged(40); OnSourceVolumeChanged(50); OnTargetVolumeChanged(70);
            await TogglePlaybackAsync();
            Check(_engine.IsRunning && _mediaKeyHook.InterceptKeys, "successful sync acquires media keys");
            var initialGeneration = _mediaKeyHook.Generation;
            // Inject while the UI caller is deliberately not pumping messages.
            var injection = Task.Run(() =>
            {
                Thread.Sleep(300);
                for (var i = 0; i < 10; i++) Press(0xAF);
            });
            Thread.Sleep(1500);
            Check(_mediaKeyHook.InterceptKeys && initialGeneration == _mediaKeyHook.Generation,
                "busy UI retains the interception session");
            await injection;
            await Task.Delay(500);
            Check(_volumePolicy.Master == 60 && _volumePolicy.Source == 50 && _volumePolicy.Target == 70
                && Math.Abs(s.MasterVolumeLevelScalar * 100 - 30) < .5 && Math.Abs(t.MasterVolumeLevelScalar * 100 - 42) < .5,
                "queued keys change z only and apply both products");
            KeybdEvent(0xAD, 0, 0, UIntPtr.Zero);
            for (var i = 0; i < 10; i++) KeybdEvent(0xAD, 0, 0, UIntPtr.Zero);
            KeybdEvent(0xAD, 0, 2, UIntPtr.Zero);
            await Task.Delay(200);
            Check(_volumePolicy.Master == 0 && !s.Mute && !t.Mute, "held mute changes software once without system mute");
            Press(0xAD); await Task.Delay(200);
            Check(_volumePolicy.Master == 60 && _volumePolicy.Source == 50 && _volumePolicy.Target == 70, "unmute restores z and preserves coefficients");
            var generation = _mediaKeyHook.Generation;
            _targetDelay.Value = -10;
            Check(_mediaKeyHook.InterceptKeys && generation == _mediaKeyHook.Generation, "delay restart does not release media keys");
            for (var i = 0; i < 300 && (_startInProgress || _delayRestartPending); i++) await Task.Delay(50);
            File.AppendAllText(log, $"restart status={_status.Text}, running={_engine.IsRunning}, keys={_mediaKeyHook.InterceptKeys}, generation={_mediaKeyHook.Generation}/{generation}, busy={_startInProgress}/{_delayRestartPending}\n");
            Check(_engine.IsRunning && _mediaKeyHook.InterceptKeys && generation == _mediaKeyHook.Generation, "restart retains same key session");
            var stopping = TogglePlaybackAsync();
            if (!stopping.IsCompleted) Check(_mediaKeyHook.InterceptKeys, "asynchronous stop retains keys until restoration");
            await stopping;
            Check(!_mediaKeyHook.InterceptKeys && s.MasterVolumeLevelScalar == originalSource.MasterVolumeLevelScalar
                && t.MasterVolumeLevelScalar == originalTarget.MasterVolumeLevelScalar && s.Mute == originalSource.Mute && t.Mute == originalTarget.Mute,
                "stop releases keys and restores both endpoint states");
            File.AppendAllText(log, "PASSED\n");
            return true;
        }
        finally
        {
            _mediaKeyHook.InterceptKeys = false;
            _engine.Stop();
            EndVolumeSession();
            DefaultAudioDeviceManager.SetDefaultRenderDevice(defaultId);
        }
    }

    internal void SuppressTrayHintForDiagnostics() => _trayHintShown = true;

    internal bool TestTrayVolumeSynchronization()
    {
        _endpointVolumeSyncing = true;
        var originalMaster = _masterVolume.Value;
        var originalSource = _sourceVolume.Value;
        var originalTarget = _targetVolume.Value;
        _trayMasterVolume.Value = 57;
        _traySourceVolume.Value = 81;
        _trayTargetVolume.Value = 43;
        Application.DoEvents();
        var passed = _masterVolume.Value == 57 && _masterValue.Text == "57"
            && _sourceVolume.Value == 81 && _sourceValue.Text == "81"
            && _targetVolume.Value == 43 && _targetValue.Text == "43";
        _trayMasterVolume.Value = originalMaster;
        _traySourceVolume.Value = originalSource;
        _trayTargetVolume.Value = originalTarget;
        Application.DoEvents();
        _endpointVolumeSyncing = false;
        return passed;
    }

    internal bool TestSignedDelayInput()
    {
        var original = _targetDelay.Value;
        _delayValue.Text = "-250";
        Application.DoEvents();
        var passed = _targetDelay.Value == -250 && _delayValue.Text == "-250";
        _targetDelay.Value = original;
        Application.DoEvents();
        return passed;
    }

    internal bool TestSystemEndpointBinding()
    {
        if (_sourceBox.SelectedItem is not DeviceItem source) return false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(source.Id);
            var original = device.AudioEndpointVolume.MasterVolumeLevelScalar;
            _sourceVolume.Value = 37;
            Application.DoEvents();
            var wroteSystemVolume = Math.Abs(device.AudioEndpointVolume.MasterVolumeLevelScalar - 0.37f) < 0.02f;
            device.AudioEndpointVolume.MasterVolumeLevelScalar = 0.62f;
            SyncEndpointSlidersFromSystem();
            var readSystemVolume = _sourceVolume.Value is >= 60 and <= 64;
            device.AudioEndpointVolume.MasterVolumeLevelScalar = original;
            SyncEndpointSlidersFromSystem();
            return wroteSystemVolume && readSystemVolume;
        }
        catch { return false; }
    }

    internal bool TestDelayDirectionSwitch()
    {
        if (_sourceBox.SelectedItem is not DeviceItem || _targetBox.SelectedItem is not DeviceItem)
            return false;
        var original = _targetDelay.Value;
        try
        {
            _targetDelay.Value = -100;
            _ = TogglePlaybackAsync();
            for (var i = 0; i < 40 && (!_engine.IsRunning || _startInProgress); i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
            if (!_engine.IsRunning) return false;
            _targetDelay.Value = 100;
            for (var i = 0; i < 30 && (!_engine.IsRunning || _activeNegativeDelay); i++)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
            return _engine.IsRunning && !_activeNegativeDelay;
        }
        finally
        {
            if (_engine.IsRunning)
            {
                _ = TogglePlaybackAsync();
                for (var i = 0; i < 40 && _startInProgress; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(25);
                }
            }
            _targetDelay.Value = original;
        }
    }

    internal bool TestMediaKeyAdjustment()
    {
        _endpointVolumeSyncing = true;
        var original = _masterVolume.Value;
        _masterVolume.Value = 50;
        AdjustMasterVolume(MediaVolumeKey.VolumeUp);
        var passed = _masterVolume.Value == 52;
        AdjustMasterVolume(MediaVolumeKey.VolumeDown);
        passed &= _masterVolume.Value == 50;
        AdjustMasterVolume(MediaVolumeKey.Mute);
        passed &= _masterVolume.Value == 0;
        AdjustMasterVolume(MediaVolumeKey.Mute);
        passed &= _masterVolume.Value == 50;
        _masterVolume.Value = original;
        _endpointVolumeSyncing = false;
        return passed;
    }

    internal bool TestGlobalMediaKeyInterception()
    {
        var startedHere = false;
        if (!_engine.IsRunning)
        {
            _ = TogglePlaybackAsync();
            for (var i = 0; i < 40 && (!_engine.IsRunning || _startInProgress); i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
            startedHere = _engine.IsRunning;
        }
        if (!startedHere && !_engine.IsRunning) return false;

        _endpointVolumeSyncing = true;
        var original = _masterVolume.Value;
        _masterVolume.Value = 50;
        _mediaKeyHook.InterceptKeys = true;
        KeybdEvent(0xAF, 0, 0, UIntPtr.Zero);
        KeybdEvent(0xAF, 0, 2, UIntPtr.Zero);
        for (var i = 0; i < 10; i++)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
        var passed = _masterVolume.Value == 52;
        _mediaKeyHook.InterceptKeys = false;
        _masterVolume.Value = original;
        _endpointVolumeSyncing = false;
        if (startedHere && _engine.IsRunning)
        {
            _ = TogglePlaybackAsync();
            for (var i = 0; i < 40 && _startInProgress; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
        }
        return passed;
    }

    internal bool TestGlobalToggleHotKey()
    {
        var activationCountBefore = _toggleHotKeyActivationCount;
        KeybdEvent(0x12, 0, 0, UIntPtr.Zero);
        KeybdEvent(0x2D, 0, 0, UIntPtr.Zero);
        KeybdEvent(0x2D, 0, 2, UIntPtr.Zero);
        KeybdEvent(0x12, 0, 2, UIntPtr.Zero);
        for (var i = 0; i < 20; i++)
        {
            Application.DoEvents();
            Thread.Sleep(20);
            if (_toggleHotKeyActivationCount > activationCountBefore) break;
        }
        KeybdEvent(0x12, 0, 0, UIntPtr.Zero);
        KeybdEvent(0x2D, 0, 0, UIntPtr.Zero);
        KeybdEvent(0x2D, 0, 2, UIntPtr.Zero);
        KeybdEvent(0x12, 0, 2, UIntPtr.Zero);
        for (var i = 0; i < 20; i++)
        {
            Application.DoEvents();
            Thread.Sleep(20);
            if (_toggleHotKeyActivationCount >= activationCountBefore + 2) break;
        }
        if (_engine.IsRunning)
        {
            _ = TogglePlaybackAsync();
            for (var i = 0; i < 40 && _startInProgress; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
        }
        return _toggleHotKeyActivationCount == activationCountBefore + 2;
    }

    internal void ExitForDiagnostics()
    {
        _exitRequested = true;
        Close();
    }

    private void WireCoefficientInput(LevelSlider slider, TextBox number, Action<double> apply)
    {
        var edited = false;
        number.TextChanged += (_, _) => { if (!_syncingInputs && number.Focused) edited = true; };
        slider.ValueChanged += (_, _) =>
        {
            if (_syncingInputs) return;
            apply(slider.Value);
        };
        void Commit()
        {
            if (_syncingInputs || !edited) return;
            edited = false;
            if (double.TryParse(number.Text, out var value) && double.IsFinite(value) && value >= 0 && value <= 10000)
                apply(value);
            else DisplayVolumePolicy();
            _syncingInputs = true;
            number.Text = (ReferenceEquals(slider, _sourceVolume) ? _volumePolicy.Source : _volumePolicy.Target).ToString("0.##");
            _syncingInputs = false;
        }
        number.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            Commit();
            e.SuppressKeyPress = true;
        };
        number.Leave += (_, _) => Commit();
    }

    private void WireInputPair(LevelSlider slider, TextBox number, int maximum, Action<int> apply, int minimum = 0)
    {
        slider.ValueChanged += (_, _) =>
        {
            if (_syncingInputs) return;
            _syncingInputs = true;
            number.Text = slider.Value.ToString();
            _syncingInputs = false;
            apply(slider.Value);
        };
        number.TextChanged += (_, _) =>
        {
            if (_syncingInputs) return;
            if (!int.TryParse(number.Text, out var parsed) || parsed < minimum || parsed > maximum) return;
            _syncingInputs = true;
            slider.Value = parsed;
            _syncingInputs = false;
            apply(slider.Value);
        };
        number.KeyPress += (_, e) =>
        {
            var replacingAll = number.SelectionLength == number.TextLength;
            var validMinus = e.KeyChar == '-' && minimum < 0 && number.SelectionStart == 0
                             && (!number.Text.Contains('-') || replacingAll);
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && !validMinus) e.Handled = true;
        };
        number.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            CommitNumberInput(number, slider, minimum, maximum, apply);
            e.SuppressKeyPress = true;
        };
        number.Leave += (_, _) => CommitNumberInput(number, slider, minimum, maximum, apply);
    }

    private void CommitNumberInput(TextBox number, LevelSlider slider, int minimum, int maximum, Action<int> apply)
    {
        var parsed = int.TryParse(number.Text, out var value) ? Math.Clamp(value, minimum, maximum) : slider.Value;
        _syncingInputs = true;
        number.Text = parsed.ToString();
        slider.Value = parsed;
        _syncingInputs = false;
        apply(parsed);
        number.SelectAll();
    }

    private static void ConfigureNumberInput(TextBox input)
    {
        input.TextAlign = HorizontalAlignment.Right;
        input.BorderStyle = BorderStyle.FixedSingle;
        input.BackColor = Color.FromArgb(34, 37, 42);
        input.ForeColor = Color.FromArgb(238, 240, 243);
        input.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
        input.Enter += (_, _) => input.SelectAll();
        input.HandleCreated += (_, _) => ApplyDarkControlTheme(input);
    }

    private static void ApplyDarkControlTheme(Control control)
    {
        if (control.IsHandleCreated)
            _ = SetWindowTheme(control.Handle, "DarkMode_CFD", null);
    }

    private static void DrawDeviceItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox box || e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Color.FromArgb(48, 53, 61) : box.BackColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(e.Graphics, box.Items[e.Index]?.ToString() ?? string.Empty, box.Font,
            new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height), box.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        SuspendLayout();
        base.OnDpiChanged(e);
        PerformLayout();
        ResumeLayout(true);
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var enabled = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var border = ColorTranslator.ToWin32(Color.FromArgb(44, 47, 53));
            var caption = ColorTranslator.ToWin32(_background);
            var captionText = ColorTranslator.ToWin32(Color.FromArgb(225, 228, 232));
            _ = DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
            _ = DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(Handle, 36, ref captionText, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    private Label MakeLabel(string text, float size, FontStyle style, Color color) => new()
    {
        Text = text,
        Font = new Font("Microsoft YaHei UI", size, style),
        ForeColor = color,
        BackColor = Color.Transparent
    };

    private static void StyleSecondaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(77, 81, 92);
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = Color.FromArgb(31, 33, 39);
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
    }

    private sealed record DeviceItem(string Id, string Name, bool IsDefault)
    {
        public override string ToString() => IsDefault ? $"{Name}  （系统默认）" : Name;
    }

    private sealed class LevelSlider : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private bool _dragging;

        public LevelSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = true;
            Height = 28;
            Cursor = Cursors.Hand;
        }

        public int Minimum
        {
            get => _minimum;
            set { _minimum = value; Value = Math.Max(Value, value); Invalidate(); }
        }

        public int Maximum
        {
            get => _maximum;
            set { _maximum = Math.Max(value, Minimum + 1); Value = Math.Min(Value, _maximum); Invalidate(); }
        }

        public int Value
        {
            get => _value;
            set
            {
                var clamped = Math.Clamp(value, Minimum, Maximum);
                if (_value == clamped) return;
                _value = clamped;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public int SmallChange { get; set; } = 1;
        public int LargeChange { get; set; } = 10;
        public event EventHandler? ValueChanged;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            const float padding = 9F;
            var startX = padding;
            var endX = Math.Max(startX + 1F, Width - padding);
            var centerY = Height / 2F;
            var ratio = (Value - Minimum) / (float)(Maximum - Minimum);
            var thumbX = startX + (endX - startX) * ratio;

            using var trackPen = new Pen(Color.FromArgb(66, 71, 78), 4F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawLine(trackPen, startX, centerY, endX, centerY);

            if (thumbX > startX)
            {
                using var fillPen = new Pen(Color.FromArgb(34, 197, 139), 4F)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                e.Graphics.DrawLine(fillPen, startX, centerY, thumbX, centerY);
            }

            if (Focused)
            {
                using var focusBrush = new SolidBrush(Color.FromArgb(70, 34, 197, 139));
                e.Graphics.FillEllipse(focusBrush, thumbX - 8F, centerY - 8F, 16F, 16F);
            }
            using var thumbBrush = new SolidBrush(Color.FromArgb(226, 232, 229));
            e.Graphics.FillEllipse(thumbBrush, thumbX - 5.5F, centerY - 5.5F, 11F, 11F);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            _dragging = true;
            Capture = true;
            SetValueFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SetValueFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Value += Math.Sign(e.Delta) * SmallChange;
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys keyData)
            => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown
               || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Down:
                    Value -= SmallChange;
                    e.Handled = true;
                    break;
                case Keys.Right:
                case Keys.Up:
                    Value += SmallChange;
                    e.Handled = true;
                    break;
                case Keys.PageDown:
                    Value -= LargeChange;
                    e.Handled = true;
                    break;
                case Keys.PageUp:
                    Value += LargeChange;
                    e.Handled = true;
                    break;
                case Keys.Home:
                    Value = Minimum;
                    e.Handled = true;
                    break;
                case Keys.End:
                    Value = Maximum;
                    e.Handled = true;
                    break;
            }
        }

        private void SetValueFromX(int x)
        {
            const float padding = 9F;
            var width = Math.Max(1F, Width - padding * 2F);
            var ratio = Math.Clamp((x - padding) / width, 0F, 1F);
            Value = Minimum + (int)Math.Round(ratio * (Maximum - Minimum));
        }
    }

    private sealed class TrayVolumeEditor : UserControl
    {
        private readonly LevelSlider _slider = new();
        private readonly Label _valueLabel = new();

        public TrayVolumeEditor(string label)
        {
            Size = new Size(300, 52);
            BackColor = Color.FromArgb(28, 30, 35);
            Margin = Padding.Empty;

            var nameLabel = new Label
            {
                Text = label,
                ForeColor = Color.FromArgb(224, 227, 232),
                Font = new Font("Microsoft YaHei UI", 8.5F),
                Location = new Point(10, 3),
                Size = new Size(170, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(nameLabel);

            _valueLabel.ForeColor = Color.FromArgb(151, 157, 166);
            _valueLabel.Font = new Font("Segoe UI", 8.5F);
            _valueLabel.Location = new Point(228, 3);
            _valueLabel.Size = new Size(60, 20);
            _valueLabel.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_valueLabel);

            _slider.Minimum = 0;
            _slider.Maximum = 100;
            _slider.BackColor = BackColor;
            _slider.Location = new Point(6, 23);
            _slider.Size = new Size(286, 25);
            _slider.AutoSize = false;
            _slider.ValueChanged += (_, _) =>
            {
                _valueLabel.Text = $"{_slider.Value}%";
                ValueChanged?.Invoke(this, EventArgs.Empty);
            };
            Controls.Add(_slider);
        }

        public int Maximum { get => _slider.Maximum; set => _slider.Maximum = value; }

        public int Value
        {
            get => _slider.Value;
            set => _slider.Value = Math.Clamp(value, _slider.Minimum, _slider.Maximum);
        }

        public event EventHandler? ValueChanged;
    }

    private sealed class RoundedPanel : Panel
    {
        public int CornerRadius { get; set; } = 12;
        public Color BorderColor { get; set; } = Color.Transparent;

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            UpdateRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = CreateRoundedPath(new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F), CornerRadius);
            using var pen = new Pen(BorderColor, 1F);
            e.Graphics.DrawPath(pen, path);
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using var path = CreateRoundedPath(new RectangleF(0, 0, Width, Height), CornerRadius);
            Region?.Dispose();
            Region = new Region(path);
        }
    }

    private sealed class RoundedButton : Button
    {
        public int CornerRadius { get; set; } = 8;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;
            using var path = CreateRoundedPath(new RectangleF(0, 0, Width, Height), CornerRadius);
            Region?.Dispose();
            Region = new Region(path);
        }
    }

    private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2F, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
