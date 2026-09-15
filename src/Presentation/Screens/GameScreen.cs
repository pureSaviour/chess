using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain;
using Xiangqi.Presentation.Effects;
using Xiangqi.Presentation.Audio;
using Xiangqi.Presentation.Networking;
using Xiangqi.Presentation.Profiles;
using Xiangqi.Presentation.Records;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>
/// Match presentation and input adapter. Local clicks and incoming commands share GameSession;
/// the host is authoritative for clocks and timeout adjudication, while clients render synced
/// clock snapshots. Board orientation is a presentation concern derived from the local side.
/// </summary>
public partial class GameScreen : Control
{
    private readonly GameSession _session;
    private readonly AppSettings _settings;
    private readonly IBoardThemeCatalog _themes;
    private readonly IMatchTransport? _transport;
    private readonly bool _isNetworkHost;
    private readonly NetworkMatchConfiguration? _hostConfiguration;
    private readonly Action _back;
    private readonly string _matchLabel;
    private readonly bool _debugNetworkSmoke;
    private readonly bool _debugTimeoutSmoke;
    private readonly MatchPlayerProfiles? _playerProfiles;
    private readonly bool _exchangeLocalProfile;
    private readonly AiMatchConfiguration? _aiConfiguration;
    private readonly IXiangqiAiEngine? _aiEngine;
    private bool _profileResourcesReleased;
    private bool _localProfileSent;
    private NetworkGameCoordinator? _network;
    private AiGameCoordinator? _ai;
    private bool _aiMoveScheduled;
    private bool _aiInitialized;
    private NetworkMatchConfiguration? _configuration;
    private MatchClock _clock = new(TimeControl.Unlimited);
    private PieceSide? _localSide;
    private bool _smokeMoveScheduled;
    private bool _smokeNegotiationScheduled;
    private bool _settlementShown;
    private int _lastPly;
    private double _clockSyncAccumulator;
    private BoardView _board = null!;
    private CueEffectLayer _effects = null!;
    private Label _connectionLabel = null!;
    private readonly TwoPlayerProfileRow _topPlayer = new(false);
    private readonly TwoPlayerProfileRow _bottomPlayer = new(true);
    private Button _undoButton = null!;
    private Button _requestUndoButton = null!;
    private Button _requestDrawButton = null!;
    private Button _chatSendButton = null!;
    private RichTextLabel _chatLog = null!;
    private LineEdit _chatInput = null!;
    private ConfirmationDialog _offerDialog = null!;
    private GameplayAudioPlayer _audio = null!;
    private InGameSettingsDialog _settingsDialog = null!;
    private readonly List<string> _chatLines = [];
    private MatchOfferRequest? _visibleIncomingOffer;
    private GameSnapshot? _displayedSnapshot;
    private ChessMove? _pendingVisualMove;
    private bool _pendingVisualCapture;
    private BoardPosition? _selected;
    private IReadOnlyList<BoardPosition> _legalDestinations = [];
    private ClassicTrialSession? _trial;
    private Button _trialButton = null!;
    private bool _trialOpponentMoveNotified;
    private Button _saveRecordButton = null!;
    private MenuButton _functionMenu = null!;
    private bool _recentRecordWritten;
    private Control? _messageSidePanel;

    internal bool IsMessagePanelFullyVisible
    {
        get
        {
            if (_messageSidePanel is null) return false;
            var rect = _messageSidePanel.GetGlobalRect();
            var viewport = GetViewportRect();
            return rect.Position.X >= viewport.Position.X && rect.Position.Y >= viewport.Position.Y &&
                   rect.End.X <= viewport.End.X + 0.5f && rect.End.Y <= viewport.End.Y + 0.5f;
        }
    }
    internal string TrialActionText => _trialButton.Text;
    internal bool HasCompactFunctionMenu => _functionMenu.GetParent() is not null && _trialButton.Visible == false;
    internal bool HasUnifiedPlayerCards => _topPlayer.HasRoundedAvatar && _bottomPlayer.HasRoundedAvatar &&
        !_topPlayer.HasColoredAvatarBackground && !_bottomPlayer.HasColoredAvatarBackground;
    internal string MessagePanelBounds => _messageSidePanel is null ? "missing" :
        $"panel={_messageSidePanel.GetGlobalRect()} viewport={GetViewportRect()} screen={GetGlobalRect()}";

    public GameScreen(
        AppSettings settings,
        Action back,
        string matchLabel = "本地双人",
        IMatchTransport? transport = null,
        bool isNetworkHost = false,
        NetworkMatchConfiguration? hostConfiguration = null,
        bool debugNetworkSmoke = false,
        bool debugTimeoutSmoke = false,
        MatchPlayerProfiles? playerProfiles = null,
        bool exchangeLocalProfile = false,
        TimeControl? localTimeControl = null,
        AiMatchConfiguration? aiConfiguration = null,
        IXiangqiAiEngine? aiEngine = null,
        IBoardThemeCatalog? themes = null,
        GameSession? session = null)
    {
        _session = session ?? new GameSession([new DoubleChariotMateDetector()]);
        _settings = settings;
        _themes = themes ?? BoardThemeCatalog.Default;
        _back = back;
        _matchLabel = matchLabel;
        _transport = transport;
        _isNetworkHost = isNetworkHost;
        _hostConfiguration = hostConfiguration;
        _debugNetworkSmoke = debugNetworkSmoke;
        _debugTimeoutSmoke = debugTimeoutSmoke;
        _playerProfiles = playerProfiles;
        _exchangeLocalProfile = exchangeLocalProfile;
        _aiConfiguration = aiConfiguration;
        _aiEngine = aiEngine;
        if ((_aiConfiguration is null) != (_aiEngine is null))
            throw new ArgumentException("AI 对局配置和引擎必须同时提供。");
        if (_aiConfiguration is { } pendingAi) _localSide = pendingAi.HumanSide;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildInterface(settings);

        if (_transport is not null)
        {
            _transport.StatusChanged += OnNetworkStatus;
            _transport.MatchConfigurationReceived += OnMatchConfigurationReceived;
            _transport.ClockSyncReceived += OnClockSyncReceived;
            _transport.PlayerProfileReceived += OnPlayerProfileReceived;
            if (_isNetworkHost && hostConfiguration is { } configuration)
                ConfigureNetworkMatch(configuration, configuration.HostSide);
            if (_transport is not Node transportNode)
                throw new ArgumentException("Godot 对局传输必须由 Node 驱动生命周期。", nameof(transport));
            AddChild(transportNode);
        }
        else if (_aiConfiguration is { } aiMatch && _aiEngine is not null)
        {
            aiMatch = aiMatch.Validate();
            _localSide = aiMatch.HumanSide;
            _board.BottomSide = aiMatch.HumanSide;
            _configuration = new NetworkMatchConfiguration(aiMatch.TimeControl, aiMatch.HumanSide);
            _clock = new MatchClock(aiMatch.TimeControl) { IsRunning = true };
            _ai = new AiGameCoordinator(_session, _aiEngine, aiMatch.HumanSide);
            _ai.ThinkingChanged += OnAiThinkingChanged;
            if (_aiEngine is not Node engineNode)
                throw new ArgumentException("Godot AI 引擎适配器必须由 Node 驱动生命周期。", nameof(aiEngine));
            AddChild(engineNode);
        }
        else
        {
            _localSide = PieceSide.Red;
            _board.BottomSide = PieceSide.Red;
            var control = localTimeControl ?? TimeControl.Unlimited;
            _configuration = new NetworkMatchConfiguration(control, PieceSide.Red);
            _clock = new MatchClock(control) { IsRunning = true };
        }
    }

