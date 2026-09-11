using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.Jieqi;
using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.Jieqi;
using Xiangqi.Presentation.Records;
using Xiangqi.Presentation.Audio;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.Profiles;
using Xiangqi.Presentation.Networking;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>Jieqi match view; every local or network move passes through the injected controller.</summary>
public partial class JieqiGameScreen : Control
{
    private readonly IJieqiMatchController _controller;
    private readonly Action _back;
    private readonly MatchPlayerProfiles? _profiles;
    private readonly RawMatchProfileExchange? _profileExchange;
    private readonly RawTwoPlayerRoomChat? _roomChat;
    private readonly BoardView _board;
    private readonly Label _status = new();
    private readonly Button _save;
    private readonly GameplayAudioPlayer _audio = new();
    private readonly TwoPlayerProfileRow _topPlayer = new(false);
    private readonly TwoPlayerProfileRow _bottomPlayer = new(true);
    private readonly TwoPlayerChatPanel? _chatPanel;
    private BoardPosition? _selected;
    private IReadOnlyList<BoardPosition> _legal = [];
    private bool _recentSaved;
    private bool _profileSideInitialized;
    private PieceSide? _profileSide;
    private GameSnapshot _displayed;

    internal bool HasClassicTwoPlayerRoomLayout => _topPlayer.GetParent() is not null &&
        _bottomPlayer.GetParent() is not null;
    internal bool HasNetworkChat => _chatPanel is not null;

    public JieqiGameScreen(AppSettings settings, IBoardThemeCatalog themes, ulong seed, TimeControl timeControl,
        Action back, MatchPlayerProfiles? profiles = null)
        : this(settings, themes, new LocalJieqiMatchController(seed, timeControl), "本地对决", back,
            profiles: profiles) { }

