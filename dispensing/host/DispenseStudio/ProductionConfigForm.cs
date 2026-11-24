using DispenseHost.CadImport;
using DispenseHost.Net;
using DispenseHost.PathPlan;

namespace DispenseStudio;

/// <summary>从五页配置窗口提交给主窗口的一个区域生成请求。</summary>
public sealed record RegionGenerationRequest(string Name, RegionSource Source, SurfaceProjectSettings Settings);

/// <summary>
/// P9 人工生产配置。窗口只负责人工字段、校验和反馈；模型路径、ACK 同步及生产状态均由 MainForm 统一处理。
/// </summary>
public sealed class ProductionConfigForm : Form
{
    private readonly Func<Task>? _syncAsync;
    private readonly Func<string, string?, Task>? _commandAsync;
    private readonly Func<RegionGenerationRequest, string>? _generateRegion;
    private readonly Func<IReadOnlyCollection<int>>? _selectedFaces;
    private ModelAsset? _model;
    private bool _busy;

    private readonly TextBox _projectId = new(), _projectName = new();
    private readonly TextBox _modelFile = new(), _modelMeta = new();
    private readonly ComboBox _unit = new();
    private readonly TextBox _fixtureName = new(), _fixing = new(), _partSignal = new(), _clampSignal = new();
    private readonly CheckBox _autoPart = new(), _autoClamp = new(), _flipNormals = new(), _fullProduction = new();
    private readonly NumericUpDown _scale = new();
    private readonly PoseEditor _fixturePose = new(), _placementPose = new(), _pickPose = new(), _unloadPose = new();

    private readonly ListBox _regions = new();
    private readonly TextBox _regionName = new(), _regionIndices = new();
    private readonly ComboBox _regionKind = new();
    private readonly NumericUpDown _zStart = new(), _zEnd = new(), _layer = new(), _offset = new();
    private readonly NumericUpDown _feed = new(), _spacing = new(), _smoothPasses = new(), _smoothStrength = new();
    private readonly NumericUpDown _maxPoseStep = new(), _open = new(), _early = new(), _close = new(), _retract = new();
    private readonly NumericUpDown _approach = new(), _lift = new(), _delay = new(), _batch = new();
    private readonly Label _feedback = new(), _snapshot = new();

    public ModelAsset? ResultModel { get; private set; }
    public ProductionConfig ResultConfig { get; private set; }