    public override void _Ready()
    {
        _session.StateChanged += OnStateChanged;
        _session.CueRaised += OnCueRaised;
        _board.SquarePressed += OnSquarePressed;
        _board.MoveAnimationFinished += OnMoveAnimationFinished;
        OnStateChanged(_session.Snapshot);
        if (_ai is not null) _ = InitializeAiAsync();
    }

    public override void _Process(double delta)
    {
        if (_configuration is null || _session.Snapshot.IsFinished) return;
        var loser = _clock.Advance(delta);
        UpdateClockLabels();

        if (loser is not null && (_transport is null || _isNetworkHost))
        {
            _clock.IsRunning = false;
            _ = DeclareTimeoutAsync(loser.Value);
            return;
        }

        if (_transport is not null && _isNetworkHost && _transport.IsConnected)
        {
            _clockSyncAccumulator += delta;
            if (_clockSyncAccumulator >= 0.25)
            {
                _clockSyncAccumulator = 0;
                _ = SendClockSyncAsync();
            }
        }
    }

    public override void _ExitTree()
    {
        _session.StateChanged -= OnStateChanged;
        _session.CueRaised -= OnCueRaised;
        _board.SquarePressed -= OnSquarePressed;
        _board.MoveAnimationFinished -= OnMoveAnimationFinished;
        if (_transport is not null)
        {
            _transport.StatusChanged -= OnNetworkStatus;
            _transport.MatchConfigurationReceived -= OnMatchConfigurationReceived;
            _transport.ClockSyncReceived -= OnClockSyncReceived;
            _transport.PlayerProfileReceived -= OnPlayerProfileReceived;
        }
        if (_network is not null)
        {
            _network.SynchronizationFailed -= OnSynchronizationFailed;
            _network.Chat.MessageAdded -= OnRoomChatMessage;
            _network.MatchOfferReceived -= OnMatchOfferReceived;
            _network.MatchOfferResolved -= OnMatchOfferResolved;
            _network.MatchOfferPendingChanged -= OnMatchOfferPendingChanged;
            _ = _network.DisposeAsync();
        }
        else if (_transport is not null)
        {
            _ = _transport.DisposeAsync();
        }
        if (_ai is not null)
        {
            _ai.ThinkingChanged -= OnAiThinkingChanged;
            _ = _ai.DisposeAsync();
        }
        _settingsDialog.Saved -= ApplyRoomSettings;
        ReleaseProfileResources();
    }

    private void BuildInterface(AppSettings settings)
    {
        var margin = UiStyle.CreatePageMargin(28, 14);
        AddChild(margin);
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 6);
        margin.AddChild(page);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        page.AddChild(header);
        var backButton = UiStyle.CreateButton("‹ 返回");
        backButton.CustomMinimumSize = new Vector2(92, 41);
        backButton.Pressed += LeaveForGallery;
        header.AddChild(backButton);
        var title = UiStyle.CreateTitle("经典象棋", 26);
        title.CustomMinimumSize = new Vector2(120, 41);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.ClipText = true;
        header.AddChild(title);
        _connectionLabel = new Label
        {
            Text = "等待对局",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true
        };
        UiStyle.SetMuted(_connectionLabel, 14);
        header.AddChild(_connectionLabel);
        var settingsButton = UiStyle.CreateButton("设置");
        settingsButton.CustomMinimumSize = new Vector2(70, 41);
        settingsButton.Pressed += () => _settingsDialog.PopupFor(_settings, "房间信息", _matchLabel,
            _session.Snapshot.PlyNumber == 0 && !_session.Snapshot.IsFinished);
        header.AddChild(settingsButton);
        _trialButton = UiStyle.CreateButton("推演");
        _trialButton.CustomMinimumSize = new Vector2(76, 41);
        _trialButton.Pressed += ToggleTrial;
        _trialButton.Visible = false; header.AddChild(_trialButton);
        _saveRecordButton = UiStyle.CreateButton("保存棋局");
        _saveRecordButton.CustomMinimumSize = new Vector2(88, 41);
        _saveRecordButton.Disabled = true;
        _saveRecordButton.Pressed += SaveRecord;
        _saveRecordButton.Visible = false; header.AddChild(_saveRecordButton);
        var resign = UiStyle.CreateButton("认输");
        resign.CustomMinimumSize = new Vector2(76, 41);
        var resignDialog = new ConfirmationDialog
        {
            Title = "认输",
            DialogText = "确定认输并结束本局？",
            OkButtonText = "确认认输"
        };
        resignDialog.Confirmed += Resign;
        AddChild(resignDialog);
        resign.Pressed += () => resignDialog.PopupCentered();
        resign.Visible = false; header.AddChild(resign);
        _undoButton = UiStyle.CreateButton("悔棋");
        _undoButton.CustomMinimumSize = new Vector2(95, 41);
        _undoButton.Visible = _transport is null;
        _undoButton.Pressed += Undo;
        _undoButton.Visible = false; header.AddChild(_undoButton);
        var restart = UiStyle.CreateButton("重开");
        restart.CustomMinimumSize = new Vector2(95, 41);
        restart.Visible = _transport is null;
        restart.Pressed += Restart;
        restart.Visible = false; header.AddChild(restart);

