using System.Text;
using DispenseHost.Net;
using DispenseHost.PatternPlan;
using DispenseHost.Testing;

namespace DispenseStudio;

/// <summary>
/// 3D 点胶平台上位机主窗口。
/// 左侧: 图案参数 / 作业列表 / 上传执行 / 一键测试
/// 中间: 2D 俯视预览 (工作范围 + 规划路径 + 实测轨迹 + 针尖)
/// 右侧: 实时轴状态 / 传感器 / 测试报告 / 日志
/// </summary>
public sealed class MainForm : Form
{
    private readonly Font _font = new("Microsoft YaHei UI", 9f);
    private readonly Font _fontBold = new("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    private readonly Font _mono = new("Consolas", 9f);

    private DispenseClient? _client;
    private string _clientUrl = "";
    private readonly PatternJob _job = new();
    private PlannedPattern? _preview;
    private AxisConfigDto[]? _configs;
    private volatile MachineStateDto _state = MachineStateDto.Empty;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly bool _autoConnect;
    private bool _testing;
    private string _lastReport = "";

    // 顶部
    private readonly TextBox _txtUrl = new();
    private readonly Button _btnConnect = new();
    private readonly Label _lblConn = new();
    private readonly Label _lblMode = new();
    private readonly Label _lblAlarm = new();

    // 图案参数
    private readonly ComboBox _cmbKind = new();
    private readonly TextBox _txtName = new();
    private readonly NumericUpDown _numCx = new(), _numCy = new(), _numZ = new(), _numFeed = new();
    private readonly NumericUpDown _numR = new(), _numSeg = new(), _numAngle = new();
    private readonly NumericUpDown _numW = new(), _numH = new(), _numCorner = new(), _numCornerSeg = new();
    private readonly CheckBox _chkClosed = new(), _chkCcw = new();
    private readonly Panel _pnlCircle = new(), _pnlRect = new();
    private readonly Label _lblPreviewInfo = new();

    // 作业
    private readonly ListBox _lstJob = new();
    private readonly Label _lblJobInfo = new();
    private readonly Button _btnUpload = new(), _btnRun = new(), _btnTest = new();

    // 右侧
    private readonly Label _lblPosX = new(), _lblPosY = new(), _lblPosZ = new();
    private readonly Label _lblStX = new(), _lblStY = new(), _lblStZ = new();
    private readonly Label _lblSenX = new(), _lblSenY = new(), _lblSenZ = new();
    private readonly Label _lblValve = new(), _lblDevMode = new();
    private readonly TextBox _txtReport = new();
    private readonly ListBox _lstLog = new();
    private readonly PatternCanvas _canvas = new();

    private static readonly Color PanelBg = Color.FromArgb(24, 29, 39);
    private static readonly Color CardBg = Color.FromArgb(31, 38, 50);
    private static readonly Color Fg = Color.FromArgb(222, 230, 240);
    private static readonly Color Accent = Color.FromArgb(64, 169, 255);
    private static readonly Color Ok = Color.FromArgb(90, 200, 130);
    private static readonly Color Warn = Color.FromArgb(240, 170, 70);
    private static readonly Color Err = Color.FromArgb(240, 90, 90);

    public MainForm(bool autoConnect = true)
    {
        _autoConnect = autoConnect;
        Text = "DispenseStudio — 3D 点胶平台上位机 (端口 8530)";
        Size = new Size(1460, 920);
        MinimumSize = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PanelBg;
        ForeColor = Fg;
        Font = _font;

        BuildUi();

        _timer = new System.Windows.Forms.Timer { Interval = 50 };
        _timer.Tick += (_, _) => RefreshStateUi();
        _timer.Start();

        if (_autoConnect) _ = ConnectAsync();
    }

    // ------------------------------------------------------------------
    // UI 构建
    // ------------------------------------------------------------------

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(4),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 336));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 322));

        var top = BuildTopBar();
        root.Controls.Add(top, 0, 0);
        root.SetColumnSpan(top, 3);

        var left = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = PanelBg, Padding = new Padding(6) };
        BuildLeftPanel(left);
        root.Controls.Add(left, 0, 1);

        _canvas.Dock = DockStyle.Fill;
        _canvas.Margin = new Padding(4);
        root.Controls.Add(_canvas, 1, 1);

        root.Controls.Add(BuildRightPanel(), 2, 1);
        Controls.Add(root);
    }

    private Control BuildTopBar()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = CardBg, Padding = new Padding(8) };

        var lbl = new Label { Text = "服务端", Location = new Point(10, 16), Size = new Size(52, 24), ForeColor = Fg };
        _txtUrl.SetBounds(64, 13, 218, 26);
        _txtUrl.Text = "ws://localhost:8530/ws";
        _txtUrl.BackColor = Color.FromArgb(18, 22, 30);
        _txtUrl.ForeColor = Fg;
        _txtUrl.Font = _mono;

        _btnConnect.Text = "连接";
        _btnConnect.SetBounds(290, 12, 76, 28);
        StyleButton(_btnConnect, Accent);
        _btnConnect.Click += async (_, _) => await ToggleConnectAsync();

        _lblConn.Text = "● 未连接";
        _lblConn.SetBounds(376, 16, 110, 24);
        _lblConn.ForeColor = Err;

        _lblMode.Text = "模式: Offline";
        _lblMode.SetBounds(496, 16, 230, 24);
        _lblMode.ForeColor = Fg;
        _lblMode.Font = _mono;

        _lblAlarm.Text = "";
        _lblAlarm.SetBounds(736, 16, 400, 24);
        _lblAlarm.ForeColor = Err;
        _lblAlarm.Font = _fontBold;

        var btnCfg = new Button { Text = "读取轴配置" };
        btnCfg.SetBounds(1140, 12, 100, 28);
        StyleButton(btnCfg);
        btnCfg.Click += async (_, _) =>
        {
            if (_client is { IsConnected: true }) await _client.GetConfigAsync();
            if (_client is { IsConnected: true }) await _client.ListZonesAsync();
        };

        var btnZones = new Button { Text = "禁止区" };
        btnZones.SetBounds(1248, 12, 84, 28);
        StyleButton(btnZones);
        btnZones.Click += async (_, _) => { if (_client is { IsConnected: true }) await _client.ListZonesAsync(); };

        p.Controls.AddRange(new Control[] { lbl, _txtUrl, _btnConnect, _lblConn, _lblMode, _lblAlarm, btnCfg, btnZones });
        return p;
    }

    private void BuildLeftPanel(Panel host)
    {
        var b = new RowBuilder(host, _font);

        b.Section("① 平面图案参数");
        _cmbKind.Items.AddRange(["圆形 Circle", "矩形 Rectangle"]);
        _cmbKind.SelectedIndex = 0;
        _cmbKind.DropDownStyle = ComboBoxStyle.DropDownList;
        b.Combo("图案类型", _cmbKind);
        _cmbKind.SelectedIndexChanged += (_, _) => SyncPatternKind();

        b.Text("名称", _txtName, "圆形 R30");
        b.Num("中心 X (mm)", _numCx, 0, -500, 500);
        b.Num("中心 Y (mm)", _numCy, 0, -500, 500);
        b.Num("工作高度 Z", _numZ, (decimal)PatternPlanner.WorkHeight, 0, 200);
        b.Num("进给 (mm/s)", _numFeed, 60, 1, 300);
        b.Check("闭合路径", _chkClosed, true);
        b.Check("逆时针", _chkCcw, true);

        int dynY = b.Y;
        // 左列宽度固定 336, 动态面板用固定宽度避免父容器尚未布局时取到错误尺寸
        _pnlCircle.SetBounds(0, dynY, 320, 100);
        _pnlRect.SetBounds(0, dynY, 320, 128);
        _pnlCircle.BackColor = _pnlRect.BackColor = Color.Transparent;

        var bc = new RowBuilder(_pnlCircle, _font, 0);
        bc.Num("半径 R (mm)", _numR, 30, 1, 500);
        bc.Num("分段数 (0自动)", _numSeg, 72, 0, 720, 0);
        bc.Num("起始角 (°)", _numAngle, 0, -360, 360, 0);

        var br = new RowBuilder(_pnlRect, _font, 0);
        br.Num("宽度 W (mm)", _numW, 80, 1, 600);
        br.Num("高度 H (mm)", _numH, 50, 1, 600);
        br.Num("圆角 R (mm)", _numCorner, 0, 0, 200);
        br.Num("圆角分段", _numCornerSeg, 6, 2, 90, 0);

        host.Controls.Add(_pnlCircle);
        host.Controls.Add(_pnlRect);
        b.Space(132);

        var btnPlan = new Button { Text = "生成预览" };
        var btnAdd = new Button { Text = "加入作业" };
        b.Buttons((btnPlan, (_, _) => PlanPreview()), (btnAdd, (_, _) => AddToJob()));

        _lblPreviewInfo.Text = "尚未生成图案";
        _lblPreviewInfo.ForeColor = Color.FromArgb(150, 165, 185);
        b.Line(_lblPreviewInfo);

        b.Section("② 点胶作业");
        _lstJob.Height = 104;
        b.Full(_lstJob);
        _lblJobInfo.Text = "0 段 / 0 点 / 0.0 mm";
        _lblJobInfo.ForeColor = Accent;
        b.Line(_lblJobInfo);

        var btnRemove = new Button { Text = "移除选中" };
        var btnClear = new Button { Text = "清空作业" };
        b.Buttons((btnRemove, (_, _) => RemoveSelected()), (btnClear, (_, _) => ClearJob()));
        btnClear.Click += (_, _) => _canvas.ClearTrail();

        _btnUpload.Text = "上传作业";
        _btnRun.Text = "执行点胶";
        StyleButton(_btnUpload, Accent);
        StyleButton(_btnRun, Ok);
        b.Buttons((_btnUpload, async (_, _) => await UploadJobAsync()),
                  (_btnRun, async (_, _) => await RunJobAsync()));

        b.Section("③ 自动测试");
        _btnTest.Text = "一键测试：圆形 + 矩形 + 组合";
        _btnTest.Height = 34;
        StyleButton(_btnTest, Color.FromArgb(150, 100, 220));
        _btnTest.Font = _fontBold;
        b.Full(_btnTest);
        _btnTest.Click += async (_, _) => await RunSelfTestAsync();

        var hint = new Label
        {
            Text = "测试会连接真实服务端并执行运动，采集 30Hz 状态轨迹，\n计算实测路径与规划图案的偏差后生成报告。\nZ 需与服务端 PenDownZ=8mm 一致。",
            ForeColor = Color.FromArgb(140, 155, 175), Height = 56, AutoSize = false,
        };
        b.Full(hint);

        SyncPatternKind();
    }

    private Control BuildRightPanel()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(4) };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 244));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 52));

        // --- 实时状态 ---
        var status = new GroupBox { Dock = DockStyle.Fill, Text = "实时状态 (30 Hz)", ForeColor = Accent, BackColor = CardBg, Font = _fontBold };
        var s = new RowBuilder(status, _font, 26);
        s.Header("轴      位置(mm)      速度      状态");
        _lblPosX.Font = _lblPosY.Font = _lblPosZ.Font = _mono;
        s.AxisRow("X", _lblPosX, _lblStX, _lblSenX);
        s.AxisRow("Y", _lblPosY, _lblStY, _lblSenY);
        s.AxisRow("Z", _lblPosZ, _lblStZ, _lblSenZ);
        s.Space(6);
        s.Line(_lblValve);
        s.Line(_lblDevMode);

        var btnHome = new Button { Text = "回零" };
        var btnEstop = new Button { Text = "急停" };
        var btnReset = new Button { Text = "复位报警" };
        StyleButton(btnHome); StyleButton(btnEstop, Err); StyleButton(btnReset, Warn);
        btnHome.Click += async (_, _) => { if (_client is { IsConnected: true }) await _client.HomeAsync(); };
        btnEstop.Click += async (_, _) => { if (_client is { IsConnected: true }) await _client.EstopAsync(); };
        btnReset.Click += async (_, _) => { if (_client is { IsConnected: true }) await _client.ResetAlarmAsync(); };
        s.Triple(btnHome, btnEstop, btnReset);

        // --- 测试报告 (TableLayout 明确分区, 不依赖父容器初始尺寸) ---
        var report = new GroupBox { Dock = DockStyle.Fill, Text = "测试报告", ForeColor = Accent, BackColor = CardBg, Font = _fontBold };
        var repLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Padding = new Padding(8, 20, 8, 4),
        };
        repLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        repLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _txtReport.Multiline = true;
        _txtReport.ReadOnly = true;
        _txtReport.ScrollBars = ScrollBars.Both;
        _txtReport.WordWrap = false;
        _txtReport.Dock = DockStyle.Fill;
        _txtReport.BackColor = Color.FromArgb(16, 20, 28);
        _txtReport.ForeColor = Color.FromArgb(190, 225, 200);
        _txtReport.Font = _mono;
        _txtReport.Text = "点击左侧「一键测试」生成报告。";
        repLayout.Controls.Add(_txtReport, 0, 0);

        var btnSave = new Button { Text = "保存报告", Width = 108, Height = 28 };
        var btnClearTrail = new Button { Text = "清除轨迹", Width = 108, Height = 28 };
        StyleButton(btnSave); StyleButton(btnClearTrail);
        btnSave.Click += (_, _) => SaveReport();
        btnClearTrail.Click += (_, _) => _canvas.ClearTrail();
        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        btnRow.Controls.Add(btnSave);
        btnRow.Controls.Add(btnClearTrail);
        repLayout.Controls.Add(btnRow, 0, 1);
        report.Controls.Add(repLayout);

        // --- 日志 ---
        var log = new GroupBox { Dock = DockStyle.Fill, Text = "通信日志", ForeColor = Accent, BackColor = CardBg, Font = _fontBold };
        var logLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1,
            Padding = new Padding(8, 20, 8, 4),
        };
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _lstLog.Dock = DockStyle.Fill;
        _lstLog.BackColor = Color.FromArgb(16, 20, 28);
        _lstLog.ForeColor = Color.FromArgb(175, 190, 210);
        _lstLog.Font = _mono;
        _lstLog.IntegralHeight = false;
        logLayout.Controls.Add(_lstLog, 0, 0);
        log.Controls.Add(logLayout);

        t.Controls.Add(status, 0, 0);
        t.Controls.Add(report, 0, 1);
        t.Controls.Add(log, 0, 2);
        return t;
    }

    private static void StyleButton(Button b, Color? color = null)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = color ?? Color.FromArgb(52, 64, 82);
        b.ForeColor = Color.White;
        b.Font = new Font("Microsoft YaHei UI", 9f);
        b.Cursor = Cursors.Hand;
    }

    private void SyncPatternKind()
    {
        bool circle = _cmbKind.SelectedIndex == 0;
        _pnlCircle.Visible = circle;
        _pnlRect.Visible = !circle;
        if (_txtName.Text.StartsWith("圆形") || _txtName.Text.StartsWith("矩形"))
            _txtName.Text = circle ? $"圆形 R{_numR.Value}" : $"矩形 {_numW.Value}x{_numH.Value}";
    }

    // ------------------------------------------------------------------
    // 客户端连接
    // ------------------------------------------------------------------

    private DispenseClient EnsureClient()
    {
        string url = _txtUrl.Text.Trim();
        if (_client is not null && _clientUrl == url && _client.IsConnected) return _client;
        if (_client is not null && _clientUrl == url && !_client.IsConnected) return _client;

        _client?.Dispose();
        _client = new DispenseClient(url);
        _clientUrl = url;
        _client.OnLog += AppendLog;
        _client.OnEvent += AppendLog;
        _client.OnConnected += connected =>
        {
            Ui(() =>
            {
                _lblConn.Text = connected ? "● 已连接" : "● 未连接";
                _lblConn.ForeColor = connected ? Ok : Err;
                _btnConnect.Text = connected ? "断开" : "连接";
                if (!connected) _lblMode.Text = "模式: Offline";
            });
        };
        _client.OnState += st => _state = st;
        _client.OnConfigData += cfg =>
        {
            _configs = cfg;
            Ui(() =>
            {
                _canvas.SetConfigs(cfg);
                AppendLog($"轴配置: X[{cfg[0].TravelMin},{cfg[0].TravelMax}] " +
                          $"Y[{cfg[1].TravelMin},{cfg[1].TravelMax}] Z[{cfg[2].TravelMin},{cfg[2].TravelMax}]");
            });
        };
        _client.OnZones += z => Ui(() => _canvas.SetZones(z));
        _client.OnPath += p => AppendLog($"服务端路径回显: {p.Name}, {p.Points.Count} 点");
        _client.OnEventData += (level, msg) =>
        {
            if (level == "error" && (msg.Contains("撞机") || msg.Contains("急停")))
                Ui(() => _lblAlarm.Text = "⚠ " + msg);
            if (msg.Contains("撞机报警已复位"))
                Ui(() => _lblAlarm.Text = "");
        };
        return _client;
    }

    private async Task ConnectAsync()
    {
        var client = EnsureClient();
        try
        {
            await client.ConnectAsync();
            await client.GetConfigAsync();
            await client.ListZonesAsync();
        }
        catch (Exception ex)
        {
            AppendLog("连接失败: " + ex.Message + " (请先启动 server\\run.bat)");
        }
    }

    private async Task ToggleConnectAsync()
    {
        if (_client is { IsConnected: true }) await _client.DisconnectAsync();
        else await ConnectAsync();
    }

    // ------------------------------------------------------------------
    // 图案 / 作业
    // ------------------------------------------------------------------

    private PatternSpec ReadSpec() => new()
    {
        Kind = _cmbKind.SelectedIndex == 0 ? PatternKind.Circle : PatternKind.Rectangle,
        Name = string.IsNullOrWhiteSpace(_txtName.Text) ? "图案" : _txtName.Text.Trim(),
        CenterX = (double)_numCx.Value,
        CenterY = (double)_numCy.Value,
        Z = (double)_numZ.Value,
        Feed = (double)_numFeed.Value,
        Closed = _chkClosed.Checked,
        CounterClockwise = _chkCcw.Checked,
        Radius = (double)_numR.Value,
        Segments = (int)_numSeg.Value,
        StartAngleDeg = (double)_numAngle.Value,
        Width = (double)_numW.Value,
        Height = (double)_numH.Value,
        CornerRadius = (double)_numCorner.Value,
        CornerSegments = (int)_numCornerSeg.Value,
    };

    private void PlanPreview()
    {
        try
        {
            _preview = PatternPlanner.Plan(ReadSpec());
            _lblPreviewInfo.Text = $"{_preview.Name}: {_preview.PointCount} 点, " +
                                   $"{_preview.Length:F1} mm, 预计 {_preview.EstimatedSeconds:F1}s";
            _lblPreviewInfo.ForeColor = Ok;
            RefreshCanvasPatterns();
            AppendLog($"生成图案: {_preview.Spec.Summary} → {_preview.PointCount} 点 / {_preview.Length:F1}mm");
            var warns = new PatternJob();
            warns.Add(_preview);
            foreach (var w in warns.ValidateAgainst(_configs)) AppendLog("⚠ " + w);
        }
        catch (Exception ex)
        {
            _lblPreviewInfo.Text = "参数错误: " + ex.Message;
            _lblPreviewInfo.ForeColor = Err;
        }
    }

    private void AddToJob()
    {
        if (_preview is null) PlanPreview();
        if (_preview is null) return;
        _job.Add(_preview);
        _preview = null;
        RefreshJobList();
        RefreshCanvasPatterns();
        AppendLog($"加入作业: {_job.Patterns[^1].Name}");
    }

    private void RemoveSelected()
    {
        if (_lstJob.SelectedIndex < 0) return;
        _job.RemoveAt(_lstJob.SelectedIndex);
        RefreshJobList();
        RefreshCanvasPatterns();
    }

    private void ClearJob()
    {
        _job.Clear();
        _preview = null;
        RefreshJobList();
        RefreshCanvasPatterns();
    }

    private void RefreshJobList()
    {
        _lstJob.Items.Clear();
        for (int i = 0; i < _job.Patterns.Count; i++)
        {
            var p = _job.Patterns[i];
            _lstJob.Items.Add($"{i + 1}. {p.Name}  {p.PointCount}点  {p.Length:F1}mm  {p.Feed:F0}mm/s");
        }
        _lblJobInfo.Text = $"{_job.Patterns.Count} 段 / {_job.TotalPoints} 点 / {_job.TotalLength:F1} mm";
    }

    private void RefreshCanvasPatterns()
    {
        var list = new List<PlannedPattern>(_job.Patterns);
        if (_preview is not null) list.Add(_preview);
        _canvas.SetPatterns(list);
    }

    private async Task UploadJobAsync()
    {
        if (_job.Patterns.Count == 0) { AppendLog("作业为空，请先生成并加入图案"); return; }
        var client = EnsureClient();
        if (!client.IsConnected) { AppendLog("未连接服务端"); return; }
        var warns = _job.ValidateAgainst(_configs);
        foreach (var w in warns) AppendLog("⚠ " + w);
        await client.UploadStepsAsync($"studio_{DateTime.Now:HHmmss}", _job.ToUploadSteps());
    }

    private async Task RunJobAsync()
    {
        var client = EnsureClient();
        if (!client.IsConnected) { AppendLog("未连接服务端"); return; }
        await client.RunJobAsync();
    }

    // ------------------------------------------------------------------
    // 一键自动测试
    // ------------------------------------------------------------------

    private SelfTestOptions ReadTestOptions() => new()
    {
        ServerUrl = _txtUrl.Text.Trim(),
        CircleRadius = (double)_numR.Value,
        RectWidth = (double)_numW.Value,
        RectHeight = (double)_numH.Value,
        CenterX = (double)_numCx.Value,
        CenterY = (double)_numCy.Value,
        Z = (double)_numZ.Value,
        Feed = (double)_numFeed.Value,
        CircleSegments = (int)_numSeg.Value,
    };

    private async Task RunSelfTestAsync()
    {
        if (_testing) return;
        _testing = true;
        _btnTest.Enabled = false;
        _btnTest.Text = "测试进行中...";
        _txtReport.Text = "正在执行自动测试，请稍候...";
        _canvas.ClearTrail();

        var lines = new StringBuilder();
        try
        {
            var client = EnsureClient();
            if (!client.IsConnected) await client.ConnectAsync();

            var runner = new SelfTestRunner(client, ReadTestOptions());
            runner.OnLog += m => { AppendLog(m); lines.AppendLine(m); };
            runner.OnCaseCompleted += r =>
                Ui(() => _txtReport.Text = $"{r.Name}: {(r.Passed ? "PASS" : "FAIL")}\n" +
                                           $"偏差 {r.MaxDeviationMm:F3}mm  Z误差 {r.MaxZErrorMm:F3}mm  " +
                                           $"采样 {r.MotionSamples}  耗时 {r.ElapsedSeconds:F2}s\n\n" +
                                           lines.ToString());

            var report = await runner.RunAsync();
            _lastReport = report.ToText();
            Ui(() =>
            {
                _txtReport.Text = _lastReport;
                AppendLog($"自动测试结束: {(report.AllPassed ? "全部通过" : "存在失败")}");
            });
            // 测试完成后把本次测试图案同步到画布, 便于与实测轨迹对照
            Ui(() =>
            {
                var o = ReadTestOptions();
                _canvas.SetPatterns(new[]
                {
                    PatternPlanner.Plan(new PatternSpec
                    {
                        Kind = PatternKind.Circle, Name = "圆形", CenterX = o.CenterX, CenterY = o.CenterY,
                        Radius = o.CircleRadius, Segments = o.CircleSegments, Z = o.Z, Feed = o.Feed,
                    }),
                    PatternPlanner.Plan(new PatternSpec
                    {
                        Kind = PatternKind.Rectangle, Name = "矩形", CenterX = o.CenterX, CenterY = o.CenterY,
                        Width = o.RectWidth, Height = o.RectHeight, Z = o.Z, Feed = o.Feed,
                    }),
                });
            });
        }
        catch (Exception ex)
        {
            _lastReport = "自动测试失败: " + ex;
            AppendLog(_lastReport);
            Ui(() => _txtReport.Text = _lastReport);
        }
        finally
        {
            Ui(() => { _btnTest.Enabled = true; _btnTest.Text = "一键测试：圆形 + 矩形 + 组合"; });
            _testing = false;
        }
    }

    private void SaveReport()
    {
        if (string.IsNullOrWhiteSpace(_lastReport)) { AppendLog("尚无测试报告"); return; }
        using var dlg = new SaveFileDialog
        {
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"dispense-selftest-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            File.WriteAllText(dlg.FileName, _lastReport, Encoding.UTF8);
            AppendLog("报告已保存: " + dlg.FileName);
        }
    }

    // ------------------------------------------------------------------
    // 状态刷新 / 日志
    // ------------------------------------------------------------------

    private void RefreshStateUi()
    {
        var st = _state;
        SetAxis(_lblPosX, _lblStX, _lblSenX, st.X);
        SetAxis(_lblPosY, _lblStY, _lblSenY, st.Y);
        SetAxis(_lblPosZ, _lblStZ, _lblSenZ, st.Z);
        _lblValve.Text = st.Valve ? "胶阀: 开 ●" : "胶阀: 关 ○";
        _lblValve.ForeColor = st.Valve ? Ok : Color.FromArgb(150, 165, 185);
        _lblMode.Text = "模式: " + st.Mode;
        _lblDevMode.Text = st.Collision ? "状态: 撞机报警" : "状态: 正常";
        _lblDevMode.ForeColor = st.Collision ? Err : Ok;
        if (!st.Collision && _lblAlarm.Text.Length > 0 && st.Mode == "Idle") _lblAlarm.Text = "";
        _canvas.SetCurrent(st.X.Pos, st.Y.Pos, st.Valve);
    }

    private static void SetAxis(Label pos, Label state, Label sen, AxisStateDto a)
    {
        pos.Text = $"{a.Pos,9:F2}  {a.Vel,7:F1}";
        state.Text = a.State;
        state.ForeColor = a.State == "Faulted" ? Color.FromArgb(240, 90, 90)
                        : a.State == "Homing" ? Color.FromArgb(240, 170, 70)
                        : a.State == "Moving" ? Color.FromArgb(64, 169, 255)
                        : Color.FromArgb(150, 165, 185);
        sen.Text = SensorText(a);
    }

    private static string SensorText(AxisStateDto a) =>
        $"H{(a.Home ? "●" : "○")} N{(a.LimNeg ? "●" : "○")} P{(a.LimPos ? "●" : "○")}";

    private void AppendLog(string msg)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => AppendLog(msg)); } catch { } return; }
        _lstLog.Items.Add($"{DateTime.Now:HH:mm:ss} {msg}");
        if (_lstLog.Items.Count > 300) _lstLog.Items.RemoveAt(0);
        _lstLog.TopIndex = Math.Max(0, _lstLog.Items.Count - 1);
    }

    private void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(a); } catch { } return; }
        a();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _client?.Dispose();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _font.Dispose(); _fontBold.Dispose(); _mono.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>绝对定位的行式布局助手 (避免 Designer 文件)</summary>
