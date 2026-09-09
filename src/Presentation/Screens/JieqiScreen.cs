using Godot;
using Xiangqi.Core.Application.Jieqi;
using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.Jieqi;
using Xiangqi.Presentation.Records;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>Local Jieqi match screen; the shared BoardView supplies classic movement and selection effects.</summary>
public partial class JieqiGameScreen : Control
{
    private readonly JieqiGameSession _session;
    private readonly BoardView _board;
    private readonly Label _status = new();
    private readonly Button _save;
    private BoardPosition? _selected;
    private IReadOnlyList<BoardPosition> _legal = [];
    private bool _recentSaved;

    public JieqiGameScreen(AppSettings settings, IBoardThemeCatalog themes, ulong seed, Action back)
    {
        _session = new JieqiGameSession(seed);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(32, 20); AddChild(margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 10); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle("揭棋 · 本地对决", 30); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        _status.VerticalAlignment = VerticalAlignment.Center; header.AddChild(_status);
        _save = UiStyle.CreateButton("保存棋局"); _save.Disabled = true; header.AddChild(_save);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; root.AddChild(center);
        _board = new BoardView(themes); _board.ApplyAppearance(settings.ThemeId, settings.ShowBoardCoordinates);
        // null is intentional: live UI receives no hidden-identity dictionary.
        _board.SetCoveredIdentityHints(null, false); center.AddChild(_board);
        var hint = new Label { Text = "暗子首步按所在槽位走法；落子后翻明。翻明的士、象可走出九宫或过河。", HorizontalAlignment = HorizontalAlignment.Center };
        UiStyle.SetMuted(hint, 13); root.AddChild(hint);
        _board.SquarePressed += OnSquarePressed;
        _session.StateChanged += Render;
        _session.MoveCommitted += Animate;
        _save.Pressed += Save;
        Render(_session.Snapshot);
    }

    private void OnSquarePressed(BoardPosition square)
    {
        if (_board.IsAnimating || _session.Snapshot.IsFinished) return;
        var own = _session.Snapshot.Pieces.FirstOrDefault(x => x.Position == square).Piece;
        if (own.Kind.Value is not null && own.Side == _session.Snapshot.SideToMove)
        {
            _selected = square; _legal = _session.GetLegalDestinations(square); Render(_session.Snapshot); return;
        }
        if (_selected is not { } from || !_legal.Contains(square)) return;
        _session.TryMove(from, square); _selected = null; _legal = [];
    }

    private void Animate(GameSnapshot before, GameSnapshot after, ChessMove move, bool captured) =>
        _board.AnimateMove(before, after, move, captured);

    private void Render(GameSnapshot snapshot)
    {
        if (!_board.IsAnimating) _board.SetPosition(snapshot, _selected, _legal);
        _status.Text = snapshot.IsFinished
            ? snapshot.Winner == PieceSide.Red ? "红方胜" : "黑方胜"
            : snapshot.SideToMove == PieceSide.Red ? "红方行棋" : "黑方行棋";
        if (!snapshot.IsFinished) return;
        _save.Disabled = false;
        if (_recentSaved) return;
        _recentSaved = true;
        try { JieqiRecordStore.SaveRecent(BuildRecord(snapshot)); }
        catch (Exception error) { GD.PushWarning($"保存揭棋最近对局失败：{error.Message}"); }
    }

    private JieqiGameRecord BuildRecord(GameSnapshot snapshot) => _session.CreateRecord(new Dictionary<string, string>
    {
        ["Event"] = "本地联机", ["Red"] = "本地玩家 1", ["Black"] = "本地玩家 2",
        ["Result"] = snapshot.Winner == PieceSide.Red ? "1-0" : snapshot.Winner == PieceSide.Black ? "0-1" : "*"
    });

    private void Save()
    {
        try { JieqiRecordStore.SavePermanent(BuildRecord(_session.Snapshot)); _save.Disabled = true; _save.Text = "已保存"; }
        catch (Exception error) { GD.PushWarning($"保存揭棋棋局失败：{error.Message}"); }
    }

    public override void _ExitTree()
    {
        _board.SquarePressed -= OnSquarePressed; _session.StateChanged -= Render; _session.MoveCommitted -= Animate;
    }
}

public partial class JieqiRecordScreen : Control
{
    public JieqiRecordScreen(bool saved, Action back, Action<JieqiGameRecord> replay)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(54, 34); AddChild(margin); var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 12); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header); var title = UiStyle.CreateTitle(saved ? "揭棋 · 保存对局" : "揭棋 · 最近对局"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        var rows = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; rows.AddThemeConstantOverride("separation", 8); root.AddChild(rows);
        foreach (var path in JieqiRecordStore.List(saved))
        {
            var record = JieqiRecordStore.Load(path); var result = record.Tags.GetValueOrDefault("Result", "*");
            var button = UiStyle.CreateButton($"{File.GetLastWriteTime(path):yyyy-MM-dd HH-mm}    {record.Tags.GetValueOrDefault("Red", "红方")}  {result}  {record.Tags.GetValueOrDefault("Black", "黑方")}");
            button.Pressed += () => replay(record); rows.AddChild(button);
        }
    }
}

public partial class JieqiReplayScreen : Control
{
    private readonly IReadOnlyList<JieqiSnapshot> _positions;
    private readonly BoardView _board;
    private readonly HSlider _timeline;
    private readonly Label _ply = new();
    private readonly CheckButton _reveal = new() { Text = "隐约显示暗子身份" };
    private int _index;

    public JieqiReplayScreen(AppSettings settings, IBoardThemeCatalog themes, JieqiGameRecord record, Action back)
    {
        _positions = record.Replay(); SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(32, 20); AddChild(margin); var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 8); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header); var title = UiStyle.CreateTitle("揭棋复盘", 30); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title); header.AddChild(_reveal); header.AddChild(_ply);
        var leave = UiStyle.CreateButton("返回记录"); leave.Pressed += back; header.AddChild(leave);
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; root.AddChild(center);
        _board = new BoardView(themes) { MouseFilter = MouseFilterEnum.Ignore }; _board.ApplyAppearance(settings.ThemeId, settings.ShowBoardCoordinates); center.AddChild(_board);
        _timeline = new HSlider { MinValue = 0, MaxValue = Math.Max(0, _positions.Count - 1), Step = 1,
            TickCount = Math.Clamp(_positions.Count, 2, 31), TicksOnBorders = true, CustomMinimumSize = new Vector2(0, 42) }; root.AddChild(_timeline);
        var controls = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; root.AddChild(controls);
        var previous = UiStyle.CreateButton("◀ 上一步"); var next = UiStyle.CreateButton("下一步 ▶"); controls.AddChild(previous); controls.AddChild(next);
        void Render()
        {
            var position = _positions[_index]; _board.SetCoveredIdentityHints(position.HiddenIdentityHints(), _reveal.ButtonPressed);
            _board.SetPosition(position.ToPublicPosition()); _timeline.SetValueNoSignal(_index); _ply.Text = $"第 {_index}/{_positions.Count - 1} 手";
            previous.Disabled = _index == 0; next.Disabled = _index == _positions.Count - 1;
        }
        previous.Pressed += () => { if (_index > 0) _index--; Render(); }; next.Pressed += () => { if (_index + 1 < _positions.Count) _index++; Render(); };
        _timeline.ValueChanged += value => { _index = Math.Clamp((int)Math.Round(value), 0, _positions.Count - 1); Render(); };
        _reveal.Toggled += _ => Render(); Render();
    }
}
