using System.Text;
using Xiangqi.Core.Domain;

namespace Xiangqi.Core.Application;

public enum MatchWireMessageType : byte
{
    Move = 1,
    Configuration = 2,
    Timeout = 3,
    ClockSync = 4,
    Chat = 5,
    MatchOffer = 6,
    MatchOfferResponse = 7,
    PlayerProfile = 8
}

public readonly record struct MatchWireMessage(MatchWireMessageType Type, object Payload);

/// <summary>
/// Versioned, transport-independent binary protocol for a Xiangqi match. ENet and Steam
/// Networking Messages both carry these exact packets, so adding a transport cannot silently
/// change gameplay semantics. Decoding is deliberately closed: packets have fixed lengths where
/// possible, bounded UTF-8 text, defined enum values and valid board coordinates before they are
/// allowed to reach <see cref="NetworkGameCoordinator"/>.
/// </summary>
public static class MatchWireProtocol
{
    public const byte Version = 6;
    public const int MaximumPacketBytes = 16600;
    public const int MaximumChatUtf8Bytes = RoomChatManager.MaximumTextUtf8Bytes;
    public const int MaximumProfileTextUtf8Bytes = 96;
    public const int MaximumAvatarSide = 64;
    private const int MovePacketLength = 26;
    private const int MatchOfferPacketLength = 19;
    private const int MatchOfferResponsePacketLength = 20;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(MoveCommand command)
    {
        using var stream = new MemoryStream(MovePacketLength);
        using var writer = Writer(stream, MatchWireMessageType.Move);
        writer.Write(command.Sequence);
        writer.Write(command.Move.From.File);
        writer.Write(command.Move.From.Rank);
        writer.Write(command.Move.To.File);
        writer.Write(command.Move.To.Rank);
        return stream.ToArray();
    }

    public static byte[] Encode(NetworkMatchConfiguration configuration)
    {
        using var stream = new MemoryStream(20);
        using var writer = Writer(stream, MatchWireMessageType.Configuration);
        writer.Write(configuration.TimeControl.GameSeconds);
        writer.Write(configuration.TimeControl.MoveSeconds);
        writer.Write((byte)configuration.HostSide);
        writer.Write((byte)configuration.Mode);
        writer.Write(configuration.BoardSize);
        writer.Write(configuration.RuleFlags);
        return stream.ToArray();
    }

    public static byte[] Encode(TimeoutCommand command)
    {
        using var stream = new MemoryStream(11);
        using var writer = Writer(stream, MatchWireMessageType.Timeout);
        writer.Write(command.PlyNumber);
        writer.Write((byte)command.Loser);
        return stream.ToArray();
    }

    public static byte[] Encode(ClockSyncCommand command)
    {
        using var stream = new MemoryStream(35);
        using var writer = Writer(stream, MatchWireMessageType.ClockSync);
        writer.Write(command.PlyNumber);
        writer.Write(command.Clock.RedGameSeconds);
        writer.Write(command.Clock.BlackGameSeconds);
        writer.Write(command.Clock.MoveSeconds);
        writer.Write((byte)command.Clock.SideToMove);
        return stream.ToArray();
    }

    public static byte[] Encode(ChatCommand command)
    {
        var entry = command.Entry ?? throw new ArgumentException("聊天消息不能为空。", nameof(command));
        var text = entry.Text.Trim();
        if (text.Length == 0 || text.Any(char.IsControl))
            throw new ArgumentException("聊天消息不能为空或包含控制字符。", nameof(command));
        if (entry.Id is not { Length: 32 } || !entry.Id.All(Uri.IsHexDigit) || entry.SenderSeat is < 0 or > 1 ||
            entry.TargetSeat is { } target && (target is < 0 or > 1 || target == entry.SenderSeat))
            throw new ArgumentException("聊天消息身份或目标无效。", nameof(command));
        var content = StrictUtf8.GetBytes(text);
        if (content.Length > MaximumChatUtf8Bytes)
            throw new ArgumentException(
                $"聊天消息不能超过 {MaximumChatUtf8Bytes} 个 UTF-8 字节。", nameof(command));

        using var stream = new MemoryStream(content.Length + 38);
        using var writer = Writer(stream, MatchWireMessageType.Chat);
        writer.Write(Encoding.ASCII.GetBytes(entry.Id));
        writer.Write((byte)entry.SenderSeat);
        writer.Write((sbyte)(entry.TargetSeat ?? -1));
        writer.Write((ushort)content.Length);
        writer.Write(content);
        return stream.ToArray();
    }

    public static byte[] Encode(MatchOfferRequest command)
    {
        using var stream = new MemoryStream(MatchOfferPacketLength);
        using var writer = Writer(stream, MatchWireMessageType.MatchOffer);
        writer.Write(command.RequestId);
        writer.Write(command.PlyNumber);
        writer.Write((byte)command.Kind);
        return stream.ToArray();
    }