internal sealed class RowBuilder
{
    private readonly Control _host;
    private readonly Font _font;
    private readonly int _labelW;
    private readonly int _ctrlW;
    private int _y;

    public int Y => _y;

    public RowBuilder(Control host, Font font, int startY = 8, int labelW = 112, int ctrlW = 190)
    {
        _host = host; _font = font; _y = startY; _labelW = labelW; _ctrlW = ctrlW;
    }

    private void AddLabel(string text, int y)
    {
        _host.Controls.Add(new Label
        {
            Text = text, Location = new Point(8, y + 3), Size = new Size(_labelW, 22),
            ForeColor = Color.FromArgb(185, 200, 220), Font = _font,
        });
    }

    public void Section(string title)
    {
        _host.Controls.Add(new Label
        {
            Text = title, Location = new Point(6, _y), Size = new Size(_host.Width - 20, 24),
            ForeColor = Color.FromArgb(64, 169, 255),
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
        });
        _y += 28;
    }

    public void Header(string text)
    {
        _host.Controls.Add(new Label
        {
            Text = text, Location = new Point(8, _y), Size = new Size(_host.Width - 20, 20),
            ForeColor = Color.FromArgb(140, 155, 175), Font = new Font("Consolas", 8.5f),
        });
        _y += 22;
    }

