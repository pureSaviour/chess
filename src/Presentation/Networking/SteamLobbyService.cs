#if GODOT_WINDOWS
using System.Globalization;
using Godot;
using Steamworks;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain;
using Xiangqi.Presentation.Profiles;

namespace Xiangqi.Presentation.Networking;

public readonly record struct SteamMatchReady(
    SteamLobbyId Lobby,
    SteamMatchTransport Transport,
    bool IsHost,
    NetworkMatchConfiguration Configuration,
    string OpponentName,
    MatchPlayerProfiles Profiles);

/// <summary>
/// Process-wide Steam integration boundary. It alone initializes/shuts down SteamAPI, pumps
/// callbacks, manages a two-person friends-only lobby and translates lobby membership into an
/// <see cref="IMatchTransport"/>. UI code receives semantic status/match events and never calls
/// Steamworks directly, while rules and clocks remain entirely in the application/core layers.
/// </summary>
public partial class SteamLobbyService : Node, ISteamLobbyService
{
    public const uint DevelopmentAppId = 480;
    private const string ProductKey = "xq_product";
    private const string ProductValue = "csharp-xiangqi";
    private const string ProtocolKey = "xq_protocol";
    private const string GameSecondsKey = "xq_game_seconds";
    private const string MoveSecondsKey = "xq_move_seconds";
    private const string HostSideKey = "xq_host_side";

    private readonly Callback<GameLobbyJoinRequested_t> _joinRequestedCallback;
    private readonly Callback<LobbyEnter_t> _lobbyEnterCallback;
    private readonly Callback<LobbyChatUpdate_t> _lobbyChatUpdateCallback;
    private readonly Callback<AvatarImageLoaded_t> _avatarImageLoadedCallback;
    private readonly CallResult<LobbyCreated_t> _createLobbyResult;
    private readonly CallResult<LobbyEnter_t> _joinLobbyResult;
    private TaskCompletionSource<SteamLobbyId>? _createCompletion;
    private TaskCompletionSource<SteamLobbyId>? _joinCompletion;
    private bool _cancelCreateRequested;
    private NetworkMatchConfiguration? _hostConfiguration;
    private CSteamID? _currentLobby;
    private SteamMatchTransport? _activeTransport;
    private readonly Dictionary<ulong, PlayerProfileSnapshot> _profileCache = [];
    private double _avatarWaitSeconds;
    private bool _disposed;

