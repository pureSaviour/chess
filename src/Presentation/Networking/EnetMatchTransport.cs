using Godot;
using Xiangqi.Core.Application;

namespace Xiangqi.Presentation.Networking;

/// <summary>
/// Godot ENet adapter for <see cref="IMatchTransport"/>. It owns only LAN connection state;
/// packet validation and command encoding live in <see cref="MatchWireProtocol"/> and are shared
/// with Steam, keeping gameplay behavior independent of the selected network backend.
/// </summary>
public partial class EnetMatchTransport : Node, IMatchTransport
{
    public const int DefaultPort = 27888;
    private readonly ENetMultiplayerPeer _peer = new();
    private readonly bool _isHost;
    private bool _disposed;
    private long? _remotePeerId;
    private MultiplayerPeer.ConnectionStatus _lastPeerStatus;

    private EnetMatchTransport(bool isHost)
    {
        _isHost = isHost;
        ProcessMode = ProcessModeEnum.Always;
        _peer.TransferMode = MultiplayerPeer.TransferModeEnum.Reliable;
        _peer.PeerConnected += OnPeerConnected;
        _peer.PeerDisconnected += OnPeerDisconnected;
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
    public new bool IsConnected { get; private set; }
    public bool IsHost => _isHost;

    public static EnetMatchTransport Host(int port = DefaultPort)
    {
        ValidatePort(port);
        var transport = new EnetMatchTransport(true);
        var error = transport._peer.CreateServer(port, 1);
        if (error != Error.Ok)
        {
            transport.DisposePeer();
            throw new InvalidOperationException($"无法在 UDP 端口 {port} 创建房间：{error}。");
        }
        transport._lastPeerStatus = transport._peer.GetConnectionStatus();
        return transport;
    }

    public static EnetMatchTransport Join(string address, int port = DefaultPort)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("服务器地址不能为空。", nameof(address));
        ValidatePort(port);
        var transport = new EnetMatchTransport(false);
        var error = transport._peer.CreateClient(address.Trim(), port);
        if (error != Error.Ok)
        {
            transport.DisposePeer();
            throw new InvalidOperationException($"无法连接 {address}:{port}：{error}。");
        }
        transport._lastPeerStatus = transport._peer.GetConnectionStatus();
        return transport;
    }

    public override void _Ready() => StatusChanged?.Invoke(new TransportStatus(
        TransportConnectionState.Connecting,
        _isHost ? "房间已创建，等待另一位玩家加入。" : "正在连接房主。"));

    public override void _Process(double delta)
    {
        if (_disposed) return;
        if (_peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Disconnected)
        {
            UpdateConnectionStatus();
            return;
        }
        _peer.Poll();
        UpdateConnectionStatus();
        while (_peer.GetAvailablePacketCount() > 0)
        {
            var sender = _peer.GetPacketPeer();
            var packet = _peer.GetPacket();
            if (_remotePeerId is not null && sender != _remotePeerId.Value) continue;
            if (TryDispatch(packet)) continue;
            StatusChanged?.Invoke(new TransportStatus(
                TransportConnectionState.Faulted, "收到格式或版本不正确的联机数据。"));
        }
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

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        DisposePeer();
        IsConnected = false;
        if (IsInsideTree()) QueueFree();
        return ValueTask.CompletedTask;
    }

    private ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SendPacket(packet);
        return ValueTask.CompletedTask;
    }

    private void OnPeerConnected(long id)
    {
        if (_remotePeerId is not null && _remotePeerId != id)
        {
            _peer.DisconnectPeer((int)id, true);
            return;
        }
        _remotePeerId = id;
        IsConnected = true;
        if (_isHost) _peer.RefuseNewConnections = true;
        StatusChanged?.Invoke(new TransportStatus(
            TransportConnectionState.Connected, "双方已连接，可以开始对局。"));
    }

    private void OnPeerDisconnected(long id)
    {
        if (_remotePeerId != id) return;
        _remotePeerId = null;
        IsConnected = false;
        SetProcess(false);
        StatusChanged?.Invoke(new TransportStatus(
            TransportConnectionState.Disconnected, "另一位玩家已断开连接。"));
    }

    private void UpdateConnectionStatus()
    {
        var current = _peer.GetConnectionStatus();
        if (current == _lastPeerStatus) return;
        var previous = _lastPeerStatus;
        _lastPeerStatus = current;
        if (!_isHost && current == MultiplayerPeer.ConnectionStatus.Connected)
        {
            _remotePeerId ??= MultiplayerPeer.TargetPeerServer;
            IsConnected = true;
            StatusChanged?.Invoke(new TransportStatus(TransportConnectionState.Connected, "已连接房主。"));
        }
        else if (current == MultiplayerPeer.ConnectionStatus.Disconnected &&
                 previous == MultiplayerPeer.ConnectionStatus.Connecting)
        {
            IsConnected = false;
            StatusChanged?.Invoke(new TransportStatus(
                TransportConnectionState.Faulted, "连接失败，请检查地址、端口和防火墙。"));
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

    private void SendPacket(byte[] packet)
    {
        if (!IsConnected || _remotePeerId is null)
            throw new InvalidOperationException("联机连接尚未建立。");
        _peer.SetTargetPeer((int)_remotePeerId.Value);
        var error = _peer.PutPacket(packet);
        if (error != Error.Ok) throw new IOException($"发送联机数据失败：{error}。");
    }

    private void DisposePeer()
    {
        _peer.PeerConnected -= OnPeerConnected;
        _peer.PeerDisconnected -= OnPeerDisconnected;
        _peer.Close();
        _peer.Dispose();
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须介于 1024 和 65535 之间。");
    }
}
