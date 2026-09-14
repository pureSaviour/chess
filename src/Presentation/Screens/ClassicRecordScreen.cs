using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain;
using Xiangqi.Presentation.Records;
using Xiangqi.Presentation.AI;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

public partial class ClassicRecordScreen : Control
{
    private readonly List<ImageTexture> _textures = [];
    private readonly RecordExportDialog _exportDialog;
    private readonly AcceptDialog _errorDialog = new() { Title = "记录操作失败" };

    public ClassicRecordScreen(bool saved, Action back, Action<ClassicGameRecord> replay)
    {
        _exportDialog = new RecordExportDialog(".pgn", "PGN / ICCS", ClassicRecordStore.Export);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(60, 36); AddChild(margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 12); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle(saved ? "经典象棋 · 保存对局" : "经典象棋 · 最近对局"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var backButton = UiStyle.CreateButton("返回画廊"); backButton.Pressed += back; header.AddChild(backButton);
        root.AddChild(RecordEntryRow.CreateHeadings(true));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(900, 360) }; root.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; rows.AddThemeConstantOverride("separation", 10); scroll.AddChild(rows);
        AddChild(_exportDialog);
        AddChild(_errorDialog);
        foreach (var entry in ClassicRecordStore.ListEntries(saved))
        {
            bool SaveEntry()
            {
                try { ClassicRecordStore.SavePermanent(entry); return true; }
                catch (Exception error) { ShowError(error); return false; }
            }
            rows.AddChild(RecordEntryRow.Create(entry.Presentation,
                () => replay(ClassicRecordStore.Load(entry.RecordPath)), _textures,
                saved ? null : SaveEntry, () => _exportDialog.Open(entry.RecordPath)));
        }
    }

    private void ShowError(Exception error)
    {
        _errorDialog.DialogText = error.Message;
        _errorDialog.PopupCentered(new Vector2I(620, 170));
    }
    public override void _ExitTree() { foreach (var texture in _textures) texture.Dispose(); _textures.Clear(); }
}

public partial class ClassicImportScreen : Control
{
    public ClassicImportScreen(bool puzzle, Action back, Action<ClassicGameRecord> replay, Action<GameSession> startPuzzle)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(60, 36); AddChild(margin); var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 14); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header); var title = UiStyle.CreateTitle(puzzle ? "残局导入" : "棋谱导入"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        var source = new TextEdit { CustomMinimumSize = new(0, 360), SizeFlagsVertical = SizeFlags.ExpandFill,
            PlaceholderText = puzzle ? "粘贴 Xiangqi FEN" : "粘贴带有 [Format \"ICCS\"] 的中国象棋 PGN" }; root.AddChild(source);
        var submit = UiStyle.CreateButton(puzzle ? "从该残局开始" : "导入并复盘", true); root.AddChild(submit);
        var errorDialog = new AcceptDialog { Title = "无法导入" }; AddChild(errorDialog);
        submit.Pressed += () =>
        {
            if (!puzzle)
            {
                if (XiangqiPgnCodec.TryParse(source.Text, out var record, out var pgnError)) replay(record!);
                else { errorDialog.DialogText = pgnError; errorDialog.PopupCentered(new(580, 170)); }
                return;
            }
            if (XiangqiUciNotation.TryParseFen(source.Text.Trim(), out var position, out var fenError))
                startPuzzle(new GameSession(position, [new DoubleChariotMateDetector()]));
            else { errorDialog.DialogText = fenError; errorDialog.PopupCentered(new(580, 170)); }
        };
    }
}

