using Godot;
using Xiangqi.Core.Application.Junqi;
using Xiangqi.Core.Domain.Junqi;
using Xiangqi.Presentation.Boards;
using Xiangqi.Presentation.Records;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>Junqi match UI. It submits cell intents to a controller and never evaluates rules.</summary>
public partial class JunqiGameScreen : Control
{
    private readonly IJunqiMatchController _controller;
    private readonly JunqiBoardView _board = new();
    private readonly Label _status = new();
    private readonly Button _save;
    private readonly Control? _privacyCurtain;
    private JunqiCell? _selected;
    private IReadOnlyList<JunqiCell> _legal = [];
    private bool _recentSaved;
    private bool _turnRevealed;

    public JunqiGameScreen(Action back) : this(new LocalJunqiMatchController(), "本地联机", back) { }

    public JunqiGameScreen(IJunqiMatchController controller, string roomTitle, Action back, Node? networkDriver = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        if (networkDriver is not null) AddChild(networkDriver);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(30, 18); AddChild(margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 8); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle($"军旗 · {roomTitle}", 30); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        _status.VerticalAlignment = VerticalAlignment.Center; header.AddChild(_status);
        _save = UiStyle.CreateButton("保存棋局"); _save.Disabled = true; header.AddChild(_save);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; root.AddChild(center);
        var stage = new Control { CustomMinimumSize = new Vector2(520, 700) }; center.AddChild(stage);
        _board.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); stage.AddChild(_board);
        if (_controller.LocalSide is null)
        {
            _privacyCurtain = CreatePrivacyCurtain(); stage.AddChild(_privacyCurtain);
        }
        var hint = new Label { Text = "铁路直行；工兵可沿铁路转弯。行营不可攻击，大本营内棋子不能移动。", HorizontalAlignment = HorizontalAlignment.Center };
        UiStyle.SetMuted(hint, 13); root.AddChild(hint);
        _board.CellPressed += OnCellPressed; _controller.Changed += OnChanged; _controller.Message += OnMessage; _save.Pressed += Save;
        Render();
    }

    private Control CreatePrivacyCurtain()
    {
        var curtain = UiStyle.CreatePanel(new Color(0.035f, 0.025f, 0.018f, .97f), 12);
        curtain.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); curtain.MouseFilter = MouseFilterEnum.Stop;
        var center = new CenterContainer(); center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); curtain.AddChild(center);
        var content = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; content.AddThemeConstantOverride("separation", 18); center.AddChild(content);
        var label = UiStyle.CreateTitle("请当前玩家接手设备", 25); label.HorizontalAlignment = HorizontalAlignment.Center; content.AddChild(label);
        var reveal = UiStyle.CreateButton("查看本方棋盘", true); reveal.CustomMinimumSize = new Vector2(260, 48); content.AddChild(reveal);
        reveal.Pressed += () => { _turnRevealed = true; curtain.Visible = false; Render(); };
        return curtain;
    }

    private async void OnCellPressed(JunqiCell cell)
    {
        var snapshot = _controller.Snapshot;
        if (!_turnRevealed && _controller.LocalSide is null || snapshot.IsFinished || !_controller.CanSubmitMove) return;
        var piece = snapshot.Pieces.FirstOrDefault(x => x.Cell == cell);
        var maySelect = piece.PieceId > 0 && piece.Side == snapshot.SideToMove &&
            (_controller.LocalSide is null || piece.Side == _controller.LocalSide);
        if (maySelect) { _selected = cell; _legal = _controller.LegalDestinations(cell); Render(); return; }
        if (_selected is not { } from || !_legal.Contains(cell)) return;
        var result = await _controller.SubmitMoveAsync(from, cell); _selected = null; _legal = [];
        if (!result.Accepted) OnMessage(result.Error ?? "着法被拒绝。");
        if (_controller.LocalSide is null && result.Accepted) { _turnRevealed = false; if (_privacyCurtain is not null) _privacyCurtain.Visible = true; }
        Render();
    }

    private void OnChanged()
    {
        _selected = null; _legal = [];
        if (_controller.LocalSide is null) { _turnRevealed = false; if (_privacyCurtain is not null) _privacyCurtain.Visible = true; }
        Render();
    }
    private void OnMessage(string text) => _status.Text = text;
    private void Render()
    {
        var snapshot = _controller.Snapshot; var bottom = _controller.LocalSide ?? snapshot.SideToMove;
        _board.SetPosition(snapshot, _selected, _legal, bottom);
        _status.Text = !_controller.IsConnected ? "等待联机连接" : !_controller.IsReady ? "等待对手布阵"
            : snapshot.IsFinished ? snapshot.Winner == JunqiSide.First ? "先手胜" : snapshot.Winner == JunqiSide.Second ? "后手胜" : "对局结束"
            : $"{(snapshot.SideToMove == JunqiSide.First ? "先手" : "后手")}行棋 · 连续未交战 {snapshot.QuietPlyCount}/{JunqiGame.StandardMandatoryAttackPly}";
        if (!snapshot.IsFinished) return;
        _save.Disabled = false;
        if (_recentSaved) return; _recentSaved = true;
        try { if (TryRecord(out var record)) JunqiRecordStore.SaveRecent(record!); }
        catch (Exception error) { GD.PushWarning($"保存军旗最近对局失败：{error.Message}"); }
    }
    private bool TryRecord(out JunqiGameRecord? record) => _controller.TryCreateFinishedRecord("先手", "后手", out record, out _);
    private void Save()
    {
        try
        {
            if (!TryRecord(out var record)) throw new InvalidOperationException("对局或赛后布阵尚未就绪。");
            JunqiRecordStore.SavePermanent(record!); _save.Disabled = true; _save.Text = "已保存";
        }
        catch (Exception error) { GD.PushWarning($"保存军旗棋局失败：{error.Message}"); }
    }
    internal void RevealForSmoke()
    {
        if (_controller.LocalSide is not null) return;
        _turnRevealed = true; if (_privacyCurtain is not null) _privacyCurtain.Visible = false; Render();
    }
    public override void _ExitTree()
    {
        _board.CellPressed -= OnCellPressed; _controller.Changed -= OnChanged; _controller.Message -= OnMessage; _ = _controller.DisposeAsync();
    }
}

