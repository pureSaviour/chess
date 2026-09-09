using Xiangqi.Core.Application;
using Xiangqi.Core.Application.Jieqi;
using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.Jieqi;

internal static class JieqiTests
{
    public static readonly (string Name, Action Run)[] All =
    [
        ("揭棋同一种子产生完全相同的暗子排列", DeterministicSeed),
        ("揭棋公开局面隐藏真实身份且首步按槽位走法", RedactionAndCoverMove),
        ("揭棋落子后翻明并在棋谱中往返种子", RevealAndRecordRoundTrip),
        ("揭棋未结束时不允许释放种子棋谱", LiveRecordIsBlocked),
        ("揭棋房主权威协议只同步脱敏局面", AuthoritativeNetworkRedaction)
    ];

    private static void DeterministicSeed()
    {
        var first = JieqiGame.Create(0x123456789ABCDEF0).Snapshot;
        var second = JieqiGame.Create(0x123456789ABCDEF0).Snapshot;
        Equal(32, first.Pieces.Count); Equal(30, first.Pieces.Count(x => !x.Piece.IsRevealed));
        True(first.Pieces.SequenceEqual(second.Pieces));
    }

    private static void RedactionAndCoverMove()
    {
        var game = JieqiGame.Create(7); var snapshot = game.Snapshot;
        var publicPosition = snapshot.ToPublicPosition();
        Equal(30, publicPosition.Pieces.Count(x => x.Piece.Kind == BuiltInPieceKinds.Covered));
        Equal(30, snapshot.HiddenIdentityHints().Count);
        var session = new JieqiGameSession(7);
        Equal(30, session.Snapshot.Pieces.Count(x => x.Piece.Kind == BuiltInPieceKinds.Covered));
        // The covered piece at a horse slot moves as a horse, regardless of its shuffled identity.
        True(game.GetLegalDestinations(new BoardPosition(1, 0)).Contains(new BoardPosition(2, 2)));
        False(game.GetLegalDestinations(new BoardPosition(1, 0)).Contains(new BoardPosition(1, 1)));
    }

    private static void RevealAndRecordRoundTrip()
    {
        const ulong seed = 99;
        var game = JieqiGame.Create(seed);
        var move = new ChessMove(new BoardPosition(0, 3), new BoardPosition(0, 4));
        var result = game.TryMove(move); True(result.IsSuccess); True(result.Revealed);
        True(game.Snapshot.Pieces.Single(x => x.Position == move.To).Piece.IsRevealed);
        var record = new JieqiGameRecord(seed, [move], new Dictionary<string, string> { ["Result"] = "*" });
        var text = JieqiPgnCodec.Serialize(record);
        True(text.Contains("[Variant \"Jieqi\"]", StringComparison.Ordinal));
        True(text.Contains("[Shuffle \"splitmix64-fy-v1\"]", StringComparison.Ordinal));
        True(JieqiPgnCodec.TryParse(text, out var parsed, out var error));
        Equal(seed, parsed!.Seed); Equal(1, parsed.Replay()[^1].PlyNumber);
        False(JieqiPgnCodec.TryParse(text.Replace("splitmix64-fy-v1", "unknown"), out _, out _));
    }

    private static void LiveRecordIsBlocked()
    {
        var session = new JieqiGameSession(123);
        try { _ = session.CreateRecord(); throw new InvalidOperationException("Expected live record rejection."); }
        catch (InvalidOperationException error) when (error.Message.Contains("结束", StringComparison.Ordinal)) { }
    }

    private static void AuthoritativeNetworkRedaction()
    {
        const ulong seed = 0x123456789ABCDEF0;
        var hostTransport = new FakePacketTransport(true); var clientTransport = new FakePacketTransport(false);
        hostTransport.Peer = clientTransport; clientTransport.Peer = hostTransport;
        var host = new NetworkJieqiMatchController(hostTransport, PieceSide.Black, seed);
        var client = new NetworkJieqiMatchController(clientTransport, PieceSide.Black);
        hostTransport.Connect(); clientTransport.Connect();
        Equal(30, client.Snapshot.Pieces.Count(x => x.Piece.Kind == BuiltInPieceKinds.Covered));
        False(clientTransport.ReceivedText.Any(x => x.Contains(seed.ToString("X16"), StringComparison.OrdinalIgnoreCase)));
        var submit = client.SubmitMoveAsync(new(0, 3), new(0, 4)).AsTask().GetAwaiter().GetResult();
        True(submit.Accepted); Equal(1, host.Snapshot.PlyNumber); Equal(1, client.Snapshot.PlyNumber);
        True(host.Snapshot.Pieces.SequenceEqual(client.Snapshot.Pieces));
        False(clientTransport.ReceivedText.Any(x => x.Contains(seed.ToString("X16"), StringComparison.OrdinalIgnoreCase)));
        host.DisposeAsync().AsTask().GetAwaiter().GetResult(); client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void False(bool value) => True(!value);
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }
}

internal sealed class FakePacketTransport(bool host) : ITwoPlayerPacketTransport
{
    public FakePacketTransport? Peer { get; set; }
    public List<string> ReceivedText { get; } = [];
    public event Action<ReadOnlyMemory<byte>>? PacketReceived;
    public event Action<TransportStatus>? PacketStatusChanged;
    public bool IsPacketConnected { get; private set; }
    public bool IsPacketHost { get; } = host;
    public void Connect()
    {
        IsPacketConnected = true;
        if (Peer is not null) Peer.IsPacketConnected = true;
        PacketStatusChanged?.Invoke(new TransportStatus(TransportConnectionState.Connected));
    }
    public ValueTask SendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPacketConnected || Peer is null) throw new InvalidOperationException("Disconnected fake.");
        Peer.ReceivedText.Add(System.Text.Encoding.UTF8.GetString(packet.Span));
        Peer.PacketReceived?.Invoke(packet.ToArray()); return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() { IsPacketConnected = false; return ValueTask.CompletedTask; }
}
