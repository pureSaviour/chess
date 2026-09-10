using Godot;
using Xiangqi.Core.Application.Jieqi;
using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.Jieqi;
using Xiangqi.Presentation.Records;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>Jieqi match view; every local or network move passes through the injected controller.</summary>
public partial class JieqiGameScreen : Control
{
    private readonly IJieqiMatchController _controller;
    private readonly BoardView _board;
    private readonly Label _status = new();
    private readonly Button _save;
    private BoardPosition? _selected;
    private IReadOnlyList<BoardPosition> _legal = [];
    private bool _recentSaved;
    private GameSnapshot _displayed;

    public JieqiGameScreen(AppSettings settings, IBoardThemeCatalog themes, ulong seed, Action back)
        : this(settings, themes, new LocalJieqiMatchController(seed), "本地对决", back) { }

    public JieqiGameScreen(AppSettings settings, IBoardThemeCatalog themes,
        IJieqiMatchController controller, string roomTitle, Action back, Node? networkDriver = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _displayed = controller.Snapshot;
        if (networkDriver is not null) AddChild(networkDriver);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(32, 20); AddChild(margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 10); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle($"揭棋 · {roomTitle}", 30); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        _status.VerticalAlignment = VerticalAlignment.Center; header.AddChild(_status);
        _save = UiStyle.CreateButton("保存棋局"); _save.Disabled = true; header.AddChild(_save);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; root.AddChild(center);
        _board = new BoardView(themes); _board.ApplyAppearance(settings.ThemeId, settings.ShowBoardCoordinates);
        _board.SetCoveredIdentityHints(null, false); center.AddChild(_board);
        var hint = new Label { Text = "暗子首步按所在槽位走法；联机对局只由房主保管秘密排列。", HorizontalAlignment = HorizontalAlignment.Center };
        UiStyle.SetMuted(hint, 13); root.AddChild(hint);
        _board.SquarePressed += OnSquarePressed;
        _controller.Changed += OnChanged; _controller.Message += OnMessage;
        _save.Pressed += Save; Render(controller.Snapshot);
    }

    private async void OnSquarePressed(BoardPosition square)
    {
        var snapshot = _controller.Snapshot;
        if (_board.IsAnimating || snapshot.IsFinished || !_controller.CanSubmitMove) return;
        var own = snapshot.Pieces.FirstOrDefault(x => x.Position == square).Piece;
        if (own.Kind.Value is not null && own.Side == snapshot.SideToMove &&
            (_controller.LocalSide is null || own.Side == _controller.LocalSide))
        {
            _selected = square; _legal = _controller.GetLegalDestinations(square); Render(snapshot); return;
        }
        if (_selected is not { } from || !_legal.Contains(square)) return;
        var result = await _controller.SubmitMoveAsync(from, square); _selected = null; _legal = [];
        if (!result.Accepted) OnMessage(result.Error ?? $"非法着法：{result.Rejection}。");
        Render(_controller.Snapshot);
    }

    private void OnChanged()
    {
        var next = _controller.Snapshot;
        if (next.PlyNumber == _displayed.PlyNumber + 1 && TryFindMove(_displayed, next, out var move))
            _board.AnimateMove(_displayed, next, move, next.Pieces.Count < _displayed.Pieces.Count);
        _displayed = next; Render(next);
    }

    private static bool TryFindMove(GameSnapshot before, GameSnapshot after, out ChessMove move)
    {
        foreach (var old in before.Pieces)
        {
            var current = after.Pieces.FirstOrDefault(x => x.Piece.PieceInstanceId == old.Piece.PieceInstanceId);
            if (current.Piece.Kind.Value is null || current.Position == old.Position) continue;
            move = new ChessMove(old.Position, current.Position); return true;
        }
        move = default; return false;
    }

    private void OnMessage(string message) => _status.Text = message;
    private void Render(GameSnapshot snapshot)
    {
        if (!_board.IsAnimating) _board.SetPosition(snapshot, _selected, _legal);
        _status.Text = !_controller.IsConnected ? "等待联机连接"
            : snapshot.IsFinished ? snapshot.Winner == PieceSide.Red ? "红方胜" : "黑方胜"
            : $"{(snapshot.SideToMove == PieceSide.Red ? "红方" : "黑方")}行棋" +
              (_controller.LocalSide is { } local ? $" · 你是{(local == PieceSide.Red ? "红方" : "黑方")}" : "");
        if (!snapshot.IsFinished) return;
        _save.Disabled = false;
        if (_recentSaved) return;
        _recentSaved = true;
        try { if (TryBuildRecord(snapshot, out var record)) JieqiRecordStore.SaveRecent(record!); }
        catch (Exception error) { GD.PushWarning($"保存揭棋最近对局失败：{error.Message}"); }
    }

    private bool TryBuildRecord(GameSnapshot snapshot, out JieqiGameRecord? record) =>
        _controller.TryCreateFinishedRecord(new Dictionary<string, string>
        {
            ["Event"] = _controller.LocalSide is null ? "本地联机" : "网络联机",
            ["Red"] = "红方", ["Black"] = "黑方",
            ["Result"] = snapshot.Winner == PieceSide.Red ? "1-0" : snapshot.Winner == PieceSide.Black ? "0-1" : "*"
        }, out record, out _);

    private void Save()
    {
        try
        {
            if (!TryBuildRecord(_controller.Snapshot, out var record)) throw new InvalidOperationException("赛后种子尚未就绪。");
            JieqiRecordStore.SavePermanent(record!); _save.Disabled = true; _save.Text = "已保存";
        }
        catch (Exception error) { GD.PushWarning($"保存揭棋棋局失败：{error.Message}"); }
    }

    public override void _ExitTree()
    {
        _board.SquarePressed -= OnSquarePressed; _controller.Changed -= OnChanged; _controller.Message -= OnMessage;
        _ = _controller.DisposeAsync();
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
