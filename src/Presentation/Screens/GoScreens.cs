using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.Go;
using Xiangqi.Core.Domain.Go;
using Xiangqi.Core.Domain.Grid;
using Xiangqi.Presentation.Boards;
using Xiangqi.Presentation.Networking;
using Xiangqi.Presentation.Profiles;
using Xiangqi.Presentation.Records;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

public partial class GoGalleryScreen : Control
{
    public GoGalleryScreen(Action back, Action<GoRoomOptions> local, Action<int, GoRoomOptions> host,
        Action<string, int> join, Action<GoRoomOptions> steam, ClassicGalleryEntry entry)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(54, 34); AddChild(margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 16); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle(entry == ClassicGalleryEntry.Local ? "围棋 · 本地对决" :
            entry == ClassicGalleryEntry.Lan ? "围棋 · 局域网" : "围棋 · Steam", 34);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; root.AddChild(center);
        var panel = UiStyle.CreatePanel(UiStyle.PanelLight, 14);
        panel.CustomMinimumSize = new(700, entry == ClassicGalleryEntry.Lan ? 500 : 390); center.AddChild(panel);
        var inset = new MarginContainer();
        foreach (var key in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) inset.AddThemeConstantOverride(key, 28);
        panel.AddChild(inset);
        var content = new VBoxContainer(); content.AddThemeConstantOverride("separation", 16); inset.AddChild(content);
        content.AddChild(new Label
        {
            Text = "中国围棋规则 · 标准 19 路 · 黑先贴 7.5 目 · 禁止自杀与全局同形。双方连续虚着后标记死子并确认数子。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
        var timeEnabled=new CheckButton{Text="启用计时（局时 + 步时）",ButtonPressed=true};content.AddChild(timeEnabled);var timeRow=new HBoxContainer();content.AddChild(timeRow);timeRow.AddChild(new Label{Text="局时（分钟）"});var gameMinutes=new SpinBox{MinValue=1,MaxValue=180,Value=20,Step=1};timeRow.AddChild(gameMinutes);timeRow.AddChild(new Label{Text="步时（秒）"});var moveSeconds=new SpinBox{MinValue=5,MaxValue=300,Value=60,Step=5};timeRow.AddChild(moveSeconds);timeEnabled.Toggled+=value=>{gameMinutes.Editable=value;moveSeconds.Editable=value;};
        var color = new OptionButton { CustomMinimumSize = new(240, 42) };
        color.AddItem("房主执黑"); color.AddItem("房主执白"); color.AddItem("随机猜先");
        if (entry != ClassicGalleryEntry.Local) content.AddChild(color);
        GoRoomOptions Options() => new(color.Selected == 1 ? GoStone.White :
            color.Selected == 2 && Random.Shared.Next(2) == 1 ? GoStone.White : GoStone.Black,
            timeEnabled.ButtonPressed?new TimeControl((int)gameMinutes.Value*60,(int)moveSeconds.Value):TimeControl.Unlimited);
        if (entry == ClassicGalleryEntry.Local)
        {
            var start = UiStyle.CreateButton("开始本地对决", true); start.Pressed += () => local(Options()); content.AddChild(start);
        }
        else if (entry == ClassicGalleryEntry.Steam)
        {
            var start = UiStyle.CreateButton("创建房间并邀请好友", true); start.Pressed += () => steam(Options()); content.AddChild(start);
        }
        else
        {
            var form = new GridContainer { Columns = 2 }; content.AddChild(form);
            var address = new LineEdit { Text = "127.0.0.1", PlaceholderText = "房主局域网 IP", CustomMinimumSize = new(390, 42) };
            var port = new SpinBox { MinValue = 1024, MaxValue = 65535, Value = EnetMatchTransport.DefaultPort, Step = 1, CustomMinimumSize = new(180, 42) };
            form.AddChild(address); form.AddChild(port);
            var create = UiStyle.CreateButton("创建房间", true); create.Pressed += () => host((int)port.Value, Options()); form.AddChild(create);
            var connect = UiStyle.CreateButton("加入房间"); connect.Pressed += () => join(address.Text.Trim(), (int)port.Value); form.AddChild(connect);
        }
    }
}

public partial class GoGameScreen : Control
{
    private readonly IGoMatchController _controller;
    private readonly GoBoardView _board = new();
    private readonly Label _status = new();
    private readonly Button _pass;
    private readonly Button _confirm;
    private readonly Button _resign;
    private readonly Button _save;
    private readonly Button _estimateButton;
    private readonly Button _rematch;
    private readonly Label _estimateLabel = new();
    private readonly MatchPlayerProfiles? _profiles;
    private readonly RawMatchProfileExchange? _profileExchange;
    private readonly RawTwoPlayerRoomChat? _roomChat;
    private readonly TwoPlayerChatPanel? _chatPanel;
    private readonly TwoPlayerProfileRow _topPlayer = new(false);
    private readonly TwoPlayerProfileRow _bottomPlayer = new(true);
    private bool _profilesDirty = true;
    private GoStone? _profileColor;
    private GoManualScoreSession? _scorePreview;
    private bool _recent;
    public bool AreCoordinatesVisible => _board.CoordinatesVisible;
    public bool AreMoveNumbersVisible => _board.MoveNumbersVisible;
    public bool IsScorePreviewActive => _scorePreview is not null && _board.ScorePreviewVisible;
    public bool HasLastMoveHighlight => _board.HasLastMoveHighlight;
    public bool HasUnifiedPlayerCards => _topPlayer.GetParent() is not null && _bottomPlayer.GetParent() is not null;
    public string ScorePreviewText => _estimateLabel.Text;
    public Vector2 BoardAreaSize => _board.Size;
    public string ChatBounds => _chatPanel is null ? "missing" :
        $"panel={_chatPanel.GetGlobalRect()} viewport={GetViewportRect()} board={_board.GetGlobalRect()} screen={GetGlobalRect()}";
    public bool IsChatFullyVisible
    {
        get
        {
            if (_chatPanel is null) return false;
            var rect = _chatPanel.GetGlobalRect();
            var viewport = GetViewportRect();
            return rect.Position.X >= viewport.Position.X && rect.Position.Y >= viewport.Position.Y &&
                   rect.End.X <= viewport.End.X + .5f && rect.End.Y <= viewport.End.Y + .5f;
        }
    }
    public void SetCoordinateLabelsForSmoke(bool visible) => _board.SetCoordinatesVisible(visible);
    public void SetMoveNumbersForSmoke(bool visible) => _board.SetMoveNumbersVisible(visible);
    public void ToggleScorePreviewForSmoke() => ToggleScorePreview();
    public bool TogglePreviewDeadForSmoke(GridPoint point) => TogglePreviewDead(point);