        _requestUndoButton = UiStyle.CreateButton("申请悔棋");
        _requestUndoButton.CustomMinimumSize = new Vector2(92, 41);
        _requestUndoButton.Visible = _transport is not null;
        _requestUndoButton.Pressed += () => RequestMatchOffer(MatchOfferKind.Undo);
        _requestUndoButton.Visible = false; header.AddChild(_requestUndoButton);
        _requestDrawButton = UiStyle.CreateButton("申请和棋");
        _requestDrawButton.CustomMinimumSize = new Vector2(92, 41);
        _requestDrawButton.Visible = _transport is not null;
        _requestDrawButton.Pressed += () => RequestMatchOffer(MatchOfferKind.Draw);
        _requestDrawButton.Visible = false; header.AddChild(_requestDrawButton);

        _functionMenu = new MenuButton { Text = "功能 ▾", CustomMinimumSize = new Vector2(96, 41) };
        var popup = _functionMenu.GetPopup();
        popup.AddItem("推演", 1); popup.AddItem("保存棋局", 2); popup.AddSeparator(); popup.AddItem("认输", 3);
        if (_transport is null) { popup.AddItem("悔棋", 4); popup.AddItem("重开", 5); }
        else { popup.AddItem("申请悔棋", 6); popup.AddItem("申请和棋", 7); }
        popup.AboutToPopup += RefreshFunctionMenu;
        popup.IdPressed += id =>
        {
            switch (id)
            {
                case 1: ToggleTrial(); break;
                case 2: SaveRecord(); break;
                case 3: resignDialog.PopupCentered(); break;
                case 4: Undo(); break;
                case 5: Restart(); break;
                case 6: RequestMatchOffer(MatchOfferKind.Undo); break;
                case 7: RequestMatchOffer(MatchOfferKind.Draw); break;
            }
        };
        header.AddChild(_functionMenu);

        var matchBody = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        matchBody.AddThemeConstantOverride("separation", 10);
        page.AddChild(matchBody);

