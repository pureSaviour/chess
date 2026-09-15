using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.Jieqi;
using Xiangqi.Core.Application.Junqi;
using Xiangqi.Core.Application.Gomoku;
using Xiangqi.Core.Application.Go;
using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.Junqi;
using Xiangqi.Core.Domain.Gomoku;
using Xiangqi.Core.Domain.Go;
using Xiangqi.Core.Domain.Grid;
using Xiangqi.Presentation.AI;
using Xiangqi.Presentation.Networking;
using Xiangqi.Presentation.Profiles;
using Xiangqi.Presentation.Screens;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;
using Xiangqi.GameHost;
using Xiangqi.Presentation.GameModules;

namespace Xiangqi.Presentation;

/// <summary>
/// Application shell and screen router. Individual screens have no knowledge of one another;
/// they report navigation intentions here, keeping menu flow separate from match logic.
/// </summary>
public partial class Main : Control
{
    private AppSettings _settings = null!;
    private IBoardThemeCatalog _themes = null!;
    private EnginePackageInstaller _engineInstaller = null!;
    private Control? _currentScreen;
    private AcceptDialog _errorDialog = null!;
    private bool _debugNetworkSmoke;
    private bool _debugTimeoutSmoke;
#if GODOT_WINDOWS
    private SteamLobbyService? _steam;
#endif
    private string _steamUnavailableReason = "当前平台不支持 Steam 联机。";