    public void Combo(string label, ComboBox c)
    {
        AddLabel(label, _y);
        c.SetBounds(_labelW + 12, _y, _ctrlW, 24);
        c.Font = _font;
        c.BackColor = Color.FromArgb(18, 22, 30);
        c.ForeColor = Color.FromArgb(222, 230, 240);
        _host.Controls.Add(c);
        _y += 29;
    }

    public void Text(string label, TextBox t, string def)
    {
        AddLabel(label, _y);
        t.SetBounds(_labelW + 12, _y, _ctrlW, 24);
        t.Text = def;
        t.Font = _font;
        t.BackColor = Color.FromArgb(18, 22, 30);
        t.ForeColor = Color.FromArgb(222, 230, 240);
        _host.Controls.Add(t);
        _y += 29;
    }

    public void Num(string label, NumericUpDown n, decimal def, decimal min, decimal max, int dec = 1)
    {
        AddLabel(label, _y);
        n.SetBounds(_labelW + 12, _y, _ctrlW, 24);
        n.Minimum = min; n.Maximum = max; n.Value = Math.Clamp(def, min, max);
        n.DecimalPlaces = dec;
        n.Increment = dec == 0 ? 1 : 0.5m;
        n.Font = new Font("Consolas", 9f);
        n.BackColor = Color.FromArgb(18, 22, 30);
        n.ForeColor = Color.FromArgb(222, 230, 240);
        _host.Controls.Add(n);
        _y += 29;
    }

