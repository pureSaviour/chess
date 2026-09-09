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
    private readonly List<ImageTexture> _profileTextures = [];
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
    private Label _topClock = null!;
    private Label _bottomClock = null!;
    private TextureRect _topAvatar = null!;
    private TextureRect _bottomAvatar = null!;
    private Label _topAvatarInitial = null!;
    private Label _bottomAvatarInitial = null!;
    private Label _topPlayerName = null!;
    private Label _bottomPlayerName = null!;
    private Label _topPlayerId = null!;
    private Label _bottomPlayerId = null!;
    private Button _undoButton = null!;
    private Button _requestUndoButton = null!;
    private Button _requestDrawButton = null!;
    private Button _chatSendButton = null!;
    private RichTextLabel _chatLog = null!;
    private LineEdit _chatInput = null!;
    private OptionButton? _chatTarget;
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
    private bool _recentRecordWritten;

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
        var backButton = UiStyle.CreateButton("‹ 经典象棋");
        backButton.CustomMinimumSize = new Vector2(130, 41);
        backButton.Pressed += _back;
        header.AddChild(backButton);
        var title = UiStyle.CreateTitle("经典象棋", 26);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.AddChild(title);
        _connectionLabel = new Label
        {
            Text = _matchLabel,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        UiStyle.SetMuted(_connectionLabel, 14);
        header.AddChild(_connectionLabel);
        var settingsButton = UiStyle.CreateButton("设置");
        settingsButton.CustomMinimumSize = new Vector2(82, 41);
        settingsButton.Pressed += () => _settingsDialog.PopupFor(_settings);
        header.AddChild(settingsButton);
        _trialButton = UiStyle.CreateButton("试下");
        _trialButton.CustomMinimumSize = new Vector2(82, 41);
        _trialButton.Pressed += ToggleTrial;
        header.AddChild(_trialButton);
        _saveRecordButton = UiStyle.CreateButton("保存棋局");
        _saveRecordButton.CustomMinimumSize = new Vector2(100, 41);
        _saveRecordButton.Disabled = true;
        _saveRecordButton.Pressed += SaveRecord;
        header.AddChild(_saveRecordButton);
        _undoButton = UiStyle.CreateButton("悔棋");
        _undoButton.CustomMinimumSize = new Vector2(95, 41);
        _undoButton.Visible = _transport is null;
        _undoButton.Pressed += Undo;
        header.AddChild(_undoButton);
        var restart = UiStyle.CreateButton("重开");
        restart.CustomMinimumSize = new Vector2(95, 41);
        restart.Visible = _transport is null;
        restart.Pressed += Restart;
        header.AddChild(restart);

        _requestUndoButton = UiStyle.CreateButton("申请悔棋");
        _requestUndoButton.CustomMinimumSize = new Vector2(105, 41);
        _requestUndoButton.Visible = _transport is not null;
        _requestUndoButton.Pressed += () => RequestMatchOffer(MatchOfferKind.Undo);
        header.AddChild(_requestUndoButton);
        _requestDrawButton = UiStyle.CreateButton("申请和棋");
        _requestDrawButton.CustomMinimumSize = new Vector2(105, 41);
        _requestDrawButton.Visible = _transport is not null;
        _requestDrawButton.Pressed += () => RequestMatchOffer(MatchOfferKind.Draw);
        header.AddChild(_requestDrawButton);

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
        matchColumn.AddChild(CreatePlayerRow(isBottom: false));
        var boardPanel = UiStyle.CreatePanel();
        boardPanel.CustomMinimumSize = new Vector2(680, 615);
        boardPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        boardPanel.SizeFlagsVertical = SizeFlags.ExpandFill;
        matchColumn.AddChild(boardPanel);
        _board = new BoardView(_themes)
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(600, 600)
        };
        _board.ApplyAppearance(settings.ThemeId, settings.ShowBoardCoordinates);
        _board.SetFeedbackAnimated(settings.EffectsEnabled);
        boardPanel.AddChild(_board);
        matchColumn.AddChild(CreatePlayerRow(isBottom: true));

        ApplyPlayerProfiles();

        if (_transport is not null || _aiConfiguration is not null) BuildMessageSidePanel(matchBody);

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
        panel.CustomMinimumSize = new Vector2(280, 0);
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
        _chatLog = new RichTextLabel
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(250, 410),
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
        if (_transport is not null)
        {
            _chatTarget = new OptionButton();
            _chatTarget.AddItem("发送给：房间所有人", 100);
            _chatTarget.AddItem("仅发送给：对方", 1);
            column.AddChild(_chatTarget);
        }
        _chatSendButton = UiStyle.CreateButton("发送", true);
        _chatSendButton.CustomMinimumSize = new Vector2(0, 40);
        _chatSendButton.Pressed += SendChat;
        column.AddChild(_chatSendButton);

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
                    _board.SetPosition(_trial.Snapshot);
                    _displayedSnapshot = _trial.Snapshot;
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
        _audio.PlaySelection();
    }

    private void OnStateChanged(GameSnapshot snapshot)
    {
        if (_trial is not null)
        {
            if (!_trialOpponentMoveNotified && snapshot.PlyNumber > _trial.BasePly)
            {
                _trialOpponentMoveNotified = true;
                AppendChat("系统", "真实对局已有新着法；退出试下后将回到最新局面。", true);
                _connectionLabel.Text = $"{_matchLabel} · 试下中（真实对局已更新）";
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
        return _session.CreateRecord(new Dictionary<string, string>
        {
            ["Event"] = _matchLabel,
            ["Date"] = DateTime.Now.ToString("yyyy.MM.dd"),
            ["Result"] = result,
            ["Termination"] = snapshot.EndReason.ToString(),
            ["Red"] = presentation.Red.Name,
            ["Black"] = presentation.Black.Name
        });
    }

    private ClassicRecordStore.Presentation BuildRecordPresentation(GameSnapshot snapshot)
    {
        var localSide = _localSide ?? PieceSide.Red;
        ClassicRecordStore.Player FromProfile(PlayerProfileSnapshot profile, bool local) => new(
            profile.DisplayName, local, profile.Avatar?.Width ?? 0, profile.Avatar?.Height ?? 0,
            profile.Avatar?.CopyRgba());
        ClassicRecordStore.Player local;
        ClassicRecordStore.Player opponent;
        if (_playerProfiles is not null)
        {
            local = FromProfile(_playerProfiles.Local, true);
            opponent = FromProfile(_playerProfiles.Opponent, false);
        }
        else
        {
            local = new(_bottomPlayerName.Text, true);
            opponent = new(_topPlayerName.Text, false);
        }
        var red = localSide == PieceSide.Red ? local : opponent;
        var black = localSide == PieceSide.Black ? local : opponent;
        var result = snapshot.EndReason == GameEndReason.Draw ? "和棋" : snapshot.Winner == PieceSide.Red ? "红方胜" : "黑方胜";
        var mode = _ai is not null ? "人机对战" : _matchLabel.StartsWith("Steam", StringComparison.OrdinalIgnoreCase)
            ? "Steam 好友" : _transport is not null ? "局域网联机" : "本地联机";
        return new(DateTimeOffset.Now, mode, red, black, result);
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
            _trialButton.Text = "退出试下";
            _connectionLabel.Text = $"{_matchLabel} · 试下分支";
            _board.SetPosition(_trial.Snapshot);
            _displayedSnapshot = _trial.Snapshot;
            return;
        }
        _trial = null;
        _trialButton.Text = "试下";
        _connectionLabel.Text = _matchLabel;
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
        _audio.PlayCue(cue);
    }

    private void OnMoveAnimationFinished(bool captured)
    {
        _audio.PlayLanding(captured);
        ScheduleAiMoveIfReady();
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
            var selectedTarget = _chatTarget is { Selected: >= 0 }
                ? _chatTarget.GetItemId(_chatTarget.Selected) : 100;
            var targetSeat = selectedTarget == 100 ? (int?)null : (int)_network!.LocalSide.Opponent();
            var error = await _network!.SendChatAsync(text, targetSeat);
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
        var destination = entry.TargetSeat is null ? " → 房间" : " → 私聊";
        AppendChat(sender + destination, entry.Text);
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
        _connectionLabel.Text = $"{_matchLabel}{sideText} · {status.Detail}";
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
        _connectionLabel.Text = $"{_matchLabel} · 你执{SideName(_localSide!.Value)}";
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
            _topClock.Visible = false;
            _bottomClock.Visible = false;
            return;
        }
        if (!_topClock.Visible) _topClock.Visible = true;
        if (!_bottomClock.Visible) _bottomClock.Visible = true;
        var clock = _clock.Snapshot;
        var topSide = _localSide.Value.Opponent();
        var topText = ClockText(topSide, clock);
        var bottomText = ClockText(_localSide.Value, clock);
        if (_topClock.Text != topText) _topClock.Text = topText;
        if (_bottomClock.Text != bottomText) _bottomClock.Text = bottomText;
        var topColor = clock.SideToMove == topSide ? UiStyle.Gold : UiStyle.Muted;
        var bottomColor = clock.SideToMove == _localSide ? UiStyle.Gold : UiStyle.Muted;
        if (_topClock.GetThemeColor("font_color") != topColor)
            _topClock.AddThemeColorOverride("font_color", topColor);
        if (_bottomClock.GetThemeColor("font_color") != bottomColor)
            _bottomClock.AddThemeColorOverride("font_color", bottomColor);
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

    private static Label CreateClockLabel()
    {
        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 26)
        };
        label.AddThemeFontSizeOverride("font_size", 17);
        return label;
    }

    private Control CreatePlayerRow(bool isBottom)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 50) };
        row.AddThemeConstantOverride("separation", 10);
        var avatarRoot = new Control { CustomMinimumSize = new Vector2(46, 46) };
        var background = new ColorRect { Color = isBottom ? new Color("#8E3B35") : new Color("#34373D") };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        avatarRoot.AddChild(background);
        var avatar = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
        };
        avatar.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        avatarRoot.AddChild(avatar);
        var initial = new Label
        {
            Text = "棋",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        initial.AddThemeFontSizeOverride("font_size", 22);
        initial.AddThemeColorOverride("font_color", Colors.White);
        initial.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        avatarRoot.AddChild(initial);
        row.AddChild(avatarRoot);

        var identity = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        identity.AddThemeConstantOverride("separation", 0);
        var name = new Label { Text = isBottom ? "本地玩家" : "对手" };
        name.AddThemeFontSizeOverride("font_size", 16);
        name.AddThemeColorOverride("font_color", UiStyle.Text);
        identity.AddChild(name);
        var id = new Label { Text = "身份等待中" };
        UiStyle.SetMuted(id, 12);
        identity.AddChild(id);
        row.AddChild(identity);

        var clock = CreateClockLabel();
        clock.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        clock.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(clock);

        if (isBottom)
        {
            _bottomAvatar = avatar;
            _bottomAvatarInitial = initial;
            _bottomPlayerName = name;
            _bottomPlayerId = id;
            _bottomClock = clock;
        }
        else
        {
            _topAvatar = avatar;
            _topAvatarInitial = initial;
            _topPlayerName = name;
            _topPlayerId = id;
            _topClock = clock;
        }
        return row;
    }

    private void ApplyPlayerProfiles()
    {
        if (_playerProfiles is null)
        {
            if (_ai is not null || _aiConfiguration is not null)
            {
                _topPlayerName.Text = _aiEngine?.DisplayName ?? "AI 引擎";
                _bottomPlayerName.Text = "本地玩家";
                _topPlayerId.Text = "UCI 象棋引擎";
                _bottomPlayerId.Text = $"玩家执{SideName(_localSide ?? PieceSide.Red)}";
                return;
            }
            _topPlayerName.Text = _transport is null ? "黑方" : "联机对手";
            _bottomPlayerName.Text = _transport is null ? "红方" : "本地玩家";
            _topPlayerId.Text = _transport is null ? "本地双人" : "临时联机身份";
            _bottomPlayerId.Text = _transport is null ? "本地双人" : "临时联机身份";
            return;
        }
        ApplyPlayerProfile(_bottomAvatar, _bottomAvatarInitial,
            _bottomPlayerName, _bottomPlayerId, _playerProfiles.Local);
        ApplyPlayerProfile(_topAvatar, _topAvatarInitial,
            _topPlayerName, _topPlayerId, _playerProfiles.Opponent);
        UpdatePlayerSideLabels();
    }

    private void ApplyPlayerProfile(
        TextureRect avatarView, Label initialView, Label nameView, Label idView,
        PlayerProfileSnapshot profile)
    {
        if (avatarView.Texture is ImageTexture oldTexture && _profileTextures.Remove(oldTexture))
            oldTexture.Dispose();
        avatarView.Texture = null;
        initialView.Visible = true;
        nameView.Text = profile.DisplayName;
        idView.Text = profile.Platform == "Steam" ? "Steam 昵称" : profile.Platform;
        initialView.Text = FirstTextElement(profile.DisplayName);
        if (profile.Avatar is null) return;
        var image = Image.CreateFromData(
            profile.Avatar.Width, profile.Avatar.Height, false,
            Image.Format.Rgba8, profile.Avatar.CopyRgba());
        var texture = ImageTexture.CreateFromImage(image);
        image.Dispose();
        avatarView.Texture = texture;
        initialView.Visible = false;
        _profileTextures.Add(texture);
    }

    private void OnPlayerProfileReceived(PlayerProfileCommand command)
    {
        if (_profileResourcesReleased || _playerProfiles is null) return;
        try
        {
            _playerProfiles.ReplaceOpponent(CustomPlayerProfileFactory.FromCommand(command));
            ApplyPlayerProfile(_topAvatar, _topAvatarInitial,
                _topPlayerName, _topPlayerId, _playerProfiles.Opponent);
            UpdatePlayerSideLabels();
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
        if (_localSide is null) return;
        var topSide = _localSide.Value.Opponent();
        _topPlayerId.TooltipText = $"{SideName(topSide)} · {_topPlayerId.Text}";
        _bottomPlayerId.TooltipText = $"{SideName(_localSide.Value)} · {_bottomPlayerId.Text}";
    }

    private void ReleaseProfileResources()
    {
        if (_profileResourcesReleased) return;
        _profileResourcesReleased = true;
        _topAvatar.Texture = null;
        _bottomAvatar.Texture = null;
        foreach (var texture in _profileTextures) texture.Dispose();
        _profileTextures.Clear();
        _playerProfiles?.Dispose();
    }

    private void MarkProfilesReleased()
    {
        if (_playerProfiles is null) return;
        _topAvatarInitial.Visible = true;
        _bottomAvatarInitial.Visible = true;
        _topAvatarInitial.Text = "—";
        _bottomAvatarInitial.Text = "—";
        _topPlayerName.Text = "房间已解散";
        _bottomPlayerName.Text = "本地玩家";
        _topPlayerId.Text = "临时资料已释放";
        _bottomPlayerId.Text = "临时资料已释放";
    }

    private static string FirstTextElement(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "棋";
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(value.Trim());
        return enumerator.MoveNext() ? enumerator.GetTextElement() : "棋";
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
            _topPlayerName.Text = _aiEngine.DisplayName;
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
            await _ai!.PlayAiTurnAsync(_aiConfiguration!.Value.MoveTimeMilliseconds);
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