    public override void _Ready()
    {
        AddChild(UiStyle.CreateBackground());
        _errorDialog = new AcceptDialog { Title = "提示" };
        AddChild(_errorDialog);
        _settings = AppSettings.Load();
        _themes = ThemePackageCatalog.CreateDefault();
        _engineInstaller = new EnginePackageInstaller(ProjectSettings.GlobalizePath("user://engines"));
        _settings.Apply();
        InitializeGameModuleUpdates();
        // Steam Overlay and its client APIs require an interactive display. Keeping headless
        // smoke/tests out of the native initialization path also makes server-side CI deterministic.
        if (!string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase) &&
            !(OS.IsDebugBuild() && OS.GetCmdlineUserArgs().Any(a => a is "--smoke-ui" or "--smoke-three" or "--smoke-feedback" or "--smoke-gomoku-ai" or "--smoke-engine-install-rapfi" or "--smoke-engine-install-pikafish")))
            InitializeSteam();
        if (TryStartDebugSmokeMode()) return;
        ShowMainMenu();
        ShowGameModuleActivationWarnings();
    }

    private void ShowMainMenu() => SwitchTo(new MainMenuScreen(
        ShowGallery,
        ShowSettings,
        () => GetTree().Quit()));

    private void ShowGallery()
    {
        var registrations = CreateAvailableGameModules();
        var catalog = new GameModuleCatalog(registrations);
        var ordered = catalog.Order(_settings.GameModuleOrder).Cast<GameModuleRegistration>().ToArray();
        var packageDriver = new GamePackageInstallDriver(new Version(1, 0),
            ProjectSettings.GlobalizePath("user://modules"));
        SwitchTo(new ModeGalleryScreen(ReturnFromGallery, ordered, MoveGameModule, packageDriver,
            CheckGameModuleUpdates));
    }

    private void MoveGameModule(GameModuleId id, int offset)
    {
        var catalog = new GameModuleCatalog(CreateAvailableGameModules());
        _settings.GameModuleOrder = [.. catalog.Move(_settings.GameModuleOrder, id, offset)];
        _settings.SaveAndApply();
        ShowGallery();
    }

    private void StartJieqiLocal(TimeControl timeControl)
    {
        Span<byte> bytes = stackalloc byte[8]; System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var seed = BitConverter.ToUInt64(bytes);
        SwitchTo(new JieqiGameScreen(_settings, _themes, seed, timeControl, ShowGallery,
            CreateOfflineProfiles("本地玩家 2")));
    }

    private void ShowJieqiRecords(bool saved) => SwitchTo(new JieqiRecordScreen(saved, ShowGallery,
        record => SwitchTo(new JieqiReplayScreen(_settings, _themes, record, () => ShowJieqiRecords(saved)))));

    private void ShowJieqiGallery(ClassicGalleryEntry entry) => SwitchTo(new GalleryScreen(
        ShowGallery, StartJieqiLocal, StartJieqiLanHost, StartJieqiLanClient,
        StartJieqiSteamInvite, entry, TwoPlayerGameMode.Jieqi));

    private void StartJieqiLanHost(int port, NetworkMatchConfiguration configuration)
    {
        try
        {
            var transport = EnetMatchTransport.Host(port);
            var controller = new NetworkJieqiMatchController(transport, configuration.HostSide,
                timeControl: configuration.TimeControl);
            SwitchTo(new JieqiGameScreen(_settings, _themes, controller,
                $"房主 · {GetPreferredLanAddress()}:{port}", ShowGallery, transport, CreateLanProfiles()));
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void StartJieqiLanClient(string address, int port)
    {
        try
        {
            var transport = EnetMatchTransport.Join(address, port);
            var controller = new NetworkJieqiMatchController(transport, null);
            SwitchTo(new JieqiGameScreen(_settings, _themes, controller,
                $"客户端 · {address}:{port}", ShowGallery, transport, CreateLanProfiles()));
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void StartJieqiSteamInvite(NetworkMatchConfiguration configuration) =>
        StartSteamInvite(configuration with { Mode = TwoPlayerGameMode.Jieqi });

    private void StartJunqiLocal(TimeControl timeControl) => SwitchTo(new JunqiGameScreen(
        new LocalJunqiMatchController(timeControl:timeControl),"本地对决",ShowGallery,
        profiles:CreateOfflineProfiles("本地玩家 2")));

    private void ShowJunqiRecords(bool saved) => SwitchTo(new JunqiRecordScreen(saved, ShowGallery,
        record => SwitchTo(new JunqiReplayScreen(record, () => ShowJunqiRecords(saved)))));

    private void ShowJunqiGallery(ClassicGalleryEntry entry) => SwitchTo(new GalleryScreen(
        ShowGallery, StartJunqiLocal, StartJunqiLanHost, StartJunqiLanClient,
        StartJunqiSteamInvite, entry, TwoPlayerGameMode.Junqi));

    private void StartJunqiLanHost(int port, NetworkMatchConfiguration configuration)
    {
        try
        {
            var transport = EnetMatchTransport.Host(port);
            var controller = new NetworkJunqiMatchController(transport, ToJunqiSide(configuration.HostSide),configuration.TimeControl);
            SwitchTo(new JunqiGameScreen(controller, $"房主 · {GetPreferredLanAddress()}:{port}", ShowGallery, transport, CreateLanProfiles()));
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void StartJunqiLanClient(string address, int port)
    {
        try
        {
            var transport = EnetMatchTransport.Join(address, port);
            var controller = new NetworkJunqiMatchController(transport, null);
            SwitchTo(new JunqiGameScreen(controller, $"客户端 · {address}:{port}", ShowGallery, transport, CreateLanProfiles()));
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void StartJunqiSteamInvite(NetworkMatchConfiguration configuration) =>
        StartSteamInvite(configuration with { Mode = TwoPlayerGameMode.Junqi });

    private static JunqiSide ToJunqiSide(PieceSide side) => side == PieceSide.Red ? JunqiSide.First : JunqiSide.Second;

    private void ShowGomokuGallery(ClassicGalleryEntry entry) => SwitchTo(new GomokuGalleryScreen(
        ShowGallery, StartGomokuLocal, StartGomokuLanHost, StartGomokuLanClient, StartGomokuSteamInvite, entry));
    private void StartGomokuLocal(GomokuRoomOptions options) => SwitchTo(new GomokuGameScreen(
        new LocalGomokuMatchController(options.BoardSize, options.Rules,options.TimeControl), "本地对决", ShowGallery,
        profiles: CreateOfflineProfiles("本地玩家 2")));
    private void StartGomokuLanHost(int port, GomokuRoomOptions options)
    {
        try { var transport=EnetMatchTransport.Host(port); SwitchTo(new GomokuGameScreen(
            new NetworkGomokuMatchController(transport,options), $"房主 · {GetPreferredLanAddress()}:{port}", ShowGallery, transport, CreateLanProfiles())); }
        catch(Exception error){ShowError(error.Message);}
    }
    private void StartGomokuLanClient(string address,int port)
    {
        try { var transport=EnetMatchTransport.Join(address,port); SwitchTo(new GomokuGameScreen(
            new NetworkGomokuMatchController(transport,null), $"客户端 · {address}:{port}", ShowGallery, transport, CreateLanProfiles())); }
        catch(Exception error){ShowError(error.Message);}
    }
    private void StartGomokuSteamInvite(GomokuRoomOptions options) => StartSteamInvite(new NetworkMatchConfiguration(
        options.TimeControl, options.HostColor==StoneColor.Black?PieceSide.Red:PieceSide.Black,
        TwoPlayerGameMode.Gomoku, options.BoardSize, (int)options.Rules));
    private void ShowGomokuRecords(bool saved)=>SwitchTo(new GomokuRecordScreen(saved,ShowGallery,
        record=>SwitchTo(new GomokuReplayScreen(_settings,record,()=>ShowGomokuRecords(saved),
            InstalledEnginePath(EnginePackageKind.Rapfi)))));
    private void ShowGomokuImport(bool position)=>SwitchTo(new GomokuImportScreen(position,ShowGallery,
        record=>SwitchTo(new GomokuReplayScreen(_settings,record,ShowGallery,
            InstalledEnginePath(EnginePackageKind.Rapfi)))));
    private void ShowGomokuAiSettings()=>SwitchTo(new GomokuAiSettingsScreen(_settings,_engineInstaller,saved=>{_settings=saved;_settings.SaveAndApply();ShowGallery();},ShowGallery));
    private void StartConfiguredGomokuAiMatch()
    {
#if GODOT_ANDROID
        ShowError("Android 当前不能启动外部 Rapfi 进程；移动端原生引擎尚未接入。");
#else
        var enginePath=InstalledEnginePath(EnginePackageKind.Rapfi);
        if(enginePath is null){ShowError("请先在五子棋人机设置中一键配置 Rapfi。");return;}
        var human=_settings.GomokuAiSideChoice switch{1=>StoneColor.White,2=>Random.Shared.Next(2)==0?StoneColor.Black:StoneColor.White,_=>StoneColor.Black};
        var rules=_settings.GomokuAiForbiddenMoves?GomokuRuleSet.RenjuForbiddenMoves:GomokuRuleSet.Freestyle;
        var engine=new PiskvorkProcessGomokuAiEngine(enginePath);
        var profile=AiDifficultyProfiles.Gomoku((AiDifficultyLevel)_settings.GomokuAiDifficulty,
            _settings.GomokuAiThinkMilliseconds,_settings.GomokuAiMaxDepth);
        var controller=new AiGomokuMatchController(engine,_settings.GomokuAiBoardSize,rules,human,
            profile.ThinkMilliseconds,profile.MaxDepth,profile.Strength,_settings.GomokuAiThreads,
            _settings.GomokuAiMemoryMegabytes,profile.UseDeterministicLegalMove,
            new TimeControl(_settings.AiGameSeconds,_settings.AiMoveSeconds));
        SwitchTo(new GomokuGameScreen(controller,$"人机对决 · Rapfi · 玩家执{(human==StoneColor.Black?"黑":"白")}",ShowGallery,
            profiles:CreateOfflineProfiles("AI 引擎")));
#endif
    }

    private void ShowAiSettings() => SwitchTo(new AiSettingsScreen(_settings, saved =>
    {
        _settings = saved;
        _settings.SaveAndApply();
        ShowGallery();
    }, ShowGallery, _engineInstaller));

    private void StartConfiguredAiMatch()
    {
        var enginePath = InstalledEnginePath(EnginePackageKind.Pikafish);
        if (enginePath is null) { ShowError("请先在人机设置中一键配置皮卡鱼。"); return; }
        var side = _settings.AiSideChoice switch
        {
            1 => PieceSide.Black,
            2 => Random.Shared.Next(2) == 0 ? PieceSide.Red : PieceSide.Black,
            _ => PieceSide.Red
        };
        var profile = AiDifficultyProfiles.Xiangqi((AiDifficultyLevel)_settings.AiDifficulty,
            _settings.AiThinkMilliseconds, _settings.AiMaxDepth);
        StartAiMatch(new AiMatchConfiguration(
            new TimeControl(_settings.AiGameSeconds, _settings.AiMoveSeconds), side,
            profile.MoveTimeMilliseconds, enginePath, profile.MaxDepth,
            profile.VariationCount, profile.CandidateRank, profile.MaximumCentipawnLoss,
            profile.UseDeterministicLegalMove));
    }

    private void ShowClassicImport(bool puzzle) => SwitchTo(new ClassicImportScreen(
        puzzle, ShowGallery,
        record => SwitchTo(new ClassicReplayScreen(_settings, _themes, record, ShowGallery,
            InstalledEnginePath(EnginePackageKind.Pikafish))),
        session => SwitchTo(new GameScreen(_settings, ShowGallery, "残局", localTimeControl: TimeControl.Unlimited,
            themes: _themes, session: session))));

    private void ShowClassicRecords(bool saved) => SwitchTo(new ClassicRecordScreen(saved, ShowGallery,
        record => SwitchTo(new ClassicReplayScreen(_settings, _themes, record, () => ShowClassicRecords(saved),
            InstalledEnginePath(EnginePackageKind.Pikafish)))));

    private string? InstalledEnginePath(EnginePackageKind kind) =>
        _engineInstaller.TryGetInstalled(kind, out var installed) ? installed.ExecutablePath : null;

    private void ShowClassicGallery(ClassicGalleryEntry entry) => SwitchTo(new GalleryScreen(
        ShowGallery,
        StartLocalMatch,
        StartLanHost,
        StartLanClient,
        StartSteamInvite,
        entry));

    private void ShowSettings() => SwitchTo(new SettingsScreen(
        _settings,
        _themes,
        saved =>
        {
            _settings = saved;
            _settings.SaveAndApply();
            ShowMainMenu();
        },
        ShowMainMenu));

    private void StartLocalMatch(TimeControl timeControl)
    {
        CancelWaitingSteamLobby();
        SwitchTo(new GameScreen(_settings, ShowGallery, localTimeControl: timeControl,
            playerProfiles: CreateOfflineProfiles("本地玩家 2"), themes: _themes));
    }

    private void StartAiMatch(AiMatchConfiguration configuration)
    {
        CancelWaitingSteamLobby();
        try
        {
            configuration = configuration.Validate();
            if (!System.IO.File.Exists(configuration.EnginePath))
                throw new FileNotFoundException("找不到所选 AI 引擎程序。", configuration.EnginePath);
            var engine = new UciProcessAiEngine(configuration.EnginePath,
                new UciEngineResourceOptions(_settings.AiThreads, _settings.AiHashMegabytes));
            SwitchTo(new GameScreen(
                _settings,
                ShowGallery,
                "人机对决",
                playerProfiles: CreateOfflineProfiles("AI 引擎"),
                aiConfiguration: configuration,
                aiEngine: engine,
                themes: _themes));
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private void StartLanHost(int port, NetworkMatchConfiguration configuration)
    {
        CancelWaitingSteamLobby();
        try
        {
            var transport = EnetMatchTransport.Host(port);
            var address = GetPreferredLanAddress();
            SwitchTo(new GameScreen(
                _settings,
                ShowGallery,
                $"房主 · {address}:{port}",
                transport,
                true,
                configuration,
                _debugNetworkSmoke,
                _debugTimeoutSmoke,
                CreateLanProfiles(),
                _settings.HasCustomProfile,
                themes: _themes));
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private void StartLanClient(string address, int port)
    {
        CancelWaitingSteamLobby();
        try
        {
            var transport = EnetMatchTransport.Join(address, port);
            SwitchTo(new GameScreen(
                _settings,
                ShowGallery,
                $"客户端 · {address}:{port}",
                transport,
                false,
                null,
                _debugNetworkSmoke,
                _debugTimeoutSmoke,
                CreateLanProfiles(),
                _settings.HasCustomProfile,
                themes: _themes));
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private void SwitchTo(Control screen)
    {
        if (_currentScreen is not null)
        {
            RemoveChild(_currentScreen);
            _currentScreen.QueueFree();
        }
        _currentScreen = screen;
        AddChild(screen);
    }

    private void ShowError(string message)
    {
        _errorDialog.DialogText = message;
        _errorDialog.PopupCentered(new Vector2I(480, 190));
    }

    private async void StartSteamInvite(NetworkMatchConfiguration configuration)
    {
#if GODOT_WINDOWS
        if (_steam is null)
        {
            ShowError(_steamUnavailableReason);
            return;
        }
        try
        {
            var lobby = await _steam.CreateInviteLobbyAsync(configuration);
            await _steam.OpenInviteOverlayAsync(lobby);
        }
        catch (OperationCanceledException)
        {
            // The player navigated away while Steam was still creating the asynchronous lobby.
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
#else
        await Task.CompletedTask;
        ShowError(_steamUnavailableReason);
#endif
    }

    private void InitializeSteam()
    {
#if GODOT_WINDOWS
        if (!SteamLobbyService.TryCreate(out var service, out _steamUnavailableReason) || service is null)
        {
            GD.Print($"[Xiangqi.Steam] {_steamUnavailableReason}");
            return;
        }
        _steam = service;
        InitializeThreeSteam();
        _steam.StatusChanged += OnSteamStatusChanged;
        _steam.LobbyJoinRequested += OnSteamLobbyJoinRequested;
        _steam.MatchReady += OnSteamMatchReady;
        AddChild(_steam);
#endif
    }

#if GODOT_WINDOWS
    private static void OnSteamStatusChanged(string status) => GD.Print($"[Xiangqi.Steam] {status}");

    private static void OnSteamLobbyJoinRequested(SteamLobbyId lobby) =>
        GD.Print($"[Xiangqi.Steam] 收到加入邀请：Lobby {lobby.Value}");

    private void OnSteamMatchReady(SteamMatchReady match)
    {
        var hasCustomProfile = _settings.HasCustomProfile;
        if (hasCustomProfile)
            match.Profiles.ReplaceLocal(CustomPlayerProfileFactory.ApplyOverrides(_settings, match.Profiles.Local));
        if (match.Configuration.Mode == TwoPlayerGameMode.Jieqi)
        {
            var controller = new NetworkJieqiMatchController(match.Transport, match.Configuration.HostSide,
                timeControl: match.Configuration.TimeControl);
            SwitchTo(new JieqiGameScreen(_settings, _themes, controller,
                $"Steam · 对手 {match.OpponentName}", ShowGallery, match.Transport, match.Profiles));
            return;
        }
        if (match.Configuration.Mode == TwoPlayerGameMode.Junqi)
        {
            var controller = new NetworkJunqiMatchController(match.Transport, ToJunqiSide(match.Configuration.HostSide),match.Configuration.TimeControl);
            SwitchTo(new JunqiGameScreen(controller, $"Steam · 对手 {match.OpponentName}", ShowGallery, match.Transport, match.Profiles));
            return;
        }
        if (match.Configuration.Mode == TwoPlayerGameMode.Gomoku)
        {
            var options = new GomokuRoomOptions(match.Configuration.BoardSize,
                (GomokuRuleSet)match.Configuration.RuleFlags,
                match.Configuration.HostSide == PieceSide.Red ? StoneColor.Black : StoneColor.White,match.Configuration.TimeControl);
            var controller = new NetworkGomokuMatchController(match.Transport, match.IsHost ? options : null);
            SwitchTo(new GomokuGameScreen(controller, $"Steam · 对手 {match.OpponentName}", ShowGallery, match.Transport, match.Profiles));
            return;
        }
        if (match.Configuration.Mode == TwoPlayerGameMode.Go)
        {
            var options = new GoRoomOptions(match.Configuration.HostSide == PieceSide.Red
                ? GoStone.Black : GoStone.White,match.Configuration.TimeControl);
            var controller = new NetworkGoMatchController(match.Transport, match.IsHost ? options : null);
            SwitchTo(new GoGameScreen(controller, $"Steam · 对手 {match.OpponentName}", ShowGallery,
                match.Transport, match.Profiles));
            return;
        }
        SwitchTo(new GameScreen(
            _settings,
            ShowGallery,
            $"Steam · 对手 {match.OpponentName}",
            match.Transport,
            match.IsHost,
            match.IsHost ? match.Configuration : null,
            _debugNetworkSmoke,
            _debugTimeoutSmoke,
            match.Profiles,
            hasCustomProfile,
            themes: _themes));
    }
#endif

    private void ReturnFromGallery()
    {
#if GODOT_WINDOWS
        _threeSteam?.Cancel();
        _steam?.CancelCurrentLobby();
#endif
        ShowMainMenu();
    }

    private void CancelWaitingSteamLobby()
    {
#if GODOT_WINDOWS
        _threeSteam?.Cancel();
        _steam?.CancelCurrentLobby();
#endif
    }

    public override void _ExitTree()
    {
        _engineInstaller?.Dispose();
#if GODOT_WINDOWS
        _threeSteam?.Dispose();
        if (_steam is null) return;
        _steam.StatusChanged -= OnSteamStatusChanged;
        _steam.LobbyJoinRequested -= OnSteamLobbyJoinRequested;
        _steam.MatchReady -= OnSteamMatchReady;
        _ = _steam.DisposeAsync();
#endif
    }

    private static string GetPreferredLanAddress()
    {
        var addresses = IP.GetLocalAddresses();
        return addresses.FirstOrDefault(address => address.Contains('.') &&
                   !address.StartsWith("127.") && !address.StartsWith("169.254."))
               ?? "本机局域网IP";
    }

    private bool TryStartDebugSmokeMode()
    {
        if (!OS.IsDebugBuild()) return false;
        var arguments = OS.GetCmdlineUserArgs();
        if (arguments.Contains("--smoke-three"))
        {
            SwitchTo(new Diagnostics.ThreeBoardSmoke(_settings));
            return true;
        }
        if (arguments.Contains("--smoke-feedback"))
        {
            SwitchTo(new Diagnostics.BoardFeedbackSmoke());
            return true;
        }
#if GODOT_WINDOWS
        if (arguments.Contains("--smoke-engine-install-rapfi") || arguments.Contains("--smoke-engine-install-pikafish"))
        {
            _ = RunEngineInstallSmokeAsync(arguments.Contains("--smoke-engine-install-rapfi")
                ? EnginePackageKind.Rapfi : EnginePackageKind.Pikafish);
            return true;
        }
        if (arguments.Contains("--smoke-gomoku-ai"))
        {
            _ = RunGomokuAiSmokeAsync();
            return true;
        }
        if (arguments.Contains("--smoke-steam-init"))
        {
            if (_steam is null)
            {
                GD.PushError($"[Xiangqi.Steam.Smoke] {_steamUnavailableReason}");
                GetTree().Quit(2);
            }
            else
            {
                GD.Print($"[Xiangqi.Steam.Smoke] OK AppID={_steam.ActiveAppId} User={_steam.PersonaName}");
                GetTree().Quit();
            }
            return true;
        }
        if (arguments.Contains("--smoke-steam-lobby"))
        {
            if (_steam is null)
            {
                GD.PushError($"[Xiangqi.Steam.LobbySmoke] {_steamUnavailableReason}");
                GetTree().Quit(2);
            }
            else
            {
                _ = RunSteamLobbySmokeAsync();
            }
            return true;
        }
#endif
        if (arguments.Contains("--smoke-ui"))
        {
            _ = RunUiSmokeAsync(arguments.Contains("--capture-ui"));
            return true;
        }
        if (arguments.Contains("--smoke-profile-ui"))
        {
            var profiles = new MatchPlayerProfiles(
                new PlayerProfileSnapshot("测试", "local-001", "本地玩家",
                    CreateSmokeAvatar(142, 59, 53)),
                new PlayerProfileSnapshot("测试", "remote-002", "远端玩家",
                    CreateSmokeAvatar(52, 55, 61)));
            SwitchTo(new GameScreen(_settings, ShowGallery, playerProfiles: profiles, themes: _themes));
            GetTree().CreateTimer(0.3).Timeout += () =>
            {
                GD.Print("[Xiangqi.Profile.UI.Smoke] Player cards and temporary avatar textures instantiated.");
                GetTree().Quit();
            };
            return true;
        }
        if (arguments.Contains("--smoke-lan-host"))
        {
            _debugNetworkSmoke = true;
            StartLanHost(EnetMatchTransport.DefaultPort,
                new NetworkMatchConfiguration(TimeControl.Unlimited, PieceSide.Red));
            return true;
        }
        if (arguments.Contains("--smoke-timeout-host"))
        {
            _debugTimeoutSmoke = true;
            StartLanHost(EnetMatchTransport.DefaultPort,
                new NetworkMatchConfiguration(new TimeControl(0, 1), PieceSide.Red));
            return true;
        }
        if (arguments.Contains("--smoke-lan-client"))
        {
            _debugNetworkSmoke = true;
            StartLanClient("127.0.0.1", EnetMatchTransport.DefaultPort);
            return true;
        }
        if (arguments.Contains("--smoke-timeout-client"))
        {
            _debugTimeoutSmoke = true;
            StartLanClient("127.0.0.1", EnetMatchTransport.DefaultPort);
            return true;
        }
        return false;
    }

    private async Task RunUiSmokeAsync(bool capture)
    {
        try
        {
            var profileSettings = _settings.Copy();
            profileSettings.CustomPlayerId = "烟测昵称";
            profileSettings.CustomAvatarPath = string.Empty;
            var fallbackProfile = new PlayerProfileSnapshot("测试", "local-smoke", "Steam 默认",
                CreateSmokeAvatar(142, 59, 53));
            var mergedProfile = CustomPlayerProfileFactory.ApplyOverrides(profileSettings, fallbackProfile);
            if (mergedProfile.DisplayName != "烟测昵称" ||
                !ReferenceEquals(mergedProfile.Avatar, fallbackProfile.Avatar))
                throw new InvalidOperationException("Custom nickname must retain the platform default avatar.");
            mergedProfile.Avatar?.Dispose();
            ShowGallery();
            await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);
            var smokeModules = CreateAvailableGameModules();
            if (_currentScreen is not ModeGalleryScreen gallery || !gallery.HasPackageInstaller ||
                gallery.CardCount != smokeModules.Count ||
                gallery.OptionCount != smokeModules.Sum(module => module.Routes.Count))
                throw new InvalidOperationException("Gallery module cards/routes do not match the validated module catalog.");
            var declarative = CreateDeclarativeUiSmokeModule();
            var declarativeRegistration = new GameModuleRegistration(declarative.PackageManifest.Module,
                declarative.PackageManifest.Routes.Select(route =>
                    (route, (Action)(() => ShowDeclarativeGame(declarative)))),
                declarative.CardImage, declarative.CardImageExtension);
            var extended = smokeModules.Append(declarativeRegistration).ToArray();
            SwitchTo(new ModeGalleryScreen(ShowMainMenu, extended, (_, _) => { },
                new GamePackageInstallDriver(new Version(1, 0),
                    ProjectSettings.GlobalizePath("user://modules")), () => { }));
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            if (_currentScreen is not ModeGalleryScreen
                { CardCount: var cardCount, OptionCount: var optionCount } ||
                cardCount != extended.Length || optionCount != extended.Sum(module => module.Routes.Count))
                throw new InvalidOperationException("Declarative module card failed to materialize.");
            ShowDeclarativeGame(declarative);
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            if (_currentScreen is not DeclarativeLineGameScreen)
                throw new InvalidOperationException("Declarative line-placement screen failed to instantiate.");
            if (capture && !string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase))
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var directory = ProjectSettings.GlobalizePath("res://build/ui-qa");
                System.IO.Directory.CreateDirectory(directory);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(System.IO.Path.Combine(directory, "mode-gallery.png")) != Error.Ok)
                    throw new IOException("Gallery capture failed.");
            }
            ShowAiSettings();
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            if (_currentScreen is not AiSettingsScreen { AreAdvancedSettingsVisible: false, HasManualEnginePicker: false, HasReplayAnalysisTimeControl: true })
                throw new InvalidOperationException("Classic AI settings must be collapsed and reject manual engine selection.");
            ShowGomokuAiSettings();
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            if (_currentScreen is not GomokuAiSettingsScreen { AreAdvancedSettingsVisible: false, HasManualEnginePicker: false, HasReplayAnalysisTimeControl: true })
                throw new InvalidOperationException("Gomoku AI settings must be collapsed and reject manual engine selection.");
            StartLanHost(EnetMatchTransport.DefaultPort,
                new NetworkMatchConfiguration(TimeControl.Unlimited, PieceSide.Red));
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            var lanScreen = _currentScreen as GameScreen;
            if (lanScreen is null || !lanScreen.IsMessagePanelFullyVisible)
                throw new InvalidOperationException($"Classic LAN message panel is outside the viewport: {lanScreen?.MessagePanelBounds ?? "screen missing"}.");
            if (capture && !string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase))
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var directory = ProjectSettings.GlobalizePath("res://build/ui-qa");
                System.IO.Directory.CreateDirectory(directory);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(System.IO.Path.Combine(directory, "classic-lan.png")) != Error.Ok)
                    throw new IOException("Classic LAN capture failed.");
            }
            StartJieqiLanHost(EnetMatchTransport.DefaultPort + 1,
                new NetworkMatchConfiguration(TimeControl.Unlimited, PieceSide.Red,
                    TwoPlayerGameMode.Jieqi));
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            if (_currentScreen is not JieqiGameScreen { HasClassicTwoPlayerRoomLayout: true, HasNetworkChat: true })
                throw new InvalidOperationException("Jieqi network room must expose classic player strips and two-player chat.");
            SwitchTo(new JieqiReplayScreen(_settings, _themes,
                new JieqiGameRecord(1, [], new Dictionary<string, string>()), ShowGallery));
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            if (_currentScreen is not JieqiReplayScreen { AreBottomControlsFullyVisible: true })
                throw new InvalidOperationException("Jieqi replay controls are outside the viewport.");
            var gomokuController = new LocalGomokuMatchController(19, GomokuRuleSet.RenjuForbiddenMoves);
            SwitchTo(new GomokuGameScreen(gomokuController, "UI smoke", ShowGallery,
                profiles: CreateOfflineProfiles("本地玩家 2")));
            await gomokuController.SubmitAsync(new GridPoint(9, 9));
            await gomokuController.SubmitAsync(new GridPoint(9, 10));
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            if (_currentScreen is not GomokuGameScreen gomoku || !gomoku.HasUnifiedPlayerCards || gomoku.BoardAreaSize.X < 700 ||
                gomoku.AreMoveNumbersVisible)
                throw new InvalidOperationException("Gomoku local screen failed to instantiate.");
            gomoku.SetMoveNumbersForSmoke(true);
            if (!gomoku.AreMoveNumbersVisible)
                throw new InvalidOperationException("Gomoku in-game move-number setting did not update the board projection.");
            await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
            var goController = new LocalGoMatchController();
            SwitchTo(new GoGameScreen(goController, "UI smoke", ShowGallery,
                profiles: CreateOfflineProfiles("本地玩家 2")));
            await goController.SubmitAsync(GoActionKind.Place, new GridPoint(9, 9));
            await goController.SubmitAsync(GoActionKind.Place, new GridPoint(9, 10));
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            if (_currentScreen is not GoGameScreen go || !go.HasUnifiedPlayerCards || !go.AreCoordinatesVisible ||
                go.BoardAreaSize.X < 700 || go.AreMoveNumbersVisible || !go.HasLastMoveHighlight)
                throw new InvalidOperationException("Go local screen or coordinate labels failed to instantiate.");
            go.SetCoordinateLabelsForSmoke(false);
            go.SetMoveNumbersForSmoke(true);
            if (go.AreCoordinatesVisible)
                throw new InvalidOperationException("Go in-game coordinate setting did not update the board projection.");
            if (!go.AreMoveNumbersVisible)
                throw new InvalidOperationException("Go in-game move-number setting did not update the board projection.");
            go.ToggleScorePreviewForSmoke();
            if (!go.IsScorePreviewActive || !go.ScorePreviewText.Contains("非正式") ||
                !go.TogglePreviewDeadForSmoke(new GridPoint(9, 9)))
                throw new InvalidOperationException("Go manual score preview did not enter or toggle a dead chain.");
            await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
            await goController.SubmitAsync(GoActionKind.Place, new GridPoint(10, 9));
            await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
            if (go.IsScorePreviewActive || !go.ScorePreviewText.Contains("自动关闭") || !go.HasLastMoveHighlight)
                throw new InvalidOperationException("Go score preview stayed stale after an authoritative move.");
            StartGoLanHost(EnetMatchTransport.DefaultPort + 2, new GoRoomOptions(GoStone.Black));
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            if (_currentScreen is not GoGameScreen { IsChatFullyVisible: true } lanGo)
                throw new InvalidOperationException($"Go LAN chat panel is outside the viewport: {(_currentScreen as GoGameScreen)?.ChatBounds ?? "screen missing"}.");
            lanGo.ToggleScorePreviewForSmoke();
            await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
            if (!lanGo.IsScorePreviewActive || !lanGo.IsChatFullyVisible)
                throw new InvalidOperationException($"Go score preview obscured LAN chat: {lanGo.ChatBounds}.");
            StartJunqiLocal(TimeControl.Unlimited);
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            if (_currentScreen is not JunqiGameScreen { HasUnifiedPlayerCards: true } junqi)
                throw new InvalidOperationException("Junqi local screen failed to instantiate.");
            if (capture && !string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase))
            {
                junqi.RevealForSmoke();
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var directory = ProjectSettings.GlobalizePath("res://build/ui-qa");
                System.IO.Directory.CreateDirectory(directory);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(System.IO.Path.Combine(directory, "junqi-board.png")) != Error.Ok)
                    throw new IOException("Junqi capture failed.");
            }
            ShowSettings();
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            var smokeSettings = _settings.Copy();
            smokeSettings.BoardSkin = BoardSkinKind.Spring;
            smokeSettings.ShowBoardCoordinates = true;
            SwitchTo(new GameScreen(smokeSettings, ShowGallery, localTimeControl: TimeControl.Unlimited, themes: _themes));
            await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
            if (_currentScreen is not GameScreen { TrialActionText: "推演", HasCompactFunctionMenu: true, HasUnifiedPlayerCards: true })
                throw new InvalidOperationException("Classic match must use the compact function menu and unified player cards.");
            if (capture && !string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase))
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var directory = ProjectSettings.GlobalizePath("res://build/ui-qa");
                System.IO.Directory.CreateDirectory(directory);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(System.IO.Path.Combine(directory, "classic-match.png")) != Error.Ok)
                    throw new IOException("Classic match capture failed.");
            }
            GD.Print("[Xiangqi.UI.Smoke] Validated module catalog, Junqi, Gomoku, Go, settings and spring-board local match instantiated.");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError($"[Xiangqi.UI.Smoke] FAIL {error}");
            GetTree().Quit(3);
        }
    }

#if GODOT_WINDOWS
    private async Task RunEngineInstallSmokeAsync(EnginePackageKind kind)
    {
        var root = Path.Combine(Path.GetTempPath(), $"xiangqi-engine-install-smoke-{Guid.NewGuid():N}");
        try
        {
            using var installer = new EnginePackageInstaller(root);
            var result = await installer.InstallAsync(kind);
            if (!File.Exists(result.ExecutablePath) || !Path.GetFullPath(result.ExecutablePath).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Installer returned an invalid executable path.");
            if (!installer.TryGetInstalled(kind, out var resolved) ||
                !Path.GetFullPath(resolved.ExecutablePath).Equals(Path.GetFullPath(result.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Pinned engine could not be resolved from the managed install directory.");
            if (kind == EnginePackageKind.Rapfi)
            {
                await using var engine = new PiskvorkProcessGomokuAiEngine(result.ExecutablePath);
                await engine.InitializeAsync(15, GomokuRuleSet.RenjuForbiddenMoves);
                var position = new GomokuGame(15, GomokuRuleSet.RenjuForbiddenMoves).Snapshot;
                var analysis = await engine.AnalyzeAsync(new GomokuAiSearchRequest(position, 500, 4, 65, 1, 256));
                if (!float.IsFinite(analysis.BlackWinRate) || analysis.BlackWinRate is < 0 or > 1 ||
                    analysis.BestMove is not { } bestMove || bestMove.Column < 0 || bestMove.Row < 0)
                    throw new InvalidOperationException("Rapfi returned an invalid analysis or best move.");
                _ = await engine.FindMoveAsync(new GomokuAiSearchRequest(position, 500, 4, 65, 1, 256));
            }
            else
            {
                var engine = new UciProcessAiEngine(result.ExecutablePath); AddChild(engine);
                try
                {
                    await engine.InitializeAsync();
                    var search = await engine.SearchAsync(new GameSession().Snapshot, new AiSearchRequest(250, 2, 4));
                    if (search.BestMove == default) throw new InvalidOperationException("Pikafish returned no move.");
                }
                finally { await engine.DisposeAsync(); engine.QueueFree(); }
            }
            GD.Print($"[Xiangqi.EngineInstall.Smoke] PASS {kind} {result.Version} {Path.GetFileName(result.ExecutablePath)}");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError($"[Xiangqi.EngineInstall.Smoke] FAIL {kind} {error}"); GetTree().Quit(3); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private async Task RunGomokuAiSmokeAsync()
    {
        var path = System.Environment.GetEnvironmentVariable("XIANGQI_GOMOKU_AI_PATH");
        if (string.IsNullOrWhiteSpace(path)) { GD.PushError("[Xiangqi.Gomoku.AI.Smoke] XIANGQI_GOMOKU_AI_PATH is missing."); GetTree().Quit(2); return; }
        await using var controller = new AiGomokuMatchController(new PiskvorkProcessGomokuAiEngine(path),
            15, GomokuRuleSet.RenjuForbiddenMoves, StoneColor.Black, 1000);
        try
        {
            await controller.InitializeAsync();
            var human = await controller.SubmitAsync(new GridPoint(7, 7));
            if (!human.Accepted) throw new InvalidOperationException("Human smoke move was rejected.");
            var deadline = Time.GetTicksMsec() + 15_000;
            while (controller.Snapshot.PlyNumber < 2 && Time.GetTicksMsec() < deadline)
            { await ToSignal(GetTree().CreateTimer(0.02), SceneTreeTimer.SignalName.Timeout); controller.Poll(); }
            if (controller.Snapshot.PlyNumber != 2 || controller.Snapshot.LastMove is not { } move || move.Point == new GridPoint(7, 7))
                throw new InvalidOperationException("Rapfi did not return one legal reply.");
            GD.Print($"[Xiangqi.Gomoku.AI.Smoke] PASS reply={move.Point.Column},{move.Point.Row}");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError($"[Xiangqi.Gomoku.AI.Smoke] FAIL {error}"); GetTree().Quit(3); }
    }

    private async Task RunSteamLobbySmokeAsync()
    {
        try
        {
            var configuration = new NetworkMatchConfiguration(
                new TimeControl(1200, 60), PieceSide.Red);
            var firstLobby = await _steam!.CreateInviteLobbyAsync(configuration);
            _steam.CancelCurrentLobby();
            // Let the first Steam CallResult callback unwind before reusing it. In the real UI
            // the next button press necessarily occurs on a later frame as well.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var secondLobby = await _steam.CreateInviteLobbyAsync(configuration);
            GD.Print($"[Xiangqi.Steam.LobbySmoke] OK First={firstLobby.Value} " +
                     $"Recreated={secondLobby.Value} AppID={_steam.ActiveAppId}");
            _steam.CancelCurrentLobby();
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError($"[Xiangqi.Steam.LobbySmoke] {error.Message}");
            GetTree().Quit(3);
        }
    }
#endif

    private static PlayerAvatarPixels CreateSmokeAvatar(byte red, byte green, byte blue)
    {
        const int size = 8;
        var rgba = new byte[size * size * 4];
        for (var index = 0; index < rgba.Length; index += 4)
        {
            rgba[index] = red;
            rgba[index + 1] = green;
            rgba[index + 2] = blue;
            rgba[index + 3] = 255;
        }
        return new PlayerAvatarPixels(size, size, rgba);
    }

    private MatchPlayerProfiles CreateLanProfiles()
    {
        var profiles = new MatchPlayerProfiles(CreateDefaultLocalProfile(),
            new PlayerProfileSnapshot("联机", "远端棋手", "联机对手"));
        if (_settings.HasCustomProfile)
            profiles.ReplaceLocal(CustomPlayerProfileFactory.ApplyOverrides(_settings, profiles.Local));
        return profiles;
    }

    private MatchPlayerProfiles CreateOfflineProfiles(string opponentName)
    {
        var profiles = new MatchPlayerProfiles(CreateDefaultLocalProfile(),
            new PlayerProfileSnapshot("本地", opponentName, opponentName));
        if (_settings.HasCustomProfile)
            profiles.ReplaceLocal(CustomPlayerProfileFactory.ApplyOverrides(_settings, profiles.Local));
        return profiles;
    }

    /// <summary>
    /// Returns a caller-owned local profile. Steam identity is the default when available; the
    /// match composition path may then apply independently configured nickname/avatar overrides.
    /// </summary>
    private PlayerProfileSnapshot CreateDefaultLocalProfile(string fallbackName = "本地玩家")
    {
#if GODOT_WINDOWS
        if (_steam is not null && _steam.TryGetLocalPlayerProfile(out var steamProfile)) return steamProfile;
#endif
        return new PlayerProfileSnapshot("本地", "本地棋手", fallbackName);
    }
}
