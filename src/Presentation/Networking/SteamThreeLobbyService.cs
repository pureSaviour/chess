#if GODOT_WINDOWS
using Steamworks;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain.ThreeKingdoms;

namespace Xiangqi.Presentation.Networking;

/// <summary>
/// 三国 Lobby 路由器，复用 SteamLobbyService 唯一的 SteamAPI 生命周期与回调泵。
/// 只管理三席房间创建/进入；对局通信交给全连接适配器，不复用经典双人传输。
/// </summary>
public sealed class SteamThreeLobbyService : IDisposable
{
    public const string ModeKey = "xq_mode";
    public const string Mode = "three-kingdoms-v2";
    private readonly CallResult<LobbyCreated_t> _created;
    private TaskCompletionSource<ulong>? _creation;
    private TimeControl _time;
    private AdvisorConquestPolicy _policy;
    private bool _cancelled;
    private SteamThreeRoomTransport? _current;
    public event Action<SteamThreeRoomTransport, TimeControl, AdvisorConquestPolicy>? Ready;
    public event Action<string>? Error;
    public SteamThreeLobbyService() => _created = CallResult<LobbyCreated_t>.Create(OnCreated);
    public Task<ulong> Create(TimeControl time, AdvisorConquestPolicy policy)
    {
        if (_creation is not null) throw new InvalidOperationException("三人房间正在创建。");
        Cancel(); _time = time.Validate(); _policy = policy; _cancelled = false;
        _creation = new();
        _created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 3));
        return _creation.Task;
    }
    private void OnCreated(LobbyCreated_t callback, bool failed)
    {
        var task = _creation; _creation = null;
        var id = new CSteamID(callback.m_ulSteamIDLobby);
        if (_cancelled) { if (id.IsValid()) SteamMatchmaking.LeaveLobby(id); return; }
        if (failed || callback.m_eResult != EResult.k_EResultOK)
        { task?.TrySetException(new IOException("Steam 三席房间创建失败。")); return; }
        var ok = SteamMatchmaking.SetLobbyData(id, ModeKey, Mode) &&
            SteamMatchmaking.SetLobbyData(id, "xq3_game", _time.GameSeconds.ToString()) &&
            SteamMatchmaking.SetLobbyData(id, "xq3_step", _time.MoveSeconds.ToString()) &&
            SteamMatchmaking.SetLobbyData(id, "xq3_policy", ((int)_policy).ToString());
        if (!ok) { SteamMatchmaking.LeaveLobby(id); task?.TrySetException(new IOException("Steam 房间规则写入失败。")); return; }
        Enter(callback.m_ulSteamIDLobby);
        SteamFriends.ActivateGameOverlayInviteDialog(id);
        task?.TrySetResult(callback.m_ulSteamIDLobby);
    }
    public void Enter(ulong id)
    {
        if (_current is { IsDisposed: false } current && current.Lobby == id) return;
        var lobby = new CSteamID(id);
        if (SteamMatchmaking.GetLobbyData(lobby, ModeKey) != Mode) return;
        if (!int.TryParse(SteamMatchmaking.GetLobbyData(lobby, "xq3_game"), out var game) || game is < 0 or > 864000 ||
            !int.TryParse(SteamMatchmaking.GetLobbyData(lobby, "xq3_step"), out var step) || step is < 0 or > 86400 ||
            !int.TryParse(SteamMatchmaking.GetLobbyData(lobby, "xq3_policy"), out var policy) || policy is < 0 or > 255 || !Enum.IsDefined((AdvisorConquestPolicy)policy))
        { SteamMatchmaking.LeaveLobby(lobby); Error?.Invoke("三国房间规则无效或版本不兼容。"); return; }
        _current?.Dispose(); _current = new(id);
        Ready?.Invoke(_current, new(game, step), (AdvisorConquestPolicy)policy);
    }
    public void Cancel()
    {
        if (_creation is not null) { _cancelled = true; _creation.TrySetCanceled(); }
        _current?.Dispose(); _current = null;
    }
    public void Dispose() { Cancel(); _created.Dispose(); }
}
#endif
