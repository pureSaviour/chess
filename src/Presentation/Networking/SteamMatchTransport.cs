#if GODOT_WINDOWS
using System.Runtime.InteropServices;
using Godot;
using Steamworks;
using Xiangqi.Core.Application;

namespace Xiangqi.Presentation.Networking;

/// <summary>
/// Steam Networking Messages adapter for the transport-neutral match contract. The lobby service
/// authenticates the opponent as a current member before constructing this adapter; it then accepts
/// messages only from that exact Steam ID. Steam supplies NAT traversal/relay selection while this
/// class retains the same bounded <see cref="MatchWireProtocol"/> used by LAN play.
/// </summary>
public partial class SteamMatchTransport : Node, IMatchTransport, ITwoPlayerPacketTransport
{
    private const int MatchChannel = 48017;
    private const int ReceiveBatchSize = 16;
    private readonly CSteamID _remoteUser;
    private SteamNetworkingIdentity _remoteIdentity;
    private readonly bool _isHost;
    private Callback<SteamNetworkingMessagesSessionRequest_t>? _sessionRequest;
    private Callback<SteamNetworkingMessagesSessionFailed_t>? _sessionFailed;
    private bool _disposed;

    internal SteamMatchTransport(CSteamID remoteUser, bool isHost)
    {
        _remoteUser = remoteUser;
        _isHost = isHost;
        _remoteIdentity = new SteamNetworkingIdentity();
        _remoteIdentity.SetSteamID(remoteUser);
        ProcessMode = ProcessModeEnum.Always;
        IsConnected = true;
        _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
        _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
    }

    public event Action<MoveCommand>? MoveReceived;
    public event Action<NetworkMatchConfiguration>? MatchConfigurationReceived;
    public event Action<TimeoutCommand>? TimeoutReceived;
    public event Action<ClockSyncCommand>? ClockSyncReceived;
    public event Action<ChatCommand>? ChatReceived;
    public event Action<MatchOfferRequest>? MatchOfferReceived;
    public event Action<MatchOfferResponse>? MatchOfferResponseReceived;
    public event Action<PlayerProfileCommand>? PlayerProfileReceived;
    public event Action<TransportStatus>? StatusChanged;
    public event Action<ReadOnlyMemory<byte>>? PacketReceived;
    public event Action<TransportStatus>? PacketStatusChanged;
    internal event Action? Closed;
    public new bool IsConnected { get; private set; }
    public bool IsHost => _isHost;
    public bool IsPacketConnected => IsConnected;
    public bool IsPacketHost => IsHost;
    internal CSteamID RemoteUser => _remoteUser;

    public override void _Ready() => PublishStatus(new TransportStatus(
        TransportConnectionState.Connected, "Steam 好友已加入，P2P 对局连接就绪。"));

    public override void _Process(double delta)
    {
        if (_disposed || !IsConnected) return;
        var pointers = new IntPtr[ReceiveBatchSize];
        int count;
        do
        {
            count = SteamNetworkingMessages.ReceiveMessagesOnChannel(
                MatchChannel, pointers, pointers.Length);
            for (var index = 0; index < count; index++) Receive(pointers[index]);
        } while (count == pointers.Length);
    }

    public ValueTask SendMoveAsync(MoveCommand command, CancellationToken cancellationToken = default) =>
        SendAsync(MatchWireProtocol.Encode(command), cancellationToken);
    public ValueTask SendMatchConfigurationAsync(
        NetworkMatchConfiguration configuration, CancellationToken cancellationToken = default) =>
        SendAsync(MatchWireProtocol.Encode(configuration), cancellationToken);
    public ValueTask SendTimeoutAsync(
        TimeoutCommand command, CancellationToken cancellationToken = default) =>
        SendAsync(MatchWireProtocol.Encode(command), cancellationToken);
    public ValueTask SendClockSyncAsync(
        ClockSyncCommand command, CancellationToken cancellationToken = default) =>
        SendAsync(MatchWireProtocol.Encode(command), cancellationToken);
    public ValueTask SendChatAsync(
        ChatCommand command, CancellationToken cancellationToken = default) =>
        SendAsync(MatchWireProtocol.Encode(command), cancellationToken);
    public ValueTask SendMatchOfferAsync(
        MatchOfferRequest command, CancellationToken cancellationToken = default) =>
        SendAsync(MatchWireProtocol.Encode(command), cancellationToken);
    public ValueTask SendMatchOfferResponseAsync(
        MatchOfferResponse command, CancellationToken cancellationToken = default) =>
        SendAsync(MatchWireProtocol.Encode(command), cancellationToken);
    public ValueTask SendPlayerProfileAsync(
        PlayerProfileCommand command, CancellationToken cancellationToken = default) =>
        SendAsync(MatchWireProtocol.Encode(command), cancellationToken);
    public ValueTask SendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        if (packet.Length is < 1 or > MatchWireProtocol.MaximumPacketBytes)
            throw new ArgumentOutOfRangeException(nameof(packet), "联机数据包长度无效。");
        return SendAsync(packet.ToArray(), cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        IsConnected = false;
        SteamNetworkingMessages.CloseChannelWithUser(ref _remoteIdentity, MatchChannel);
        _sessionRequest?.Dispose();
        _sessionRequest = null;
        _sessionFailed?.Dispose();
        _sessionFailed = null;
        Closed?.Invoke();
        if (IsInsideTree()) QueueFree();
        return ValueTask.CompletedTask;
    }