    public GoGameScreen(IGoMatchController controller, string roomTitle, Action back, Node? driver = null,
        MatchPlayerProfiles? profiles = null)
    {
        _controller = controller; _profiles = profiles;
        if (driver is ITwoPlayerPacketTransport packetTransport)
        {
            if (profiles is not null) { _profileExchange = new(packetTransport, profiles); _profileExchange.OpponentChanged += OnProfileChanged; }
            _roomChat = new(packetTransport, () => _controller.LocalColor is { } color ? (int)color : null);
        }
        if (driver is not null) AddChild(driver);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(24, 14); AddChild(margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 7); margin.AddChild(root);
        var header = new HBoxContainer(); header.AddThemeConstantOverride("separation", 7); root.AddChild(header);
        var title = UiStyle.CreateTitle("围棋", 27); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        header.AddChild(_status);
        _rematch=UiStyle.CreateButton("再来一局");_rematch.Visible=_controller is IRematchableMatchController;_rematch.Pressed+=RequestRematch;header.AddChild(_rematch);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, Visible = false };
        actions.AddThemeConstantOverride("separation", 7); root.AddChild(actions);
        _pass = UiStyle.CreateButton("虚着"); _pass.Pressed += () => Submit(GoActionKind.Pass); actions.AddChild(_pass);
        _confirm = UiStyle.CreateButton("确认数子"); _confirm.Pressed += () => Submit(GoActionKind.ConfirmScore); actions.AddChild(_confirm);
        _estimateButton = UiStyle.CreateButton("数目估算");
        _estimateButton.TooltipText = "本地手动标记预计死子并查看非正式面积估算，不会改变真实对局。";
        _estimateButton.Pressed += ToggleScorePreview; actions.AddChild(_estimateButton);
        _resign = UiStyle.CreateButton("认输"); actions.AddChild(_resign);
        var resignDialog = new ConfirmationDialog { Title = "认输", DialogText = "确定认输并结束本局围棋吗？",
            OkButtonText = "确认认输", CancelButtonText = "取消" };
        AddChild(resignDialog); _resign.Pressed += () => resignDialog.PopupCentered();
        resignDialog.Confirmed += () => Submit(GoActionKind.Resign);
        _save = UiStyle.CreateButton("保存棋谱"); _save.Disabled = true; _save.Pressed += Save; actions.AddChild(_save);
        var settings = UiStyle.CreateButton("局内设置"); header.AddChild(settings);
        foreach (var actionButton in new[] { _pass, _confirm, _estimateButton, _resign, _save, settings })
            actionButton.CustomMinimumSize = new(140, 48);
        var settingsDialog = new AcceptDialog { Title = "围棋 · 局内设置", OkButtonText = "完成", MinSize = new(520, 220) }; AddChild(settingsDialog);
        var settingsMargin = new MarginContainer();
        foreach (var key in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            settingsMargin.AddThemeConstantOverride(key, 20);
        settingsDialog.AddChild(settingsMargin);
        var settingsColumn = new VBoxContainer(); settingsColumn.AddThemeConstantOverride("separation", 12); settingsMargin.AddChild(settingsColumn);
        var roomInfo = new Label { Text = $"房间信息：{roomTitle}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        UiStyle.SetMuted(roomInfo, 13); settingsColumn.AddChild(roomInfo);
        var coordinates = new CheckButton { Text = "显示棋盘坐标（A–T 跳过 I，1–19）", ButtonPressed = true };
        coordinates.Toggled += _board.SetCoordinatesVisible; settingsColumn.AddChild(coordinates);
        var moveNumbers = new CheckButton { Text = "在棋子上显示手数", ButtonPressed = false };
        moveNumbers.Toggled += _board.SetMoveNumbersVisible; settingsColumn.AddChild(moveNumbers);
        settings.Pressed += () => settingsDialog.PopupCentered(new(520, 220));
        var functions = new MenuButton { Text = "功能 ▾", CustomMinimumSize = new(110, 48) };
        var functionPopup = functions.GetPopup();
        functionPopup.AddItem("虚着", 1); functionPopup.AddItem("确认数子", 2);
        functionPopup.AddItem("数目估算", 3); functionPopup.AddSeparator();
        functionPopup.AddItem("认输", 4); functionPopup.AddItem("保存棋谱", 5);
        functionPopup.AboutToPopup += () =>
        {
            void Sync(int id, Button source) { var index = functionPopup.GetItemIndex(id); functionPopup.SetItemText(index, source.Text); functionPopup.SetItemDisabled(index, source.Disabled); }
            Sync(1, _pass); Sync(2, _confirm); Sync(3, _estimateButton); Sync(4, _resign); Sync(5, _save);
        };
        functionPopup.IdPressed += id =>
        {
            switch (id) { case 1: Submit(GoActionKind.Pass); break; case 2: Submit(GoActionKind.ConfirmScore); break;
                case 3: ToggleScorePreview(); break; case 4: resignDialog.PopupCentered(); break; case 5: Save(); break; }
        };
        header.AddChild(functions);
        var body = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10); root.AddChild(body);
        body.AddChild(_board);
        var rail = new VBoxContainer { CustomMinimumSize = new(300, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
        rail.AddThemeConstantOverride("separation", 6); body.AddChild(rail); rail.AddChild(_topPlayer);
        if (_roomChat is not null)
        {
            _chatPanel = new TwoPlayerChatPanel(_roomChat, () => _controller.LocalColor is { } color ? (int)color : null);
            rail.AddChild(_chatPanel);
        }
        else rail.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        rail.AddChild(_bottomPlayer);
        _estimateLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _estimateLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _estimateLabel.Visible = false;
        UiStyle.SetMuted(_estimateLabel, 13); root.AddChild(_estimateLabel);
        var hint = new Label { Text = "SGF FF[4] / GM[1] · 数目估算仅修改本地临时标记，不会改变对局",
            HorizontalAlignment = HorizontalAlignment.Center };
        UiStyle.SetMuted(hint, 13); root.AddChild(hint);
        _board.PointPressed += OnPoint; _controller.Changed += OnChanged; _controller.Message += OnMessage;
        ApplyProfiles(); Render();
    }

    public override async void _Ready()
    { try { await _controller.InitializeAsync(); Render(); } catch (Exception error) { OnMessage(error.Message); } }
    public override void _Process(double delta) { _controller.Poll(); _controller.Tick(delta); UpdateClocks(); }
    private void OnPoint(GridPoint point)
    {
        if (_scorePreview is not null)
        {
            if (!TogglePreviewDead(point)) OnMessage("数目估算时请点击棋子，空点不会改变标记。");
            return;
        }
        if (!_controller.CanAct) return;
        Submit(_controller.Snapshot.Phase == GoPhase.Scoring ? GoActionKind.MarkDead : GoActionKind.Place, point);
    }
    private async void Submit(GoActionKind kind, GridPoint? point = null)
    {
        if (!_controller.CanAct) return;
        var result = await _controller.SubmitAsync(kind, point);
        if (!result.Accepted) OnMessage(RejectText(result.Rejection, result.Error));
    }
    private async void RequestRematch(){if(_controller is not IRematchableMatchController value)return;try{await value.RequestRematchAsync();Render();}catch(Exception error){OnMessage(error.Message);}}
    private void OnChanged() { ApplyProfiles(); Render(); }
    private void ApplyProfiles()
    {
        if (!_profilesDirty && _profileColor == _controller.LocalColor) return;
        _profilesDirty = false; _profileColor = _controller.LocalColor;
        var bottom = _controller.LocalColor ?? GoStone.Black;
        var top = bottom == GoStone.Black ? GoStone.White : GoStone.Black;
        _bottomPlayer.Apply(_profiles?.Local, StoneName(bottom), _controller.LocalColor is null ? "本地双人" : $"你是{StoneName(bottom)}", string.Empty);
        _topPlayer.Apply(_profiles?.Opponent, StoneName(top), _controller.LocalColor is null ? "本地双人" : $"对方是{StoneName(top)}", string.Empty);
    }
    private void OnProfileChanged() { _profilesDirty = true; ApplyProfiles(); }
    private void Render()
    {
        var snapshot = _controller.Snapshot;
        if(snapshot.Phase!=GoPhase.Finished&&_recent){_recent=false;_save.Disabled=true;_save.Text="保存棋谱";}
        if (_scorePreview is not null && _scorePreview.SourceActionNumber != snapshot.ActionNumber)
        {
            _scorePreview = null;
            _estimateButton.Text = "数目估算";
            _estimateLabel.Text = "局面已经变化，旧的数目估算已自动关闭。";
            _estimateLabel.Visible = true;
            _board.SetScorePreview(null);
        }
        _board.SetPosition(snapshot);
        var estimating = _scorePreview is not null;
        _pass.Disabled = estimating || !_controller.CanAct || snapshot.Phase != GoPhase.Playing;
        _confirm.Disabled = estimating || !_controller.CanAct || snapshot.Phase != GoPhase.Scoring;
        _resign.Disabled = estimating || !_controller.CanAct;
        _estimateButton.Disabled = !_controller.IsReady;
        _status.Text = !_controller.IsConnected ? "等待联机连接" : !_controller.IsReady ? "等待房主配置" :
            snapshot.Phase == GoPhase.Finished ? ResultText(snapshot) :
            snapshot.Phase == GoPhase.Scoring ? $"数子 · {StoneName(snapshot.SideToMove)}确认 · 已确认 {snapshot.ScoreConfirmations.Count}/2" :
            $"{StoneName(snapshot.SideToMove)}行棋" + (_controller.LocalColor is { } local ? $" · 你是{StoneName(local)}" : string.Empty);
        if(_controller is IRematchableMatchController rematch){_rematch.Disabled=!rematch.CanRequestRematch;_rematch.Text=rematch.Rematch.LocalReady?"等待对手同意":"再来一局";}
        UpdateClocks();
        if (snapshot.Phase != GoPhase.Finished) return;
        _save.Disabled = false;
        if (_recent) return;
        _recent = true;
        try { GoRecordStore.SaveRecent(_controller.CreateRecord(), BuildPresentation(snapshot)); }
        catch (Exception error) { GD.PushWarning(error.Message); }
    }
    private void UpdateClocks(){var local=_controller.LocalColor??GoStone.Black;var other=local==GoStone.Black?GoStone.White:GoStone.Black;var c=_controller.Clock;string Text(GoStone s){var value=s==GoStone.Black?c.FirstGameSeconds:c.SecondGameSeconds;return _controller.TimeControl.GameSeconds==0?"局时 ∞":$"局时 {TimeSpan.FromSeconds(value):mm\\:ss}";}_bottomPlayer.SetTrailing(Text(local));_topPlayer.SetTrailing(Text(other));var active=_controller.Snapshot.Phase!=GoPhase.Finished;_bottomPlayer.SetMoveClock(MoveClockIndicator.From(c.MoveSeconds,_controller.TimeControl.MoveSeconds,active&&c.ActiveSeat==(int)local));_topPlayer.SetMoveClock(MoveClockIndicator.From(c.MoveSeconds,_controller.TimeControl.MoveSeconds,active&&c.ActiveSeat==(int)other));}
    private void ToggleScorePreview()
    {
        if (_scorePreview is null)
        {
            _scorePreview = new GoManualScoreSession(_controller.Snapshot);
            _estimateButton.Text = "结束估算";
            RefreshScorePreview();
        }
        else
        {
            _scorePreview = null;
            _estimateButton.Text = "数目估算";
            _estimateLabel.Visible = false;
            _board.SetScorePreview(null);
        }
        Render();
    }
    private bool TogglePreviewDead(GridPoint point)
    {
        if (_scorePreview is null || !_scorePreview.TryToggleDeadGroup(point)) return false;
        RefreshScorePreview();
        return true;
    }
    private void RefreshScorePreview()
    {
        if (_scorePreview is null) return;
        var estimate = _scorePreview.Estimate;
        _board.SetScorePreview(estimate);
        var lead = estimate.Score.Winner is { } winner
            ? $"{StoneName(winner)}暂时领先 {estimate.Score.Margin:0.0} 目"
            : "暂时持平";
        _estimateLabel.Text = $"非正式手动估算 · 黑 {estimate.Score.Black:0.0} / 白 {estimate.Score.White:0.0}（含贴目 {estimate.Score.Komi:0.0}）· {lead} · 点击棋链切换死子";
        _estimateLabel.Visible = true;
    }
    private static string ResultText(GoSnapshot snapshot) => snapshot.EndReason == GoEndReason.Resignation
        ? $"{StoneName(snapshot.Winner!.Value)}胜 · 认输"
        : snapshot.Score is { } score
            ? $"{(snapshot.Winner is { } winner ? StoneName(winner) + "胜" : "和棋")} · 黑 {score.Black:0.0} / 白 {score.White:0.0}"
            : "对局结束";
    private void OnMessage(string message) => _status.Text = message;
    private void Save()
    {
        try
        {
            GoRecordStore.SavePermanent(_controller.CreateRecord(), BuildPresentation(_controller.Snapshot));
            _save.Disabled = true; _save.Text = "已保存";
        }
        catch (Exception error) { OnMessage(error.Message); }
    }
    private MatchRecordPresentation BuildPresentation(GoSnapshot snapshot)
    {
        var localColor = _controller.LocalColor ?? GoStone.Black;
        var opponentColor = localColor == GoStone.Black ? GoStone.White : GoStone.Black;
        static RecordPlayer Player(PlayerProfileSnapshot profile, bool local) => new(profile.DisplayName, local,
            profile.Avatar?.Width ?? 0, profile.Avatar?.Height ?? 0, profile.Avatar?.CopyRgba());
        var local = _profiles is null ? new RecordPlayer(StoneName(localColor), true) : Player(_profiles.Local, true);
        var opponent = _profiles is null ? new RecordPlayer(StoneName(opponentColor), false) : Player(_profiles.Opponent, false);
        var result = snapshot.Winner is null ? "和" : snapshot.Winner == localColor ? "胜" : "败";
        return new(DateTimeOffset.Now, "围棋", local, [opponent], result);
    }
    private static string StoneName(GoStone color) => color == GoStone.Black ? "黑方" : "白方";
    private static string RejectText(GoRejection rejection, string? fallback) => rejection switch
    {
        GoRejection.Occupied => "该交叉点已有棋子。", GoRejection.Suicide => "禁着点：落子后己方无气。",
        GoRejection.RepeatedPosition => "该着会造成全局同形再现。", GoRejection.EmptyPoint => "数子时请点击棋块。",
        _ => fallback ?? "当前不能执行该操作。"
    };
    public override void _ExitTree()
    {
        _board.PointPressed -= OnPoint; _controller.Changed -= OnChanged; _controller.Message -= OnMessage;
        if (_profileExchange is not null) _profileExchange.OpponentChanged -= OnProfileChanged;
        _profileExchange?.Dispose(); _roomChat?.Dispose(); _profiles?.Dispose(); _ = _controller.DisposeAsync();
    }
}

public partial class GoRecordScreen : Control
{
    private readonly List<ImageTexture> _textures = [];
    private readonly RecordExportDialog _exportDialog;
    private readonly AcceptDialog _actionError = new() { Title = "记录操作失败" };
    public GoRecordScreen(bool saved, Action back, Action<GoGameRecord> replay)
    {
        _exportDialog = new(".sgf", "SGF FF[4] / GM[1]", GoRecordStore.Export);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(60, 36); AddChild(margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 12); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle(saved ? "围棋 · 保存对局" : "围棋 · 最近对局");
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        root.AddChild(RecordEntryRow.CreateHeadings(true));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(900, 360) };
        root.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 10); scroll.AddChild(rows);
        AddChild(_exportDialog); AddChild(_actionError);
        foreach (var entry in GoRecordStore.ListEntries(saved))
        {
            bool SaveEntry()
            {
                try { GoRecordStore.SavePermanent(entry); return true; }
                catch (Exception error) { _actionError.DialogText = error.Message; _actionError.PopupCentered(new(620, 170)); return false; }
            }
            rows.AddChild(RecordEntryRow.Create(entry.Presentation, () => replay(GoRecordStore.Load(entry.RecordPath)),
                _textures, saved ? null : SaveEntry, () => _exportDialog.Open(entry.RecordPath)));
        }
    }
    public override void _ExitTree() { foreach (var texture in _textures) texture.Dispose(); _textures.Clear(); }
}

public partial class GoReplayScreen : Control
{
    private readonly List<GoSnapshot> _positions = [];
    private readonly GoBoardView _board = new();
    private readonly Label _step = new();
    private int _index;
    public GoReplayScreen(GoGameRecord record, Action back)
    {
        var game = new GoGame(); _positions.Add(game.Snapshot);
        foreach (var move in record.Moves)
        {
            var result = LocalGoMatchController.Apply(game, move.Kind, move.Point, move.Actor);
            if (!result.IsSuccess) throw new InvalidDataException($"第 {move.Sequence} 手无法重放。");
            _positions.Add(game.Snapshot);
        }
        var final = record.Replay().Snapshot;
        if (!_positions[^1].Equals(final)) _positions.Add(final);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(30, 18); AddChild(margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 8); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle("围棋复盘", 30); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var coordinates = new CheckButton { Text = "显示坐标", ButtonPressed = true };
        coordinates.Toggled += _board.SetCoordinatesVisible; header.AddChild(coordinates);
        var leave = UiStyle.CreateButton("返回记录"); leave.Pressed += back; header.AddChild(leave);
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; center.AddChild(_board); root.AddChild(center);
        var controls = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; controls.AddThemeConstantOverride("separation", 14); root.AddChild(controls);
        var previous = UiStyle.CreateButton("上一手"); previous.Pressed += () => Select(_index - 1); controls.AddChild(previous);
        controls.AddChild(_step);
        var next = UiStyle.CreateButton("下一手"); next.Pressed += () => Select(_index + 1); controls.AddChild(next);
        Select(0);
    }
    private void Select(int index)
    { _index = Math.Clamp(index, 0, _positions.Count - 1); _board.SetPosition(_positions[_index]); _step.Text = $"{_index} / {_positions.Count - 1}"; }
}
