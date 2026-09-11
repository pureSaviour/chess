using Xiangqi.Core.Application;

namespace Xiangqi.Presentation.Networking;

/// <summary>
/// Adapts the shared room-chat envelope to a raw two-player game channel. The local seat is
/// resolved lazily because clients learn it from the authoritative game's hello/state packet.
/// Every local message is directed to the sole opponent; this adapter owns neither the transport
/// nor its lifetime and must be disposed before the game controller disposes that transport.
/// </summary>
public sealed class RawTwoPlayerRoomChat : IDisposable
{
    private readonly ITwoPlayerPacketTransport _transport;
    private readonly Func<int?> _localSeat;
    private bool _disposed;

    public RawTwoPlayerRoomChat(ITwoPlayerPacketTransport transport, Func<int?> localSeat)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _localSeat = localSeat ?? throw new ArgumentNullException(nameof(localSeat));
        _transport.PacketReceived += OnPacket;
    }

    public RoomChatManager Messages { get; } = new(2);

    public async ValueTask<string?> SendToOpponentAsync(string text,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) return "聊天通道已关闭。";
        if (!_transport.IsPacketConnected) return "尚未连接到对手。";
        if (_localSeat() is not { } local) return "正在等待房间席位。";
        var error = Messages.PostToOpponent(local, text, out var entry);
        if (error is not null || entry is null) return error;
        try
        {
            await _transport.SendPacketAsync(MatchWireProtocol.Encode(new ChatCommand(entry)), cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            return $"发送失败：{exception.Message}";
        }
    }

    private void OnPacket(ReadOnlyMemory<byte> packet)
    {
        if (_disposed || _localSeat() is not { } local ||
            !MatchWireProtocol.TryDecode(packet.ToArray(), out var message) ||
            message.Type != MatchWireMessageType.Chat) return;
        var entry = ((ChatCommand)message.Payload).Entry;
        Messages.AcceptLive(entry, 1 - local);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.PacketReceived -= OnPacket;
    }
}