    internal void NotifyPeerLeft()
    {
        if (_disposed || !IsConnected) return;
        IsConnected = false;
        PublishStatus(new TransportStatus(
            TransportConnectionState.Disconnected, "Steam 好友已离开房间。"));
    }

    private ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !IsConnected) throw new InvalidOperationException("Steam 对局连接尚未建立。");
        var handle = GCHandle.Alloc(packet, GCHandleType.Pinned);
        try
        {
            var result = SteamNetworkingMessages.SendMessageToUser(
                ref _remoteIdentity,
                handle.AddrOfPinnedObject(),
                (uint)packet.Length,
                Constants.k_nSteamNetworkingSend_Reliable,
                MatchChannel);
            if (result != EResult.k_EResultOK)
                throw new IOException($"Steam 联机数据发送失败：{result}。");
        }
        finally
        {
            handle.Free();
        }
        return ValueTask.CompletedTask;
    }

    private void Receive(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return;
        try
        {
            var native = SteamNetworkingMessage_t.FromIntPtr(pointer);
            if (native.m_identityPeer.GetSteamID() != _remoteUser ||
                native.m_cbSize is < 2 or > MatchWireProtocol.MaximumPacketBytes) return;
            var packet = new byte[native.m_cbSize];
            Marshal.Copy(native.m_pData, packet, 0, packet.Length);
            if (PacketReceived is not null) PacketReceived.Invoke(packet);
            else if (!TryDispatch(packet))
                PublishStatus(new TransportStatus(
                    TransportConnectionState.Faulted, "收到格式或版本不正确的 Steam 对局数据。"));
        }
        finally
        {
            SteamNetworkingMessage_t.Release(pointer);
        }
    }

    private bool TryDispatch(byte[] packet)
    {
        if (!MatchWireProtocol.TryDecode(packet, out var message)) return false;
        switch (message.Type)
        {
            case MatchWireMessageType.Move: MoveReceived?.Invoke((MoveCommand)message.Payload); break;
            case MatchWireMessageType.Configuration:
                MatchConfigurationReceived?.Invoke((NetworkMatchConfiguration)message.Payload); break;
            case MatchWireMessageType.Timeout: TimeoutReceived?.Invoke((TimeoutCommand)message.Payload); break;
            case MatchWireMessageType.ClockSync: ClockSyncReceived?.Invoke((ClockSyncCommand)message.Payload); break;
            case MatchWireMessageType.Chat: ChatReceived?.Invoke((ChatCommand)message.Payload); break;
            case MatchWireMessageType.MatchOffer:
                MatchOfferReceived?.Invoke((MatchOfferRequest)message.Payload); break;
            case MatchWireMessageType.MatchOfferResponse:
                MatchOfferResponseReceived?.Invoke((MatchOfferResponse)message.Payload); break;
            case MatchWireMessageType.PlayerProfile:
                PlayerProfileReceived?.Invoke((PlayerProfileCommand)message.Payload); break;
            default: return false;
        }
        return true;
    }

    private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t callback)
    {
        if (callback.m_identityRemote.GetSteamID() == _remoteUser)
            SteamNetworkingMessages.AcceptSessionWithUser(ref callback.m_identityRemote);
    }

    private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t callback)
    {
        if (callback.m_info.m_identityRemote.GetSteamID() != _remoteUser) return;
        IsConnected = false;
        PublishStatus(new TransportStatus(
            TransportConnectionState.Faulted, "Steam P2P 会话失败，请检查双方 Steam 在线状态。"));
        _ = DisposeAfterCallbackAsync();
    }

    private async Task DisposeAfterCallbackAsync()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await DisposeAsync();
    }

    private void PublishStatus(TransportStatus status)
    {
        StatusChanged?.Invoke(status);
        PacketStatusChanged?.Invoke(status);
    }
}
#endif