    public JieqiGameScreen(AppSettings settings, IBoardThemeCatalog themes,
        IJieqiMatchController controller, string roomTitle, Action back, Node? networkDriver = null,
        MatchPlayerProfiles? profiles = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _back = back ?? throw new ArgumentNullException(nameof(back));
        _profiles = profiles;
        if (networkDriver is ITwoPlayerPacketTransport packetTransport)
        {
            if (profiles is not null)
            {
                _profileExchange = new RawMatchProfileExchange(packetTransport, profiles);
                _profileExchange.OpponentChanged += ApplyPlayerProfiles;
            }
            _roomChat = new RawTwoPlayerRoomChat(packetTransport,
                () => _controller.LocalSide is { } side ? (int)side : null);
        }
        _displayed = controller.Snapshot;
        if (networkDriver is not null) AddChild(networkDriver);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(32, 20); AddChild(margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 10); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle($"揭棋 · {roomTitle}", 30); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        _status.VerticalAlignment = VerticalAlignment.Center; header.AddChild(_status);
        _save = UiStyle.CreateButton("保存棋局"); _save.CustomMinimumSize = new Vector2(88, 41);
        _save.Disabled = true; header.AddChild(_save);
        var resign = UiStyle.CreateButton("认输"); resign.CustomMinimumSize = new Vector2(76, 41); header.AddChild(resign);
        var resignDialog = new ConfirmationDialog { Title = "认输", DialogText = "确定认输并结束本局？", OkButtonText = "确认认输" };
        AddChild(resignDialog); resign.Pressed += () => resignDialog.PopupCentered(); resignDialog.Confirmed += Resign;
        var leave = UiStyle.CreateButton("返回画廊"); leave.CustomMinimumSize = new Vector2(110, 41);
        leave.Pressed += LeaveForGallery; header.AddChild(leave);

        var matchBody = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        matchBody.AddThemeConstantOverride("separation", 10); root.AddChild(matchBody);
        var matchColumn = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Center
        };
        matchColumn.AddThemeConstantOverride("separation", 3); matchBody.AddChild(matchColumn);
        matchColumn.AddChild(_topPlayer);
        var boardPanel = UiStyle.CreatePanel(); boardPanel.CustomMinimumSize = new Vector2(560, 560);
        boardPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill; boardPanel.SizeFlagsVertical = SizeFlags.ExpandFill;
        matchColumn.AddChild(boardPanel);
        _board = new BoardView(themes)
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(540, 540)
        };
        _board.ApplyAppearance(settings.ThemeId, settings.ShowBoardCoordinates);
        _board.SetCoveredIdentityHints(null, false); boardPanel.AddChild(_board);
        matchColumn.AddChild(_bottomPlayer);
        if (_roomChat is not null)
        {
            _chatPanel = new TwoPlayerChatPanel(_roomChat,
                () => _controller.LocalSide is { } side ? (int)side : null);
            matchBody.AddChild(_chatPanel);
        }
        AddChild(_audio);
        var hint = new Label { Text = "暗子首步按所在槽位走法；联机对局只由房主保管秘密排列。", HorizontalAlignment = HorizontalAlignment.Center };
        UiStyle.SetMuted(hint, 13); root.AddChild(hint);
        _board.SquarePressed += OnSquarePressed;
        _board.MoveAnimationFinished += OnMoveAnimationFinished;
        _controller.Changed += OnChanged; _controller.Message += OnMessage;
        _save.Pressed += Save; ApplyPlayerProfiles(); Render(controller.Snapshot);
    }

    public override void _Process(double delta)
    {
        _controller.Tick(delta);
        UpdateClocks();
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
        if (!_profileSideInitialized || _profileSide != _controller.LocalSide) ApplyPlayerProfiles();
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
    private void OnMoveAnimationFinished(bool captured) => _audio.PlayLanding(captured);
    private void Render(GameSnapshot snapshot)
    {
        UpdateClocks();
        _board.BottomSide = _controller.LocalSide ?? PieceSide.Red;
        if (!_board.IsAnimating) _board.SetPosition(snapshot, _selected, _legal);
        _status.Text = !_controller.IsConnected ? "等待联机连接"
            : snapshot.IsFinished ? snapshot.Winner == PieceSide.Red ? "红方胜" : "黑方胜"
            : $"{(snapshot.SideToMove == PieceSide.Red ? "红方" : "黑方")}行棋" +
              (_controller.LocalSide is { } local ? $" · 你是{(local == PieceSide.Red ? "红方" : "黑方")}" : "");
        if (!snapshot.IsFinished) return;
        _save.Disabled = false;
        if (_recentSaved) return;
        _recentSaved = true;
        try { if (TryBuildRecord(snapshot, out var record)) JieqiRecordStore.SaveRecent(record!, BuildPresentation(snapshot)); }
        catch (Exception error) { GD.PushWarning($"保存揭棋最近对局失败：{error.Message}"); }
    }

    private void UpdateClocks()
    {
        var clock = _controller.Clock; var control = _controller.TimeControl;
        var red = $"红方  {ClockText(clock.RedGameSeconds, control.GameSeconds)} · 步时 {ClockText(clock.MoveSeconds, control.MoveSeconds, clock.SideToMove == PieceSide.Red)}";
        var black = $"黑方  {ClockText(clock.BlackGameSeconds, control.GameSeconds)} · 步时 {ClockText(clock.MoveSeconds, control.MoveSeconds, clock.SideToMove == PieceSide.Black)}";
        var bottom = _controller.LocalSide ?? PieceSide.Red;
        _bottomPlayer.SetTrailing(bottom == PieceSide.Red ? red : black);
        _topPlayer.SetTrailing(bottom == PieceSide.Red ? black : red);
    }

    private void ApplyPlayerProfiles()
    {
        var bottom = _controller.LocalSide ?? PieceSide.Red;
        var top = bottom.Opponent();
        var localDetail = _controller.LocalSide is null ? "本地双人" : $"你是{SideName(bottom)}";
        var opponentDetail = _controller.LocalSide is null ? "本地双人" : $"对方是{SideName(top)}";
        _bottomPlayer.Apply(_profiles?.Local, SideName(bottom), localDetail, string.Empty);
        _topPlayer.Apply(_profiles?.Opponent, SideName(top), opponentDetail, string.Empty);
        _profileSide = _controller.LocalSide; _profileSideInitialized = true;
        UpdateClocks();
    }

    private static string SideName(PieceSide side) => side == PieceSide.Red ? "红方" : "黑方";
    private static string ClockText(double value, int limit, bool active = true) => limit == 0 ? "不限" : active ? $"{(int)Math.Ceiling(value) / 60:00}:{(int)Math.Ceiling(value) % 60:00}" : "--:--";
    private PieceSide ResigningSide => _controller.LocalSide ?? _controller.Snapshot.SideToMove;
    private async void Resign() { try { await _controller.SubmitResignationAsync(ResigningSide); } catch (Exception error) { OnMessage(error.Message); } }
    private async void LeaveForGallery()
    {
        try { if (!_controller.Snapshot.IsFinished) await _controller.SubmitResignationAsync(ResigningSide); }
        catch (Exception error) { GD.PushWarning($"返回画廊时提交认输失败：{error.Message}"); }
        _back();
    }

    private bool TryBuildRecord(GameSnapshot snapshot, out JieqiGameRecord? record) =>
        _controller.TryCreateFinishedRecord(new Dictionary<string, string>
        {
            ["Event"] = _controller.LocalSide is null ? "本地联机" : "网络联机",
            ["Red"] = "红方", ["Black"] = "黑方",
            ["Result"] = snapshot.Winner == PieceSide.Red ? "1-0" : snapshot.Winner == PieceSide.Black ? "0-1" : "*"
        }, out record, out _);

    private MatchRecordPresentation BuildPresentation(GameSnapshot snapshot)
    {
        var localSide = _controller.LocalSide ?? PieceSide.Red;
        RecordPlayer FromProfile(PlayerProfileSnapshot profile, bool local) => new(profile.DisplayName, local,
            profile.Avatar?.Width ?? 0, profile.Avatar?.Height ?? 0, profile.Avatar?.CopyRgba());
        var local = _profiles is null
            ? new RecordPlayer(localSide == PieceSide.Red ? "红方" : "黑方", true)
            : FromProfile(_profiles.Local, true);
        var opponent = _profiles is null
            ? new RecordPlayer(localSide == PieceSide.Red ? "黑方" : "红方", false)
            : FromProfile(_profiles.Opponent, false);
        var result = snapshot.Winner is null ? "和" : snapshot.Winner == localSide ? "胜" : "败";
        var mode = _controller.LocalSide is null ? "本地联机" : "联机对战";
        return new(DateTimeOffset.Now, mode, local, [opponent], result);
    }

    private void Save()
    {
        try
        {
            if (!TryBuildRecord(_controller.Snapshot, out var record)) throw new InvalidOperationException("赛后种子尚未就绪。");
            JieqiRecordStore.SavePermanent(record!, BuildPresentation(_controller.Snapshot));
            _save.Disabled = true; _save.Text = "已保存";
        }
        catch (Exception error) { GD.PushWarning($"保存揭棋棋局失败：{error.Message}"); }
    }

    public override void _ExitTree()
    {
        _board.SquarePressed -= OnSquarePressed; _controller.Changed -= OnChanged; _controller.Message -= OnMessage;
        _board.MoveAnimationFinished -= OnMoveAnimationFinished;
        if (_profileExchange is not null) _profileExchange.OpponentChanged -= ApplyPlayerProfiles;
        _roomChat?.Dispose();
        _profileExchange?.Dispose();
        _profiles?.Dispose();
        _ = _controller.DisposeAsync();
    }
}