public partial class JunqiRecordScreen : Control
{
    public JunqiRecordScreen(bool saved, Action back, Action<JunqiGameRecord> replay)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(54, 34); AddChild(margin); var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 12); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header); var title = UiStyle.CreateTitle(saved ? "军旗 · 保存对局" : "军旗 · 最近对局"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; root.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; rows.AddThemeConstantOverride("separation", 8); scroll.AddChild(rows);
        foreach (var path in JunqiRecordStore.List(saved))
        {
            var record = JunqiRecordStore.Load(path); var result = record.Winner == JunqiSide.First ? "先手胜" : record.Winner == JunqiSide.Second ? "后手胜" : "未决";
            var button = UiStyle.CreateButton($"{File.GetLastWriteTime(path):yyyy-MM-dd HH-mm}    {record.FirstPlayer}  {result}  {record.SecondPlayer}");
            button.Pressed += () => replay(record); rows.AddChild(button);
        }
    }
}

public partial class JunqiReplayScreen : Control
{
    private readonly IReadOnlyList<JunqiSnapshot> _positions;
    private readonly JunqiBoardView _board = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly HSlider _timeline;
    private readonly Label _ply = new();
    private int _index;
    public JunqiReplayScreen(JunqiGameRecord record, Action back)
    {
        _positions = record.Replay(); SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(30, 18); AddChild(margin); var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 8); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header); var title = UiStyle.CreateTitle("军旗复盘", 30); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title); header.AddChild(_ply);
        var leave = UiStyle.CreateButton("返回记录"); leave.Pressed += back; header.AddChild(leave);
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; root.AddChild(center); center.AddChild(_board);
        _timeline = new HSlider { MinValue = 0, MaxValue = Math.Max(0, _positions.Count - 1), Step = 1,
            TickCount = Math.Clamp(_positions.Count, 2, 31), TicksOnBorders = true, CustomMinimumSize = new Vector2(0, 42) }; root.AddChild(_timeline);
        var controls = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; root.AddChild(controls);
        var previous = UiStyle.CreateButton("◀ 上一步"); var next = UiStyle.CreateButton("下一步 ▶"); controls.AddChild(previous); controls.AddChild(next);
        void Render() { _board.SetPosition(_positions[_index].ForReplay()); _timeline.SetValueNoSignal(_index); _ply.Text = $"第 {_index}/{_positions.Count - 1} 手"; previous.Disabled = _index == 0; next.Disabled = _index == _positions.Count - 1; }
        previous.Pressed += () => { if (_index > 0) _index--; Render(); }; next.Pressed += () => { if (_index + 1 < _positions.Count) _index++; Render(); };
        _timeline.ValueChanged += value => { _index = Math.Clamp((int)Math.Round(value), 0, _positions.Count - 1); Render(); }; Render();
    }
}