        var matchColumn = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Center
        };
        matchColumn.AddThemeConstantOverride("separation", 3);
        matchBody.AddChild(matchColumn);
        var hasMessageRail = _transport is not null || _aiConfiguration is not null;
        if (!hasMessageRail) matchColumn.AddChild(_topPlayer);
        var boardPanel = UiStyle.CreatePanel();
        // Keep enough horizontal budget for the fixed message rail at the 1100px design size.
        // The board itself remains square inside the flexible column and grows on wide displays.
        boardPanel.CustomMinimumSize = new Vector2(560, 560);
        boardPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        boardPanel.SizeFlagsVertical = SizeFlags.ExpandFill;
        matchColumn.AddChild(boardPanel);
        _board = new BoardView(_themes)
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(540, 540)
        };
        _board.ApplyAppearance(settings.ThemeId, settings.ShowBoardCoordinates);
        _board.SetFeedbackAnimated(settings.EffectsEnabled);
        boardPanel.AddChild(_board);
        if (!hasMessageRail) matchColumn.AddChild(_bottomPlayer);

        ApplyPlayerProfiles();

        if (hasMessageRail) BuildMessageSidePanel(matchBody);

        _effects = new CueEffectLayer { EnabledBySettings = settings.EffectsEnabled };
        _effects.ApplyTheme(_themes.Resolve(settings.ThemeId));
        AddChild(_effects);
        _audio = new GameplayAudioPlayer();
        AddChild(_audio);
        _settingsDialog = new InGameSettingsDialog(_themes);
        _settingsDialog.Saved += ApplyRoomSettings;
        AddChild(_settingsDialog);
    }

    private void ApplyRoomSettings(AppSettings updated)
    {
        _settings.UpdateFrom(updated);
        _settings.SaveAndApply();
        _board.ApplyAppearance(_settings.ThemeId, _settings.ShowBoardCoordinates);
        _board.SetFeedbackAnimated(_settings.EffectsEnabled);
        _effects.EnabledBySettings = _settings.EffectsEnabled;
        _effects.ApplyTheme(_themes.Resolve(_settings.ThemeId));
    }

    private void BuildMessageSidePanel(Container parent)
    {
        var panel = UiStyle.CreatePanel(UiStyle.PanelLight);
        _messageSidePanel = panel;
        panel.CustomMinimumSize = new Vector2(300, 0);
        panel.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        panel.SizeFlagsVertical = SizeFlags.ExpandFill;
        parent.AddChild(panel);
        var margin = new MarginContainer();
        foreach (var key in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(key, 12);
        panel.AddChild(margin);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        margin.AddChild(column);

        var title = UiStyle.CreateTitle(_aiConfiguration is null ? "对局消息" : "AI 分析", 20);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(title);
        column.AddChild(_topPlayer);
        _chatLog = new RichTextLabel
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 360),
            ScrollFollowing = true,
            SelectionEnabled = true
        };
        _chatLog.AddThemeFontSizeOverride("normal_font_size", 15);
        column.AddChild(_chatLog);
        _chatInput = new LineEdit
        {
            PlaceholderText = _aiConfiguration is null
                ? "输入消息（最多 120 字）" : "询问当前局面的候选走法",
            MaxLength = 120,
            CustomMinimumSize = new Vector2(0, 40)
        };
        _chatInput.TextSubmitted += _ => SendChat();
        column.AddChild(_chatInput);
        _chatSendButton = UiStyle.CreateButton("发送", true);
        _chatSendButton.CustomMinimumSize = new Vector2(0, 40);
        _chatSendButton.Pressed += SendChat;
        column.AddChild(_chatSendButton);
        column.AddChild(_bottomPlayer);

        _offerDialog = new ConfirmationDialog
        {
            Title = "对局申请",
            OkButtonText = "同意",
            CancelButtonText = "拒绝",
            Exclusive = true
        };
        _offerDialog.Confirmed += () => RespondToVisibleOffer(true);
        _offerDialog.Canceled += () => RespondToVisibleOffer(false);
        AddChild(_offerDialog);
        AppendChat("系统", _aiConfiguration is null
            ? "连接建立后可以在这里发送消息。"
            : "AI 就绪后，可在这里询问当前局面的候选走法。", true);
        UpdateNetworkControls();
    }

    private async void OnSquarePressed(BoardPosition position)
    {
        var snapshot = _trial?.Snapshot ?? _session.Snapshot;
        if (snapshot.IsFinished || _board.IsAnimating || (_trial is null && !MayControlCurrentSide(snapshot))) return;
        var found = snapshot.Pieces.FirstOrDefault(item => item.Position == position);
        var hasPiece = found.Piece.Kind.Value is not null;
        if (_selected is null)
        {
            if (hasPiece && found.Piece.Side == snapshot.SideToMove) Select(position);
            return;
        }
        if (hasPiece && found.Piece.Side == snapshot.SideToMove)
        {
            Select(position);
            return;
        }

        var from = _selected.Value;
        try
        {
            MoveResult result;
            var trialBefore = _trial?.Snapshot;
            if (_trial is not null) result = _trial.TryMove(from, position);
            else if (_ai is not null) result = _ai.SubmitHumanMove(from, position);
            else if (_network is null) result = _session.TryMove(from, position);
            else result = await _network.SubmitLocalMoveAsync(from, position);
            if (result.IsSuccess)
            {
                _selected = null;
                _legalDestinations = [];
                if (_trial is not null)
                {
                    var after = _trial.Snapshot;
                    foreach (var cue in _session.DescribeMove(trialBefore!, after, result))
                    {
                        _effects.Play(cue);
                    }
                    _board.AnimateMove(trialBefore!, after, result.Move, result.CapturedPiece is not null);
                    _displayedSnapshot = after;
                }
                // StateChanged already started the transition from authoritative snapshots.
                // Reapplying the position here would intentionally cancel that animation.
                return;
            }
        }
        catch (Exception error)
        {
            OnSynchronizationFailed(error.Message);
        }
        _board.SetPosition(_trial?.Snapshot ?? _session.Snapshot, _selected, _legalDestinations);
    }

    private bool MayControlCurrentSide(GameSnapshot snapshot) =>
        _ai?.CanMakeHumanMove ??
        (_transport is null || _network?.CanMakeLocalMove == true);

    private void Select(BoardPosition position)
    {
        _selected = position;
        _legalDestinations = _trial?.GetLegalDestinations(position) ?? _session.GetLegalDestinations(position);
        _board.SetPosition(_trial?.Snapshot ?? _session.Snapshot, _selected, _legalDestinations);
    }

    private void OnStateChanged(GameSnapshot snapshot)
    {
        if (_trial is not null)
        {
            if (!_trialOpponentMoveNotified && snapshot.PlyNumber > _trial.BasePly)
            {
                _trialOpponentMoveNotified = true;
                AppendChat("系统", "真实对局已有新着法；退出推演后将回到最新局面。", true);
                _connectionLabel.Text = "推演中 · 真实对局已有更新";
            }
            UpdateNetworkControls();
            return;
        }
        if (_displayedSnapshot is { } previous && _pendingVisualMove is { } move &&
            snapshot.PlyNumber == previous.PlyNumber + 1)
        {
            _board.AnimateMove(previous, snapshot, move, _pendingVisualCapture);
        }
        else
        {
            _board.SetPosition(snapshot, _selected, _legalDestinations);
        }
        _displayedSnapshot = snapshot;
        _pendingVisualMove = null;
        _pendingVisualCapture = false;
        _undoButton.Disabled = _ai is not null
            ? !_ai.CanUndoHumanTurn
            : !_session.CanUndo;
        UpdateNetworkControls();
        if (snapshot.PlyNumber != _lastPly)
        {
            _lastPly = snapshot.PlyNumber;
            _clock.BeginTurn(snapshot.SideToMove);
        }
        UpdateClockLabels();

        if (snapshot.IsFinished && !_settlementShown)
        {
            _settlementShown = true;
            _clock.IsRunning = false;
            var isDraw = snapshot.EndReason == GameEndReason.Draw;
            var victory = !isDraw && _localSide is not null && snapshot.Winner == _localSide;
            var hasLocalPerspective = _transport is not null || _ai is not null;
            var resultText = isDraw
                ? "和棋"
                : !hasLocalPerspective
                    ? $"{(snapshot.Winner == PieceSide.Red ? "红方" : "黑方")}胜利"
                    : victory ? "胜利" : "失败";
            bool? settlementOutcome = isDraw ? null : !hasLocalPerspective || victory;
            _effects.PlaySettlement(resultText, settlementOutcome, snapshot.EndReason);
            _saveRecordButton.Disabled = false;
            if (!_recentRecordWritten)
            {
                _recentRecordWritten = true;
                try { ClassicRecordStore.SaveRecent(BuildRecord(snapshot), BuildRecordPresentation(snapshot)); }
                catch (Exception error) { GD.PushWarning($"Unable to retain recent record: {error.Message}"); }
            }
            if (_debugNetworkSmoke && snapshot.EndReason == GameEndReason.Draw)
            {
                GD.Print("[Xiangqi.Network.Smoke] Chat, accepted undo and accepted draw synchronized.");
                GetTree().CreateTimer(0.35).Timeout += () => GetTree().Quit();
            }
            if (_debugTimeoutSmoke && snapshot.EndReason == GameEndReason.Timeout)
            {
                GD.Print($"[Xiangqi.Network.Smoke] Timeout synchronized. Winner={snapshot.Winner}.");
                GetTree().CreateTimer(0.35).Timeout += () => GetTree().Quit();
            }
        }

        if (OS.IsDebugBuild())
        {
            var side = snapshot.SideToMove == PieceSide.Red ? "红方" : "黑方";
            GD.Print($"[Xiangqi] Ply={snapshot.PlyNumber}, Side={side}, Check={snapshot.IsInCheck}, " +
                     $"Finished={snapshot.IsFinished}, EndReason={snapshot.EndReason}");
            RunNetworkSmoke(snapshot);
        }
        ScheduleAiMoveIfReady();
    }

    private ClassicGameRecord BuildRecord(GameSnapshot snapshot)
    {
        var result = snapshot.EndReason == GameEndReason.Draw ? "1/2-1/2" : snapshot.Winner switch
        { PieceSide.Red => "1-0", PieceSide.Black => "0-1", _ => "*" };
        var presentation = BuildRecordPresentation(snapshot);
        var localSide = _localSide ?? PieceSide.Red;
        var opponent = presentation.Opponents[0];
        var redName = localSide == PieceSide.Red ? presentation.Local.Name : opponent.Name;
        var blackName = localSide == PieceSide.Black ? presentation.Local.Name : opponent.Name;
        return _session.CreateRecord(new Dictionary<string, string>
        {
            ["Event"] = _matchLabel,
            ["Date"] = DateTime.Now.ToString("yyyy.MM.dd"),
            ["Result"] = result,
            ["Termination"] = snapshot.EndReason.ToString(),
            ["Red"] = redName,
            ["Black"] = blackName
        });
    }

    private MatchRecordPresentation BuildRecordPresentation(GameSnapshot snapshot)
    {
        var localSide = _localSide ?? PieceSide.Red;
        RecordPlayer FromProfile(PlayerProfileSnapshot profile, bool local) => new(
            profile.DisplayName, local, profile.Avatar?.Width ?? 0, profile.Avatar?.Height ?? 0,
            profile.Avatar?.CopyRgba());
        RecordPlayer local;
        RecordPlayer opponent;
        if (_playerProfiles is not null)
        {
            local = FromProfile(_playerProfiles.Local, true);
            opponent = FromProfile(_playerProfiles.Opponent, false);
        }
        else
        {
            local = new(_bottomPlayer.DisplayName, true);
            opponent = new(_topPlayer.DisplayName, false);
        }
        var result = snapshot.EndReason == GameEndReason.Draw ? "和" : snapshot.Winner == localSide ? "胜" : "败";
        var mode = _ai is not null ? "人机对战" : _matchLabel.StartsWith("Steam", StringComparison.OrdinalIgnoreCase)
            ? "Steam 好友" : _transport is not null ? "局域网联机" : "本地联机";
        return new(DateTimeOffset.Now, mode, local, [opponent], result);
    }

    private void SaveRecord()
    {
        try
        {
            var path = ClassicRecordStore.SavePermanent(BuildRecord(_session.Snapshot), BuildRecordPresentation(_session.Snapshot));
            _saveRecordButton.Disabled = true;
            _saveRecordButton.Text = "已保存";
            AppendChat("系统", $"棋谱已保存：{Path.GetFileName(path)}", true);
        }
        catch (Exception error)
        {
            OnSynchronizationFailed($"保存棋谱失败：{error.Message}");
        }
    }

    private void ToggleTrial()
    {
        _selected = null;
        _legalDestinations = [];
        if (_trial is null)
        {
            _trial = _session.BeginTrial();
            _trialOpponentMoveNotified = false;
            _trialButton.Text = "退出推演";
            _connectionLabel.Text = "推演分支";
            _board.SetPosition(_trial.Snapshot);
            _displayedSnapshot = _trial.Snapshot;
            return;
        }
        _trial = null;
        _trialButton.Text = "推演";
        _connectionLabel.Text = "返回真实对局";
        _displayedSnapshot = _session.Snapshot;
        _board.SetPosition(_session.Snapshot);
        ScheduleAiMoveIfReady();
    }

    private void OnCueRaised(PresentationCue cue)
    {
        if ((cue.Kind is PresentationCueKind.Move or PresentationCueKind.Capture) && cue.Move is { } move)
        {
            _pendingVisualMove = move;
            _pendingVisualCapture = cue.Kind == PresentationCueKind.Capture;
        }
        _effects.Play(cue);
    }

    private void OnMoveAnimationFinished(bool captured)
    {
        _audio.PlayLanding(captured);
        ScheduleAiMoveIfReady();
    }

    private async void Resign()
    {
        try { await DeclareLocalResignationAsync(); }
        catch (Exception error) { OnSynchronizationFailed($"认输失败：{error.Message}"); }
    }

    private async void LeaveForGallery()
    {
        try { await DeclareLocalResignationAsync(); }
        catch (Exception error) { GD.PushWarning($"返回画廊前同步认输失败：{error.Message}"); }
        _back();
    }

    private async ValueTask<bool> DeclareLocalResignationAsync()
    {
        if (_session.Snapshot.IsFinished) return true;
        if (_transport is not null)
        {
            if (_network is null) return false;
            return await _network.DeclareResignationAsync();
        }
        _clock.IsRunning = false;
        return _session.DeclareResignation(_localSide ?? PieceSide.Red);
    }

    private async void SendChat()
    {
        if (_network is null && _ai is null) return;
        var text = _chatInput.Text.Trim();
        if (text.Length == 0) return;
        if (_ai is not null)
        {
            AppendChat("我", text);
            _chatInput.Clear();
            UpdateNetworkControls();
            try
            {
                var analysis = await _ai.AnalyzeAsync(
                    Math.Min(_aiConfiguration!.Value.MoveTimeMilliseconds, 3_000), 3);
                AppendChat(_aiEngine!.DisplayName, FormatAiAnalysis(analysis));
            }
            catch (Exception error)
            {
                AppendChat("系统", $"AI 分析失败：{error.Message}", true);
            }
            finally
            {
                UpdateNetworkControls();
                _chatInput.GrabFocus();
            }
            return;
        }
        try
        {
            var error = await _network!.SendChatAsync(text);
            if (error is not null) throw new InvalidOperationException(error);
            _chatInput.Clear();
            _chatInput.GrabFocus();
        }
        catch (Exception error)
        {
            AppendChat("系统", $"发送失败：{error.Message}", true);
        }
    }

    private async void RequestMatchOffer(MatchOfferKind kind)
    {
        if (_network is null) return;
        try
        {
            await _network.RequestMatchOfferAsync(kind);
            AppendChat("系统", $"已发送{OfferName(kind)}申请，等待对方处理。", true);
        }
        catch (Exception error)
        {
            AppendChat("系统", error.Message, true);
        }
    }

    private void OnRoomChatMessage(RoomChatEntry entry)
    {
        if (_network is null || !RoomChatManager.IsVisibleTo(entry, (int)_network.LocalSide)) return;
        var sender = entry.SenderSeat == (int)_network.LocalSide ? "我" : "对方";
        AppendChat(sender, entry.Text);
        if (_debugNetworkSmoke && entry.Text == "联机协商冒烟消息" && sender == "对方")
            GD.Print("[Xiangqi.Network.Smoke] Chat message received.");
    }

    private void OnMatchOfferReceived(MatchOfferRequest request)
    {
        _visibleIncomingOffer = request;
        var name = OfferName(request.Kind);
        AppendChat("系统", $"对方申请{name}，等待你的决定。", true);
        if (_debugNetworkSmoke)
        {
            GetTree().CreateTimer(0.05).Timeout += () => RespondToVisibleOffer(true);
            return;
        }
        _offerDialog.DialogText = request.Kind == MatchOfferKind.Undo
            ? "对方申请撤销最近一手棋，是否同意？"
            : "对方申请结束本局并判为和棋，是否同意？";
        _offerDialog.PopupCentered(new Vector2I(430, 190));
    }

    private async void RespondToVisibleOffer(bool accepted)
    {
        if (_network is null || _visibleIncomingOffer is not { } request) return;
        _visibleIncomingOffer = null;
        try
        {
            await _network.RespondToMatchOfferAsync(request.RequestId, accepted);
        }
        catch (Exception error)
        {
            AppendChat("系统", $"处理申请失败：{error.Message}", true);
        }
    }

    private void OnMatchOfferResolved(MatchOfferResolution resolution)
    {
        _visibleIncomingOffer = null;
        if (_offerDialog.Visible) _offerDialog.Hide();
        var actor = resolution.WasLocalRequest ? "对方" : "你";
        var action = resolution.Accepted ? "同意" : "拒绝";
        AppendChat("系统", $"{actor}{action}了{OfferName(resolution.Kind)}申请。", true);
        if (_debugNetworkSmoke && resolution.WasLocalRequest && resolution.Accepted &&
            resolution.Kind == MatchOfferKind.Undo && _localSide == PieceSide.Black)
            _ = SendSmokeDrawOfferAsync();
    }

    private void OnMatchOfferPendingChanged(bool pending)
    {
        if (pending)
        {
            _selected = null;
            _legalDestinations = [];
            _board.SetPosition(_session.Snapshot);
            _clock.IsRunning = false;
        }
        else
        {
            _clock.IsRunning = _transport?.IsConnected == true && !_session.Snapshot.IsFinished;
        }
        UpdateNetworkControls();
        UpdateClockLabels();
        if (_isNetworkHost && _transport?.IsConnected == true) _ = SendClockSyncAsync();
    }

    private void AppendChat(string sender, string message, bool system = false)
    {
        if (_transport is null && _ai is null) return;
        var prefix = system ? $"[{sender}]" : $"{sender}：";
        _chatLines.Add($"{prefix} {message}");
        if (_chatLines.Count > 100) _chatLines.RemoveAt(0);
        _chatLog.Text = string.Join('\n', _chatLines);
    }

    private void UpdateNetworkControls()
    {
        if (_ai is not null)
        {
            var available = _aiInitialized && !_ai.IsThinking && !_session.Snapshot.IsFinished;
            _chatSendButton.Disabled = !available;
            _chatInput.Editable = available;
            _undoButton.Disabled = !_ai.CanUndoHumanTurn;
            return;
        }
        if (_transport is null) return;
        var connected = _transport.IsConnected && _network is not null;
        var pending = _network?.HasPendingOffer == true;
        var finished = _session.Snapshot.IsFinished;
        _requestUndoButton.Disabled = !connected || pending || finished ||
                                      _network?.CanRequestLocalUndo != true;
        _requestDrawButton.Disabled = !connected || pending || finished;
        _chatSendButton.Disabled = !connected;
        _chatInput.Editable = connected;
    }

    private static string OfferName(MatchOfferKind kind) =>
        kind == MatchOfferKind.Undo ? "悔棋" : "和棋";

    private async void OnNetworkStatus(TransportStatus status)
    {
        if (OS.IsDebugBuild()) GD.Print($"[Xiangqi.Network] State={status.State}, Detail={status.Detail}");
        var sideText = _localSide is null ? string.Empty : $" · 你执{SideName(_localSide.Value)}";
        _connectionLabel.Text = status.State switch
        {
            TransportConnectionState.Connected => $"已连接{sideText}",
            TransportConnectionState.Faulted => "连接失败",
            TransportConnectionState.Disconnected => "连接已断开",
            _ => "等待对手加入"
        };
        _connectionLabel.TooltipText = "连接与房间详情请在设置中查看";
        _connectionLabel.AddThemeColorOverride("font_color", status.State switch
        {
            TransportConnectionState.Connected => new Color("#79C99A"),
            TransportConnectionState.Faulted => new Color("#E87777"),
            TransportConnectionState.Disconnected => new Color("#E8A565"),
            _ => UiStyle.Muted
        });
        UpdateNetworkControls();
        if (status.State is TransportConnectionState.Disconnected or TransportConnectionState.Faulted)
        {
            ReleaseProfileResources();
            MarkProfilesReleased();
        }
        if (status.State != TransportConnectionState.Connected) return;
        await SendLocalProfileAsync();

        if (_isNetworkHost && _hostConfiguration is { } configuration && _transport is not null)
        {
            await _transport.SendMatchConfigurationAsync(configuration);
            _clock.IsRunning = _network?.HasPendingOffer != true;
            if (_debugNetworkSmoke && _localSide == PieceSide.Red && !_smokeMoveScheduled)
            {
                _smokeMoveScheduled = true;
                _ = SendSmokeMoveAsync(new BoardPosition(0, 3), new BoardPosition(0, 4));
            }
        }
    }

    private void OnMatchConfigurationReceived(NetworkMatchConfiguration configuration)
    {
        if (_isNetworkHost || _network is not null) return;
        ConfigureNetworkMatch(configuration, configuration.ClientSide);
        _clock.IsRunning = _network?.HasPendingOffer != true;
        _connectionLabel.Text = $"配置完成 · 你执{SideName(_localSide!.Value)}";
    }

    private void ConfigureNetworkMatch(NetworkMatchConfiguration configuration, PieceSide localSide)
    {
        _configuration = configuration;
        _localSide = localSide;
        _clock = new MatchClock(configuration.TimeControl);
        _board.BottomSide = localSide;
        UpdatePlayerSideLabels();
        _network = new NetworkGameCoordinator(_session, _transport!, localSide);
        _network.SynchronizationFailed += OnSynchronizationFailed;
        _network.Chat.MessageAdded += OnRoomChatMessage;
        _network.MatchOfferReceived += OnMatchOfferReceived;
        _network.MatchOfferResolved += OnMatchOfferResolved;
        _network.MatchOfferPendingChanged += OnMatchOfferPendingChanged;
        UpdateNetworkControls();
        UpdateClockLabels();
    }

    private void OnClockSyncReceived(ClockSyncCommand command)
    {
        if (_isNetworkHost || command.PlyNumber != _session.Snapshot.PlyNumber) return;
        _clock.Apply(command.Clock);
        _clock.IsRunning = _network?.HasPendingOffer != true;
        UpdateClockLabels();
    }

    private async Task SendClockSyncAsync()
    {
        if (_transport is null) return;
        try
        {
            await _transport.SendClockSyncAsync(
                new ClockSyncCommand(_session.Snapshot.PlyNumber, _clock.Snapshot));
        }
        catch (Exception error)
        {
            OnSynchronizationFailed(error.Message);
        }
    }

    private async Task DeclareTimeoutAsync(PieceSide loser)
    {
        try
        {
            if (_network is null) _session.DeclareTimeout(loser);
            else await _network.DeclareTimeoutAsync(loser);
        }
        catch (Exception error)
        {
            OnSynchronizationFailed(error.Message);
        }
    }

    private void UpdateClockLabels()
    {
        if (_configuration?.TimeControl.IsEnabled != true || _localSide is null)
        {
            _topPlayer.SetTrailing(string.Empty);
            _bottomPlayer.SetTrailing(string.Empty);
            _topPlayer.SetMoveClock(default);
            _bottomPlayer.SetMoveClock(default);
            return;
        }
        var clock = _clock.Snapshot;
        var topSide = _localSide.Value.Opponent();
        var topText = ClockText(topSide, clock);
        var bottomText = ClockText(_localSide.Value, clock);
        _topPlayer.SetTrailing(topText);
        _bottomPlayer.SetTrailing(bottomText);
        var active = !_session.Snapshot.IsFinished && _clock.IsRunning;
        _topPlayer.SetMoveClock(MoveClockIndicator.From(clock.MoveSeconds,
            _configuration.Value.TimeControl.MoveSeconds, active && clock.SideToMove == topSide));
        _bottomPlayer.SetMoveClock(MoveClockIndicator.From(clock.MoveSeconds,
            _configuration.Value.TimeControl.MoveSeconds, active && clock.SideToMove == _localSide.Value));
    }

    private string ClockText(PieceSide side, ClockSnapshot clock)
    {
        var total = side == PieceSide.Red ? clock.RedGameSeconds : clock.BlackGameSeconds;
        var totalText = _configuration!.Value.TimeControl.GameSeconds > 0 ? FormatTime(total) : "不限局时";
        var moveText = _configuration.Value.TimeControl.MoveSeconds > 0 && clock.SideToMove == side
            ? $"  ·  步时 {FormatTime(clock.MoveSeconds)}"
            : string.Empty;
        return $"{SideName(side)}  {totalText}{moveText}";
    }

    private static string FormatTime(double seconds)
    {
        var rounded = Math.Max(0, (int)Math.Ceiling(seconds));
        return $"{rounded / 60:00}:{rounded % 60:00}";
    }

    private static string SideName(PieceSide side) => side == PieceSide.Red ? "红方" : "黑方";

    private void ApplyPlayerProfiles()
    {
        if (_playerProfiles is null)
        {
            if (_ai is not null || _aiConfiguration is not null)
            {
                _topPlayer.Apply(null, _aiEngine?.DisplayName ?? "AI 引擎", "UCI 象棋引擎", string.Empty);
                _bottomPlayer.Apply(null, "本地玩家", $"玩家执{SideName(_localSide ?? PieceSide.Red)}", string.Empty);
                return;
            }
            var detail = _transport is null ? "本地双人" : "临时联机身份";
            _topPlayer.Apply(null, _transport is null ? "黑方" : "联机对手", detail, string.Empty);
            _bottomPlayer.Apply(null, _transport is null ? "红方" : "本地玩家", detail, string.Empty);
            return;
        }
        var bottom = _localSide ?? PieceSide.Red;
        _bottomPlayer.Apply(_playerProfiles.Local, SideName(bottom), $"玩家执{SideName(bottom)}", string.Empty);
        _topPlayer.Apply(_playerProfiles.Opponent, SideName(bottom.Opponent()), $"对方执{SideName(bottom.Opponent())}", string.Empty);
        UpdatePlayerSideLabels();
    }

    private void RefreshFunctionMenu()
    {
        var popup = _functionMenu.GetPopup();
        void Sync(int id, Button source)
        {
            var index = popup.GetItemIndex(id);
            if (index < 0) return;
            popup.SetItemDisabled(index, source.Disabled);
            popup.SetItemText(index, source.Text);
        }
        Sync(1, _trialButton); Sync(2, _saveRecordButton); Sync(4, _undoButton);
        Sync(6, _requestUndoButton); Sync(7, _requestDrawButton);
    }

    private void OnPlayerProfileReceived(PlayerProfileCommand command)
    {
        if (_profileResourcesReleased || _playerProfiles is null) return;
        try
        {
            _playerProfiles.ReplaceOpponent(CustomPlayerProfileFactory.FromCommand(command));
            ApplyPlayerProfiles();
        }
        catch (Exception error)
        {
            GD.PushWarning($"忽略无效的远端玩家资料：{error.Message}");
        }
    }

    private async Task SendLocalProfileAsync()
    {
        if (_localProfileSent || !_exchangeLocalProfile || _transport is null ||
            _playerProfiles is null || !_transport.IsConnected) return;
        _localProfileSent = true;
        try
        {
            await _transport.SendPlayerProfileAsync(
                CustomPlayerProfileFactory.ToCommand(_playerProfiles.Local));
        }
        catch (Exception error)
        {
            _localProfileSent = false;
            GD.PushWarning($"发送自定义玩家资料失败：{error.Message}");
        }
    }

    private void UpdatePlayerSideLabels()
    {
        UpdateClockLabels();
    }

    private void ReleaseProfileResources()
    {
        if (_profileResourcesReleased) return;
        _profileResourcesReleased = true;
        _playerProfiles?.Dispose();
    }

    private void MarkProfilesReleased()
    {
        if (_playerProfiles is null) return;
        _topPlayer.Apply((PlayerProfileSnapshot?)null, "房间已解散", "临时资料已释放", string.Empty);
        _bottomPlayer.Apply((PlayerProfileSnapshot?)null, "本地玩家", "临时资料已释放", string.Empty);
    }

    private void Undo()
    {
        _selected = null;
        _legalDestinations = [];
        _settlementShown = false;
        var changed = _ai?.UndoHumanTurn() ?? _session.Undo();
        if (changed)
        {
            _clock.IsRunning = !_session.Snapshot.IsFinished;
            UpdateClockLabels();
        }
    }

    private async void Restart()
    {
        _selected = null;
        _legalDestinations = [];
        _settlementShown = false;
        _lastPly = 0;
        _clock.IsRunning = false;
        try
        {
            if (_ai is null) _session.NewGame();
            else await _ai.NewGameAsync();
        }
        catch (Exception error)
        {
            _connectionLabel.Text = $"重新开始失败：{error.Message}";
            _connectionLabel.AddThemeColorOverride("font_color", new Color("#E87777"));
            AppendChat("系统", $"重新开始失败：{error.Message}", true);
            UpdateNetworkControls();
            return;
        }
        _clock = new MatchClock(_configuration?.TimeControl ?? TimeControl.Unlimited) { IsRunning = true };
        UpdateClockLabels();
        ScheduleAiMoveIfReady();
    }

    private async Task InitializeAiAsync()
    {
        if (_ai is null || _aiEngine is null) return;
        _connectionLabel.Text = "正在连接 AI 引擎…";
        UpdateNetworkControls();
        try
        {
            await _ai.InitializeAsync();
            _aiInitialized = true;
            ApplyPlayerProfiles();
            _connectionLabel.Text = $"人机对决 · 你执{SideName(_ai.HumanSide)} · {_aiEngine.DisplayName}";
            _connectionLabel.AddThemeColorOverride("font_color", new Color("#79C99A"));
            AppendChat("系统", $"{_aiEngine.DisplayName} 已连接。输入问题可获得当前局面的 3 个候选走法。", true);
            UpdateNetworkControls();
            ScheduleAiMoveIfReady();
        }
        catch (Exception error)
        {
            _clock.IsRunning = false;
            _connectionLabel.Text = $"AI 连接失败：{error.Message}";
            _connectionLabel.AddThemeColorOverride("font_color", new Color("#E87777"));
            AppendChat("系统", $"AI 连接失败：{error.Message}", true);
            UpdateNetworkControls();
        }
    }

    private void ScheduleAiMoveIfReady()
    {
        if (!_aiInitialized || _ai is null || _aiMoveScheduled || _ai.IsThinking ||
            _board.IsAnimating || _session.Snapshot.IsFinished ||
            _session.Snapshot.SideToMove != _ai.AiSide) return;
        _aiMoveScheduled = true;
        _ = PlayAiMoveAsync();
    }

    private async Task PlayAiMoveAsync()
    {
        try
        {
            var configuration = _aiConfiguration!.Value;
            await _ai!.PlayAiTurnAsync(new AiSearchRequest(configuration.MoveTimeMilliseconds,
                configuration.VariationCount, configuration.MaxDepth),
                configuration.CandidateRank, configuration.MaximumCentipawnLoss,
                configuration.UseDeterministicLegalMove);
        }
        catch (OperationCanceledException)
        {
            // Leaving, restarting or undoing invalidates the in-flight search.
        }
        catch (Exception error)
        {
            _clock.IsRunning = false;
            _connectionLabel.Text = $"AI 行棋失败：{error.Message}";
            _connectionLabel.AddThemeColorOverride("font_color", new Color("#E87777"));
            AppendChat("系统", $"AI 行棋失败：{error.Message}", true);
        }
        finally
        {
            _aiMoveScheduled = false;
            UpdateNetworkControls();
        }
    }

    private void OnAiThinkingChanged(bool thinking)
    {
        if (_ai is null || !_aiInitialized) return;
        _connectionLabel.Text = thinking
            ? $"人机对决 · {_aiEngine!.DisplayName} 思考中…"
            : $"人机对决 · 你执{SideName(_ai.HumanSide)} · {_aiEngine!.DisplayName}";
        UpdateNetworkControls();
        if (!thinking) ScheduleAiMoveIfReady();
    }

    private static string FormatAiAnalysis(AiSearchResult result)
    {
        var lines = result.Variations.Take(3).Select(variation =>
        {
            var score = variation.MateIn is { } mate
                ? $"杀棋 {mate}"
                : variation.CentipawnScore is { } cp
                    ? $"评估 {cp / 100.0:+0.00;-0.00;0.00}"
                    : "暂无评估";
            var pv = string.Join(' ', variation.PrincipalVariation.Take(5)
                .Select(XiangqiUciNotation.FormatMove));
            return $"{variation.Rank}. {XiangqiUciNotation.FormatMove(variation.Move)} · {score} · 深度 {variation.Depth}" +
                   (pv.Length == 0 ? string.Empty : $"\n   参考变化：{pv}");
        });
        return string.Join('\n', lines);
    }

    private void OnSynchronizationFailed(string message)
    {
        GD.PushError($"[Xiangqi.Network] {message}");
        _connectionLabel.Text = message;
        _connectionLabel.AddThemeColorOverride("font_color", new Color("#E87777"));
        _effects.Play(new PresentationCue(PresentationCueKind.IllegalMove, Message: "联机同步失败"));
    }

    private void RunNetworkSmoke(GameSnapshot snapshot)
    {
        if (!_debugNetworkSmoke) return;
        if (snapshot.PlyNumber == 1 && _localSide == PieceSide.Black && !_smokeMoveScheduled)
        {
            _smokeMoveScheduled = true;
            _ = SendSmokeMoveAsync(new BoardPosition(0, 6), new BoardPosition(0, 5));
        }
        else if (snapshot.PlyNumber == 2)
        {
            GD.Print("[Xiangqi.Network.Smoke] Two synchronized moves completed.");
            if (_localSide == PieceSide.Black && !_smokeNegotiationScheduled)
            {
                _smokeNegotiationScheduled = true;
                // StateChanged is raised before SubmitLocalMoveAsync has put the move packet on
                // the wire. Defer the offer so the opponent first reaches the same ply.
                GetTree().CreateTimer(0.10).Timeout += () => _ = SendSmokeNegotiationAsync();
            }
        }
    }

    private async Task SendSmokeMoveAsync(BoardPosition from, BoardPosition to)
    {
        if (_network is null) return;
        try
        {
            var result = await _network.SubmitLocalMoveAsync(from, to);
            GD.Print($"[Xiangqi.Network.Smoke] Sent {from}->{to}, Success={result.IsSuccess}.");
        }
        catch (Exception error)
        {
            OnSynchronizationFailed(error.Message);
            GetTree().Quit(2);
        }
    }

    private async Task SendSmokeNegotiationAsync()
    {
        if (_network is null) return;
        try
        {
            await _network.SendChatAsync("联机协商冒烟消息");
            await _network.RequestMatchOfferAsync(MatchOfferKind.Undo);
        }
        catch (Exception error)
        {
            OnSynchronizationFailed(error.Message);
            GetTree().Quit(2);
        }
    }

    private async Task SendSmokeDrawOfferAsync()
    {
        if (_network is null) return;
        try
        {
            await _network.RequestMatchOfferAsync(MatchOfferKind.Draw);
        }
        catch (Exception error)
        {
            OnSynchronizationFailed(error.Message);
            GetTree().Quit(2);
        }
    }
}