public partial class JieqiRecordScreen : Control
{
    private readonly List<ImageTexture> _textures = [];
    public JieqiRecordScreen(bool saved, Action back, Action<JieqiGameRecord> replay)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(60, 36); AddChild(margin); var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 12); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header); var title = UiStyle.CreateTitle(saved ? "揭棋 · 保存对局" : "揭棋 · 最近对局"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        root.AddChild(RecordEntryRow.CreateHeadings());
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(900, 360) }; root.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; rows.AddThemeConstantOverride("separation", 10); scroll.AddChild(rows);
        foreach (var entry in JieqiRecordStore.ListEntries(saved))
        {
            rows.AddChild(RecordEntryRow.Create(entry.Presentation,
                () => replay(JieqiRecordStore.Load(entry.RecordPath)), _textures));
        }
    }
    public override void _ExitTree() { foreach (var texture in _textures) texture.Dispose(); _textures.Clear(); }
}

public partial class JieqiReplayScreen : Control
{
    private readonly IReadOnlyList<JieqiSnapshot> _positions;
    private readonly BoardView _board;
    private readonly HSlider _timeline;
    private readonly Label _ply = new();
    private readonly CheckButton _reveal = new() { Text = "隐约显示暗子身份" };
    private readonly HBoxContainer _controls = new() { Alignment = BoxContainer.AlignmentMode.Center };
    private int _index;

    public JieqiReplayScreen(AppSettings settings, IBoardThemeCatalog themes, JieqiGameRecord record, Action back)
    {
        _positions = record.Replay(); SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(32, 20); AddChild(margin); var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 8); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header); var title = UiStyle.CreateTitle("揭棋复盘", 30); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title); header.AddChild(_reveal); header.AddChild(_ply);
        var leave = UiStyle.CreateButton("返回记录"); leave.Pressed += back; header.AddChild(leave);
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; root.AddChild(center);
        _board = new BoardView(themes)
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(460, 500)
        };
        _board.ApplyAppearance(settings.ThemeId, settings.ShowBoardCoordinates); center.AddChild(_board);
        _timeline = new HSlider { MinValue = 0, MaxValue = Math.Max(0, _positions.Count - 1), Step = 1,
            TickCount = Math.Clamp(_positions.Count, 2, 31), TicksOnBorders = true, CustomMinimumSize = new Vector2(0, 42) }; root.AddChild(_timeline);
        root.AddChild(_controls);
        var previous = UiStyle.CreateButton("◀ 上一步"); var next = UiStyle.CreateButton("下一步 ▶"); _controls.AddChild(previous); _controls.AddChild(next);
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

    internal bool AreBottomControlsFullyVisible
    {
        get
        {
            var viewport = GetViewportRect();
            var controls = _controls.GetGlobalRect();
            return controls.Position.Y >= viewport.Position.Y && controls.End.Y <= viewport.End.Y &&
                   controls.Position.X >= viewport.Position.X && controls.End.X <= viewport.End.X;
        }
    }
}