    public static byte[] Encode(MatchOfferResponse command)
    {
        using var stream = new MemoryStream(MatchOfferResponsePacketLength);
        using var writer = Writer(stream, MatchWireMessageType.MatchOfferResponse);
        writer.Write(command.RequestId);
        writer.Write(command.PlyNumber);
        writer.Write((byte)command.Kind);
        writer.Write(command.Accepted);
        return stream.ToArray();
    }

    public static byte[] Encode(PlayerProfileCommand command)
    {
        var playerId = EncodeProfileText(command.PlayerId, nameof(command.PlayerId));
        var displayName = EncodeProfileText(command.DisplayName, nameof(command.DisplayName));
        ValidateAvatar(command.AvatarWidth, command.AvatarHeight, command.AvatarRgba);
        using var stream = new MemoryStream(10 + playerId.Length + displayName.Length + command.AvatarRgba.Length);
        using var writer = Writer(stream, MatchWireMessageType.PlayerProfile);
        writer.Write((ushort)playerId.Length);
        writer.Write(playerId);
        writer.Write((ushort)displayName.Length);
        writer.Write(displayName);
        writer.Write((byte)command.AvatarWidth);
        writer.Write((byte)command.AvatarHeight);
        writer.Write((ushort)command.AvatarRgba.Length);
        writer.Write(command.AvatarRgba);
        return stream.ToArray();
    }

    public static bool TryDecode(byte[] packet, out MatchWireMessage message)
    {
        message = default;
        if (packet.Length is < 2 or > MaximumPacketBytes || packet[0] != Version) return false;
        var type = (MatchWireMessageType)packet[1];
        object? payload = type switch
        {
            MatchWireMessageType.Move when TryDecodeMove(packet, out var value) => value,
            MatchWireMessageType.Configuration when TryDecodeConfiguration(packet, out var value) => value,
            MatchWireMessageType.Timeout when TryDecodeTimeout(packet, out var value) => value,
            MatchWireMessageType.ClockSync when TryDecodeClock(packet, out var value) => value,
            MatchWireMessageType.Chat when TryDecodeChat(packet, out var value) => value,
            MatchWireMessageType.MatchOffer when TryDecodeMatchOffer(packet, out var value) => value,
            MatchWireMessageType.MatchOfferResponse when TryDecodeMatchOfferResponse(packet, out var value) => value,
            MatchWireMessageType.PlayerProfile when TryDecodePlayerProfile(packet, out var value) => value,
            _ => null
        };
        if (payload is null) return false;
        message = new MatchWireMessage(type, payload);
        return true;
    }

    private static bool TryDecodeMove(byte[] packet, out MoveCommand command)
    {
        command = default;
        if (packet.Length != MovePacketLength) return false;
        using var reader = ReaderAfterHeader(packet);
        var sequence = reader.ReadInt64();
        var from = new BoardPosition(reader.ReadInt32(), reader.ReadInt32());
        var to = new BoardPosition(reader.ReadInt32(), reader.ReadInt32());
        if (sequence <= 0 || !from.IsInside || !to.IsInside) return false;
        command = new MoveCommand(sequence, new ChessMove(from, to));
        return true;
    }

    private static bool TryDecodeConfiguration(
        byte[] packet, out NetworkMatchConfiguration configuration)
    {
        configuration = default;
        if (packet.Length != 20) return false;
        using var reader = ReaderAfterHeader(packet);
        var gameSeconds = reader.ReadInt32();
        var moveSeconds = reader.ReadInt32();
        var hostSide = (PieceSide)reader.ReadByte();
        var mode = (TwoPlayerGameMode)reader.ReadByte();
        if (gameSeconds < 0 || moveSeconds < 0 || !Enum.IsDefined(hostSide) || !Enum.IsDefined(mode)) return false;
        var boardSize = reader.ReadInt32();
        var ruleFlags = reader.ReadInt32();
        if (boardSize is < 0 or > 25 || ruleFlags is < 0 or > 1) return false;
        configuration = new NetworkMatchConfiguration(new TimeControl(gameSeconds, moveSeconds), hostSide, mode, boardSize, ruleFlags);
        return true;
    }

    private static bool TryDecodeTimeout(byte[] packet, out TimeoutCommand command)
    {
        command = default;
        if (packet.Length != 11) return false;
        using var reader = ReaderAfterHeader(packet);
        var ply = reader.ReadInt64();
        var loser = (PieceSide)reader.ReadByte();
        if (ply < 0 || !Enum.IsDefined(loser)) return false;
        command = new TimeoutCommand(ply, loser);
        return true;
    }

