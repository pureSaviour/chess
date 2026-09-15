using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.ThreeKingdoms;
using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Core.Domain.Topology;
using Xiangqi.Presentation.Boards;
using Xiangqi.Presentation.Audio;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.Records;
using Xiangqi.Presentation.UI;
using Xiangqi.Presentation.Profiles;

namespace Xiangqi.Presentation.Screens;

/// <summary>三人房间视图。通过控制器提交命令，不直接修改棋局/棋钟；本地投票明确标出国号。</summary>
public partial class ThreeGameScreen : Control
{
    private readonly IThreeMatchController _controller;
    private readonly AppSettings _settings;
    private readonly ThreeBoardView _board;
    private readonly GameplayAudioPlayer _audio = new();
    private readonly Label _status = new();
    private readonly TwoPlayerProfileRow[] _players = [new(false), new(false), new(false)];
    private readonly PlayerProfileCommand?[] _renderedProfiles = new PlayerProfileCommand?[3];
    private readonly string[] _renderedNames = [string.Empty, string.Empty, string.Empty];
    private readonly string[] _renderedDetails = [string.Empty, string.Empty, string.Empty];
    private readonly VBoxContainer _votes = new();
    private readonly RichTextLabel _chat = new() { BbcodeEnabled = false, ScrollFollowing = true, CustomMinimumSize = new(260, 125), SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly OptionButton _actor = new();
    private readonly OptionButton _chatTarget = new();
    private readonly AcceptDialog _error = new() { Title = "提示" };
    private readonly InGameSettingsDialog _settingsDialog;
    private BoardCellId? _selected;
    private int _seenPly = -1;
    private int _chatLines;
    private ThreeTrialSession? _trial;
    private Button _trialButton = null!;
    private bool _trialLiveMoveNotified;
    private Button _saveButton = null!;
    private bool _recentWritten;
    private const int EveryoneTargetId = 100;

    public ThreeGameScreen(AppSettings settings, Action back, IThreeMatchController controller,
        Action? inviteFriends = null, IBoardThemeCatalog? themes = null)
    {
        _settings = settings; _controller = controller;
        var themeCatalog = themes ?? BoardThemeCatalog.Default;
        _board = new ThreeBoardView(themeCatalog);
        _settingsDialog = new InGameSettingsDialog(themeCatalog);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(20, 18); AddChild(margin);
        var root = new VBoxContainer(); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle("三国象棋", 28); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var options = UiStyle.CreateButton("设置"); options.CustomMinimumSize = new(95, 40); header.AddChild(options);
        options.Pressed += () => _settingsDialog.PopupFor(_settings);
        _trialButton = UiStyle.CreateButton("试下"); _trialButton.CustomMinimumSize = new(95, 40);
        _trialButton.Pressed += ToggleTrial; header.AddChild(_trialButton);
        _saveButton = UiStyle.CreateButton("保存棋局"); _saveButton.CustomMinimumSize = new(105, 40); _saveButton.Disabled = true;
        _saveButton.Pressed += SaveRecord; header.AddChild(_saveButton);
        if (inviteFriends is not null)
        {
            var invite = UiStyle.CreateButton("邀请好友"); invite.CustomMinimumSize = new(120, 40); invite.Pressed += inviteFriends; header.AddChild(invite);
        }
        var leave = UiStyle.CreateButton("返回画廊"); leave.CustomMinimumSize = new(120, 40); header.AddChild(leave);
        var confirmLeave = new ConfirmationDialog { Title = "离开房间", DialogText = "确定离开当前房间？联网对手会收到断线提示。", OkButtonText = "离开" };
        AddChild(confirmLeave); confirmLeave.Confirmed += back;
        leave.Pressed += () => confirmLeave.PopupCentered();
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart; UiStyle.SetMuted(_status, 14); root.AddChild(_status);
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; root.AddChild(body);
        body.AddThemeConstantOverride("separation", 18);
        body.AddChild(_board);
        var side = new VBoxContainer { CustomMinimumSize = new(280, 0) }; side.AddThemeConstantOverride("separation", 9); body.AddChild(side);
        for (var i = 0; i < 3; i++)
        {
            var panel = UiStyle.CreatePanel(); side.AddChild(panel);
            _players[i].CustomMinimumSize = new(268, 58);
            panel.AddChild(_players[i]);
            _actor.AddItem($"以{ThreeMatchSession.Name((Kingdom)i)}国身份操作 / 聊天", i);
        }
        _actor.ItemSelected += _ => Refresh();
        side.AddChild(_actor);
        var actions = new GridContainer { Columns = 2 }; side.AddChild(actions);
        AddButton(actions, "悔棋", () => Send(new(ThreeCommandKind.Undo,
            _controller.LocalKingdom ?? _controller.Session.Game.Snapshot.LastActor ?? Actor)));
        AddButton(actions, "求和", () => Send(new(ThreeCommandKind.Draw, Actor)));
        var resign = new ConfirmationDialog { Title = "认输", OkButtonText = "确认认输" }; AddChild(resign);
        Kingdom resignActor = default;
        resign.Confirmed += () => Send(new(ThreeCommandKind.Resign, resignActor));
        AddButton(actions, "认输", () => { resignActor = Actor; resign.DialogText = $"确定由{ThreeMatchSession.Name(resignActor)}国认输？将移除，余子成为障碍。"; resign.PopupCentered(); });
        AddButton(actions, "规则", ShowRules);
        side.AddChild(_votes);
        side.AddChild(new Label { Text = "房间消息板" }); side.AddChild(_chat);
        side.AddChild(_chatTarget);
        var input = new LineEdit { PlaceholderText = "输入消息并回车", MaxLength = 240 }; side.AddChild(input);
        input.TextSubmitted += text =>
        {
            var targetId = _chatTarget.GetItemId(_chatTarget.Selected);
            var target = targetId == EveryoneTargetId ? (Kingdom?)null : (Kingdom)targetId;
            var error = _controller.SendChat(Actor, target, text);
            if (error is null) input.Clear();
            else { _error.DialogText = error; _error.PopupCentered(new(500, 150)); }
        };
        AddChild(_error); AddChild(_settingsDialog);
        _settingsDialog.Saved += saved => { _settings.UpdateFrom(saved); _settings.SaveAndApply(); _board.ApplyAppearance(_settings); };
        AddChild(_audio);
        _board.ApplyAppearance(_settings); _board.CellPressed += OnCell;
        _board.MoveAnimationFinished += OnMoveAnimationFinished;
        _controller.Changed += Refresh; _controller.Message += OnMessage;
        _controller.Chat.MessageAdded += OnChatMessage;
        foreach (var entry in _controller.Chat.Entries) OnChatMessage(entry);
        Refresh();
    }
    private Kingdom Actor => _controller.LocalKingdom ?? (Kingdom)Math.Max(0, _actor.Selected);
    public override void _Process(double delta)
    {
        _controller.Tick(delta);
        var s = _controller.Session; var state = s.Game.Snapshot;
        for (var i = 0; i < 3; i++)
        {
            var flag = !s.IsRunning ? "等待开局" : !state.Alive[i] ? "已淘汰" : !_controller.Connections[i] ? "断线 · 等待重连" : state.Turn == (Kingdom)i ? "正在行棋" : "等待";
            _players[i].TooltipText = _controller.PlayerNames[i];
            _players[i].SetTrailing($"局时 {Clock(_controller.DisplayedGameTimes[i], s.TimeControl.GameSeconds)} · 步时 {Clock(_controller.DisplayedStepTimes[i], s.TimeControl.MoveSeconds)}");
            _players[i].SetMoveClock(MoveClockIndicator.From(_controller.DisplayedStepTimes[i],
                s.TimeControl.MoveSeconds, s.IsRunning && !state.IsFinished && state.Alive[i] && state.Turn == (Kingdom)i));
        }
    }
    private static string Clock(double seconds, int limit) => limit == 0 ? "不限" : $"{(int)Math.Ceiling(seconds) / 60:00}:{(int)Math.Ceiling(seconds) % 60:00}";
    private void OnCell(BoardCellId cell)
    {
        var s = _controller.Session; var state = _trial?.Snapshot ?? s.Game.Snapshot;
        if (_board.IsAnimating || state.IsFinished || (_trial is null && (!s.IsRunning || s.Offer is not null || (_controller.LocalKingdom is { } local && local != state.Turn)))) return;
        var p = state.Pieces.FirstOrDefault(p => p.Cell == cell);
        if (p?.Piece.Owner == state.Turn)
        {
            _selected = _selected == cell ? null : cell;
            RenderPosition(); return;
        }
        if (_selected is { } from)
        {
            if (_trial is null) Send(new(ThreeCommandKind.Move, state.Turn, from.Value, cell.Value));
            else
            {
                var result = _trial.TryMove(from, cell);
                if (!result.Success) { _error.DialogText = result.Error; _error.PopupCentered(new(500, 150)); }
                else { _selected = null; RenderPosition(); }
            }
        }
    }
    private void OnMoveAnimationFinished(bool captured) => _audio.PlayLanding(captured);
    private bool Send(ThreeCommand command)
    {
        var error = _controller.Submit(command);
        if (error is null) return true;
        _error.DialogText = error; _error.PopupCentered(new(500, 150)); return false;
    }
    private void Refresh()
    {
        var session = _controller.Session; var state = session.Game.Snapshot;
        RefreshPlayerProfiles();
        if (_trial is not null)
        {
            if (!_trialLiveMoveNotified && session.Revision > _trial.BaseRevision)
            {
                _trialLiveMoveNotified = true;
                _error.DialogText = "真实对局已有新事件；退出试下后将显示最新局面。";
                _error.PopupCentered(new(520, 150));
            }
            _status.Text = $"{_controller.Status} · 试下分支 · 轮到{ThreeMatchSession.Name(_trial.Snapshot.Turn)}国";
            RenderPosition();
            return;
        }
        if (_seenPly != state.Ply || session.Offer is not null) { _selected = null; _seenPly = state.Ply; }
        if (_controller.LocalKingdom is { } local) { _actor.Select((int)local); _actor.Disabled = true; }
        _board.SetBottomKingdom(_controller.LocalKingdom ?? state.Turn);
        RefreshChatTargets();
        var result = state.IsFinished ? state.Winner is { } winner ? $"{ThreeMatchSession.Name(winner)}国获胜" : "和棋" : $"轮到{ThreeMatchSession.Name(state.Turn)}国";
        _status.Text = $"{_controller.Status}  ·  {result}  ·  第 {state.Ply} 手";
        RenderPosition();
        if (state.IsFinished)
        {
            _saveButton.Disabled = false;
            if (!_recentWritten)
            {
                _recentWritten = true;
                try { ThreeRecordStore.SaveRecent(BuildRecord(), BuildPresentation()); } catch (Exception error) { GD.PushWarning(error.Message); }
            }
        }
        foreach (var child in _votes.GetChildren()) { _votes.RemoveChild(child); child.QueueFree(); }
        if (session.Offer is { } offer)
        {
            _votes.AddChild(new Label { Text = $"{ThreeMatchSession.Name(offer.Actor)}申请{(offer.Kind == KingdomOfferKind.Undo ? "悔棋" : "和棋")} · 协商期间暂停计时" });
            foreach (var k in offer.Required.Where(k => !offer.Accepted.Contains(k)))
            {
                if (_controller.LocalKingdom is { } own && k != own) continue;
                var row = new HBoxContainer(); _votes.AddChild(row);
                AddButton(row, $"{ThreeMatchSession.Name(k)}同意", () => Send(new(ThreeCommandKind.Vote, k, OfferId: offer.Id, Accepted: true)));
                AddButton(row, $"{ThreeMatchSession.Name(k)}拒绝", () => Send(new(ThreeCommandKind.Vote, k, OfferId: offer.Id)));
            }
        }
        foreach (var k in Enum.GetValues<Kingdom>().Where(k => session.IsRunning && state.Alive[(int)k] && !_controller.Connections[(int)k]))
        {
            var absent = k;
            AddButton(_votes, $"判{ThreeMatchSession.Name(k)}国断线负", () => Send(new(ThreeCommandKind.Forfeit, Actor, Target: absent)));
        }
    }
    private void RefreshPlayerProfiles()
    {
        for (var i = 0; i < _players.Length; i++)
        {
            var kingdom = (Kingdom)i;
            var local = _controller.LocalKingdom == kingdom;
            var detail = local ? $"{ThreeMatchSession.Name(kingdom)}国 · 本方" : $"{ThreeMatchSession.Name(kingdom)}国";
            if (_renderedNames[i] == _controller.PlayerNames[i] && _renderedDetails[i] == detail &&
                _renderedProfiles[i] == _controller.PlayerProfiles[i]) continue;
            _players[i].ApplyCommand(_controller.PlayerProfiles[i], _controller.PlayerNames[i], detail, string.Empty);
            _renderedNames[i] = _controller.PlayerNames[i]; _renderedDetails[i] = detail;
            _renderedProfiles[i] = _controller.PlayerProfiles[i];
        }
    }
    private void RenderPosition()
    {
        var state = _trial?.Snapshot ?? _controller.Session.Game.Snapshot;
        var legal = _selected is not { } c ? null : _trial?.GetLegalDestinations(c) ?? _controller.Session.Game.LegalDestinations(c);
        _board.SetPosition(state, _selected, legal);
    }
    private void ToggleTrial()
    {
        _selected = null;
        if (_trial is null)
        {
            _trial = _controller.Session.BeginTrial();
            _trialLiveMoveNotified = false;
            _trialButton.Text = "退出试下";
        }
        else
        {
            _trial = null;
            _trialButton.Text = "试下";
        }
        Refresh();
    }
    private ThreeGameRecord BuildRecord()
    {
        var state = _controller.Session.Game.Snapshot;
        var result = state.Winner is { } winner ? $"{ThreeMatchSession.Name(winner)}-win" : state.IsFinished ? "1/2-1/2" : "*";
        return _controller.Session.CreateRecord(new Dictionary<string, string> { ["Result"] = result, ["Date"] = DateTime.Now.ToString("yyyy.MM.dd") });
    }
    private MatchRecordPresentation BuildPresentation()
    {
        var state = _controller.Session.Game.Snapshot;
        var localKingdom = _controller.LocalKingdom ?? Kingdom.Wei;
        var localName = _controller.PlayerNames[(int)localKingdom];
        var localProfile = _controller.PlayerProfiles[(int)localKingdom];
        var custom = localProfile is null ? CustomPlayerProfileFactory.Create(_settings, localName) : null;
        var local = localProfile is { } transmittedLocal
            ? FromProfile(transmittedLocal, true)
            : new RecordPlayer(custom?.DisplayName ?? localName, true, custom?.Avatar?.Width ?? 0,
                custom?.Avatar?.Height ?? 0, custom?.Avatar?.CopyRgba());
        custom?.Avatar?.Dispose();
        var opponents = Enum.GetValues<Kingdom>().Where(k => k != localKingdom)
            .Select(k => _controller.PlayerProfiles[(int)k] is { } profile
                ? FromProfile(profile, false)
                : new RecordPlayer(_controller.PlayerNames[(int)k], false)).ToArray();
        var result = state.Winner is null ? "和" : state.Winner == localKingdom ? "胜" : "败";
        return new(DateTimeOffset.Now, "三国象棋", local, opponents, result);
    }
    private static RecordPlayer FromProfile(PlayerProfileCommand profile, bool isLocal) =>
        new(profile.DisplayName, isLocal, profile.AvatarWidth, profile.AvatarHeight, profile.AvatarRgba.ToArray());
    private void SaveRecord()
    {
        try { ThreeRecordStore.SavePermanent(BuildRecord(), BuildPresentation()); _saveButton.Text = "已保存"; _saveButton.Disabled = true; }
        catch (Exception error) { _error.DialogText = $"保存失败：{error.Message}"; _error.PopupCentered(new(520, 150)); }
    }
    private void OnMessage(Kingdom? k, string text)
    {
        if (++_chatLines > 150) { _chat.Clear(); _chatLines = 1; }
        var prefix = "系统";
        if (k is { } country)
            prefix = $"[{ThreeMatchSession.Name(country)}] {_controller.PlayerNames[(int)country]}";
        _chat.AddText($"{prefix}：{text}\n");
    }
    private void OnChatMessage(RoomChatEntry entry)
    {
        var localSeat = _controller.LocalKingdom is { } own ? (int?)own : null;
        if (!RoomChatManager.IsVisibleTo(entry, localSeat)) return;
        if (++_chatLines > 200) { _chat.Clear(); _chatLines = 1; }
        var sender = (Kingdom)entry.SenderSeat;
        var destination = entry.TargetSeat is { } target
            ? $" → [{ThreeMatchSession.Name((Kingdom)target)}] {_controller.PlayerNames[target]}"
            : " → 房间";
        _chat.AddText($"[{ThreeMatchSession.Name(sender)}] {_controller.PlayerNames[entry.SenderSeat]}{destination}：{entry.Text}\n");
    }
    private void RefreshChatTargets()
    {
        var previous = _chatTarget.Selected >= 0 ? _chatTarget.GetItemId(_chatTarget.Selected) : EveryoneTargetId;
        _chatTarget.Clear();
        _chatTarget.AddItem("发送给：房间所有人", EveryoneTargetId);
        for (var i = 0; i < _controller.PlayerNames.Count; i++)
        {
            if (i == (int)Actor || string.IsNullOrEmpty(_controller.PlayerIds[i])) continue;
            _chatTarget.AddItem($"仅发送给：{ThreeMatchSession.Name((Kingdom)i)} · {_controller.PlayerNames[i]}", i);
        }
        for (var i = 0; i < _chatTarget.ItemCount; i++)
            if (_chatTarget.GetItemId(i) == previous) { _chatTarget.Select(i); return; }
        _chatTarget.Select(0);
    }
    private static void AddButton(Node parent, string text, Action action)
    {
        var button = UiStyle.CreateButton(text); button.CustomMinimumSize = new(132, 34);
        button.AddThemeFontSizeOverride("font_size", 14); button.Pressed += action; parent.AddChild(button);
    }
    private void ShowRules()
    {
        _error.DialogText = "魏→吴→蜀；允许送将和不解将。吃将后收编余子、国土并改变控制色。\n" +
            "兵出境后沿普通邻接单步移动，不得回原属国，也不得从境外进入己方收编的国土。\n" +
            "共享国土策略下，收编后士解除九宫限制；士象不得进入存活敌国。\n" +
            "悔棋仅撤申请者刚落的一手，下一人落子前申请，所有其他存活者同意，不返还耗时。\n" +
            "认输/超时/断线判负移除将，余子留作障碍。断线等待仍计时，任一其他存活者可判负。";
        _error.PopupCentered(new(690, 320));
    }
    public override void _ExitTree()
    {
        _controller.Changed -= Refresh; _controller.Message -= OnMessage;
        _controller.Chat.MessageAdded -= OnChatMessage;
        _board.MoveAnimationFinished -= OnMoveAnimationFinished;
        _controller.Dispose();
    }
}