    public void Check(string label, CheckBox c, bool def)
    {
        c.Text = label;
        c.SetBounds(_labelW + 12, _y, _ctrlW, 22);
        c.Checked = def;
        c.ForeColor = Color.FromArgb(185, 200, 220);
        c.Font = _font;
        _host.Controls.Add(c);
        _y += 26;
    }

    public void Line(Label l)
    {
        l.Location = new Point(8, _y);
        l.Size = new Size(_host.Width - 22, 22);
        l.Font = _font;
        _host.Controls.Add(l);
        _y += 26;
    }

    public void Full(Control c)
    {
        int w = _host.Width - 22;
        c.SetBounds(8, _y, w, c.Height > 0 ? c.Height : 26);
        _host.Controls.Add(c);
        _y += c.Height + 6;
    }

    public void Buttons(params (Button Btn, EventHandler Handler)[] items)
    {
        int gap = 6;
        int w = (_host.Width - 22 - gap * (items.Length - 1)) / items.Length;
        for (int i = 0; i < items.Length; i++)
        {
            var (btn, handler) = items[i];
            btn.SetBounds(8 + i * (w + gap), _y, w, 28);
            if (btn.BackColor == SystemColors.Control)
            {
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderSize = 0;
                btn.BackColor = Color.FromArgb(52, 64, 82);
                btn.ForeColor = Color.White;
            }
            btn.Font = _font;
            btn.Click += handler;
            _host.Controls.Add(btn);
        }
        _y += 34;
    }

    public void Triple(Button a, Button b, Button c) => Buttons((a, (_, _) => { }), (b, (_, _) => { }), (c, (_, _) => { }));

    public void AxisRow(string name, Label pos, Label state, Label sen)
    {
        _host.Controls.Add(new Label
        {
            Text = name, Location = new Point(8, _y + 2), Size = new Size(22, 22),
            ForeColor = Color.FromArgb(64, 169, 255), Font = new Font("Consolas", 9.5f, FontStyle.Bold),
        });
        pos.SetBounds(30, _y + 2, 144, 20);
        state.SetBounds(178, _y + 2, 62, 20);
        state.Font = new Font("Consolas", 8.5f);
        sen.SetBounds(244, _y + 2, 64, 20);
        sen.Font = new Font("Consolas", 8f);
        sen.ForeColor = Color.FromArgb(150, 165, 185);
        _host.Controls.AddRange(new Control[] { pos, state, sen });
        _y += 24;
    }

    public void Space(int px) => _y += px;
}
