#if GODOT_WINDOWS
using System.Runtime.InteropServices;
using Steamworks;
using Xiangqi.Core.Application.ThreeKingdoms;

namespace Xiangqi.Presentation.Networking;

/// <summary>
/// Steam 身份认证的三人消息全连接。独立频道不干扰经典象棋；满席后的 Steam ID 名册写入
/// Lobby 元数据并永久冻结，重连不能换席。P2P 心跳检测与 Lobby 所有者转交相互独立。
/// 仅本实例释放自己的频道/回调/Lobby，不关闭进程级 SteamAPI。
/// </summary>
public sealed class SteamThreeRoomTransport : IThreeRoomTransport
{
    private const int ChannelId = 48028;
    private readonly CSteamID _lobby;
    private readonly List<ThreePeer> _roster = [];
    private readonly HashSet<string> _connected = [];
    private readonly Dictionary<string, double> _lastSeen = [];
    private readonly Callback<SteamNetworkingMessagesSessionRequest_t> _request;
    private readonly CallResult<LobbyEnter_t> _rejoin;
    private readonly IntPtr[] _pointers = new IntPtr[16];
    private double _now, _maintenance;
    private bool _joining;
    private string? _authority;
    public bool IsDisposed { get; private set; }
    public ulong Lobby => (ulong)_lobby;
    public string LocalId { get; } = SteamUser.GetSteamID().m_SteamID.ToString();
    public IReadOnlyList<ThreePeer> Roster => _roster.AsReadOnly();
    public IReadOnlySet<string> Connected => _connected;
    public string Status => $"Steam 三人房间 {Lobby}";
    public event Action<string, byte[]>? Received;
    public SteamThreeRoomTransport(ulong lobby)
    {
        _lobby = new(lobby); _connected.Add(LocalId);
        _request = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnRequest);
        _rejoin = CallResult<LobbyEnter_t>.Create((_, _) => _joining = false);
    }
    public void SetAuthority(string id) => _authority = id;
    public void InviteFriends() { if (!IsDisposed) SteamFriends.ActivateGameOverlayInviteDialog(_lobby); }
    public void Poll(double delta)
    {
        if (IsDisposed) return;
        _now += delta;
        if (_now >= _maintenance)
        {
            _maintenance = _now + 1; UpdateRoster();
            var local = SteamUser.GetSteamID();
            var members = Enumerable.Range(0, SteamMatchmaking.GetNumLobbyMembers(_lobby))
                .Select(i => SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i)).ToArray();
            if (!members.Contains(local) && !_joining)
            { _joining = true; _rejoin.Set(SteamMatchmaking.JoinLobby(_lobby)); }
            if (SteamMatchmaking.GetLobbyOwner(_lobby) == local && _authority is { } leader &&
                ulong.TryParse(leader, out var desired) && desired != (ulong)local && members.Contains(new CSteamID(desired)))
                SteamMatchmaking.SetLobbyOwner(_lobby, new(desired));
            foreach (var peer in _roster.Where(p => p.Id != LocalId))
            {
                SendRaw(peer.Id, [0]);
                if (_lastSeen.TryGetValue(peer.Id, out var seen) && _now - seen > 5) _connected.Remove(peer.Id);
            }
        }
        for (var batch = 0; batch < 8; batch++)
        {
            var count = SteamNetworkingMessages.ReceiveMessagesOnChannel(ChannelId, _pointers, _pointers.Length);
            for (var i = 0; i < count; i++) Receive(_pointers[i]);
            if (count < _pointers.Length) break;
        }
    }
    private void UpdateRoster()
    {
        if (_roster.Count == 3) return;
        var data = SteamMatchmaking.GetLobbyData(_lobby, "xq3_roster");
        if (string.IsNullOrEmpty(data))
        {
            var others = Enumerable.Range(0, SteamMatchmaking.GetNumLobbyMembers(_lobby))
                .Select(i => SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i).m_SteamID)
                .Where(id => id != SteamMatchmaking.GetLobbyOwner(_lobby).m_SteamID).Order();
            var ids = new[] { SteamMatchmaking.GetLobbyOwner(_lobby).m_SteamID }.Concat(others).Take(3).ToArray();
            if (ids.Length == 3 && SteamMatchmaking.GetLobbyOwner(_lobby) == SteamUser.GetSteamID())
            {
                data = string.Join(',', ids);
                if (!SteamMatchmaking.SetLobbyData(_lobby, "xq3_roster", data)) return;
            }
            else
            {
                _roster.Clear(); _roster.AddRange(ids.Select(Peer)); return;
            }
        }
        var parts = data.Split(',');
        if (parts.Length != 3 || parts.Distinct().Count() != 3 || parts.Any(p => !ulong.TryParse(p, out var id) || !new CSteamID(id).IsValid())) return;
        if (!parts.Contains(LocalId)) { Dispose(); return; }
        _roster.Clear(); _roster.AddRange(parts.Select(p => Peer(ulong.Parse(p))));
    }
    private static ThreePeer Peer(ulong id)
    {
        var name = SteamFriends.GetFriendPersonaName(new(id));
        if (id == SteamUser.GetSteamID().m_SteamID) name = SteamFriends.GetPersonaName();
        name = new string(name.Where(c => !char.IsControl(c)).Take(28).ToArray()).Trim();
        return new(id.ToString(), string.IsNullOrWhiteSpace(name) ? "Steam 玩家" : name);
    }
    public void Send(string id, byte[] packet)
    {
        if (packet.Length > ThreeNetworkController.MaximumPacketBytes) return;
        var bytes = new byte[packet.Length + 1]; bytes[0] = 1; packet.CopyTo(bytes, 1); SendRaw(id, bytes);
    }
    private void SendRaw(string id, byte[] bytes)
    {
        if (IsDisposed || !ulong.TryParse(id, out var steamId)) return;
        var identity = new SteamNetworkingIdentity(); identity.SetSteamID(new(steamId));
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            SteamNetworkingMessages.SendMessageToUser(ref identity, handle.AddrOfPinnedObject(), (uint)bytes.Length,
                Constants.k_nSteamNetworkingSend_Reliable | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession, ChannelId);
        }
        finally { handle.Free(); }
    }
    private void Receive(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return;
        try
        {
            var native = SteamNetworkingMessage_t.FromIntPtr(pointer);
            var id = native.m_identityPeer.GetSteamID().m_SteamID.ToString();
            if (!_roster.Any(p => p.Id == id) || native.m_cbSize is < 1 or > ThreeNetworkController.MaximumPacketBytes + 1) return;
            var bytes = new byte[native.m_cbSize]; Marshal.Copy(native.m_pData, bytes, 0, bytes.Length);
            _lastSeen[id] = _now; _connected.Add(id);
            if (bytes[0] == 1) Received?.Invoke(id, bytes[1..]);
        }
        finally { SteamNetworkingMessage_t.Release(pointer); }
    }
    private void OnRequest(SteamNetworkingMessagesSessionRequest_t callback)
    {
        var id = callback.m_identityRemote.GetSteamID().m_SteamID.ToString();
        if (_roster.Any(p => p.Id == id)) SteamNetworkingMessages.AcceptSessionWithUser(ref callback.m_identityRemote);
    }
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true; _request.Dispose(); _rejoin.Dispose();
        foreach (var peer in _roster.Where(p => p.Id != LocalId))
        {
            var identity = new SteamNetworkingIdentity(); identity.SetSteamID(new(ulong.Parse(peer.Id)));
            SteamNetworkingMessages.CloseChannelWithUser(ref identity, ChannelId);
        }
        SteamMatchmaking.LeaveLobby(_lobby); _connected.Clear();
    }
}
#endif
