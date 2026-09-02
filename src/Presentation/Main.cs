using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain;
using Xiangqi.Presentation.Networking;
using Xiangqi.Presentation.Profiles;
using Xiangqi.Presentation.Screens;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation;

/// <summary>
/// Application shell and screen router. Individual screens have no knowledge of one another;
/// they report navigation intentions here, keeping menu flow separate from match logic.
/// </summary>
public partial class Main : Control
{
    private AppSettings _settings = null!;
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
        _settings.Apply();
        // Steam Overlay and its client APIs require an interactive display. Keeping headless
        // smoke/tests out of the native initialization path also makes server-side CI deterministic.
        if (!string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase))
            InitializeSteam();
        if (TryStartDebugSmokeMode()) return;
        ShowMainMenu();
    }

    private void ShowMainMenu() => SwitchTo(new MainMenuScreen(
        ShowGallery,
        ShowSettings,
        () => GetTree().Quit()));

    private void ShowGallery() => SwitchTo(new GalleryScreen(
        ReturnFromGallery,
        StartLocalMatch,
        StartLanHost,
        StartLanClient,
        StartSteamInvite));

    private void ShowSettings() => SwitchTo(new SettingsScreen(
        _settings,
        saved =>
        {
            _settings = saved;
            _settings.SaveAndApply();
            ShowMainMenu();
        },
        ShowMainMenu));

    private void StartLocalMatch()
    {
        CancelWaitingSteamLobby();
        SwitchTo(new GameScreen(_settings, ShowGallery));
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
                _settings.HasCustomProfile));
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
                _settings.HasCustomProfile));
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
        var custom = CustomPlayerProfileFactory.Create(_settings, match.Profiles.Local.DisplayName);
        if (custom is not null) match.Profiles.ReplaceLocal(custom);
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
            custom is not null));
    }
#endif

    private void ReturnFromGallery()
    {
#if GODOT_WINDOWS
        _steam?.CancelCurrentLobby();
#endif
        ShowMainMenu();
    }

    private void CancelWaitingSteamLobby()
    {
#if GODOT_WINDOWS
        _steam?.CancelCurrentLobby();
#endif
    }

    public override void _ExitTree()
    {
#if GODOT_WINDOWS
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
#if GODOT_WINDOWS
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
            ShowGallery();
            GetTree().CreateTimer(0.15).Timeout += () =>
            {
                ShowSettings();
                GetTree().CreateTimer(0.15).Timeout += () =>
                {
                    StartLocalMatch();
                    GetTree().CreateTimer(0.25).Timeout += () =>
                    {
                        GD.Print("[Xiangqi.UI.Smoke] Gallery, settings and local match instantiated.");
                        GetTree().Quit();
                    };
                };
            };
            return true;
        }
        if (arguments.Contains("--smoke-profile-ui"))
        {
            var profiles = new MatchPlayerProfiles(
                new PlayerProfileSnapshot("测试", "local-001", "本地玩家",
                    CreateSmokeAvatar(142, 59, 53)),
                new PlayerProfileSnapshot("测试", "remote-002", "远端玩家",
                    CreateSmokeAvatar(52, 55, 61)));
            SwitchTo(new GameScreen(_settings, ShowGallery, playerProfiles: profiles));
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

#if GODOT_WINDOWS
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
        var local = new PlayerProfileSnapshot("本地", "本地棋手", "本地玩家");
        var custom = CustomPlayerProfileFactory.Create(_settings, local.DisplayName);
        return new MatchPlayerProfiles(custom ?? local,
            new PlayerProfileSnapshot("联机", "远端棋手", "联机对手"));
    }
}