public partial class ClassicReplayScreen : Control
{
    private readonly BoardView _board;
    private readonly ReplayAnalysisCoordinator? _analysis;
    private readonly ReplayEvaluationGraph? _graph;
    private readonly Label? _score;
    private readonly Godot.Timer? _analysisDebounce;
    private readonly IReadOnlyList<GameSnapshot> _positions;
    private readonly HSlider _timeline;
    private int _index;
    public ClassicReplayScreen(AppSettings settings, IBoardThemeCatalog themes, ClassicGameRecord record,
        Action back, string? enginePath = null)
    {
        _positions = record.Replay();
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(32, 20); AddChild(margin);
        var root = new VBoxContainer(); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle("棋局复盘"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var label = new Label(); header.AddChild(label);
        var leave = UiStyle.CreateButton("返回记录"); leave.Pressed += back; header.AddChild(leave);
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddThemeConstantOverride("separation", 16); root.AddChild(body);
        var center = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddChild(center);
        _board = new BoardView(themes) { MouseFilter = MouseFilterEnum.Ignore }; _board.ApplyAppearance(settings.ThemeId, settings.ShowBoardCoordinates); center.AddChild(_board);
        if (!string.IsNullOrWhiteSpace(enginePath) && File.Exists(enginePath))
        {
            var analysisPanel = new VBoxContainer { CustomMinimumSize = new(350, 0) }; analysisPanel.AddThemeConstantOverride("separation", 10); body.AddChild(analysisPanel);
            analysisPanel.AddChild(new Label { Text = "引擎局势分析" }); _score = new Label { Text = "正在计算…", HorizontalAlignment = HorizontalAlignment.Right }; _score.AddThemeFontSizeOverride("font_size", 20); analysisPanel.AddChild(_score);
            _graph = new ReplayEvaluationGraph(); _graph.Configure(_positions.Count); analysisPanel.AddChild(_graph);
            var hint = new Label { Text = "红色：红方优势  ·  蓝色：黑方优势\n金色竖线：当前复盘步", AutowrapMode = TextServer.AutowrapMode.WordSmart }; UiStyle.SetMuted(hint, 13); analysisPanel.AddChild(hint);
            var engine = new UciProcessAiEngine(enginePath,
                new UciEngineResourceOptions(AiDifficultyProfiles.ReplayThreads,
                    AiDifficultyProfiles.ReplayHashMegabytes)); AddChild(engine);
            _analysis = new ReplayAnalysisCoordinator(engine, _positions, settings.ReplayAnalysisMilliseconds);
            _analysis.AnalysisAvailable += OnAnalysisAvailable;
            _analysisDebounce = new Godot.Timer { OneShot = true, WaitTime = .15 };
            _analysisDebounce.Timeout += () => _ = AnalyzeAsync(_index);
            AddChild(_analysisDebounce);
        }
        _timeline = new HSlider
        {
            MinValue = 0, MaxValue = Math.Max(0, _positions.Count - 1), Step = 1,
            TickCount = Math.Clamp(_positions.Count, 2, 31), TicksOnBorders = true,
            CustomMinimumSize = new Vector2(0, 42), SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _timeline.TooltipText = "拖动到任意刻度以跳转到对应手数";
        root.AddChild(_timeline);
        var controls = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; root.AddChild(controls);
        var previous = UiStyle.CreateButton("◀ 上一步"); var next = UiStyle.CreateButton("下一步 ▶"); controls.AddChild(previous); controls.AddChild(next);
        void Render(bool requestAnalysis)
        {
            _board.SetPosition(_positions[_index]); label.Text = $"第 {_index}/{_positions.Count - 1} 手";
            _timeline.SetValueNoSignal(_index);
            previous.Disabled = _index == 0; next.Disabled = _index == _positions.Count - 1; _graph?.SetCurrent(_index);
            if (_analysis?.Cache.TryGetValue(_index, out var point) == true) ApplyCurrentAnalysis(point);
            else { _board.SetAnalysisMove(null); if (_score is not null) _score.Text = _positions[_index].IsFinished ? "终局" : "正在计算…"; }
            if (requestAnalysis && _analysisDebounce is not null) _analysisDebounce.Start();
        }
        previous.Pressed += () => { if (_index > 0) _index--; Render(true); }; next.Pressed += () => { if (_index + 1 < _positions.Count) _index++; Render(true); };
        _timeline.ValueChanged += value => { _index = Math.Clamp((int)Math.Round(value), 0, _positions.Count - 1); Render(true); };
        Render(true);
    }
    private async Task AnalyzeAsync(int index)
    {
        try { if (_analysis is not null) await _analysis.SelectAndSweepAsync(index); }
        catch (Exception error) { if (_score is not null) _score.Text = $"引擎错误：{error.Message}"; }
    }
    private void OnAnalysisAvailable(ReplayAnalysisPoint point)
    {
        _graph?.SetPoint(point);
        if (point.PositionIndex == _index) ApplyCurrentAnalysis(point);
    }
    private void ApplyCurrentAnalysis(ReplayAnalysisPoint point)
    {
        _board.SetAnalysisMove(point.BestMove);
        if (_score is null) return;
        if (point.IsMate) _score.Text = point.RedCentipawns >= 0 ? "红方有杀" : "黑方有杀";
        else if (Math.Abs(point.RedCentipawns) < 10) _score.Text = "局势均衡";
        else _score.Text = point.RedCentipawns > 0 ? $"红优 {point.RedCentipawns} 分" : $"黑优 {-point.RedCentipawns} 分";
    }
    public override void _ExitTree()
    {
        if (_analysis is null) return;
        _analysis.AnalysisAvailable -= OnAnalysisAvailable;
        _ = _analysis.DisposeAsync();
    }
}