    private static bool TryDecodeClock(byte[] packet, out ClockSyncCommand command)
    {
        command = default;
        if (packet.Length != 35) return false;
        using var reader = ReaderAfterHeader(packet);
        var ply = reader.ReadInt64();
        var red = reader.ReadDouble();
        var black = reader.ReadDouble();
        var move = reader.ReadDouble();
        var side = (PieceSide)reader.ReadByte();
        if (ply < 0 || !double.IsFinite(red) || !double.IsFinite(black) ||
            !double.IsFinite(move) || red < 0 || black < 0 || move < 0 || !Enum.IsDefined(side))
            return false;
        command = new ClockSyncCommand(ply, new ClockSnapshot(red, black, move, side));
        return true;
    }

    private static bool TryDecodeChat(byte[] packet, out ChatCommand command)
    {
        command = default;
        if (packet.Length < 39 || packet.Length > MaximumChatUtf8Bytes + 38) return false;
        var id = Encoding.ASCII.GetString(packet, 2, 32);
        var sender = packet[34];
        var targetValue = unchecked((sbyte)packet[35]);
        int? target = targetValue < 0 ? null : targetValue;
        var length = BitConverter.ToUInt16(packet, 36);
        if (id.Length != 32 || !id.All(Uri.IsHexDigit) || sender > 1 ||
            target is { } recipient && (recipient > 1 || recipient == sender) ||
            length == 0 || length > MaximumChatUtf8Bytes || packet.Length != length + 38) return false;
        try
        {
            var text = StrictUtf8.GetString(packet, 38, length);
            if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) return false;
            command = new ChatCommand(new RoomChatEntry(id, sender, target, text.Trim()));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryDecodeMatchOffer(byte[] packet, out MatchOfferRequest command)
    {
        command = default;
        if (packet.Length != MatchOfferPacketLength) return false;
        using var reader = ReaderAfterHeader(packet);
        var requestId = reader.ReadInt64();
        var ply = reader.ReadInt64();
        var kind = (MatchOfferKind)reader.ReadByte();
        if (requestId <= 0 || ply < 0 || !Enum.IsDefined(kind)) return false;
        command = new MatchOfferRequest(requestId, ply, kind);
        return true;
    }

    private static bool TryDecodeMatchOfferResponse(
        byte[] packet, out MatchOfferResponse command)
    {
        command = default;
        if (packet.Length != MatchOfferResponsePacketLength) return false;
        using var reader = ReaderAfterHeader(packet);
        var requestId = reader.ReadInt64();
        var ply = reader.ReadInt64();
        var kind = (MatchOfferKind)reader.ReadByte();
        var accepted = reader.ReadBoolean();
        if (requestId <= 0 || ply < 0 || !Enum.IsDefined(kind)) return false;
        command = new MatchOfferResponse(requestId, ply, kind, accepted);
        return true;
    }

    private static bool TryDecodePlayerProfile(byte[] packet, out PlayerProfileCommand command)
    {
        command = default;
        try
        {
            using var reader = ReaderAfterHeader(packet);
            var id = ReadProfileText(reader);
            var name = ReadProfileText(reader);
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            var avatarLength = reader.ReadUInt16();
            if (reader.BaseStream.Length - reader.BaseStream.Position != avatarLength) return false;
            var avatar = reader.ReadBytes(avatarLength);
            ValidateAvatar(width, height, avatar);
            command = new PlayerProfileCommand(id, name, width, height, avatar);
            return true;
        }
        catch (Exception exception) when (exception is EndOfStreamException or DecoderFallbackException or ArgumentException)
        {
            return false;
        }
    }

    private static byte[] EncodeProfileText(string value, string parameterName)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Any(char.IsControl))
            throw new ArgumentException("玩家资料文字不能为空或包含控制字符。", parameterName);
        var bytes = StrictUtf8.GetBytes(text);
        if (bytes.Length > MaximumProfileTextUtf8Bytes)
            throw new ArgumentException($"玩家资料文字不能超过 {MaximumProfileTextUtf8Bytes} 个 UTF-8 字节。", parameterName);
        return bytes;
    }

    private static string ReadProfileText(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        if (length is 0 or > MaximumProfileTextUtf8Bytes) throw new ArgumentException("玩家资料文字长度无效。");
        var value = StrictUtf8.GetString(reader.ReadBytes(length));
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException("玩家资料文字无效。");
        return value.Trim();
    }

    private static void ValidateAvatar(int width, int height, byte[] rgba)
    {
        if (width == 0 && height == 0 && rgba.Length == 0) return;
        if (width is <= 0 or > MaximumAvatarSide || height is <= 0 or > MaximumAvatarSide ||
            rgba.Length != checked(width * height * 4))
            throw new ArgumentException("玩家头像必须是最多 64×64 的完整 RGBA8 数据。");
    }

    private static BinaryWriter Writer(Stream stream, MatchWireMessageType type)
    {
        var writer = new BinaryWriter(stream, StrictUtf8, true);
        writer.Write(Version);
        writer.Write((byte)type);
        return writer;
    }

    private static BinaryReader ReaderAfterHeader(byte[] packet)
    {
        var stream = new MemoryStream(packet, false) { Position = 2 };
        return new BinaryReader(stream, StrictUtf8, false);
    }
}