    public ProductionConfigForm(
        ModelAsset? model,
        ProductionConfig production,
        SurfaceProjectSettings defaultSettings,
        IReadOnlyList<SurfaceJobItem> regions,
        Func<Task>? syncAsync = null,
        Func<string, string?, Task>? commandAsync = null,
        Func<RegionGenerationRequest, string>? generateRegion = null,
        Func<IReadOnlyCollection<int>>? selectedFaces = null)
    {
        _model = model;
        ResultModel = model;
        ResultConfig = production;
        _syncAsync = syncAsync;
        _commandAsync = commandAsync;
        _generateRegion = generateRegion;
        _selectedFaces = selectedFaces;

        Text = "P9 CAD 生产配置（模拟运行）";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 820);
        MinimumSize = new Size(760, 650);
        Font = new Font("Microsoft YaHei UI", 9f);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildModelPage());
        tabs.TabPages.Add(BuildFixturePage());
        tabs.TabPages.Add(BuildRegionPage(defaultSettings, regions));
        tabs.TabPages.Add(BuildLoadingPage());
        tabs.TabPages.Add(BuildProductionPage());

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.Controls.Add(tabs, 0, 0);
        _feedback.Dock = DockStyle.Fill;
        _feedback.Padding = new Padding(10, 7, 10, 0);
        _feedback.Text = "人工工艺只在本窗口编辑；同步成功不等于允许生产。";
        root.Controls.Add(_feedback, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var ok = new Button { Text = "应用并关闭", Width = 120, Height = 30, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "取消", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (!TryApply(out string error)) { SetFeedback(error, true); return; }
            DialogResult = DialogResult.OK;
            Close();
        };
        actions.Controls.Add(ok);
        actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 2);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        LoadProduction(production);
        ApplySettings(defaultSettings);
        RefreshModel();
    }

    private TabPage BuildModelPage()
    {
        var (page, grid) = NewPage("模型", "原始 STL 随工程保存；cm/inch 在导入后统一换算为 mm。");
        _modelFile.ReadOnly = true;
        _modelMeta.ReadOnly = true;
        _modelMeta.Multiline = true;
        _modelMeta.Height = 76;
        _modelMeta.ScrollBars = ScrollBars.Vertical;
        Row(grid, "原始模型", _modelFile);
        Row(grid, "模型标识 / 校验", _modelMeta);
        _unit.DropDownStyle = ComboBoxStyle.DropDownList;
        _unit.Items.AddRange(["mm", "cm", "inch"]);
        Row(grid, "源文件单位", _unit);
        Row(grid, "工程名称 *", _projectName);
        _projectId.ReadOnly = true;
        Row(grid, "工程 ID", _projectId);
        var choose = Button("选择 / 重新关联 STL...", (_, _) => ChooseModel());
        Row(grid, "模型文件", choose);
        var sync = Button("同步模型与生产配置", async (_, _) => await SyncFromForm());
        Row(grid, "服务端同步", sync);
        return page;
    }

    private TabPage BuildFixturePage()
    {
        var (page, grid) = NewPage("工件与夹具", "坐标顺序固定为缩放 → Rx → Ry → Rz → 平移；先工件放置，后夹具位姿。");
        Row(grid, "夹具名称 *", _fixtureName);
        Row(grid, "夹具机器位姿 XYZ / RxRyRz", _fixturePose);
        Row(grid, "机械固定方式 *", _fixing);
        Row(grid, "工件到位信号 *", _partSignal);
        Row(grid, "夹紧到位信号 *", _clampSignal);
        CheckRow(grid, "虚拟工件到位自动确认", _autoPart);
        CheckRow(grid, "虚拟夹紧到位自动确认", _autoClamp);
        Row(grid, "工件相对夹具位姿", _placementPose);
        NumberRow(grid, "统一缩放", _scale, 1, 0.0001m, 1000, 4);
        CheckRow(grid, "全局法向反向（修正模型绕序）", _flipNormals);
        return page;
    }

    private TabPage BuildRegionPage(SurfaceProjectSettings settings, IReadOnlyList<SurfaceJobItem> regions)
    {
        var (page, grid) = NewPage("点胶区域", "Faces 使用三角面索引（从 0 开始）；也可在主场景启用面拾取后读取。Edges 仅保留并校验引用。");
        _regions.Dock = DockStyle.Fill;
        _regions.Height = 92;
        foreach (var item in regions) _regions.Items.Add(RegionText(item));
        _regions.SelectedIndexChanged += (_, _) => LoadSelectedRegion(regions);
        Row(grid, "已有区域（点击载入参数）", _regions);
        Row(grid, "区域名称 *", _regionName);
        _regionKind.DropDownStyle = ComboBoxStyle.DropDownList;
        _regionKind.Items.AddRange(["Slice", "Boundary", "Faces", "Edges"]);
        _regionKind.SelectedIndex = 0;
        Row(grid, "几何来源", _regionKind);
        Row(grid, "Faces / Edges 索引", _regionIndices);
        var readFaces = Button("读取场景选中面", (_, _) =>
        {
            if (_selectedFaces is null) { SetFeedback("主场景未提供面拾取。", true); return; }
            _regionIndices.Text = string.Join(",", _selectedFaces());
            _regionKind.SelectedItem = "Faces";
        });
        Row(grid, "场景选择", readFaces);
        NumberRow(grid, "切片 Z 起点 (mm)", _zStart, 0, -10000, 10000);
        NumberRow(grid, "切片 Z 终点 (mm)", _zEnd, 20, -10000, 10000);
        NumberRow(grid, "层间距 (mm)", _layer, 1, 0.001m, 1000, 3);
        NumberRow(grid, "法向偏置 (mm)", _offset, 0, -1000, 1000);
        NumberRow(grid, "等弧长间距 (mm，0原始)", _spacing, 2, 0, 1000);
        NumberRow(grid, "法向平滑次数", _smoothPasses, 2, 0, 100, 0);
        NumberRow(grid, "法向平滑强度", _smoothStrength, 0.5m, 0, 1, 2);
        NumberRow(grid, "最大姿态步长 (°)", _maxPoseStep, 30, 1, 30);
        NumberRow(grid, "进给 (mm/s)", _feed, 60, 0.001m, 10000, 3);
        NumberRow(grid, "开阀延时 (ms)", _open, 0, 0, 600000, 0);
        NumberRow(grid, "提前关胶 (ms)", _early, 0, 0, 600000, 0);
        NumberRow(grid, "关胶保持 (ms)", _close, 0, 0, 600000, 0);
        NumberRow(grid, "退枪高度 (mm)", _retract, 0, 0, 10000);
        var generate = Button("生成并添加区域路径", (_, _) => GenerateRegion());
        Row(grid, "复用现有路径生成器", generate);
        return page;
    }

    private TabPage BuildLoadingPage()
    {
        var (page, grid) = NewPage("上料装夹", "P0 只模拟动作和 IO，不直接驱动真实机器人、气缸或输送线。");
        Row(grid, "抓取位姿 XYZ / RxRyRz", _pickPose);
        Row(grid, "下料位姿 XYZ / RxRyRz", _unloadPose);
        NumberRow(grid, "接近高度 (mm)", _approach, 30, 0, 10000);
        NumberRow(grid, "抬升高度 (mm)", _lift, 50, 0, 10000);
        NumberRow(grid, "动作等待 (ms)", _delay, 300, 0, 600000, 0);
        NumberRow(grid, "批次数量", _batch, 1, 1, 1000000, 0);
        CheckRow(grid, "完整生产（含上下料）", _fullProduction);
        var step = Button("单步推进（上料 / 下料由当前阶段决定）", async (_, _) => await RunCommand("production_step", null));
        Row(grid, "模拟上料 / 装夹 / 下料", step);
        Row(grid, "工件到位人工确认", Button("确认 PART_PRESENT", async (_, _) => await RunCommand("production_confirm", "PART_PRESENT")));
        Row(grid, "夹紧到位人工确认", Button("确认 CLAMP_OK", async (_, _) => await RunCommand("production_confirm", "CLAMP_OK")));
        return page;
    }

    private TabPage BuildProductionPage()
    {
        var (page, grid) = NewPage("生产仿真", "启动前重新检查模型、指纹、RTCP 和路径安全；所有状态以服务端快照为准。");
        _snapshot.Height = 100;
        _snapshot.Dock = DockStyle.Fill;
        _snapshot.Text = "尚未收到生产快照";
        Row(grid, "当前状态", _snapshot);
        Row(grid, "同步", Button("同步模型与配置", async (_, _) => await SyncFromForm()));
        Row(grid, "生产", Button("开始生产", async (_, _) => await RunCommand("production_start", null)));
        Row(grid, "单步", Button("单步推进", async (_, _) => await RunCommand("production_step", null)));
        Row(grid, "暂停", Button("暂停", async (_, _) => await RunCommand("production_pause", null)));
        Row(grid, "继续", Button("继续", async (_, _) => await RunCommand("production_resume", null)));
        Row(grid, "停止（保留工件占用）", Button("停止", async (_, _) => await RunCommand("production_stop", null)));
        Row(grid, "失败恢复", Button("从当前步骤重试", async (_, _) => await RunCommand("production_retry", null)));
        Row(grid, "复位（清空当前循环）", Button("复位", async (_, _) => await RunCommand("production_reset", null)));
        return page;
    }

    private void LoadProduction(ProductionConfig p)
    {
        _projectId.Text = p.ProjectId;
        _projectName.Text = p.Name;
        _fixtureName.Text = p.Fixture.Name;
        _fixing.Text = p.Fixture.Fixing;
        _partSignal.Text = p.Fixture.PartSignal;
        _clampSignal.Text = p.Fixture.ClampSignal;
        _autoPart.Checked = p.Fixture.AutoPartConfirm;
        _autoClamp.Checked = p.Fixture.AutoClampConfirm;
        _fixturePose.Value = p.Fixture.Pose;
        _placementPose.Value = p.Placement.Pose;
        _scale.Value = Math.Clamp((decimal)p.Placement.Scale, _scale.Minimum, _scale.Maximum);
        _flipNormals.Checked = p.Placement.FlipNormals;
        _pickPose.Value = p.Loading.Pick;
        _unloadPose.Value = p.Loading.Unload;
        _approach.Value = Math.Clamp((decimal)p.Loading.ApproachMm, _approach.Minimum, _approach.Maximum);
        _lift.Value = Math.Clamp((decimal)p.Loading.LiftMm, _lift.Minimum, _lift.Maximum);
        _delay.Value = Math.Clamp(p.Loading.ActionDelayMs, _delay.Minimum, _delay.Maximum);
        _batch.Value = Math.Clamp(p.Loading.BatchCount, _batch.Minimum, _batch.Maximum);
        _fullProduction.Checked = p.FullProduction;
    }

    private void ApplySettings(SurfaceProjectSettings s)
    {
        SetNum(_zStart, s.ZStart); SetNum(_zEnd, s.ZEnd); SetNum(_layer, s.LayerHeight);
        SetNum(_offset, s.NormalOffset); SetNum(_spacing, s.PointSpacing);
        SetNum(_smoothPasses, s.NormalSmoothPasses); SetNum(_smoothStrength, s.NormalSmoothStrength);
        SetNum(_maxPoseStep, s.MaxPoseStepDeg); SetNum(_feed, s.Feed);
        SetNum(_open, s.Timing.OpenDelayMs); SetNum(_early, s.Timing.EarlyCloseMs);
        SetNum(_close, s.Timing.CloseDelayMs); SetNum(_retract, s.Timing.RetractMm);
    }

    private void LoadSelectedRegion(IReadOnlyList<SurfaceJobItem> regions)
    {
        int index = _regions.SelectedIndex;
        if (index < 0 || index >= regions.Count) return;
        var item = regions[index];
        _regionName.Text = item.Name;
        if (item.Settings is not null) ApplySettings(item.Settings);
        _regionKind.SelectedItem = item.Source?.Kind ?? "Slice";
        _regionIndices.Text = item.Source is null ? "" : string.Join(",", item.Source.Indices);
    }

    private static string RegionText(SurfaceJobItem item)
        => $"{item.Name} | {item.Source?.Kind ?? "旧曲面"} | {item.Path.Points.Count}点 | {(string.IsNullOrEmpty(item.Fingerprint) ? "无指纹" : "已指纹")}";

    /// <summary>校验并生成主窗口可直接应用的 DTO；失败时不修改已应用结果。</summary>
    public bool TryApply(out string error)
    {
        error = "";
        if (_model is null) { error = "模型缺失，请先选择或重新关联 STL。"; return false; }
        if (string.IsNullOrWhiteSpace(_projectName.Text) || string.IsNullOrWhiteSpace(_fixtureName.Text)
            || string.IsNullOrWhiteSpace(_fixing.Text) || string.IsNullOrWhiteSpace(_partSignal.Text)
            || string.IsNullOrWhiteSpace(_clampSignal.Text))
        { error = "工程名称、夹具名称、固定方式和两个到位信号均不能为空。"; return false; }
        if (_scale.Value <= 0) { error = "工件缩放必须大于 0。"; return false; }
        if (_zEnd.Value < _zStart.Value || _layer.Value <= 0 || _spacing.Value < 0
            || _feed.Value <= 0 || _maxPoseStep.Value is < 1 or > 30)
        { error = "区域切片、布点、进给或姿态步长参数无效。"; return false; }
        if (_batch.Value < 1) { error = "批次数量必须为正整数。"; return false; }
        try
        {
            var model = _model with { Unit = _unit.SelectedItem?.ToString() ?? "mm" };
            _ = model.UnitToMm;
            model.ValidateContent();
            var config = new ProductionConfig(
                _projectId.Text, _projectName.Text.Trim(),
                new FixtureConfig(_fixtureName.Text.Trim(), _fixturePose.Value, _fixing.Text.Trim(),
                    _partSignal.Text.Trim(), _clampSignal.Text.Trim(), _autoPart.Checked, _autoClamp.Checked),
                new PlacementConfig(_placementPose.Value, (double)_scale.Value, _flipNormals.Checked),
                new LoadingRecipe(_pickPose.Value, _unloadPose.Value, (double)_approach.Value,
                    (double)_lift.Value, (int)_delay.Value, (int)_batch.Value),
                _fullProduction.Checked);
            ResultModel = model;
            ResultConfig = config;
            return true;
        }
        catch (Exception ex) { error = "配置校验失败: " + ex.Message; return false; }
    }

    private void GenerateRegion()
    {
        if (_generateRegion is null) { SetFeedback("区域生成入口未连接。", true); return; }
        try
        {
            if (string.IsNullOrWhiteSpace(_regionName.Text)) throw new InvalidDataException("区域名称不能为空");
            string kind = _regionKind.SelectedItem?.ToString() ?? "Slice";
            int[] indices = ParseIndices(_regionIndices.Text);
            if (kind == "Faces" && indices.Length == 0 && _selectedFaces is not null)
                indices = _selectedFaces().Distinct().Order().ToArray();
            var source = kind switch
            {
                "Slice" => RegionSource.Slice(),
                "Boundary" => RegionSource.Boundary(),
                "Faces" => RegionSource.Faces(indices),
                "Edges" => RegionSource.Edges(indices),
                _ => throw new InvalidDataException("未知区域来源"),
            };
            var request = new RegionGenerationRequest(_regionName.Text.Trim(), source, ReadRegionSettings());
            string message = _generateRegion(request);
            _regions.Items.Add($"{request.Name} | {kind} | 已生成");
            SetFeedback(message);
        }
        catch (Exception ex) { SetFeedback("区域生成失败: " + ex.Message, true); }
    }

    private SurfaceProjectSettings ReadRegionSettings() => new(
        (double)_zStart.Value, (double)_zEnd.Value, (double)_layer.Value, (double)_offset.Value,
        (double)_feed.Value, new DispenseTiming((double)_open.Value, (double)_early.Value,
            (double)_close.Value, (double)_retract.Value), new ModelTransform(),
        (double)_spacing.Value, (int)_smoothPasses.Value, (double)_smoothStrength.Value,
        (double)_maxPoseStep.Value);

    private static int[] ParseIndices(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Split([',', ';', '，', '、', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out int value)
                ? value : throw new InvalidDataException($"几何索引不是整数: {s}"))
            .Distinct().Order().ToArray();
    }

    private void ChooseModel()
    {
        using var dlg = new OpenFileDialog { Filter = "STL 模型 (*.stl)|*.stl|所有文件 (*.*)|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var candidate = ModelAsset.FromFile(dlg.FileName, _unit.SelectedItem?.ToString() ?? "mm");
            var mesh = StlImporter.ImportBytes(candidate.GetBytes());
            if (mesh.Triangles.Count == 0) throw new InvalidDataException("STL 中没有三角面");
            _model = candidate;
            ResultModel = candidate;
            RefreshModel();
            SetFeedback("模型已读取；应用后原路径将按新模型指纹重新校验。", false);
        }
        catch (Exception ex) { SetFeedback("模型读取失败: " + ex.Message, true); }
    }

    private void RefreshModel()
    {
        _modelFile.Text = _model?.FileName ?? "未关联模型";
        _modelMeta.Text = _model is null ? "完整生产禁止启动" :
            $"ID: {_model.Id}\r\nSHA-256: {_model.Sha256}\r\n原始字节: {_model.GetBytes().Length:N0}";
        _unit.SelectedItem = _model?.Unit ?? "mm";
        if (_unit.SelectedIndex < 0) _unit.SelectedIndex = 0;
        if (string.IsNullOrWhiteSpace(_projectName.Text)) _projectName.Text = _model?.FileName ?? "P9 生产工程";
    }

    private async Task SyncFromForm()
    {
        if (_syncAsync is null) { SetFeedback("同步入口未连接。", true); return; }
        if (!TryApply(out string error)) { SetFeedback(error, true); return; }
        await RunBusy(async () =>
        {
            await _syncAsync();
            SetFeedback("模型和生产配置已收到服务端成功 ACK。", false);
        });
    }

    private Task RunCommand(string command, string? signal)
    {
        if (_commandAsync is null) { SetFeedback("生产控制入口未连接。", true); return Task.CompletedTask; }
        if (!TryApply(out string error)) { SetFeedback(error, true); return Task.CompletedTask; }
        return RunBusy(async () =>
        {
            await _commandAsync(command, signal);
            SetFeedback($"命令 {command} 已收到成功 ACK；等待生产快照。", false);
        });
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception ex) { SetFeedback(ex.Message, true); }
        finally { _busy = false; }
    }

    public void UpdateSnapshot(ProductionSnapshotDto s)
    {
        if (IsDisposed) return;
        _snapshot.Text = $"阶段: {s.Phase}    {(s.Paused ? "已暂停" : "模拟运行")}\r\n" +
                         $"工件: {s.PieceIndex}    完成: {s.CompletedCount}/{s.BatchCount}    进度: {s.Progress:P1}\r\n" +
                         $"到位: {s.PartPresent}    夹紧: {s.Clamped}    可重试: {s.CanRetry}\r\n" +
                         $"阻断原因: {s.Error ?? "无"}";
    }

    public void SetFeedback(string text, bool error = false)
    {
        _feedback.Text = text;
        _feedback.ForeColor = error ? Color.Firebrick : Color.DarkGreen;
    }

    private static (TabPage Page, TableLayoutPanel Grid) NewPage(string title, string hint)
    {
        var page = new TabPage(title);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true,
            Padding = new Padding(12), BackColor = SystemColors.Window,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var label = new Label { Text = hint, Dock = DockStyle.Fill, AutoSize = false, Height = 48, ForeColor = Color.DimGray };
        grid.Controls.Add(label, 0, 0);
        grid.SetColumnSpan(label, 2);
        page.Controls.Add(grid);
        return (page, grid);
    }

    private static int NextRow(TableLayoutPanel grid)
    {
        int row = grid.RowCount;
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return row;
    }

    private static void Row(TableLayoutPanel grid, string label, Control control)
    {
        int row = NextRow(grid);
        grid.Controls.Add(new Label
        {
            Text = label, AutoSize = false, Dock = DockStyle.Fill,
            Height = Math.Max(30, control.Height + 6), TextAlign = ContentAlignment.MiddleLeft,
        }, 0, row);
        control.Dock = control is NumericUpDown or System.Windows.Forms.Button ? DockStyle.Left : DockStyle.Fill;
        control.Margin = new Padding(3, 3, 3, 8);
        grid.Controls.Add(control, 1, row);
    }

    private static void CheckRow(TableLayoutPanel grid, string text, CheckBox check)
    {
        check.Text = text;
        check.AutoSize = true;
        Row(grid, "人工确认", check);
    }

    private static void NumberRow(TableLayoutPanel grid, string label, NumericUpDown number,
        decimal value, decimal min, decimal max, int decimals = 1)
    {
        number.Minimum = min; number.Maximum = max; number.DecimalPlaces = decimals;
        number.Increment = decimals == 0 ? 1 : decimals >= 3 ? 0.001m : 0.1m;
        number.Value = Math.Clamp(value, min, max);
        number.Width = 150;
        Row(grid, label, number);
    }

    private static Button Button(string text, EventHandler handler)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30, Padding = new Padding(8, 0, 8, 0) };
        button.Click += handler;
        return button;
    }

    private static void SetNum(NumericUpDown control, double value)
        => control.Value = Math.Clamp((decimal)value, control.Minimum, control.Maximum);

    /// <summary>六分量位姿输入，避免 Designer 自动生成大量重复代码。</summary>
    private sealed class PoseEditor : TableLayoutPanel
    {
        private readonly NumericUpDown[] _values = new NumericUpDown[6];

        public PoseEditor()
        {
            ColumnCount = 6;
            RowCount = 2;
            AutoSize = true;
            Height = 56;
            string[] names = ["X", "Y", "Z", "Rx", "Ry", "Rz"];
            for (int i = 0; i < 6; i++)
            {
                ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6f));
                Controls.Add(new Label { Text = names[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, i, 0);
                _values[i] = new NumericUpDown
                {
                    Minimum = -100000, Maximum = 100000, DecimalPlaces = 3,
                    Increment = i < 3 ? 1 : 0.5m, Dock = DockStyle.Fill,
                };
                Controls.Add(_values[i], i, 1);
            }
        }

        public Pose3D Value
        {
            get => new((double)_values[0].Value, (double)_values[1].Value, (double)_values[2].Value,
                (double)_values[3].Value, (double)_values[4].Value, (double)_values[5].Value);
            set
            {
                double[] v = [value.X, value.Y, value.Z, value.Rx, value.Ry, value.Rz];
                for (int i = 0; i < 6; i++) _values[i].Value = Math.Clamp((decimal)v[i], _values[i].Minimum, _values[i].Maximum);
            }
        }
    }
}