    private SteamLobbyService()
    {
        ProcessMode = ProcessModeEnum.Always;
        _joinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
        _lobbyEnterCallback = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
        _lobbyChatUpdateCallback = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdated);
        _avatarImageLoadedCallback = Callback<AvatarImageLoaded_t>.Create(OnAvatarImageLoaded);
        _createLobbyResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        _joinLobbyResult = CallResult<LobbyEnter_t>.Create(OnJoinCompleted);
    }

    public event Action<SteamLobbyId>? LobbyJoinRequested;
    public event Action<SteamMatchReady>? MatchReady;
    public event Action<string>? StatusChanged;
    public bool IsAvailable => !_disposed;
    public string PersonaName => IsAvailable ? SteamFriends.GetPersonaName() : string.Empty;
    public uint ActiveAppId => IsAvailable ? (uint)SteamUtils.GetAppID() : 0;

    public static bool TryCreate(out SteamLobbyService? service, out string error)
    {
        service = null;
        error = string.Empty;
        try
        {
            EnsureDevelopmentAppIdFile();
            GD.Print("[Xiangqi.Steam] 正在检测 Steam 客户端……");
            if (!SteamAPI.IsSteamRunning())
            {
                error = "Steam 客户端未运行。请先登录 Steam，再重新启动游戏。";
                return false;
            }
            GD.Print("[Xiangqi.Steam] Steam 客户端已运行，正在初始化 SteamAPI……");
            var result = SteamAPI.InitEx(out var detail);
            if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
            {
                error = $"SteamAPI 初始化失败：{result}。{detail}";
                return false;
            }
            try
            {
                service = new SteamLobbyService();
                return true;
            }
            catch
            {
                SteamAPI.Shutdown();
                throw;
            }
        }
        catch (DllNotFoundException)
        {
            error = "未找到 steam_api64.dll。请确认 Windows x64 Steamworks 运行库随游戏发布。";
            return false;
        }
        catch (BadImageFormatException)
        {
            error = "steam_api64.dll 架构不匹配；当前游戏需要 Windows x64 版本。";
            return false;
        }
        catch (Exception exception)
        {
            error = $"Steam 初始化异常：{exception.Message}";
            return false;
        }
    }

    public override void _Ready()
    {
        StatusChanged?.Invoke(
            $"Steam 已连接：{PersonaName}（App ID {ActiveAppId}，当前测试使用 Spacewar 480）。");
        TryJoinLobbyFromCommandLine(OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).ToArray());
    }

    public override void _Process(double delta)
    {
        if (_disposed) return;
        SteamAPI.RunCallbacks();
        if (_currentLobby is not null && _activeTransport is null &&
            SteamMatchmaking.GetNumLobbyMembers(_currentLobby.Value) == 2)
        {
            _avatarWaitSeconds += delta;
            if (_avatarWaitSeconds >= 3) TryActivateMatch(allowAvatarFallback: true);
        }
    }

    public ValueTask<SteamLobbyId> CreateInviteLobbyAsync(
        NetworkMatchConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (_activeTransport is not null)
            throw new InvalidOperationException("Steam 对局进行中，不能创建新房间。");
        if (_createCompletion is not null)
            throw new InvalidOperationException("Steam 房间正在创建中。");
        // Closing an invite overlay does not emit a dedicated cancellation callback. Starting a
        // new room therefore replaces an unused waiting room instead of trapping the user in it.
        if (_currentLobby is not null) LeaveCurrentLobby();
        _hostConfiguration = configuration;
        _createCompletion = new TaskCompletionSource<SteamLobbyId>();
        _createLobbyResult.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2));
        StatusChanged?.Invoke("正在创建 Steam 好友房间……");
        return new ValueTask<SteamLobbyId>(_createCompletion.Task.WaitAsync(cancellationToken));
    }

    public ValueTask OpenInviteOverlayAsync(
        SteamLobbyId lobby, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        if (_currentLobby is not { } current || (ulong)current != lobby.Value)
            throw new InvalidOperationException("Steam 房间已失效。");
        SteamFriends.ActivateGameOverlayInviteDialog(current);
        StatusChanged?.Invoke(SteamUtils.IsOverlayEnabled()
            ? "已打开 Steam 好友邀请界面，等待好友加入。"
            : "已请求 Steam 邀请界面；若未显示，请确认游戏内 Steam Overlay 已启用。");
        return ValueTask.CompletedTask;
    }

    public async ValueTask JoinLobbyAsync(
        SteamLobbyId lobby, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (_activeTransport is not null) throw new InvalidOperationException("当前已有 Steam 对局。");
        if (_joinCompletion is not null) throw new InvalidOperationException("正在加入另一个 Steam 房间。");
        if (_currentLobby is not null) LeaveCurrentLobby();
        _joinCompletion = new TaskCompletionSource<SteamLobbyId>();
        _joinLobbyResult.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobby.Value)));
        StatusChanged?.Invoke("正在加入好友的 Steam 房间……");
        await _joinCompletion.Task.WaitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        CancelCurrentLobby();
        _joinRequestedCallback.Dispose();
        _lobbyEnterCallback.Dispose();
        _lobbyChatUpdateCallback.Dispose();
        _avatarImageLoadedCallback.Dispose();
        _createLobbyResult.Dispose();
        _joinLobbyResult.Dispose();
        SteamAPI.Shutdown();
        return ValueTask.CompletedTask;
    }

    public override void _ExitTree() => _ = DisposeAsync();

    public void CancelCurrentLobby()
    {
        if (_createCompletion is not null)
        {
            _cancelCreateRequested = true;
            _createCompletion.TrySetCanceled();
        }
        if (_activeTransport is { } transport)
        {
            transport.Closed -= OnTransportClosed;
            _activeTransport = null;
            _ = transport.DisposeAsync();
        }
        LeaveCurrentLobby();
    }

    private void OnLobbyCreated(LobbyCreated_t callback, bool ioFailure)
    {
        var completion = _createCompletion;
        _createCompletion = null;
        if (_cancelCreateRequested)
        {
            _cancelCreateRequested = false;
            if (!ioFailure && callback.m_eResult == EResult.k_EResultOK &&
                callback.m_ulSteamIDLobby != 0)
                SteamMatchmaking.LeaveLobby(new CSteamID(callback.m_ulSteamIDLobby));
            return;
        }
        if (completion is null) return;
        if (ioFailure || callback.m_eResult != EResult.k_EResultOK || callback.m_ulSteamIDLobby == 0)
        {
            completion.TrySetException(new InvalidOperationException(
                $"Steam 房间创建失败：{(ioFailure ? "网络 I/O 错误" : callback.m_eResult)}。"));
            _hostConfiguration = null;
            return;
        }

        var lobby = new CSteamID(callback.m_ulSteamIDLobby);
        _currentLobby = lobby;
        if (_hostConfiguration is not { } configuration || !WriteLobbyConfiguration(lobby, configuration))
        {
            SteamMatchmaking.LeaveLobby(lobby);
            _currentLobby = null;
            _hostConfiguration = null;
            completion.TrySetException(new InvalidOperationException("Steam 房间配置写入失败。"));
            return;
        }
        SteamMatchmaking.SetLobbyJoinable(lobby, true);
        completion.TrySetResult(new SteamLobbyId((ulong)lobby));
        StatusChanged?.Invoke($"Steam 好友房间已创建（Lobby {(ulong)lobby}）。");
        TryActivateMatch();
    }

    private void OnJoinRequested(GameLobbyJoinRequested_t callback)
    {
        var lobby = new SteamLobbyId((ulong)callback.m_steamIDLobby);
        LobbyJoinRequested?.Invoke(lobby);
        _ = JoinRequestedLobbyAsync(lobby);
    }

    private async Task JoinRequestedLobbyAsync(SteamLobbyId lobby)
    {
        try
        {
            await JoinLobbyAsync(lobby);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(exception.Message);
        }
    }

    private void OnJoinCompleted(LobbyEnter_t callback, bool ioFailure)
    {
        var completion = _joinCompletion;
        _joinCompletion = null;
        if (completion is null) return;
        if (ioFailure || callback.m_EChatRoomEnterResponse !=
            (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        {
            completion.TrySetException(new InvalidOperationException(
                $"加入 Steam 房间失败：响应 {callback.m_EChatRoomEnterResponse}。"));
            return;
        }
        completion.TrySetResult(new SteamLobbyId(callback.m_ulSteamIDLobby));
    }

    private void OnLobbyEntered(LobbyEnter_t callback)
    {
        if (callback.m_EChatRoomEnterResponse !=
            (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) return;
        var lobby = new CSteamID(callback.m_ulSteamIDLobby);
        _currentLobby = lobby;
        var isOwner = SteamMatchmaking.GetLobbyOwner(lobby) == SteamUser.GetSteamID();
        if (!isOwner && !TryReadLobbyConfiguration(lobby, out _))
        {
            SteamMatchmaking.LeaveLobby(lobby);
            _currentLobby = null;
            _joinCompletion?.TrySetException(new InvalidOperationException(
                "该 Spacewar 房间不是本项目创建的兼容中国象棋房间。"));
            _joinCompletion = null;
            StatusChanged?.Invoke("已拒绝不兼容的 Spacewar 房间。");
            return;
        }
        StatusChanged?.Invoke($"已进入 Steam 房间 {(ulong)lobby}。");
        TryActivateMatch();
    }

    private void OnLobbyChatUpdated(LobbyChatUpdate_t callback)
    {
        if (_currentLobby is not { } lobby || callback.m_ulSteamIDLobby != (ulong)lobby) return;
        var change = (EChatMemberStateChange)callback.m_rgfChatMemberStateChange;
        if ((change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
        {
            _avatarWaitSeconds = 0;
            TryActivateMatch();
            return;
        }
        if (_activeTransport is not null &&
            callback.m_ulSteamIDUserChanged == (ulong)_activeTransport.RemoteUser)
        {
            var transport = _activeTransport;
            transport.NotifyPeerLeft();
            _ = transport.DisposeAsync();
        }
    }

    private void TryActivateMatch(bool allowAvatarFallback = false)
    {
        if (_currentLobby is not { } lobby || _activeTransport is not null ||
            SteamMatchmaking.GetNumLobbyMembers(lobby) != 2) return;
        if (!TryReadLobbyConfiguration(lobby, out var configuration))
        {
            StatusChanged?.Invoke("Steam 房间配置缺失或版本不兼容。");
            return;
        }

        var localUser = SteamUser.GetSteamID();
        CSteamID remoteUser = CSteamID.Nil;
        for (var index = 0; index < 2; index++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, index);
            if (member != localUser) remoteUser = member;
        }
        if (!remoteUser.IsValid()) return;

        var isHost = SteamMatchmaking.GetLobbyOwner(lobby) == localUser;
        SteamFriends.RequestUserInformation(remoteUser, true);
        if (!TryGetPlayerProfile(localUser, allowAvatarFallback, out var localProfile) ||
            !TryGetPlayerProfile(remoteUser, allowAvatarFallback, out var opponentProfile))
        {
            StatusChanged?.Invoke("正在从 Steam 下载玩家头像……");
            return;
        }

        SteamMatchmaking.SetLobbyJoinable(lobby, false);
        _activeTransport = new SteamMatchTransport(remoteUser, isHost);
        _activeTransport.Closed += OnTransportClosed;
        var profiles = new MatchPlayerProfiles(localProfile, opponentProfile);
        _profileCache.Clear();
        MatchReady?.Invoke(new SteamMatchReady(
            new SteamLobbyId((ulong)lobby), _activeTransport, isHost, configuration,
            opponentProfile.DisplayName, profiles));
    }

    private void OnTransportClosed()
    {
        if (_activeTransport is not null) _activeTransport.Closed -= OnTransportClosed;
        _activeTransport = null;
        LeaveCurrentLobby();
    }

    private void OnAvatarImageLoaded(AvatarImageLoaded_t callback)
    {
        if (_currentLobby is null || _activeTransport is not null) return;
        if (_profileCache.Remove((ulong)callback.m_steamID, out var staleProfile))
            staleProfile.Avatar?.Dispose();
        TryActivateMatch();
    }

    private bool TryGetPlayerProfile(
        CSteamID user, bool allowFallback, out PlayerProfileSnapshot profile)
    {
        var userId = (ulong)user;
        if (_profileCache.TryGetValue(userId, out profile!)) return true;

        var imageHandle = SteamFriends.GetLargeFriendAvatar(user);
        if (imageHandle == -1 && !allowFallback)
        {
            profile = null!;
            return false;
        }
        if (imageHandle == -1) imageHandle = SteamFriends.GetMediumFriendAvatar(user);

        PlayerAvatarPixels? avatar = null;
        if (imageHandle > 0 && SteamUtils.GetImageSize(imageHandle, out var width, out var height) &&
            width is > 0 and <= 512 && height is > 0 and <= 512)
        {
            var byteCount = checked((int)(width * height * 4));
            var rgba = new byte[byteCount];
            if (SteamUtils.GetImageRGBA(imageHandle, rgba, rgba.Length))
                avatar = new PlayerAvatarPixels((int)width, (int)height, rgba);
        }

        var localUser = user == SteamUser.GetSteamID();
        var displayName = localUser
            ? SteamFriends.GetPersonaName()
            : SteamFriends.GetFriendPersonaName(user);
        profile = new PlayerProfileSnapshot(
            "Steam", userId.ToString(CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(displayName) ? userId.ToString(CultureInfo.InvariantCulture) : displayName,
            avatar);
        _profileCache[userId] = profile;
        return true;
    }

    private bool WriteLobbyConfiguration(CSteamID lobby, NetworkMatchConfiguration configuration) =>
        SteamMatchmaking.SetLobbyData(lobby, ProductKey, ProductValue) &&
        SteamMatchmaking.SetLobbyData(lobby, ProtocolKey,
            MatchWireProtocol.Version.ToString(CultureInfo.InvariantCulture)) &&
        SteamMatchmaking.SetLobbyData(lobby, GameSecondsKey,
            configuration.TimeControl.GameSeconds.ToString(CultureInfo.InvariantCulture)) &&
        SteamMatchmaking.SetLobbyData(lobby, MoveSecondsKey,
            configuration.TimeControl.MoveSeconds.ToString(CultureInfo.InvariantCulture)) &&
        SteamMatchmaking.SetLobbyData(lobby, HostSideKey,
            ((byte)configuration.HostSide).ToString(CultureInfo.InvariantCulture));

    private static bool TryReadLobbyConfiguration(
        CSteamID lobby, out NetworkMatchConfiguration configuration)
    {
        configuration = default;
        if (SteamMatchmaking.GetLobbyData(lobby, ProductKey) != ProductValue ||
            SteamMatchmaking.GetLobbyData(lobby, ProtocolKey) !=
            MatchWireProtocol.Version.ToString(CultureInfo.InvariantCulture) ||
            !int.TryParse(SteamMatchmaking.GetLobbyData(lobby, GameSecondsKey),
                NumberStyles.None, CultureInfo.InvariantCulture, out var gameSeconds) ||
            !int.TryParse(SteamMatchmaking.GetLobbyData(lobby, MoveSecondsKey),
                NumberStyles.None, CultureInfo.InvariantCulture, out var moveSeconds) ||
            !byte.TryParse(SteamMatchmaking.GetLobbyData(lobby, HostSideKey),
                NumberStyles.None, CultureInfo.InvariantCulture, out var hostSideValue)) return false;
        var hostSide = (PieceSide)hostSideValue;
        if (gameSeconds < 0 || moveSeconds < 0 || !Enum.IsDefined(hostSide)) return false;
        configuration = new NetworkMatchConfiguration(new TimeControl(gameSeconds, moveSeconds), hostSide);
        return true;
    }

    private void TryJoinLobbyFromCommandLine(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "+connect_lobby", StringComparison.OrdinalIgnoreCase) ||
                !ulong.TryParse(arguments[index + 1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var lobbyId)) continue;
            _ = JoinRequestedLobbyAsync(new SteamLobbyId(lobbyId));
            return;
        }
    }

    private void LeaveCurrentLobby()
    {
        if (_currentLobby is { } lobby) SteamMatchmaking.LeaveLobby(lobby);
        _currentLobby = null;
        _hostConfiguration = null;
        _avatarWaitSeconds = 0;
        foreach (var profile in _profileCache.Values) profile.Avatar?.Dispose();
        _profileCache.Clear();
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SteamLobbyService));
    }

    /// <summary>
    /// Valve resolves a development App ID from the process working directory, which is not the
    /// .NET data subfolder used by Godot exports. Debug builds therefore create the public Spacewar
    /// marker exactly where SteamAPI expects it. Release builds never create this file and must be
    /// launched by Steam with the product's real App ID context.
    /// </summary>
    private static void EnsureDevelopmentAppIdFile()
    {
        if (!OS.IsDebugBuild()) return;
        var path = Path.Combine(System.Environment.CurrentDirectory, "steam_appid.txt");
        if (!File.Exists(path))
            File.WriteAllText(path, DevelopmentAppId + System.Environment.NewLine);
    }
}
#endif
